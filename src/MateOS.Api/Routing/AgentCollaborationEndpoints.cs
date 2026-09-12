using System.Text.Json;
using System.Text.Json.Nodes;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Outbox;
using MateOS.Api.Persistence;
using MateOS.Contracts.Protocol;
using MateOS.Domain.Routing;
using Microsoft.EntityFrameworkCore;
using DbCollaborationRequest = MateOS.Api.Persistence.CollaborationRequest;

namespace MateOS.Api.Routing;

/// <summary>ACCEPT 时必须提供的三件套（形状对齐 <c>contracts/schemas/collaboration/decision.json</c>）。</summary>
public sealed record AgentDecisionAnalysis(bool Capability, double? ContextScore, bool Permission);

/// <summary>Agent 侧提交决策的 body。</summary>
/// <remarks>
/// <c>collaboration_request_id</c> 走路径参数而非 body：它是寻址信息，
/// 放在 body 里会出现「路径与 body 不一致」这种需要额外裁决的状态。
/// </remarks>
public sealed record AgentDecisionRequest(
    string Decision,
    string? Reason,
    AgentDecisionAnalysis? Analysis,
    IReadOnlyList<string>? Needs);

/// <summary>
/// E4 的 Agent 侧门面：收协作请求（轮询）+ 提交决策。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：M3b 只做了 <c>execution.dispatch</c> 这一个出站帧，
/// Agent 收不到 <c>collaboration.request</c>，也就无法对「要不要接这活」表态。
/// 没有这条链路，S1 的 <c>@mention → Agent 接手</c> 只能靠人代提交决策，
/// 等于 Agent 决策这一步没有被真实验证。
/// </para>
/// <para>
/// <b>投递方式</b>：V1 用轮询（与 <c>/agents/{id}/executions/inbox</c> 同形）。
/// WS 推送 <c>collaboration.request</c> 属下一阶段 —— 先有可靠基线，再加实时优化，
/// 这样推送出问题时不至于连「Agent 能不能收单」都不可知。
/// </para>
/// <para>
/// <b>与 <c>/internal</c> 入口的关系</b>：两者调用同一个
/// <see cref="DecisionApplier"/>。区别只在鉴权与审计主体 —— 这里的主体是 Agent 自己
/// （agent_token 的 <c>sub</c>），<c>/internal</c> 那侧是人代提交。
/// </para>
/// </remarks>
public static class AgentCollaborationEndpoints
{
    public static void MapAgentCollaborationEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/agents/{agentId:guid}/collaboration-requests")
            .WithTags("Collaboration.Agent")
            .RequireAuthorization();

        group.MapGet("/inbox", ListInboxAsync);
        group.MapPost("/{crId:guid}/decision", SubmitDecisionAsync);
    }

    // ──────────────────────────── 收协作请求 ────────────────────────────

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

        List<DbCollaborationRequest> pending = await db.CollaborationRequests
            .AsNoTracking()
            .Where(cr => cr.TargetAgentId == agentId && cr.Status == CrStatusMap.PendingValue)
            .OrderBy(cr => cr.CreatedAt)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return Results.Ok(Array.Empty<CollaborationRequestPayload>());
        }

        Dictionary<Guid, WorkItemRef> workItemRefs = await LoadWorkItemRefsAsync(db, pending, ct);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        List<CollaborationRequestPayload> payloads = pending
            .Select(cr => new CollaborationRequestPayload(
                CollaborationRequestId: cr.Id,
                FromActor: new ActorRef(cr.FromActorType, cr.FromActorId),
                ContextRefs: ToContextRefs(cr.ContextRefs, workItemRefs),
                RequiredCapabilities: ParseCapabilities(cr.RequiredCapabilities),
                DeadlineS: RemainingSeconds(cr.CreatedAt, cr.DeadlineS, now)))
            .ToList();

        return Results.Ok(payloads);
    }

    // ──────────────────────────── 提交决策 ────────────────────────────

    private static async Task<IResult> SubmitDecisionAsync(
        Guid agentId,
        Guid crId,
        AgentDecisionRequest request,
        MateOSDbContext db,
        OutboxWriter outboxWriter,
        AgentDispatchNotifier notifier,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid tokenAgentId = http.RequireAgentId();

        if (tokenAgentId != agentId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "agent_token 与路径 agentId 不一致");
        }

        // DELEGATE 虽是 schema 里的保留枚举位，但 CrDecisionMap 不认它，
        // 这里会走「必须是 ACCEPT / REJECT / NEED_CONTEXT / CANCEL」——
        // 正是想要的：保留位不被静默当成 CANCEL 处理。
        if (!CrDecisionMap.TryParse(request.Decision, out CrDecision decision))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "decision 必须是 ACCEPT / REJECT / NEED_CONTEXT / CANCEL");
        }

        if (request.Analysis?.ContextScore is { } score && (score < 0 || score > 100))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "analysis.context_score 必须在 0-100");
        }

        DbCollaborationRequest? cr = await db.CollaborationRequests
            .FirstOrDefaultAsync(x => x.Id == crId, ct);

        if (cr is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "collaboration_request 不存在");
        }

        // 越权闸门：CR 是「派给某个 agent 的活」，另一个 agent 不能替它决策。
        // 缺这道闸，任何持有 agent_token 的主体都能改掉别人 CR 的终态。
        if (cr.TargetAgentId != agentId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "该 collaboration_request 未派给本 agent");
        }

        DecisionApplyResult applied = await DecisionApplier.ApplyAsync(
            cr,
            new DecisionCommand(
                Decision: decision,
                Reason: request.Reason,
                Needs: request.Needs is null ? null : JsonSerializer.SerializeToElement(request.Needs),
                AnalysisCapability: request.Analysis?.Capability,
                AnalysisContextScore: request.Analysis?.ContextScore is { } cs ? (int)Math.Round(cs) : null,
                AnalysisPermission: request.Analysis?.Permission,
                ActorType: AuditActorTypes.Agent,
                ActorId: agentId),
            db, outboxWriter, notifier, audit, http, ct);

        if (applied.Failed)
        {
            return applied.Error!;
        }

        DecisionApplyOutcome outcome = applied.Outcome!;

        return Results.Ok(new
        {
            collaboration_request_id = cr.Id,
            status = cr.Status,
            target_execution_id = cr.TargetExecutionId,
            decision = new
            {
                id = outcome.Record.Id,
                decision = outcome.Record.Decision,
                reason = outcome.Record.Reason,
                accepted_execution_id = outcome.AcceptedExecutionId,
                actor_type = outcome.Record.ActorType,
                actor_id = outcome.Record.ActorId,
                decided_at_ms = outcome.DecidedAt.ToUnixTimeMilliseconds(),
            },
        });
    }

    // ──────────────────────────── helpers ────────────────────────────

    /// <summary>
    /// 批量取出 CR 引用到的 WorkItem（provider_key / external_ref），避免逐条查询。
    /// </summary>
    private static async Task<Dictionary<Guid, WorkItemRef>> LoadWorkItemRefsAsync(
        MateOSDbContext db,
        List<DbCollaborationRequest> crs,
        CancellationToken ct)
    {
        List<Guid> workItemIds = crs
            .Select(cr => ReadGuid(Parse(cr.ContextRefs) is JsonObject obj ? obj["work_item_id"] : null))
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (workItemIds.Count == 0)
        {
            return [];
        }

        return (await db.WorkItems
                .AsNoTracking()
                .Where(w => workItemIds.Contains(w.Id))
                .Select(w => new { w.Id, w.ProviderKey, w.ExternalRef })
                .ToListAsync(ct))
            .ToDictionary(
                w => w.Id,
                w => new WorkItemRef(w.ProviderKey, w.Id.ToString(), w.ExternalRef));
    }

    /// <summary>
    /// <c>collaboration_requests.context_refs</c>（自由形态 jsonb）→ 契约 <see cref="CollaborationContextRefs"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="ExecutionPayloadMapper"/> 同一策略：<b>只做映射，不因为形状不匹配就丢内容</b>。
    /// </para>
    /// <para>
    /// <b>已发现的契约缺口</b>：各触发路径写进 <c>context_refs</c> 的 <c>project_id</c>
    /// 在 <see cref="CollaborationContextRefs"/> 里没有对应字段，因此不会出现在本 payload 上。
    /// Agent 若需要它（例如为了知道订阅哪个 Project 的 channel），当前只能从 work_item_ref 侧推。
    /// 这属于契约需要补齐的项，已登记到 <c>docs/review/</c>，不在此自行发明字段。
    /// </para>
    /// </remarks>
    private static CollaborationContextRefs ToContextRefs(
        string? storedJson,
        IReadOnlyDictionary<Guid, WorkItemRef> workItemRefs)
    {
        if (Parse(storedJson) is not JsonObject obj)
        {
            return new CollaborationContextRefs(null, null, null, null);
        }

        WorkItemRef? workItemRef = ReadGuid(obj["work_item_id"]) is { } workItemId
                                   && workItemRefs.TryGetValue(workItemId, out WorkItemRef? found)
            ? found
            : null;

        return new CollaborationContextRefs(
            ChannelId: ReadGuid(obj["channel_id"]),
            MessageSeq: ReadLong(obj["message_seq"]),
            MemoryRefs: ReadGuidList(obj["memory_refs"]),
            WorkItemRef: workItemRef);
    }

    private static IReadOnlyList<string> ParseCapabilities(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            // 能力列表坏掉不该让整条收单链路挂掉：Agent 仍应看到这条请求并自行判断
            return [];
        }
    }

    /// <summary>
    /// 契约里的 <c>deadline_s</c> 是<b>相对秒</b>（绝对时间戳会让快时钟的 Agent 提前放弃），
    /// 这里把 CR 创建时的相对额度换算成此刻的剩余量。
    /// </summary>
    private static int RemainingSeconds(DateTimeOffset createdAt, int deadlineS, DateTimeOffset now)
    {
        double remaining = (createdAt.AddSeconds(deadlineS) - now).TotalSeconds;

        return (int)Math.Clamp(remaining, 0, int.MaxValue);
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid? ReadGuid(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) && Guid.TryParse(text, out Guid id)
            ? id
            : null;

    private static long? ReadLong(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out long number) ? number : null;

    private static IReadOnlyList<Guid>? ReadGuidList(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count == 0)
        {
            return null;
        }

        List<Guid> ids = [];

        foreach (JsonNode? item in array)
        {
            if (ReadGuid(item) is { } id)
            {
                ids.Add(id);
            }
        }

        return ids.Count == 0 ? null : ids;
    }
}
