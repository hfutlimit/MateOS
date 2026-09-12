using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Contracts.Protocol;
using MateOS.Api.Observability;
using MateOS.Api.Outbox;
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

/// <summary>
/// Agent 断线重连后的续传询问（detailed/03 §3.1，v0.4.3 反向协议：Agent 问、Runtime 答）。
/// </summary>
/// <remarks>
/// 两个字段都可空：Agent 刚重连时可能不知道自己手里是哪个 attempt，
/// 由 Runtime 回权威的 <c>attempt_no</c>；<c>expected_seq</c> 只在发现 gap 时带上。
/// </remarks>
public sealed record ResumeRequestPayload(int? AttemptNo, long? ExpectedSeq);

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

// dispatch 的出参直接用契约层的 ExecutionDispatchPayload（MateOS.Contracts.Protocol），
// 不在此另造 DTO：轮询与 WS 推送是同一份事实的两条投递通道，形状由
// contracts/schemas/execution/dispatch.json 约束，SDK 只应有一份解析代码。

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
        agentGroup.MapPost("/{executionId:guid}/resume_request", ReportResumeRequestAsync);
        agentGroup.MapGet("/{executionId:guid}", GetExecutionAsync);
    }

    // ──────────────────────────── Dispatch 创建 ────────────────────────────

    private static async Task<IResult> CreateExecutionAsync(
        CreateAgentExecutionRequest request,
        MateOSDbContext db,
        AgentDispatchNotifier notifier,
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

        // 提交后再推：事务未落地就推，等于给 Agent 发一条可能不存在的指令。
        // 推不出去不是错误——Agent 仍可轮询 inbox（M3b Phase 2 兜底路径）。
        await notifier.PushDispatchAsync(executionId, source: "e7-create", ct);

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
            return Results.Ok(Array.Empty<ExecutionDispatchPayload>());
        }

        // 拉对应的 execution + input
        Dictionary<Guid, DbAgentExecution> execs = await db.AgentExecutions
            .Where(e => inbox.Select(x => x.ExecutionId).Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        // work_item_ref 要带 provider_key / external_ref，批量取回避免 N+1
        List<Guid> workItemIds = execs.Values
            .Where(e => e.WorkItemRef is not null)
            .Select(e => e.WorkItemRef!.Value)
            .Distinct()
            .ToList();

        Dictionary<Guid, WorkItemRef> workItemRefs = (await db.WorkItems
                .AsNoTracking()
                .Where(w => workItemIds.Contains(w.Id))
                .Select(w => new { w.Id, w.ProviderKey, w.ExternalRef })
                .ToListAsync(ct))
            .ToDictionary(
                w => w.Id,
                w => new WorkItemRef(w.ProviderKey, w.Id.ToString(), w.ExternalRef));

        List<ExecutionDispatchPayload> dispatches = inbox
            .Where(x => execs.ContainsKey(x.ExecutionId))
            .Select(x =>
            {
                DbAgentExecution e = execs[x.ExecutionId];

                return new ExecutionDispatchPayload(
                    ExecutionId: e.Id,
                    AttemptNo: x.AttemptNo,
                    CollaborationRequestId: e.CollaborationRequestId,
                    WorkItemRef: e.WorkItemRef is { } workItemId
                                  && workItemRefs.TryGetValue(workItemId, out WorkItemRef? itemRef)
                        ? itemRef
                        : null,
                    Input: ExecutionPayloadMapper.ToInput(e.Input),
                    Context: ExecutionPayloadMapper.ToContext(e.ContextRefs),
                    DeadlineS: e.DeadlineAt is { } deadline
                        ? (int)Math.Clamp((long)(deadline - DateTimeOffset.UtcNow).TotalSeconds, 0, int.MaxValue)
                        : null,
                    IdempotencyKey: x.IdempotencyKey);
            })
            .ToList();

        return Results.Ok(dispatches);
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
        OutboxWriter outboxWriter,
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

        // 列是 jsonb，而参数按 text 发送：必须显式 cast，否则 PG 报
        // 42804「column "result_output" is of type jsonb but expression is of type text」。
        string? outputJson = request.Output?.GetRawText();
        string? usageJson = request.Usage?.GetRawText();

        // CAS UPDATE：只 PENDING/RUNNING 才迁移到终态
        int rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE agent_executions
               SET status = {targetStatus.ToDbValue()},
                   completed_at = {now},
                   terminal_envelope_id = {request.EnvelopeId},
                   result_output = {outputJson}::jsonb,
                   result_usage = {usageJson}::jsonb,
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

        // M7 outbox：写 execution.completed 事件（relay 后续推 E3 AGENT_OUTPUT 投影 + WS）。
        // S3 补充：FAILED 也要发——否则 WorkItem 上的 Agent 永远「看起来还在跑」，
        // 失败只能靠人自己去翻 execution 列表。消费者按 payload.status 分支
        // （SUCCEEDED 才写 AGENT_OUTPUT 投影），因此对 E3 行为零影响。
        // CANCELLED 不发：那是人的主动取消，不需要再通知一遍。
        if (targetStatus is ExecutionStatus.SUCCEEDED or ExecutionStatus.FAILED)
        {
            outboxWriter.Append(
                db,
                aggregateType: "execution",
                aggregateId: executionId,
                eventType: Domain.Outbox.OutboxEventType.ExecutionCompleted,
                payload: new
                {
                    execution_id = executionId,
                    agent_id = agentId,
                    status = targetStatus.ToDbValue(),
                    output_markdown = ExtractMarkdownFromOutput(request.Output),
                },
                idempotencyKey: $"execution.completed:{executionId}:{request.EnvelopeId}");
        }

        await db.Entry(execution).ReloadAsync(ct);
        return Results.Ok(ToSummary(execution));
    }

    private static string? ExtractMarkdownFromOutput(JsonElement? output)
    {
        if (output is null) return null;
        if (output.Value.ValueKind != JsonValueKind.Object) return null;
        if (!output.Value.TryGetProperty("markdown", out JsonElement md)) return null;
        return md.ValueKind == JsonValueKind.String ? md.GetString() : null;
    }

    // ──────────────────────────── 续传询问 ────────────────────────────

    /// <summary>
    /// Agent 重连后问「我该从哪个 seq 接着发」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 返回的 <c>last_persisted_seq</c> 是 <b>连续位点</b>，不是 <c>MAX(seq)</c>：
    /// 用 MAX 的话，中断期间丢掉的那些 seq 会变成永久缺口，
    /// 而 cursor 校验要求连续，于是这条 attempt 再也发不出任何事件。
    /// </para>
    /// <para>
    /// 终态 execution 与过期 attempt 都要显式拒绝，而不是返回一个位点：
    /// 前者是「活儿已经结束了」，后者是「你手里的是上一轮」（与 B8 迟到结果同根因）——
    /// 两者若返回位点，Agent 会照着继续产出，把已经收尾的事实再写一遍。
    /// </para>
    /// </remarks>
    private static async Task<IResult> ReportResumeRequestAsync(
        Guid agentId,
        Guid executionId,
        ResumeRequestPayload request,
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

        if (ExecutionStatusMap.TryParse(execution.Status, out ExecutionStatus status) && status.IsTerminal())
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"execution 已处于 {execution.Status} 终态，无需续传");
        }

        int activeAttemptNo = execution.ActiveAttemptNo ?? 1;

        if (request.AttemptNo is { } askedAttemptNo && askedAttemptNo != activeAttemptNo)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"attempt_no={askedAttemptNo} 已非当前 attempt（当前 {activeAttemptNo}），其续传位点不再有效");
        }

        DbExecutionAttempt? attempt = await db.ExecutionAttempts
            .FirstOrDefaultAsync(a => a.ExecutionId == executionId && a.AttemptNo == activeAttemptNo, ct);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.Agent, agentId, AuditActions.ExecutionResumeRequested,
            TargetType: "agent_execution", TargetId: executionId,
            Detail: new
            {
                attempt_no = activeAttemptNo,
                last_persisted_seq = attempt?.LastPersistedSeq ?? 0,
                expected_seq = request.ExpectedSeq,
            }));

        ExecutionDispatchSnapshot? snapshot = attempt is null
            ? null
            : new ExecutionDispatchSnapshot(
                ExecutionId: executionId,
                Input: ExecutionPayloadMapper.ToInput(execution.Input),
                Context: ExecutionPayloadMapper.ToContext(execution.ContextRefs),
                DeadlineS: execution.DeadlineAt is { } deadline
                    ? (int)Math.Clamp((long)(deadline - DateTimeOffset.UtcNow).TotalSeconds, 0, int.MaxValue)
                    : null);

        return Results.Ok(new ExecutionResumeAckPayload(
            ExecutionId: executionId,
            AttemptNo: activeAttemptNo,
            LastPersistedSeq: attempt?.LastPersistedSeq ?? 0,
            Snapshot: snapshot));
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
