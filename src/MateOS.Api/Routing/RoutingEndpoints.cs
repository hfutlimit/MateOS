using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Persistence;
using MateOS.Domain.Agent;
using MateOS.Domain.Routing;
using Microsoft.EntityFrameworkCore;
using DbTrigger = MateOS.Api.Persistence.Trigger;
using DbCollaborationRequest = MateOS.Api.Persistence.CollaborationRequest;
using DbDecisionRecord = MateOS.Api.Persistence.DecisionRecord;
using DbAgent = MateOS.Api.Persistence.Agent;

namespace MateOS.Api.Routing;

// ── DTO ──

public sealed record CreateTriggerRequest(
    string TriggerType,
    JsonElement TriggerRef,
    string FromActorType,
    Guid FromActorId,
    string? RequestKind,
    Guid? TargetAgentId,
    IReadOnlyList<string>? RequiredCapabilities,
    JsonElement? ContextRefs,
    int? DeadlineS,
    string IdempotencyKey);

public sealed record CreateDecisionRequest(
    string Decision,
    string? Reason,
    JsonElement? Needs,
    bool? AnalysisCapability,
    int? AnalysisContextScore,
    bool? AnalysisPermission);

public sealed record TriggerSummary(
    Guid Id,
    string TriggerType,
    string FromActorType,
    Guid FromActorId,
    long CapturedAtMs);

public sealed record CrSummary(
    Guid Id,
    Guid TriggerId,
    string TriggerType,
    string RequestKind,
    string FromActorType,
    Guid FromActorId,
    Guid? TargetAgentId,
    string Status,
    Guid? TargetExecutionId,
    int DeadlineS,
    long CreatedAtMs,
    long? ResolvedAtMs);

public sealed record DecisionSummary(
    Guid Id,
    Guid CollaborationRequestId,
    string Decision,
    string? Reason,
    bool? AnalysisCapability,
    int? AnalysisContextScore,
    bool? AnalysisPermission,
    Guid? AcceptedExecutionId,
    string ActorType,
    Guid ActorId,
    long DecidedAtMs);

/// <summary>
/// E4 §3 + §5 内部门面：写 trigger + CR + decision（v0.4.3 关键：ACCEPT 时
/// E4 同步调 E7 创建 Execution）。
/// </summary>
/// <remarks>
/// <para>
/// V1 简化：
/// <list type="bullet">
///   <item>无 Resolver Worker（V1 由 E3 mention 提取后端到端直调本端面）</item>
///   <item>无 Redis Lua slot reservation（V1：slot_lease_id 字段留 NULL，V2 接入）</item>
///   <item>无 60s lease sweep（V1：依赖决策路径主动收尾）</item>
///   <item>ACCEPT 决策 E4 同步调 E7 internal API（v0.4.3 D9 口径 + outbox 兜底）</item>
/// </list>
/// </para>
/// </remarks>
public static class RoutingEndpoints
{
    public static void MapRoutingEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/internal")
            .WithTags("Routing.Internal")
            .RequireAuthorization();

        group.MapPost("/triggers", CreateTriggerAsync);
        group.MapPost("/collaboration-requests/{crId:guid}/decision", SubmitDecisionAsync);
    }

    // ──────────────────────────── Create Trigger + CR ────────────────────────────

    private static async Task<IResult> CreateTriggerAsync(
        CreateTriggerRequest request,
        MateOSDbContext db,
        HttpClient httpClient,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (!TriggerTypeMap.TryParse(request.TriggerType, out TriggerType triggerType))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "trigger_type 必须是 MENTION / WORK_ITEM / API / AUTOMATION");
        }

        if (!TriggerActorTypeMap.TryParse(request.FromActorType, out TriggerActorType actorType))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "from_actor_type 必须是 USER / AGENT / SYSTEM");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "idempotency_key 不能为空");
        }

        // V1 幂等：同 idempotency_key 重复发返同 CR
        DbCollaborationRequest? existingCr = await db.CollaborationRequests
            .FirstOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);

        if (existingCr is not null)
        {
            DbTrigger? existingTrigger = await db.Triggers
                .FirstOrDefaultAsync(t => t.Id == existingCr.TriggerId, ct);

            return Results.Ok(new
            {
                trigger = existingTrigger is null ? null : ToTriggerSummary(existingTrigger),
                collaboration_request = ToCrSummary(existingCr),
                idempotent = true,
            });
        }

        // 解析 request_kind：缺省按 trigger_type 推
        CrRequestKind kind;
        if (!string.IsNullOrEmpty(request.RequestKind))
        {
            if (!CrRequestKindMap.TryParse(request.RequestKind, out kind))
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "request_kind 非法");
            }
        }
        else
        {
            kind = triggerType switch
            {
                TriggerType.MENTION => CrRequestKind.MESSAGE_RESPONSE,
                TriggerType.WORK_ITEM => CrRequestKind.WORK_ITEM_EXECUTION,
                TriggerType.API => CrRequestKind.API_CALL,
                TriggerType.AUTOMATION => CrRequestKind.AUTOMATION_RUN,
                _ => CrRequestKind.API_CALL,
            };
        }

        // 校验 target_agent_id 若指定则存在
        if (request.TargetAgentId is { } aid)
        {
            DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == aid, ct);
            if (agent is null)
            {
                return ApiErrors.NotFoundResult(ApiErrors.NotFound, "target_agent 不存在");
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid triggerId = Guid.NewGuid();
        Guid crId = Guid.NewGuid();
        int deadlineS = request.DeadlineS ?? 600;

        var trigger = new DbTrigger
        {
            Id = triggerId,
            TriggerType = triggerType.ToDbValue(),
            TriggerRef = request.TriggerRef.GetRawText(),
            FromActorType = actorType.ToDbValue(),
            FromActorId = request.FromActorId,
            CapturedAt = now,
        };

        var cr = new DbCollaborationRequest
        {
            Id = crId,
            TriggerId = triggerId,
            TriggerType = triggerType.ToDbValue(),
            TriggerRef = request.TriggerRef.GetRawText(),
            RequestKind = kind.ToDbValue(),
            FromActorType = actorType.ToDbValue(),
            FromActorId = request.FromActorId,
            TargetAgentId = request.TargetAgentId,
            RequiredCapabilities = JsonSerializer.Serialize(request.RequiredCapabilities ?? Array.Empty<string>()),
            ContextRefs = request.ContextRefs?.GetRawText() ?? "{}",
            Status = CrStatusMap.PendingValue,
            DeadlineS = deadlineS,
            IdempotencyKey = request.IdempotencyKey,
            CreatedAt = now,
        };

        db.Triggers.Add(trigger);
        db.CollaborationRequests.Add(cr);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.TriggerCreated,
            TargetType: "trigger", TargetId: triggerId,
            Detail: new { trigger_type = triggerType.ToDbValue() }));
        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.CollaborationRequestCreated,
            TargetType: "collaboration_request", TargetId: crId,
            Detail: new
            {
                trigger_id = triggerId,
                target_agent_id = request.TargetAgentId,
                request_kind = kind.ToDbValue(),
                deadline_s = deadlineS,
            }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/internal/collaboration-requests/{crId}",
            new
            {
                trigger = ToTriggerSummary(trigger),
                collaboration_request = ToCrSummary(cr),
                idempotent = false,
            });
    }

    // ──────────────────────────── Submit Decision ────────────────────────────

    private static async Task<IResult> SubmitDecisionAsync(
        Guid crId,
        CreateDecisionRequest request,
        MateOSDbContext db,
        IHttpClientFactory httpClientFactory,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (!CrDecisionMap.TryParse(request.Decision, out CrDecision decision))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "decision 必须是 ACCEPT / REJECT / NEED_CONTEXT / CANCEL");
        }

        // analysis_context_score 范围校验
        if (request.AnalysisContextScore is { } s && (s < 0 || s > 100))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "analysis_context_score 必须在 0-100");
        }

        DbCollaborationRequest? cr = await db.CollaborationRequests
            .FirstOrDefaultAsync(x => x.Id == crId, ct);

        if (cr is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "collaboration_request 不存在");
        }

        if (!CrStatusMap.TryParse(cr.Status, out CrStatus current))
        {
            current = CrStatus.PENDING;
        }

        // v0.4.1 状态机：PENDING → 终态；其他拒
        string? transitionError = CrStateMachine.WhyCannotTransition(current, decision switch
        {
            CrDecision.ACCEPT => CrStatus.ACCEPTED,
            CrDecision.REJECT => CrStatus.REJECTED,
            CrDecision.NEED_CONTEXT => CrStatus.NEED_CONTEXT,
            CrDecision.CANCEL => CrStatus.CANCELLED,
            _ => CrStatus.CANCELLED,
        });
        if (transitionError is not null)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, transitionError);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid decisionId = Guid.NewGuid();
        Guid? acceptedExecutionId = null;

        // v0.4.3 关键：ACCEPT 时 E4 同步调 E7 创建 Execution
        if (decision is CrDecision.ACCEPT)
        {
            acceptedExecutionId = await DispatchExecutionAsync(cr, db, httpClientFactory, http, ct);
        }

        // CAS 终态守卫（避免 PENDING/RUNNING 状态被乱改）
        string newStatus = decision switch
        {
            CrDecision.ACCEPT => CrStatusMap.AcceptedValue,
            CrDecision.REJECT => CrStatusMap.RejectedValue,
            CrDecision.NEED_CONTEXT => CrStatusMap.NeedContextValue,
            CrDecision.CANCEL => CrStatusMap.CancelledValue,
            _ => CrStatusMap.CancelledValue,
        };

        int rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE collaboration_requests
               SET status = {newStatus},
                   resolved_at = {now},
                   target_execution_id = {acceptedExecutionId}
               WHERE id = {crId} AND status = {CrStatusMap.PendingValue}", ct);

        if (rows == 0)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, "CR 状态已被其他决策覆盖");
        }

        // 写 decision_record
        var record = new DbDecisionRecord
        {
            Id = decisionId,
            CollaborationRequestId = crId,
            Decision = decision.ToDbValue(),
            Reason = request.Reason,
            Needs = request.Needs?.GetRawText(),
            AnalysisCapability = request.AnalysisCapability,
            AnalysisContextScore = request.AnalysisContextScore,
            AnalysisPermission = request.AnalysisPermission,
            AcceptedExecutionId = acceptedExecutionId,
            ActorType = AuditActorTypes.Agent,
            ActorId = cr.TargetAgentId ?? userId,  // V1 简化：决策 actor 优先用 target_agent_id
            DecidedAt = now,
        };
        db.DecisionRecords.Add(record);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.Agent, record.ActorId, AuditActions.DecisionRecorded,
            TargetType: "collaboration_request", TargetId: crId,
            Detail: new
            {
                decision = decision.ToDbValue(),
                execution_id = acceptedExecutionId,
            }));

        await db.SaveChangesAsync(ct);

        await db.Entry(cr).ReloadAsync(ct);
        return Results.Ok(new
        {
            collaboration_request = ToCrSummary(cr),
            decision = new
            {
                record.Id,
                record.CollaborationRequestId,
                record.Decision,
                record.Reason,
                record.AcceptedExecutionId,
                record.ActorType,
                record.ActorId,
                record.DecidedAt,
            },
        });
    }

    /// <summary>
    /// v0.4.3 D9 口径：E4 同步直调 E7 创建 Execution（详细设计 01 §1 T+13）。
    /// </summary>
    private static async Task<Guid?> DispatchExecutionAsync(
        DbCollaborationRequest cr,
        MateOSDbContext db,
        IHttpClientFactory httpClientFactory,
        HttpContext http,
        CancellationToken ct)
    {
        if (cr.TargetAgentId is null)
        {
            return null; // 没指定 agent（理论上 Resolver 选了 agent 才会接受）
        }

        // V1 简化：直接走 E7 internal API（同一进程内调用）
        // 用 HttpClient 走本机 loopback；详细设计 01 §1 T+13 outbox 兜底
        // 注：M4a+ 可改为 process-internal 直调避免 HTTP 开销
        try
        {
            using IServiceScope scope = http.RequestServices.CreateScope();

            // V1 直接 DB insert：避免 HTTP 自调用 + 配置 E7 端点的 idempotency_key
            // 这是 M4b 简化：M4a+ 时让 E4 → E7 走 HTTP（详细设计 01 D9）
            Guid executionId = Guid.NewGuid();
            Guid attemptId = Guid.NewGuid();
            string idempotencyKey = $"cr-{cr.Id}";

            var execution = new AgentExecution
            {
                Id = executionId,
                CollaborationRequestId = cr.Id,
                AgentId = cr.TargetAgentId.Value,
                WorkItemRef = null,
                Status = ExecutionStatusMap.PendingDbValue,
                LastPersistedSeq = 0,
                ActiveAttemptNo = 1,
                AttemptCount = 1,
                Input = cr.TriggerRef,
                ContextRefs = cr.ContextRefs,
                CreatedAt = DateTimeOffset.UtcNow,
                DispatchSentAt = DateTimeOffset.UtcNow,
            };
            var attempt = new ExecutionAttempt
            {
                Id = attemptId,
                ExecutionId = executionId,
                AttemptNo = 1,
                Status = AttemptStatusMap.DispatchedDbValue,
                DispatchSentAt = DateTimeOffset.UtcNow,
                LastPersistedSeq = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var inbox = new AgentDispatchInbox
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = idempotencyKey,
                AgentId = cr.TargetAgentId.Value,
                ExecutionId = executionId,
                AttemptNo = 1,
                DispatchedAt = DateTimeOffset.UtcNow,
            };

            db.AgentExecutions.Add(execution);
            db.ExecutionAttempts.Add(attempt);
            db.AgentDispatchInbox.Add(inbox);

            await db.SaveChangesAsync(ct);
            return executionId;
        }
        catch
        {
            // v0.4.3 §1 T+14b outbox 兜底：失败时返 null，由 M7 outbox relay 重试
            return null;
        }
    }

    private static TriggerSummary ToTriggerSummary(DbTrigger t) => new(
        t.Id, t.TriggerType, t.FromActorType, t.FromActorId,
        t.CapturedAt.ToUnixTimeMilliseconds());

    private static CrSummary ToCrSummary(DbCollaborationRequest cr) => new(
        cr.Id, cr.TriggerId, cr.TriggerType, cr.RequestKind,
        cr.FromActorType, cr.FromActorId, cr.TargetAgentId,
        cr.Status, cr.TargetExecutionId, cr.DeadlineS,
        cr.CreatedAt.ToUnixTimeMilliseconds(),
        cr.ResolvedAt?.ToUnixTimeMilliseconds());
}
