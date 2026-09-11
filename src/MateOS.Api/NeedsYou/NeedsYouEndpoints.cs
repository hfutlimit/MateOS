using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Persistence;
using MateOS.Domain.NeedsYou;
using MateOS.Domain.Routing;
using MateOS.Domain.Agent;
using MateOS.Domain.Memory;
using Microsoft.EntityFrameworkCore;
using DbCollaborationRequest = MateOS.Api.Persistence.CollaborationRequest;
using DbMemoryProposal = MateOS.Api.Persistence.MemoryProposal;
using DbAgentExecution = MateOS.Api.Persistence.AgentExecution;
using DbAgent = MateOS.Api.Persistence.Agent;

namespace MateOS.Api.NeedsYou;

public sealed record NeedsYouItem(
    string Category,
    string Source,
    Guid Id,
    string Title,
    string Summary,
    string? Link,
    string? Reason,
    long CreatedAtMs,
    long? UpdatedAtMs,
    Guid? ProjectId);

public sealed record NeedsYouResponse(
    int Total,
    int ProblemsCount,
    int ApprovalCount,
    int DecisionCount,
    int InformationCount,
    IReadOnlyList<NeedsYouItem> Items);

/// <summary>
/// Needs You 聚合端点（SYSTEM_DESIGN v0.9 §5.2 / PRD §1.2）。
/// </summary>
/// <remarks>
/// <para>
/// 跨 3 个事实源投影到统一 timeline：
/// <list type="bullet">
///   <item>E4 <c>collaboration_requests.status = NEED_CONTEXT</c> → INFORMATION</item>
///   <item>E5 <c>memory_proposals.status = PROPOSED</c> + project 用户有权限 → APPROVAL</item>
///   <item>E7 <c>agent_executions.status = FAILED</c>（在用户有权限的 project 内） → PROBLEMS</item>
///   <item>（V1 stub）E4 <c>collaboration_requests.status = PENDING</c>（用户是 target agent owner） → DECISION</item>
/// </list>
/// </para>
/// <para>
/// 排序：先按 category 优先级（PROBLEMS > APPROVAL > DECISION > INFORMATION），
/// 再按 created_at 倒序。
/// </para>
/// <para>
/// V1 简化：每源拉 ≤ 50 条（单源上限）；返回总条数 ≤ 200；不含 time-bucket 分组。
/// </para>
/// </remarks>
public static class NeedsYouEndpoints
{
    public static void MapNeedsYouEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/needs-you")
            .WithTags("NeedsYou")
            .RequireAuthorization();

        group.MapGet("", ListAsync);
        group.MapGet("/count", CountAsync);
    }

    private const int PerSourceLimit = 50;

    private static async Task<IResult> ListAsync(
        string? category,
        int? limit,
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();
        NeedsYouResponse resp = await AggregateAsync(userId, category, limit, db, ct);
        return Results.Ok(resp);
    }

    private static async Task<NeedsYouResponse> AggregateAsync(
        Guid userId,
        string? category,
        int? limit,
        MateOSDbContext db,
        CancellationToken ct)
    {
        int effectiveLimit = Math.Clamp(limit ?? 100, 1, 200);

        var items = new List<NeedsYouItem>();

        // 找用户在哪些 project 是 member（信息源过滤）
        HashSet<Guid> accessibleProjectIds = await db.ProjectMembers
            .Where(pm => pm.UserId == userId)
            .Select(pm => pm.ProjectId)
            .ToHashSetAsync(ct);

        if (accessibleProjectIds.Count > 0)
        {
            // 1. E4 CR.NEED_CONTEXT → INFORMATION
            if (FilterMatch(NeedsYouCategory.INFORMATION, category))
            {
                List<DbCollaborationRequest> infoCRs = await db.CollaborationRequests
                    .Where(cr => cr.Status == CrStatusMap.NeedContextValue)
                    .OrderByDescending(cr => cr.CreatedAt)
                    .Take(PerSourceLimit)
                    .ToListAsync(ct);

                foreach (DbCollaborationRequest cr in infoCRs)
                {
                    items.Add(new NeedsYouItem(
                        Category: NeedsYouCategory.INFORMATION.ToWireValue(),
                        Source: NeedsYouSource.CollaborationRequest.ToWireValue(),
                        Id: cr.Id,
                        Title: "Agent 需要补充信息",
                        Summary: cr.TriggerRef.Length > 200 ? cr.TriggerRef[..200] : cr.TriggerRef,
                        Link: $"/collaboration-requests/{cr.Id}",
                        Reason: null,
                        CreatedAtMs: cr.CreatedAt.ToUnixTimeMilliseconds(),
                        UpdatedAtMs: cr.ResolvedAt?.ToUnixTimeMilliseconds(),
                        ProjectId: null));
                }
            }

            // 2. E4 PENDING CR（用户是 target agent owner）→ DECISION
            if (FilterMatch(NeedsYouCategory.DECISION, category))
            {
                var myAgentIds = await db.Agents
                    .Where(a => a.OwnerUserId == userId)
                    .Select(a => a.Id)
                    .ToListAsync(ct);

                if (myAgentIds.Count > 0)
                {
                    List<DbCollaborationRequest> pendingCRs = await db.CollaborationRequests
                        .Where(cr => cr.Status == CrStatusMap.PendingValue
                                     && cr.TargetAgentId != null
                                     && myAgentIds.Contains(cr.TargetAgentId.Value))
                        .OrderByDescending(cr => cr.CreatedAt)
                        .Take(PerSourceLimit)
                        .ToListAsync(ct);

                    foreach (DbCollaborationRequest cr in pendingCRs)
                    {
                        items.Add(new NeedsYouItem(
                            Category: NeedsYouCategory.DECISION.ToWireValue(),
                            Source: NeedsYouSource.CollaborationRequest.ToWireValue(),
                            Id: cr.Id,
                            Title: "等待 Agent 决策",
                            Summary: cr.TriggerRef.Length > 200 ? cr.TriggerRef[..200] : cr.TriggerRef,
                            Link: $"/collaboration-requests/{cr.Id}",
                            Reason: null,
                            CreatedAtMs: cr.CreatedAt.ToUnixTimeMilliseconds(),
                            UpdatedAtMs: cr.ResolvedAt?.ToUnixTimeMilliseconds(),
                            ProjectId: null));
                    }
                }
            }

            // 3. E5 MemoryProposal.PROPOSED → APPROVAL
            if (FilterMatch(NeedsYouCategory.APPROVAL, category))
            {
                List<DbMemoryProposal> proposals = await db.MemoryProposals
                    .Where(mp => mp.Status == MemoryStatusMap.ProposedValue
                                 && mp.ProjectId != null
                                 && accessibleProjectIds.Contains(mp.ProjectId.Value))
                    .OrderByDescending(mp => mp.CreatedAt)
                    .Take(PerSourceLimit)
                    .ToListAsync(ct);

                foreach (DbMemoryProposal mp in proposals)
                {
                    items.Add(new NeedsYouItem(
                        Category: NeedsYouCategory.APPROVAL.ToWireValue(),
                        Source: NeedsYouSource.MemoryProposal.ToWireValue(),
                        Id: mp.Id,
                        Title: mp.Title.Length > 120 ? mp.Title[..120] : mp.Title,
                        Summary: mp.Content.Length > 200 ? mp.Content[..200] : mp.Content,
                        Link: $"/memory/proposals/{mp.Id}",
                        Reason: null,
                        CreatedAtMs: mp.CreatedAt.ToUnixTimeMilliseconds(),
                        UpdatedAtMs: mp.ApprovedAt?.ToUnixTimeMilliseconds(),
                        ProjectId: mp.ProjectId));
                }
            }

            // 4. E7 Execution.FAILED → PROBLEMS
            if (FilterMatch(NeedsYouCategory.PROBLEMS, category))
            {
                var myAgentIds = await db.Agents
                    .Where(a => a.OwnerUserId == userId)
                    .Select(a => a.Id)
                    .ToListAsync(ct);

                if (myAgentIds.Count > 0)
                {
                    List<DbAgentExecution> failedExecs = await db.AgentExecutions
                        .Where(e => e.Status == ExecutionStatusMap.FailedDbValue
                                    && myAgentIds.Contains(e.AgentId))
                        .OrderByDescending(e => e.CreatedAt)
                        .Take(PerSourceLimit)
                        .ToListAsync(ct);

                    foreach (DbAgentExecution exec in failedExecs)
                    {
                        string reason = "执行失败";
                        if (!string.IsNullOrEmpty(exec.ResultOutput))
                        {
                            reason = exec.ResultOutput.Length > 200
                                ? exec.ResultOutput[..200]
                                : exec.ResultOutput;
                        }

                        items.Add(new NeedsYouItem(
                            Category: NeedsYouCategory.PROBLEMS.ToWireValue(),
                            Source: NeedsYouSource.Execution.ToWireValue(),
                            Id: exec.Id,
                            Title: $"Agent {exec.AgentId} 的执行失败",
                            Summary: reason,
                            Link: $"/agent-executions/{exec.Id}",
                            Reason: reason,
                            CreatedAtMs: exec.CreatedAt.ToUnixTimeMilliseconds(),
                            UpdatedAtMs: exec.CompletedAt?.ToUnixTimeMilliseconds(),
                            ProjectId: null));
                    }
                }

                // 4b. Agent activity=ERROR（lifecycle=ACTIVE 时的告警）
                List<DbAgent> errorAgents = await db.Agents
                    .Where(a => a.OwnerUserId == userId
                                && a.Lifecycle == AgentLifecycleMap.ActiveDbValue
                                && a.Activity == AgentActivityMap.ErrorDbValue)
                    .OrderByDescending(a => a.ActivityUpdatedAt)
                    .Take(PerSourceLimit)
                    .ToListAsync(ct);

                foreach (DbAgent agent in errorAgents)
                {
                    items.Add(new NeedsYouItem(
                        Category: NeedsYouCategory.PROBLEMS.ToWireValue(),
                        Source: NeedsYouSource.Agent.ToWireValue(),
                        Id: agent.Id,
                        Title: $"Agent {agent.Name} 处于 ERROR 状态",
                        Summary: agent.ActivityReason ?? "（无具体原因）",
                        Link: $"/agents/{agent.Id}",
                        Reason: agent.ActivityReason,
                        CreatedAtMs: agent.CreatedAt.ToUnixTimeMilliseconds(),
                        UpdatedAtMs: agent.ActivityUpdatedAt?.ToUnixTimeMilliseconds(),
                        ProjectId: null));
                }
            }
        }

        // 排序：category 优先级 + created_at 倒序
        IReadOnlyList<NeedsYouItem> sorted = items
            .OrderBy(i => ParseCategory(i.Category))
            .ThenByDescending(i => i.CreatedAtMs)
            .Take(effectiveLimit)
            .ToList();

        return new NeedsYouResponse(
            Total: sorted.Count,
            ProblemsCount: items.Count(i => i.Category == NeedsYouCategory.PROBLEMS.ToWireValue()),
            ApprovalCount: items.Count(i => i.Category == NeedsYouCategory.APPROVAL.ToWireValue()),
            DecisionCount: items.Count(i => i.Category == NeedsYouCategory.DECISION.ToWireValue()),
            InformationCount: items.Count(i => i.Category == NeedsYouCategory.INFORMATION.ToWireValue()),
            Items: sorted);
    }

    private static async Task<IResult> CountAsync(
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();
        NeedsYouResponse resp = await AggregateAsync(userId, null, null, db, ct);
        return Results.Ok(new
        {
            resp.Total,
            resp.ProblemsCount,
            resp.ApprovalCount,
            resp.DecisionCount,
            resp.InformationCount,
        });
    }

    private static bool FilterMatch(NeedsYouCategory category, string? requestedFilter)
    {
        if (string.IsNullOrEmpty(requestedFilter))
        {
            return true;
        }

        return NeedsYouCategoryMap.TryParse(requestedFilter, out NeedsYouCategory requested)
               && requested == category;
    }

    private static NeedsYouCategory ParseCategory(string wire)
    {
        NeedsYouCategoryMap.TryParse(wire, out NeedsYouCategory c);
        return c;
    }
}
