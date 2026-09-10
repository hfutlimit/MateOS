using MateOS.Api.Persistence;
using MateOS.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace MateOS.Api.Workspace;

/// <summary>
/// 三层角色的查询入口。
/// </summary>
/// <remarks>
/// E1 §3.1 的「Org owner / Team owner / Project owner 互相独立」要成立，
/// 前提是所有授权判定都从同一处取角色。端点里禁止自己拼 membership 查询。
/// </remarks>
public sealed class WorkspaceAuthorizer(MateOSDbContext db)
{
    /// <summary>只关心 Org 层级时使用（例如组织设置页）。</summary>
    public async Task<WorkspaceRoles> ForOrganizationAsync(Guid userId, Guid orgId, CancellationToken ct) =>
        await ResolveAsync(userId, orgId, teamId: null, projectId: null, ct);

    public async Task<WorkspaceRoles> ForTeamAsync(Guid userId, Team team, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(team);

        return await ResolveAsync(userId, orgId: team.OrgId, teamId: team.Id, projectId: null, ct);
    }

    public async Task<WorkspaceRoles> ForProjectAsync(Guid userId, Project project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);

        Guid? orgId = await db.Teams
            .Where(t => t.Id == project.TeamId)
            .Select(t => (Guid?)t.OrgId)
            .FirstOrDefaultAsync(ct);

        return await ResolveAsync(userId, orgId, project.TeamId, project.Id, ct);
    }

    /// <summary>该用户可见的 Org 集合（用于列表过滤）。</summary>
    public IQueryable<Guid> VisibleOrganizationIds(Guid userId) =>
        db.OrganizationMembers.Where(m => m.UserId == userId).Select(m => m.OrganizationId);

    private async Task<WorkspaceRoles> ResolveAsync(
        Guid userId, Guid? orgId, Guid? teamId, Guid? projectId, CancellationToken ct)
    {
        MembershipRole? organization = orgId is { } o
            ? await RoleOf(db.OrganizationMembers
                .Where(m => m.OrganizationId == o && m.UserId == userId)
                .Select(m => m.Role), ct)
            : null;

        MembershipRole? team = teamId is { } t
            ? await RoleOf(db.TeamMembers
                .Where(m => m.TeamId == t && m.UserId == userId)
                .Select(m => m.Role), ct)
            : null;

        MembershipRole? project = projectId is { } p
            ? await RoleOf(db.ProjectMembers
                .Where(m => m.ProjectId == p && m.UserId == userId)
                .Select(m => m.Role), ct)
            : null;

        return new WorkspaceRoles(organization, team, project);
    }

    private static async Task<MembershipRole?> RoleOf(IQueryable<string> query, CancellationToken ct)
    {
        string? value = await query.FirstOrDefaultAsync(ct);

        return MembershipRoleMap.TryParse(value, out MembershipRole role) ? role : null;
    }
}
