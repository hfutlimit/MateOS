using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Persistence;
using MateOS.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace MateOS.Api.Workspace;

public sealed record CreateOrganizationRequest(string Name);

public sealed record UpdateOrganizationRequest(string? Name);

public sealed record AddMemberRequest(string Email, string? Role);

public sealed record CreateTeamRequest(Guid OrgId, string Name);

public sealed record UpdateTeamRequest(string? Name);

public sealed record CreateProjectRequest(Guid TeamId, string Name, string? Description, string? RepoUrl);

public sealed record UpdateProjectRequest(string? Name, string? Description, string? RepoUrl);

public sealed record MemberSummary(Guid UserId, string Email, string DisplayName, string Role, long JoinedAtMs);

public sealed record OrganizationSummary(Guid Id, string Name, string Role, long CreatedAtMs);

public sealed record TeamSummary(Guid Id, Guid OrgId, string Name, string Role, long CreatedAtMs);

public sealed record ProjectSummary(
    Guid Id, Guid TeamId, string Name, string? Description, string? RepoUrl, string Role, long CreatedAtMs);

/// <summary>
/// E1 §4 的 <c>/orgs</c> · <c>/teams</c> · <c>/projects</c> 端点。
/// </summary>
/// <remarks>
/// 权限一律经 <see cref="WorkspaceAuthorizer"/> 取三层角色后再判定，
/// 端点内不自行拼装 membership 查询（否则三层正交关系会被局部实现写丢）。
/// </remarks>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        MapOrganizations(app);
        MapTeams(app);
        MapProjects(app);
    }

    // ───────────────────────────── Organizations ─────────────────────────────

    private static void MapOrganizations(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder orgs = app.MapGroup("/orgs").WithTags("Organizations").RequireAuthorization();

        orgs.MapPost("", CreateOrganizationAsync);
        orgs.MapGet("", ListOrganizationsAsync);
        orgs.MapGet("/{id:guid}", GetOrganizationAsync);
        orgs.MapPatch("/{id:guid}", UpdateOrganizationAsync);
        orgs.MapDelete("/{id:guid}", DeleteOrganizationAsync);
        orgs.MapGet("/{id:guid}/members", ListOrganizationMembersAsync);
        orgs.MapPost("/{id:guid}/members", AddOrganizationMemberAsync);
        orgs.MapDelete("/{id:guid}/members/{userId:guid}", RemoveOrganizationMemberAsync);
    }

    private static async Task<IResult> CreateOrganizationAsync(
        CreateOrganizationRequest request,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();
        string name = request.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "组织名称不能为空");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedBy = userId,
            CreatedAt = now,
        };

        db.Organizations.Add(organization);

        // 创建者自动成为 owner（E1 §5.1）——这也是「Org 至少一个 owner」的唯一保证点
        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = organization.Id,
            UserId = userId,
            Role = MembershipRole.Owner.ToDbValue(),
            JoinedAt = now,
        });

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.OrganizationCreated,
            TargetType: "organization", TargetId: organization.Id, Detail: new { name }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/orgs/{organization.Id}",
            new OrganizationSummary(organization.Id, organization.Name, MembershipRoleMap.OwnerDbValue,
                organization.CreatedAt.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> ListOrganizationsAsync(
        MateOSDbContext db, HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        var rows = await (from m in db.OrganizationMembers
                          join o in db.Organizations on m.OrganizationId equals o.Id
                          where m.UserId == userId
                          orderby o.CreatedAt
                          select new { o.Id, o.Name, m.Role, o.CreatedAt }).ToListAsync(ct);

        return Results.Ok(rows.Select(r =>
            new OrganizationSummary(r.Id, r.Name, r.Role, r.CreatedAt.ToUnixTimeMilliseconds())));
    }

    private static async Task<IResult> GetOrganizationAsync(
        Guid id, MateOSDbContext db, WorkspaceAuthorizer authorizer, HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Organization? organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);

        if (organization is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "组织不存在");
        }

        WorkspaceRoles roles = await authorizer.ForOrganizationAsync(userId, id, ct);

        if (!roles.IsOrganizationMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "你不是该组织成员");
        }

        string role = roles.IsOrganizationOwner
            ? MembershipRoleMap.OwnerDbValue
            : MembershipRoleMap.MemberDbValue;

        return Results.Ok(new OrganizationSummary(
            organization.Id, organization.Name, role, organization.CreatedAt.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> UpdateOrganizationAsync(
        Guid id,
        UpdateOrganizationRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Organization? organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);

        if (organization is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "组织不存在");
        }

        WorkspaceRoles roles = await authorizer.ForOrganizationAsync(userId, id, ct);

        if (!roles.IsOrganizationMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "你不是该组织成员");
        }

        if (!roles.CanManageOrganization)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有组织 owner 可以修改组织");
        }

        string? name = request.Name?.Trim();

        if (name is not null)
        {
            if (name.Length == 0)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "组织名称不能为空");
            }

            organization.Name = name;
        }

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.OrganizationUpdated,
            TargetType: "organization", TargetId: organization.Id));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteOrganizationAsync(
        Guid id, MateOSDbContext db, WorkspaceAuthorizer authorizer, AuditWriter audit,
        HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Organization? organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);

        if (organization is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "组织不存在");
        }

        WorkspaceRoles roles = await authorizer.ForOrganizationAsync(userId, id, ct);

        if (!roles.CanManageOrganization)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有组织 owner 可以删除组织");
        }

        db.Organizations.Remove(organization);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.OrganizationDeleted,
            TargetType: "organization", TargetId: id));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListOrganizationMembersAsync(
        Guid id, MateOSDbContext db, WorkspaceAuthorizer authorizer, HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        WorkspaceRoles roles = await authorizer.ForOrganizationAsync(userId, id, ct);

        if (!roles.IsOrganizationMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "你不是该组织成员");
        }

        return Results.Ok(await LoadOrganizationMembersAsync(db, id, ct));
    }

    private static async Task<IResult> AddOrganizationMemberAsync(
        Guid id,
        AddMemberRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Organization? organization = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);

        if (organization is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "组织不存在");
        }

        WorkspaceRoles roles = await authorizer.ForOrganizationAsync(userId, id, ct);

        if (!roles.CanManageOrganization)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有组织 owner 可以管理成员");
        }

        if (!MembershipRoleMap.TryParse(NormalizeRole(request.Role), out MembershipRole role))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "role 只能是 owner 或 member");
        }

        User? target = await FindUserByEmailAsync(db, request.Email, ct);

        if (target is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "该邮箱未注册");
        }

        OrganizationMember? existing = await db.OrganizationMembers
            .FirstOrDefaultAsync(m => m.OrganizationId == id && m.UserId == target.Id, ct);

        if (existing is not null)
        {
            // 已是成员：只做角色变更（升级/降级），不重复插入
            if (existing.Role == role.ToDbValue())
            {
                return ApiErrors.ConflictResult(ApiErrors.Conflict, "该用户已是组织成员");
            }

            existing.Role = role.ToDbValue();

            audit.Record(http, new AuditEntry(
                AuditActorTypes.User, userId, AuditActions.MembershipRoleChanged,
                TargetType: "organization_member", TargetId: target.Id,
                Detail: new { organization_id = id, role = role.ToDbValue() }));

            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }

        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = id,
            UserId = target.Id,
            Role = role.ToDbValue(),
            JoinedAt = DateTimeOffset.UtcNow,
        });

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.MembershipGranted,
            TargetType: "organization_member", TargetId: target.Id,
            Detail: new { organization_id = id, role = role.ToDbValue() }));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RemoveOrganizationMemberAsync(
        Guid id,
        Guid userId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid actorId = http.RequireUserId();

        WorkspaceRoles roles = await authorizer.ForOrganizationAsync(actorId, id, ct);

        if (!roles.CanManageOrganization)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有组织 owner 可以管理成员");
        }

        OrganizationMember? membership = await db.OrganizationMembers
            .FirstOrDefaultAsync(m => m.OrganizationId == id && m.UserId == userId, ct);

        if (membership is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "该用户不是组织成员");
        }

        // E1 §3.1：组织至少要留一个 owner，否则会变成无人可管理的孤儿组织
        List<Guid> owners = await db.OrganizationMembers
            .Where(m => m.OrganizationId == id && m.Role == MembershipRoleMap.OwnerDbValue)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        if (OrganizationOwnership.WouldOrphanOrganization(owners, userId, newRole: null))
        {
            return ApiErrors.BadRequest(
                ApiErrors.WouldOrphanOrganization, "不能移除组织唯一的 owner，请先转让所有权");
        }

        db.OrganizationMembers.Remove(membership);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, actorId, AuditActions.MembershipRevoked,
            TargetType: "organization_member", TargetId: userId,
            Detail: new { organization_id = id }));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ──────────────────────────────── Teams ────────────────────────────────

    private static void MapTeams(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder teams = app.MapGroup("/teams").WithTags("Teams").RequireAuthorization();

        teams.MapPost("", CreateTeamAsync);
        teams.MapGet("", ListTeamsAsync);
        teams.MapGet("/{id:guid}", GetTeamAsync);
        teams.MapPatch("/{id:guid}", UpdateTeamAsync);
        teams.MapGet("/{id:guid}/members", ListTeamMembersAsync);
        teams.MapPost("/{id:guid}/members", AddTeamMemberAsync);
        teams.MapDelete("/{id:guid}/members/{userId:guid}", RemoveTeamMemberAsync);
    }

    private static async Task<IResult> CreateTeamAsync(
        CreateTeamRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();
        string name = request.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "团队名称不能为空");
        }

        bool orgExists = await db.Organizations.AnyAsync(o => o.Id == request.OrgId, ct);

        if (!orgExists)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "组织不存在");
        }

        WorkspaceRoles roles = await authorizer
            .ForTeamAsync(userId, new Team { Id = Guid.Empty, OrgId = request.OrgId }, ct);

        if (!roles.IsOrganizationMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "只有组织成员可以创建团队");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var team = new Team
        {
            Id = Guid.NewGuid(),
            OrgId = request.OrgId,
            Name = name,
            CreatedAt = now,
        };

        db.Teams.Add(team);

        // 创建者是该 Team 的 owner —— 与它是否 Org owner **无关**（E1 §3.1 三层正交）
        db.TeamMembers.Add(new TeamMember
        {
            TeamId = team.Id,
            UserId = userId,
            Role = MembershipRole.Owner.ToDbValue(),
            JoinedAt = now,
        });

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.TeamCreated,
            TargetType: "team", TargetId: team.Id, Detail: new { name, org_id = request.OrgId }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/teams/{team.Id}",
            new TeamSummary(team.Id, team.OrgId, team.Name, MembershipRoleMap.OwnerDbValue,
                team.CreatedAt.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> ListTeamsAsync(
        Guid? orgId, MateOSDbContext db, HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        var rows = await (from m in db.TeamMembers
                          join t in db.Teams on m.TeamId equals t.Id
                          where m.UserId == userId && (orgId == null || t.OrgId == orgId)
                          orderby t.CreatedAt
                          select new { t.Id, t.OrgId, t.Name, m.Role, t.CreatedAt }).ToListAsync(ct);

        return Results.Ok(rows.Select(r =>
            new TeamSummary(r.Id, r.OrgId, r.Name, r.Role, r.CreatedAt.ToUnixTimeMilliseconds())));
    }

    private static async Task<IResult> GetTeamAsync(
        Guid id, MateOSDbContext db, WorkspaceAuthorizer authorizer, HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Team? team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);

        if (team is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "团队不存在");
        }

        WorkspaceRoles roles = await authorizer.ForTeamAsync(userId, team, ct);

        if (!roles.IsTeamMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "你不是该团队成员");
        }

        string role = roles.IsTeamOwner ? MembershipRoleMap.OwnerDbValue : MembershipRoleMap.MemberDbValue;

        return Results.Ok(new TeamSummary(team.Id, team.OrgId, team.Name, role, team.CreatedAt.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> UpdateTeamAsync(
        Guid id,
        UpdateTeamRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Team? team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);

        if (team is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "团队不存在");
        }

        WorkspaceRoles roles = await authorizer.ForTeamAsync(userId, team, ct);

        if (!roles.CanManageTeam)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有团队 owner 可以修改团队");
        }

        string? name = request.Name?.Trim();

        if (name is not null)
        {
            if (name.Length == 0)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "团队名称不能为空");
            }

            team.Name = name;
        }

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.TeamUpdated,
            TargetType: "team", TargetId: team.Id));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListTeamMembersAsync(
        Guid id, MateOSDbContext db, WorkspaceAuthorizer authorizer, HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Team? team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);

        if (team is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "团队不存在");
        }

        WorkspaceRoles roles = await authorizer.ForTeamAsync(userId, team, ct);

        if (!roles.IsTeamMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "你不是该团队成员");
        }

        return Results.Ok(await LoadTeamMembersAsync(db, id, ct));
    }

    private static async Task<IResult> AddTeamMemberAsync(
        Guid id,
        AddMemberRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Team? team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);

        if (team is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "团队不存在");
        }

        WorkspaceRoles roles = await authorizer.ForTeamAsync(userId, team, ct);

        if (!roles.CanManageTeam)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有团队 owner 可以管理成员");
        }

        if (!MembershipRoleMap.TryParse(NormalizeRole(request.Role), out MembershipRole role))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "role 只能是 owner 或 member");
        }

        User? target = await FindUserByEmailAsync(db, request.Email, ct);

        if (target is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "该邮箱未注册");
        }

        TeamMember? existing = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.TeamId == id && m.UserId == target.Id, ct);

        if (existing is not null)
        {
            existing.Role = role.ToDbValue();

            audit.Record(http, new AuditEntry(
                AuditActorTypes.User, userId, AuditActions.MembershipRoleChanged,
                TargetType: "team_member", TargetId: target.Id,
                Detail: new { team_id = id, role = role.ToDbValue() }));

            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }

        db.TeamMembers.Add(new TeamMember
        {
            TeamId = id,
            UserId = target.Id,
            Role = role.ToDbValue(),
            JoinedAt = DateTimeOffset.UtcNow,
        });

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.MembershipGranted,
            TargetType: "team_member", TargetId: target.Id,
            Detail: new { team_id = id, role = role.ToDbValue() }));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RemoveTeamMemberAsync(
        Guid id,
        Guid userId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid actorId = http.RequireUserId();

        Team? team = await db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);

        if (team is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "团队不存在");
        }

        WorkspaceRoles roles = await authorizer.ForTeamAsync(actorId, team, ct);

        if (!roles.CanManageTeam)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有团队 owner 可以管理成员");
        }

        TeamMember? membership = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.TeamId == id && m.UserId == userId, ct);

        if (membership is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "该用户不是团队成员");
        }

        db.TeamMembers.Remove(membership);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, actorId, AuditActions.MembershipRevoked,
            TargetType: "team_member", TargetId: userId, Detail: new { team_id = id }));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ─────────────────────────────── Projects ───────────────────────────────

    private static void MapProjects(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder projects = app.MapGroup("/projects").WithTags("Projects").RequireAuthorization();

        projects.MapPost("", CreateProjectAsync);
        projects.MapGet("", ListProjectsAsync);
        projects.MapGet("/{id:guid}", GetProjectAsync);
        projects.MapPatch("/{id:guid}", UpdateProjectAsync);
        projects.MapDelete("/{id:guid}", DeleteProjectAsync);
        projects.MapGet("/{id:guid}/members", ListProjectMembersAsync);
        projects.MapPost("/{id:guid}/members", AddProjectMemberAsync);
        projects.MapDelete("/{id:guid}/members/{userId:guid}", RemoveProjectMemberAsync);
    }

    private static async Task<IResult> CreateProjectAsync(
        CreateProjectRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();
        string name = request.Name?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "项目名称不能为空");
        }

        Team? team = await db.Teams.FirstOrDefaultAsync(t => t.Id == request.TeamId, ct);

        if (team is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "团队不存在");
        }

        WorkspaceRoles roles = await authorizer.ForTeamAsync(userId, team, ct);

        if (!roles.IsTeamMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "只有团队成员可以创建项目");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var project = new Project
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Name = name,
            Description = request.Description,
            RepoUrl = request.RepoUrl,
            CreatedAt = now,
        };

        db.Projects.Add(project);

        db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = project.Id,
            UserId = userId,
            Role = MembershipRole.Owner.ToDbValue(),
            JoinedAt = now,
        });

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.ProjectCreated,
            TargetType: "project", TargetId: project.Id, Detail: new { name, team_id = team.Id }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/projects/{project.Id}",
            new ProjectSummary(project.Id, project.TeamId, project.Name, project.Description,
                project.RepoUrl, MembershipRoleMap.OwnerDbValue, project.CreatedAt.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> ListProjectsAsync(
        Guid? teamId, MateOSDbContext db, HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        var rows = await (from m in db.ProjectMembers
                          join p in db.Projects on m.ProjectId equals p.Id
                          where m.UserId == userId && (teamId == null || p.TeamId == teamId)
                          orderby p.CreatedAt
                          select new
                          {
                              p.Id,
                              p.TeamId,
                              p.Name,
                              p.Description,
                              p.RepoUrl,
                              m.Role,
                              p.CreatedAt,
                          }).ToListAsync(ct);

        return Results.Ok(rows.Select(r => new ProjectSummary(
            r.Id, r.TeamId, r.Name, r.Description, r.RepoUrl, r.Role, r.CreatedAt.ToUnixTimeMilliseconds())));
    }

    private static async Task<IResult> GetProjectAsync(
        Guid id, MateOSDbContext db, WorkspaceAuthorizer authorizer, HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.IsProjectMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "你不是该项目成员");
        }

        string role = roles.IsProjectOwner ? MembershipRoleMap.OwnerDbValue : MembershipRoleMap.MemberDbValue;

        return Results.Ok(new ProjectSummary(project.Id, project.TeamId, project.Name, project.Description,
            project.RepoUrl, role, project.CreatedAt.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> UpdateProjectAsync(
        Guid id,
        UpdateProjectRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.CanManageProject)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以修改项目");
        }

        string? name = request.Name?.Trim();

        if (name is not null)
        {
            if (name.Length == 0)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "项目名称不能为空");
            }

            project.Name = name;
        }

        if (request.Description is not null)
        {
            project.Description = request.Description;
        }

        if (request.RepoUrl is not null)
        {
            project.RepoUrl = string.IsNullOrWhiteSpace(request.RepoUrl) ? null : request.RepoUrl.Trim();
        }

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.ProjectUpdated,
            TargetType: "project", TargetId: project.Id));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteProjectAsync(
        Guid id, MateOSDbContext db, WorkspaceAuthorizer authorizer, AuditWriter audit,
        HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.CanManageProject)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以删除项目");
        }

        db.Projects.Remove(project);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.ProjectDeleted,
            TargetType: "project", TargetId: id));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListProjectMembersAsync(
        Guid id, MateOSDbContext db, WorkspaceAuthorizer authorizer, HttpContext http, CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.IsProjectMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "你不是该项目成员");
        }

        return Results.Ok(await LoadProjectMembersAsync(db, id, ct));
    }

    private static async Task<IResult> AddProjectMemberAsync(
        Guid id,
        AddMemberRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.CanManageProject)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以管理成员");
        }

        if (!MembershipRoleMap.TryParse(NormalizeRole(request.Role), out MembershipRole role))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "role 只能是 owner 或 member");
        }

        User? target = await FindUserByEmailAsync(db, request.Email, ct);

        if (target is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "该邮箱未注册");
        }

        ProjectMember? existing = await db.ProjectMembers
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.UserId == target.Id, ct);

        if (existing is not null)
        {
            existing.Role = role.ToDbValue();

            audit.Record(http, new AuditEntry(
                AuditActorTypes.User, userId, AuditActions.MembershipRoleChanged,
                TargetType: "project_member", TargetId: target.Id,
                Detail: new { project_id = id, role = role.ToDbValue() }));

            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }

        db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = id,
            UserId = target.Id,
            Role = role.ToDbValue(),
            JoinedAt = DateTimeOffset.UtcNow,
        });

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.MembershipGranted,
            TargetType: "project_member", TargetId: target.Id,
            Detail: new { project_id = id, role = role.ToDbValue() }));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RemoveProjectMemberAsync(
        Guid id,
        Guid userId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid actorId = http.RequireUserId();

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(actorId, project, ct);

        if (!roles.CanManageProject)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以管理成员");
        }

        ProjectMember? membership = await db.ProjectMembers
            .FirstOrDefaultAsync(m => m.ProjectId == id && m.UserId == userId, ct);

        if (membership is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "该用户不是项目成员");
        }

        db.ProjectMembers.Remove(membership);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, actorId, AuditActions.MembershipRevoked,
            TargetType: "project_member", TargetId: userId, Detail: new { project_id = id }));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ─────────────────────────────── 共用 ───────────────────────────────

    /// <summary>
    /// 三层成员列表。三张表的列名不同，无法用同一段 LINQ 表达；
    /// 与其做一个需要反射/表达式拼接的抽象，不如写三份直白的实现。
    /// </summary>
    private static async Task<IReadOnlyList<MemberSummary>> LoadOrganizationMembersAsync(
        MateOSDbContext db, Guid organizationId, CancellationToken ct)
    {
        var rows = await (from m in db.OrganizationMembers
                          join u in db.Users on m.UserId equals u.Id
                          where m.OrganizationId == organizationId
                          orderby m.JoinedAt
                          select new { u.Id, u.Email, u.DisplayName, m.Role, m.JoinedAt }).ToListAsync(ct);

        return rows
            .Select(r => new MemberSummary(r.Id, r.Email, r.DisplayName, r.Role, r.JoinedAt.ToUnixTimeMilliseconds()))
            .ToList();
    }

    private static async Task<IReadOnlyList<MemberSummary>> LoadTeamMembersAsync(
        MateOSDbContext db, Guid teamId, CancellationToken ct)
    {
        var rows = await (from m in db.TeamMembers
                          join u in db.Users on m.UserId equals u.Id
                          where m.TeamId == teamId
                          orderby m.JoinedAt
                          select new { u.Id, u.Email, u.DisplayName, m.Role, m.JoinedAt }).ToListAsync(ct);

        return rows
            .Select(r => new MemberSummary(r.Id, r.Email, r.DisplayName, r.Role, r.JoinedAt.ToUnixTimeMilliseconds()))
            .ToList();
    }

    private static async Task<IReadOnlyList<MemberSummary>> LoadProjectMembersAsync(
        MateOSDbContext db, Guid projectId, CancellationToken ct)
    {
        var rows = await (from m in db.ProjectMembers
                          join u in db.Users on m.UserId equals u.Id
                          where m.ProjectId == projectId
                          orderby m.JoinedAt
                          select new { u.Id, u.Email, u.DisplayName, m.Role, m.JoinedAt }).ToListAsync(ct);

        return rows
            .Select(r => new MemberSummary(r.Id, r.Email, r.DisplayName, r.Role, r.JoinedAt.ToUnixTimeMilliseconds()))
            .ToList();
    }

    private static string? NormalizeRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? MembershipRoleMap.MemberDbValue : role.Trim().ToLowerInvariant();

    private static Task<User?> FindUserByEmailAsync(MateOSDbContext db, string? email, CancellationToken ct)
    {
        string normalized = email?.Trim() ?? string.Empty;

        return string.IsNullOrEmpty(normalized)
            ? Task.FromResult<User?>(null)
            : db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
    }
}
