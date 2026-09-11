using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Persistence;
using MateOS.Domain.Agent;
using Microsoft.EntityFrameworkCore;
using Project = MateOS.Api.Persistence.Project;
using DbAgent = MateOS.Api.Persistence.Agent;
using DbAgentExecution = MateOS.Api.Persistence.AgentExecution;
using DbExecutionAttempt = MateOS.Api.Persistence.ExecutionAttempt;
using DbExecutionEvent = MateOS.Api.Persistence.ExecutionEvent;
using DbAgentDispatchInbox = MateOS.Api.Persistence.AgentDispatchInbox;

namespace MateOS.Api.Agents;

// ── DTO ──

public sealed record CreateAgentExecutionRequest(
    Guid AgentId,
    Guid? CollaborationRequestId,
    Guid? WorkItemRef,
    JsonElement Input,
    JsonElement? ContextRefs,
    string IdempotencyKey,
    long? DeadlineS);

public sealed record ReportExecutionEventRequest(
    string EventType,
    string ProviderEventId,
    long Seq,
    JsonElement Payload);

public sealed record ReportExecutionResultRequest(
    string Status,
    string EnvelopeId,
    JsonElement? Output,
    JsonElement? Usage);

public sealed record ExecutionSummary(
    Guid Id,
    Guid? CollaborationRequestId,
    Guid AgentId,
    Guid? WorkItemRef,
    string Status,
    long? DispatchSentAtMs,
    long? DispatchAckedAtMs,
    long LastPersistedSeq,
    int? ActiveAttemptNo,
    int AttemptCount,
    long CreatedAtMs,
    long? StartedAtMs,
    long? CompletedAtMs,
    long? DeadlineAtMs);

public sealed record ExecutionAttemptSummary(
    Guid Id,
    Guid ExecutionId,
    int AttemptNo,
    string Status,
    long LastPersistedSeq,
    long? DispatchSentAtMs,
    long? DispatchAckedAtMs,
    long? StartedAtMs,
    long? CompletedAtMs,
    string? TerminalErrorCode,
    string? TerminalMessage);

public sealed record DispatchEnvelope(
    Guid ExecutionId,
    int AttemptNo,
    JsonElement Input,
    JsonElement? ContextRefs,
    long? DeadlineAtMs,
    string IdempotencyKey);

/// <summary>
/// E7 内部 API：execution dispatch + event 上报 + result 上报 + dispatch_ack + status 上报。
/// </summary>
/// <remarks>
/// <para>
/// V1 简化：
/// <list type="bullet">
///   <item>dispatch：HTTP POST 走 user/admin 鉴权，E4 内部调用创建 execution + attempt + dispatch inbox</item>
///   <item>inbox 查询：agent_token 鉴权，agent 拉取自己 PENDING 的 dispatch</item>
///   <item>event 上报：agent_token 鉴权，contiguous cursor 校验</item>
///   <item>result 上报：agent_token 鉴权，CAS 终态守卫</item>
///   <item>WS push 留 M3b Phase 2（详细设计 03 §2 execution 域协议扩展）</item>
/// </list>
/// </para>
/// <para>
/// 协议来源：detailed/01 §1 + detailed/03 §3 contiguous cursor + §5 idempotency 三层保护。
/// </para>
/// </remarks>
public static class ExecutionEndpoints
{
    public static void MapExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        // E7 内部 dispatch（系统级：user token 即可；E4 Resolver 上线后只接内部服务调用）
        var internalGroup = app.MapGroup("/internal/agent-executions")
            .WithTags("Execution.Internal")
            .RequireAuthorization();

        internalGroup.MapPost("", CreateExecutionAsync);

        // Agent 端：agent_token 鉴权
        var agentGroup = app.MapGroup("/agents/{agentId:guid}/executions")
            .WithTags("Execution.Agent")
            .RequireAuthorization();

        agentGroup.MapGet("/inbox", ListInboxAsync);
        agentGroup.MapPost("/{executionId:guid}/dispatch_ack", ReportDispatchAckAsync);
        agentGroup.MapPost("/{executionId:guid}/events", ReportEventAsync);
        agentGroup.MapPost("/{executionId:guid}/result", ReportResultAsync);
        agentGroup.MapGet("/{executionId:guid}", GetExecutionAsync);
    }

    // ──────────────────────────── Dispatch 创建 ────────────────────────────

    private static async Task<IResult> CreateExecutionAsync(
        CreateAgentExecutionRequest request,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "idempotency_key 不能为空");
        }

        // F2 幂等：同 idempotency_key 重复发返同 execution
        AgentDispatchInbox? existing = await db.AgentDispatchInbox
            .FirstOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);

        if (existing is not null)
        {
            DbAgentExecution? exec = await db.AgentExecutions
                .FirstOrDefaultAsync(e => e.Id == existing.ExecutionId, ct);

            if (exec is not null)
            {
                return Results.Ok(new { execution = ToSummary(exec), idempotent = true });
            }
        }

        // 校验 agent lifecycle=ACTIVE
        DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == request.AgentId, ct);

        if (agent is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不存在");
        }

        if (!AgentLifecycleMap.TryParse(agent.Lifecycle, out AgentLifecycle lifecycle) ||
            lifecycle is not AgentLifecycle.Active)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"agent lifecycle={agent.Lifecycle}，不能接受新 dispatch");
        }

        // v0.4.3：UNIQUE(collaboration_request_id) —— 一个 CR 只一个 Execution
        if (request.CollaborationRequestId is { } collabId)
        {
            DbAgentExecution? existingByCollab = await db.AgentExecutions
                .FirstOrDefaultAsync(e => e.CollaborationRequestId == collabId, ct);

            if (existingByCollab is not null)
            {
                return Results.Ok(new { execution = ToSummary(existingByCollab), idempotent = true });
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid executionId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();

        var execution = new DbAgentExecution
        {
            Id = executionId,
            CollaborationRequestId = request.CollaborationRequestId,
            AgentId = request.AgentId,
            WorkItemRef = request.WorkItemRef,
            Status = ExecutionStatusMap.PendingDbValue,
            LastPersistedSeq = 0,
            ActiveAttemptNo = 1,
            AttemptCount = 1,
            Input = request.Input.GetRawText(),
            ContextRefs = request.ContextRefs?.GetRawText(),
            CreatedAt = now,
            DeadlineAt = request.DeadlineS.HasValue ? now.AddSeconds(request.DeadlineS.Value) : null,
        };

        var attempt = new DbExecutionAttempt
        {
            Id = attemptId,
            ExecutionId = executionId,
            AttemptNo = 1,
            Status = AttemptStatusMap.DispatchedDbValue,
            DispatchSentAt = now,
            LastPersistedSeq = 0,
            CreatedAt = now,
        };

        execution.DispatchSentAt = now;
        execution.StartedAt = null; // RUNNING 由 Agent 回 dispatch_ack 后设置

        var inbox = new DbAgentDispatchInbox
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = request.IdempotencyKey,
            AgentId = request.AgentId,
            ExecutionId = executionId,
            AttemptNo = 1,
            DispatchedAt = now,
        };

        db.AgentExecutions.Add(execution);
        db.ExecutionAttempts.Add(attempt);
        db.AgentDispatchInbox.Add(inbox);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.ExecutionCreated,
            TargetType: "agent_execution", TargetId: executionId,
            Detail: new
            {
                agent_id = request.AgentId,
                collaboration_request_id = request.CollaborationRequestId,
                idempotency_key = request.IdempotencyKey,
            }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/internal/agent-executions/{executionId}",
            new { execution = ToSummary(execution), idempotent = false });
    }

    // ──────────────────────────── Inbox 轮询 ────────────────────────────

    private static async Task<IResult> ListInboxAsync(
        Guid agentId,
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid tokenAgentId = http.RequireAgentId();

        if (tokenAgentId != agentId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "agent_token 与路径 agentId 不一致");
        }

        // 列出 PENDING + DISPATCHED 的 inbox（agent 应该 pick up 并回 ack）
        var inbox = await db.AgentDispatchInbox
            .Where(x => x.AgentId == agentId && x.AckedAt == null)
            .OrderBy(x => x.DispatchedAt)
            .ToListAsync(ct);

        if (inbox.Count == 0)
        {
            return Results.Ok(Array.Empty<DispatchEnvelope>());
        }

        // 拉对应的 execution + input
        Dictionary<Guid, DbAgentExecution> execs = await db.AgentExecutions
            .Where(e => inbox.Select(x => x.ExecutionId).Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        List<DispatchEnvelope> envelopes = inbox
            .Where(x => execs.ContainsKey(x.ExecutionId))
            .Select(x =>
            {
                DbAgentExecution e = execs[x.ExecutionId];
                JsonElement input = JsonDocument.Parse(string.IsNullOrEmpty(e.Input) ? "{}" : e.Input).RootElement;
                JsonElement? context = string.IsNullOrEmpty(e.ContextRefs)
                    ? null
                    : JsonDocument.Parse(e.ContextRefs).RootElement;
                return new DispatchEnvelope(
                    ExecutionId: e.Id,
                    AttemptNo: x.AttemptNo,
                    Input: input,
                    ContextRefs: context,
                    DeadlineAtMs: e.DeadlineAt?.ToUnixTimeMilliseconds(),
                    IdempotencyKey: x.IdempotencyKey);
            })
            .ToList();

        return Results.Ok(envelopes);
    }

    // ──────────────────────────── Dispatch ACK ────────────────────────────

    private static async Task<IResult> ReportDispatchAckAsync(
        Guid agentId,
        Guid executionId,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid tokenAgentId = http.RequireAgentId();

        if (tokenAgentId != agentId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "agent_token 与路径 agentId 不一致");
        }

        DbAgentExecution? execution = await db.AgentExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId && e.AgentId == agentId, ct);

        if (execution is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "execution 不存在");
        }

        if (execution.DispatchAckedAt is not null)
        {
            // v0.5 §5 idempotency：重复 ack 静默忽略
            return Results.Ok(ToSummary(execution));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // v0.4.3：回填 dispatch_acked_at（幂等：仅首次回填）
        int rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE agent_executions
               SET dispatch_acked_at = {now}, status = 'RUNNING', started_at = {now}
               WHERE id = {executionId} AND dispatch_acked_at IS NULL
                 AND status IN ('PENDING','RUNNING')", ct);

        if (rows == 0)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, "execution 状态不允许 ack");
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE execution_attempts
               SET status = 'RUNNING', started_at = {now}, dispatch_acked_at = {now}
               WHERE execution_id = {executionId} AND attempt_no = {execution.ActiveAttemptNo ?? 1}
                 AND status = 'DISPATCHED'", ct);

        // inbox ack
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE agent_dispatch_inbox
               SET acked_at = {now}
               WHERE execution_id = {executionId} AND idempotency_key IN
                 (SELECT idempotency_key FROM agent_dispatch_inbox WHERE execution_id = {executionId})", ct);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.Agent, agentId, AuditActions.ExecutionDispatchAcked,
            TargetType: "agent_execution", TargetId: executionId));

        // 重新查询最新状态
        await db.Entry(execution).ReloadAsync(ct);
        return Results.Ok(ToSummary(execution));
    }

    // ──────────────────────────── Event 上报 ────────────────────────────

    private static async Task<IResult> ReportEventAsync(
        Guid agentId,
        Guid executionId,
        ReportExecutionEventRequest request,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid tokenAgentId = http.RequireAgentId();

        if (tokenAgentId != agentId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "agent_token 与路径 agentId 不一致");
        }

        if (string.IsNullOrWhiteSpace(request.EventType))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "event_type 不能为空");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderEventId))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "provider_event_id 不能为空");
        }

        if (request.Seq <= 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "seq 必须 > 0");
        }

        DbAgentExecution? execution = await db.AgentExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId && e.AgentId == agentId, ct);

        if (execution is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "execution 不存在");
        }

        if (!ExecutionStatusMap.TryParse(execution.Status, out ExecutionStatus status) || status.IsTerminal())
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"execution 已处于 {execution.Status} 终态，不接受新 event");
        }

        int? attemptNo = execution.ActiveAttemptNo ?? 1;
        DbExecutionAttempt? attempt = await db.ExecutionAttempts
            .FirstOrDefaultAsync(a => a.ExecutionId == executionId && a.AttemptNo == attemptNo, ct);

        if (attempt is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "attempt 不存在");
        }

        // v0.4.3 contiguous cursor 判定
        AttemptEventCursor.AcceptDecision decision =
            AttemptEventCursor.Decide(request.Seq, attempt.LastPersistedSeq);

        switch (decision)
        {
            case AttemptEventCursor.AcceptDecision.Duplicate:
                // 重复事件：v0.5 §5 静默忽略
                return Results.Ok(new { accepted = false, reason = "duplicate", last_seq = attempt.LastPersistedSeq });

            case AttemptEventCursor.AcceptDecision.Gap:
                // gap：拒收，要求 resume（detailed/03 §3.2 行为）
                return ApiErrors.ConflictResult(ApiErrors.Conflict,
                    $"event seq={request.Seq} 出现 gap（expected={AttemptEventCursor.ExpectedSeq(attempt.LastPersistedSeq)}），需 Agent 先 resume");

            case AttemptEventCursor.AcceptDecision.Accept:
                break;
        }

        // 写入：transactional，与 cursor advance 原子
        Guid eventId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string payloadJson = request.Payload.GetRawText();

        try
        {
            db.ExecutionEvents.Add(new DbExecutionEvent
            {
                Id = eventId,
                AttemptId = attempt.Id,
                EventType = request.EventType,
                ProviderEventId = request.ProviderEventId,
                Seq = request.Seq,
                Payload = payloadJson,
                CreatedAt = now,
            });

            // execution 级 cursor 也 advance（detailed/01 §1 T+18）
            execution.LastPersistedSeq = AttemptEventCursor.Advance(execution.LastPersistedSeq, request.Seq);
            attempt.LastPersistedSeq = AttemptEventCursor.Advance(attempt.LastPersistedSeq, request.Seq);

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            // v0.4.2 dedup：UNIQUE(attempt_id, provider_event_id) 命中
            return Results.Ok(new { accepted = false, reason = "duplicate_provider_event_id" });
        }

        return Results.Ok(new { accepted = true, event_id = eventId, seq = request.Seq });
    }

    // ──────────────────────────── Result 上报 ────────────────────────────

    private static async Task<IResult> ReportResultAsync(
        Guid agentId,
        Guid executionId,
        ReportExecutionResultRequest request,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid tokenAgentId = http.RequireAgentId();

        if (tokenAgentId != agentId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "agent_token 与路径 agentId 不一致");
        }

        if (!ExecutionStatusMap.TryParse(request.Status, out ExecutionStatus targetStatus))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "status 必须是 SUCCEEDED / FAILED / CANCELLED");
        }

        if (!targetStatus.IsTerminal())
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "result 必须是终态");
        }

        DbAgentExecution? execution = await db.AgentExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId && e.AgentId == agentId, ct);

        if (execution is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "execution 不存在");
        }

        if (!ExecutionStatusMap.TryParse(execution.Status, out ExecutionStatus currentStatus))
        {
            currentStatus = ExecutionStatus.PENDING;
        }

        // v0.5 idempotency：已终态 + 同 envelope.id → 静默忽略
        if (currentStatus.IsTerminal() && execution.TerminalEnvelopeId == request.EnvelopeId)
        {
            return Results.Ok(ToSummary(execution));
        }

        // v0.5 idempotency：已终态 + 不同 envelope.id → 409
        if (currentStatus.IsTerminal())
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"execution 已处于 {execution.Status} 终态；新 envelope.id={request.EnvelopeId} 视为迟到投递");
        }

        // CAS 终态守卫
        if (!ExecutionTerminalGuard.CanTransitionToTerminal(currentStatus))
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"execution 当前状态 {currentStatus.ToDbValue()} 不允许迁移到 {targetStatus.ToDbValue()}");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int attemptNo = execution.ActiveAttemptNo ?? 1;

        // CAS UPDATE：只 PENDING/RUNNING 才迁移到终态
        int rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE agent_executions
               SET status = {targetStatus.ToDbValue()},
                   completed_at = {now},
                   terminal_envelope_id = {request.EnvelopeId},
                   result_output = {request.Output?.GetRawText()},
                   result_usage = {request.Usage?.GetRawText()},
                   active_attempt_no = NULL
               WHERE id = {executionId} AND status IN ('PENDING','RUNNING')", ct);

        if (rows == 0)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, "execution 已被其他终态写入");
        }

        // attempt 同步收尾
        string attemptTerminalStatus = targetStatus switch
        {
            ExecutionStatus.SUCCEEDED => AttemptStatusMap.CompletedDbValue,
            ExecutionStatus.FAILED => AttemptStatusMap.FailedDbValue,
            _ => AttemptStatusMap.CancelledDbValue,
        };

        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE execution_attempts
               SET status = {attemptTerminalStatus}, completed_at = {now}
               WHERE execution_id = {executionId} AND attempt_no = {attemptNo}
                 AND status IN ('PENDING','DISPATCHED','RUNNING')", ct);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.Agent, agentId, AuditActions.ExecutionResult,
            TargetType: "agent_execution", TargetId: executionId,
            Detail: new { status = targetStatus.ToDbValue(), envelope_id = request.EnvelopeId }));

        await db.Entry(execution).ReloadAsync(ct);
        return Results.Ok(ToSummary(execution));
    }

    // ──────────────────────────── 详情查询 ────────────────────────────

    private static async Task<IResult> GetExecutionAsync(
        Guid agentId,
        Guid executionId,
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid tokenAgentId = http.RequireAgentId();

        if (tokenAgentId != agentId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "agent_token 与路径 agentId 不一致");
        }

        DbAgentExecution? execution = await db.AgentExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId && e.AgentId == agentId, ct);

        if (execution is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "execution 不存在");
        }

        return Results.Ok(ToSummary(execution));
    }

    // ── helpers ──

    private static ExecutionSummary ToSummary(DbAgentExecution e) => new(
        e.Id, e.CollaborationRequestId, e.AgentId, e.WorkItemRef,
        e.Status,
        e.DispatchSentAt?.ToUnixTimeMilliseconds(),
        e.DispatchAckedAt?.ToUnixTimeMilliseconds(),
        e.LastPersistedSeq,
        e.ActiveAttemptNo,
        e.AttemptCount,
        e.CreatedAt.ToUnixTimeMilliseconds(),
        e.StartedAt?.ToUnixTimeMilliseconds(),
        e.CompletedAt?.ToUnixTimeMilliseconds(),
        e.DeadlineAt?.ToUnixTimeMilliseconds());
}
