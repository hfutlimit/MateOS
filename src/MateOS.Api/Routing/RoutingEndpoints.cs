using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Outbox;
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
        OutboxWriter outboxWriter,
        AgentDispatchNotifier notifier,
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

        // 决策语义与人 / Agent 两条入口共用一份实现（DecisionApplier）。
        // 这里是「人代 Agent 提交」的兼容入口：决策主体仍是 Agent，
        // 故 actor_type 记 AGENT、actor_id 取 target_agent_id（未指定 agent 时退回提交人，
        // 保证审计不丢主体，而不是留一个空 actor）。
        DecisionApplyResult applied = await DecisionApplier.ApplyAsync(
            cr,
            new DecisionCommand(
                Decision: decision,
                Reason: request.Reason,
                Needs: request.Needs,
                AnalysisCapability: request.AnalysisCapability,
                AnalysisContextScore: request.AnalysisContextScore,
                AnalysisPermission: request.AnalysisPermission,
                ActorType: AuditActorTypes.Agent,
                ActorId: cr.TargetAgentId ?? userId),
            db, outboxWriter, notifier, audit, http, ct);

        if (applied.Failed)
        {
            return applied.Error!;
        }

        return Results.Ok(new
        {
            collaboration_request = ToCrSummary(cr),
            decision = ToDecisionSummary(applied.Outcome!.Record),
        });
    }

    private static DecisionSummary ToDecisionSummary(DbDecisionRecord r) => new(
        r.Id, r.CollaborationRequestId, r.Decision, r.Reason,
        r.AnalysisCapability, r.AnalysisContextScore, r.AnalysisPermission,
        r.AcceptedExecutionId, r.ActorType, r.ActorId,
        r.DecidedAt.ToUnixTimeMilliseconds());

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
