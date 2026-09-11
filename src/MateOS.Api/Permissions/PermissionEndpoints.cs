using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Persistence;
using MateOS.Api.Workspace;
using MateOS.Domain.Identity;
using MateOS.Domain.Permission;
using Microsoft.EntityFrameworkCore;
using Project = MateOS.Api.Persistence.Project;
using DbPermission = MateOS.Api.Persistence.Permission;

namespace MateOS.Api.Permissions;

public sealed record CreatePermissionRequest(
    string SubjectType,
    Guid SubjectId,
    string PermKey,
    string Effect);

public sealed record PermissionSummary(
    Guid Id,
    string ScopeType,
    Guid ScopeId,
    string SubjectType,
    Guid SubjectId,
    string PermKey,
    string Effect,
    long CreatedAtMs);

public sealed record CheckPermissionRequest(
    string SubjectType,
    Guid SubjectId,
    string PermKey,
    Guid ChannelId);

public sealed record CheckPermissionResponse(
    string Effect,
    string PermKey,
    string Source);  // "channel_override" | "project_override" | "default"

/// <summary>
/// E6 §4 Permission CRUD + 内部 /check 端点。
/// </summary>
/// <remarks>
/// <para>
/// V1 简化：
/// <list type="bullet">
///   <item>无 Redis 缓存（每次查 DB；M4a 阶段；V2 性能优化时加 + perm.changed pub/sub 失效）</item>
///   <item>无 [RequirePermission] ASP.NET filter（V1 端点层显式调 check；M4a+ 抽 filter）</item>
///   <item>三态 effect 完整支持（ALLOW / DENY / REQUIRE_APPROVAL）</item>
/// </list>
/// </para>
/// </remarks>
public static class PermissionEndpoints
{
    public static void MapPermissionEndpoints(this IEndpointRouteBuilder app)
    {
        // Project scope
        RouteGroupBuilder projectGroup = app.MapGroup("/projects/{projectId:guid}/permissions")
            .WithTags("Permissions.Project")
            .RequireAuthorization();

        projectGroup.MapGet("", ListProjectPermissionsAsync);
        projectGroup.MapPost("", CreateProjectPermissionAsync);
        projectGroup.MapDelete("/{permissionId:guid}", DeleteProjectPermissionAsync);

        // Channel scope
        RouteGroupBuilder channelGroup = app.MapGroup("/channels/{channelId:guid}/permissions")
            .WithTags("Permissions.Channel")
            .RequireAuthorization();

        channelGroup.MapGet("", ListChannelPermissionsAsync);
        channelGroup.MapPost("", CreateChannelPermissionAsync);
        channelGroup.MapDelete("/{permissionId:guid}", DeleteChannelPermissionAsync);

        // 内部 /check
        RouteGroupBuilder checkGroup = app.MapGroup("/permissions")
            .WithTags("Permissions.Check")
            .RequireAuthorization();

        checkGroup.MapPost("/check", CheckPermissionAsync);
    }

    // ───────────────────────── Project scope ─────────────────────────

    private static async Task<IResult> ListProjectPermissionsAsync(
        Guid projectId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.IsProjectMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "你不是该项目成员");
        }

        var rows = await db.Permissions
            .Where(x => x.ScopeType == PermScopeTypeMap.ProjectValue && x.ScopeId == projectId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(ToSummary));
    }

    private static async Task<IResult> CreateProjectPermissionAsync(
        Guid projectId,
        CreatePermissionRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (!ValidateRequest(request, out string? validationError))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, validationError!);
        }

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.CanManageProject)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以设置权限");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid permId = Guid.NewGuid();

        var perm = new DbPermission
        {
            Id = permId,
            ScopeType = PermScopeTypeMap.ProjectValue,
            ScopeId = projectId,
            SubjectType = request.SubjectType,
            SubjectId = request.SubjectId,
            PermKey = request.PermKey,
            Effect = request.Effect,
            CreatedAt = now,
        };

        db.Permissions.Add(perm);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.PermissionCreated,
            TargetType: "permission", TargetId: permId,
            Detail: new
            {
                scope_type = "PROJECT",
                scope_id = projectId,
                subject_type = request.SubjectType,
                subject_id = request.SubjectId,
                perm_key = request.PermKey,
                effect = request.Effect,
            }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/projects/{projectId}/permissions/{permId}",
            ToSummary(perm));
    }

    private static async Task<IResult> DeleteProjectPermissionAsync(
        Guid projectId,
        Guid permissionId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.CanManageProject)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以删除权限");
        }

        DbPermission? perm = await db.Permissions.FirstOrDefaultAsync(
            x => x.Id == permissionId && x.ScopeType == PermScopeTypeMap.ProjectValue && x.ScopeId == projectId,
            ct);

        if (perm is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "permission 不存在");
        }

        db.Permissions.Remove(perm);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.PermissionDeleted,
            TargetType: "permission", TargetId: permissionId));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ───────────────────────── Channel scope ─────────────────────────

    private static async Task<IResult> ListChannelPermissionsAsync(
        Guid channelId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.DeletedAt == null, ct);

        if (channel is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "channel 不存在");
        }

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == channel.ProjectId, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.IsProjectMember)
        {
            return ApiErrors.Forbidden(ApiErrors.NotMember, "你不是该项目成员");
        }

        var rows = await db.Permissions
            .Where(x => x.ScopeType == PermScopeTypeMap.ChannelValue && x.ScopeId == channelId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(ToSummary));
    }

    private static async Task<IResult> CreateChannelPermissionAsync(
        Guid channelId,
        CreatePermissionRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (!ValidateRequest(request, out string? validationError))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, validationError!);
        }

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.DeletedAt == null, ct);

        if (channel is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "channel 不存在");
        }

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == channel.ProjectId, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.CanManageProject)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以设置 channel 权限");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid permId = Guid.NewGuid();

        var perm = new DbPermission
        {
            Id = permId,
            ScopeType = PermScopeTypeMap.ChannelValue,
            ScopeId = channelId,
            SubjectType = request.SubjectType,
            SubjectId = request.SubjectId,
            PermKey = request.PermKey,
            Effect = request.Effect,
            CreatedAt = now,
        };

        db.Permissions.Add(perm);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.PermissionCreated,
            TargetType: "permission", TargetId: permId,
            Detail: new
            {
                scope_type = "CHANNEL",
                scope_id = channelId,
                subject_type = request.SubjectType,
                subject_id = request.SubjectId,
                perm_key = request.PermKey,
                effect = request.Effect,
            }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/channels/{channelId}/permissions/{permId}",
            ToSummary(perm));
    }

    private static async Task<IResult> DeleteChannelPermissionAsync(
        Guid channelId,
        Guid permissionId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.DeletedAt == null, ct);

        if (channel is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "channel 不存在");
        }

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == channel.ProjectId, ct);

        if (project is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
        }

        WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

        if (!roles.CanManageProject)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以删除 channel 权限");
        }

        DbPermission? perm = await db.Permissions.FirstOrDefaultAsync(
            x => x.Id == permissionId && x.ScopeType == PermScopeTypeMap.ChannelValue && x.ScopeId == channelId,
            ct);

        if (perm is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "permission 不存在");
        }

        db.Permissions.Remove(perm);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.PermissionDeleted,
            TargetType: "permission", TargetId: permissionId));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ───────────────────────── 内部 /check ─────────────────────────

    private static async Task<IResult> CheckPermissionAsync(
        CheckPermissionRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        http.RequireUserId();  // V1：user token 调；agent_token 留 M4a+

        if (!PermSubjectTypeMap.TryParse(request.SubjectType, out PermSubjectType subjectType))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "subject_type 必须是 USER 或 AGENT");
        }

        string? permKeyError = PermKey.Validate(request.PermKey);
        if (permKeyError is not null)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, permKeyError);
        }

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == request.ChannelId && c.DeletedAt == null, ct);

        if (channel is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "channel 不存在");
        }

        // 查 channel override + project override
        DbPermission? channelOverride = await db.Permissions.FirstOrDefaultAsync(
            x => x.ScopeType == PermScopeTypeMap.ChannelValue
                 && x.ScopeId == channel.Id
                 && x.SubjectType == request.SubjectType
                 && x.SubjectId == request.SubjectId
                 && x.PermKey == request.PermKey, ct);

        DbPermission? projectOverride = await db.Permissions.FirstOrDefaultAsync(
            x => x.ScopeType == PermScopeTypeMap.ProjectValue
                 && x.ScopeId == channel.ProjectId
                 && x.SubjectType == request.SubjectType
                 && x.SubjectId == request.SubjectId
                 && x.PermKey == request.PermKey, ct);

        // 决定 role：subject 在 workspace 内的角色
        // V1 简化：USER 按 project_members.role 查；AGENT 一律按 member（E6 §3.1）
        PermRole role = PermRole.Member;
        if (subjectType == PermSubjectType.USER)
        {
            bool isOwner = await db.ProjectMembers
                .AnyAsync(pm => pm.ProjectId == channel.ProjectId && pm.UserId == request.SubjectId
                              && pm.Role == MembershipRoleMap.OwnerDbValue, ct);
            if (isOwner)
            {
                role = PermRole.Owner;
            }
        }

        PermEffect? channelEffect = channelOverride is not null && PermEffectMap.TryParse(channelOverride.Effect, out PermEffect ce)
            ? ce : null;
        PermEffect? projectEffect = projectOverride is not null && PermEffectMap.TryParse(projectOverride.Effect, out PermEffect pe)
            ? pe : null;

        PermEffect finalEffect = PermissionCheck.Merge(
            channelEffect, projectEffect, subjectType, role, request.PermKey);

        string source = channelEffect.HasValue
            ? "channel_override"
            : projectEffect.HasValue
                ? "project_override"
                : "default";

        return Results.Ok(new CheckPermissionResponse(
            Effect: finalEffect.ToDbValue(),
            PermKey: request.PermKey,
            Source: source));
    }

    // ── helpers ──

    private static bool ValidateRequest(CreatePermissionRequest request, out string? error)
    {
        if (!PermSubjectTypeMap.TryParse(request.SubjectType, out _))
        {
            error = "subject_type 必须是 USER 或 AGENT";
            return false;
        }

        string? permKeyError = PermKey.Validate(request.PermKey);
        if (permKeyError is not null)
        {
            error = permKeyError;
            return false;
        }

        if (!PermEffectMap.TryParse(request.Effect, out _))
        {
            error = "effect 必须是 ALLOW / DENY / REQUIRE_APPROVAL";
            return false;
        }

        error = null;
        return true;
    }

    private static PermissionSummary ToSummary(DbPermission p) => new(
        p.Id, p.ScopeType, p.ScopeId, p.SubjectType, p.SubjectId,
        p.PermKey, p.Effect, p.CreatedAt.ToUnixTimeMilliseconds());
}
