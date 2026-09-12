using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Persistence;
using MateOS.Api.Workspace;
using MateOS.Domain.Agent;
using MateOS.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Project = MateOS.Api.Persistence.Project;
using DbAgent = MateOS.Api.Persistence.Agent;
using DbAgentToken = MateOS.Api.Persistence.AgentToken;
using AgentTokenResult = MateOS.Api.Auth.AgentTokenResult;

namespace MateOS.Api.Agents;

// ── DTO ──

public sealed record CreateCredentialRequest(string Provider, string Label, string Secret, string? Meta);

public sealed record CredentialSummary(
    Guid Id,
    string Provider,
    string Label,
    string? Meta,
    long? LastUsedAtMs,
    long CreatedAtMs);

public sealed record CreateAgentRequest(
    string Name,
    string Role,
    IReadOnlyList<string> Capabilities,
    Guid CredentialId,
    int? MaxConcurrency,
    decimal? DailyLimitUsd,
    decimal? MonthlyBudgetUsd);

public sealed record UpdateAgentRequest(
    string? Name,
    IReadOnlyList<string>? Capabilities,
    Guid? CredentialId,
    int? MaxConcurrency,
    decimal? DailyLimitUsd,
    decimal? MonthlyBudgetUsd);

public sealed record ReportActivityRequest(string Activity, string? Reason);

public sealed record AgentSummary(
    Guid Id,
    Guid OwnerUserId,
    Guid CredentialId,
    string Name,
    string Role,
    IReadOnlyList<string> Capabilities,
    string Lifecycle,
    string Activity,
    string? ActivityReason,
    long? ActivityUpdatedAtMs,
    int MaxConcurrency,
    decimal DailyLimitUsd,
    decimal MonthlyBudgetUsd,
    long? LastHeartbeatAtMs,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record AgentTokenSummary(
    Guid Id,
    Guid AgentId,
    string? Label,
    long? LastSeenAtMs,
    long? ExpiresAtMs,
    long CreatedAtMs);

public sealed record AddAgentToProjectRequest(Guid AgentId, bool? CanReadHistory);

public sealed record IssueTokenRequest(string? Label, int? LifetimeDays);

public sealed record AgentTokenIssuanceResponse(
    Guid TokenId,
    Guid AgentId,
    string JwtToken,
    string JwtJti,
    long ExpiresAtMs,
    string? Label);

/// <summary>
/// E2 §4 Agent Registry + Credentials + Tokens + Project Membership + Activity 上报。
/// </summary>
/// <remarks>
/// <para>
/// V1 简化：
/// <list type="bullet">
///   <item>Credentials AES-256-GCM 信封（<see cref="AesGcmCredentialCipher"/>）</item>
///   <item>单批准：project owner 邀请 agent 入项目（双批准留 V2）</item>
///   <item>Agent token = matk_<hex64>（DB 只存 SHA-256 hash，明文仅响应里返一次）</item>
///   <item>Activity 上报：lifecycle ≠ Active 时拒绝（E2 §5.4 业务规则）</item>
/// </list>
/// </para>
/// </remarks>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        MapCredentials(app);
        MapAgents(app);
        MapAgentTokens(app);
        MapAgentProjectMembership(app);
        MapAgentActivity(app);
    }

    // ─────────────────────────── Credentials ───────────────────────────

    private static void MapCredentials(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/credentials")
            .WithTags("Credentials")
            .RequireAuthorization();

        group.MapPost("", CreateCredentialAsync);
        group.MapGet("", ListCredentialsAsync);
        group.MapDelete("/{id:guid}", DeleteCredentialAsync);
    }

    private static async Task<IResult> CreateCredentialAsync(
        CreateCredentialRequest request,
        MateOSDbContext db,
        AesGcmCredentialCipher cipher,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        string? providerError = CredentialProvider.Validate(request.Provider);
        if (providerError is not null)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, providerError);
        }

        if (string.IsNullOrWhiteSpace(request.Label))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "label 不能为空");
        }

        if (string.IsNullOrEmpty(request.Secret))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "secret 不能为空");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid credentialId = Guid.NewGuid();
        byte[] envelope = cipher.Encrypt(request.Secret);

        var credential = new Credential
        {
            Id = credentialId,
            UserId = userId,
            Provider = request.Provider,
            Label = request.Label.Trim(),
            SecretEncrypted = envelope,
            Meta = request.Meta,
            CreatedAt = now,
        };

        db.Credentials.Add(credential);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.CredentialCreated,
            TargetType: "credential", TargetId: credentialId,
            Detail: new { provider = request.Provider, label = credential.Label }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/credentials/{credentialId}",
            new CredentialSummary(
                credential.Id, credential.Provider, credential.Label, credential.Meta,
                credential.LastUsedAt?.ToUnixTimeMilliseconds(),
                credential.CreatedAt.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> ListCredentialsAsync(
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        var rows = await db.Credentials
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.Provider,
                c.Label,
                c.Meta,
                c.LastUsedAt,
                c.CreatedAt,
            })
            .ToListAsync(ct);

        // F2：API 返回永不包含密文
        return Results.Ok(rows.Select(c => new CredentialSummary(
            c.Id, c.Provider, c.Label, c.Meta,
            c.LastUsedAt?.ToUnixTimeMilliseconds(),
            c.CreatedAt.ToUnixTimeMilliseconds())));
    }

    private static async Task<IResult> DeleteCredentialAsync(
        Guid id,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Credential? credential = await db.Credentials.FirstOrDefaultAsync(c => c.Id == id, ct);

        if (credential is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "credential 不存在");
        }

        if (credential.UserId != userId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有所有者可以删除 credential");
        }

        // F1 + 软约束：删除前检查是否有 agent 还在引用
        int referencingAgents = await db.Agents.CountAsync(a => a.CredentialId == id, ct);

        if (referencingAgents > 0)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"credential 被 {referencingAgents} 个 agent 引用，请先解除绑定");
        }

        db.Credentials.Remove(credential);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.CredentialDeleted,
            TargetType: "credential", TargetId: id));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ─────────────────────────────── Agents ───────────────────────────────

    private static void MapAgents(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/agents")
            .WithTags("Agents")
            .RequireAuthorization();

        group.MapPost("", CreateAgentAsync);
        group.MapGet("", ListAgentsAsync);
        group.MapGet("/{id:guid}", GetAgentAsync);
        group.MapPatch("/{id:guid}", UpdateAgentAsync);
        group.MapPost("/{id:guid}/pause", PauseAgentAsync);
        group.MapPost("/{id:guid}/activate", ActivateAgentAsync);
        group.MapPost("/{id:guid}/disable", DisableAgentAsync);
    }

    private static async Task<IResult> CreateAgentAsync(
        CreateAgentRequest request,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "agent 名称不能为空");
        }

        string? capabilityError = AgentCapability.Validate(request.Capabilities);

        if (capabilityError is not null)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, capabilityError);
        }

        // F1：credential 必须归属当前 user
        Credential? credential = await db.Credentials.FirstOrDefaultAsync(c => c.Id == request.CredentialId, ct);

        if (credential is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "credential 不存在");
        }

        if (credential.UserId != userId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "credential 不属于当前用户");
        }

        if (request.MaxConcurrency is { } mc && mc <= 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "max_concurrency 必须 > 0");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid agentId = Guid.NewGuid();

        var agent = new DbAgent
        {
            Id = agentId,
            OwnerUserId = userId,
            CredentialId = request.CredentialId,
            Name = request.Name.Trim(),
            Role = request.Role,
            Capabilities = JsonSerializer.Serialize(request.Capabilities),
            Lifecycle = AgentLifecycleMap.ActiveDbValue,
            Activity = AgentActivityMap.OfflineDbValue,
            MaxConcurrency = request.MaxConcurrency ?? 1,
            DailyLimitUsd = request.DailyLimitUsd ?? 5.00m,
            MonthlyBudgetUsd = request.MonthlyBudgetUsd ?? 50.00m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Agents.Add(agent);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.AgentCreated,
            TargetType: "agent", TargetId: agentId,
            Detail: new
            {
                name = agent.Name,
                role = agent.Role,
                capabilities = request.Capabilities,
                credential_id = request.CredentialId,
            }));

        await db.SaveChangesAsync(ct);

        return Results.Created($"/agents/{agentId}", ToSummary(agent));
    }

    private static async Task<IResult> ListAgentsAsync(
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        var rows = await db.Agents
            .Where(a => a.OwnerUserId == userId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(ToSummary));
    }

    private static async Task<IResult> GetAgentAsync(
        Guid id,
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);

        if (agent is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不存在");
        }

        if (agent.OwnerUserId != userId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有所有者可以查看 agent 详情");
        }

        return Results.Ok(ToSummary(agent));
    }

    private static async Task<IResult> UpdateAgentAsync(
        Guid id,
        UpdateAgentRequest request,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);

        if (agent is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不存在");
        }

        if (agent.OwnerUserId != userId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有所有者可以修改 agent");
        }

        if (request.Name is not null)
        {
            string newName = request.Name.Trim();

            if (newName.Length == 0)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "agent 名称不能为空");
            }

            agent.Name = newName;
        }

        if (request.Capabilities is not null)
        {
            string? capabilityError = AgentCapability.Validate(request.Capabilities);

            if (capabilityError is not null)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, capabilityError);
            }

            agent.Capabilities = JsonSerializer.Serialize(request.Capabilities);
        }

        if (request.CredentialId is { } newCredId && newCredId != agent.CredentialId)
        {
            Credential? credential = await db.Credentials.FirstOrDefaultAsync(c => c.Id == newCredId, ct);

            if (credential is null)
            {
                return ApiErrors.NotFoundResult(ApiErrors.NotFound, "credential 不存在");
            }

            if (credential.UserId != userId)
            {
                return ApiErrors.Forbidden(ApiErrors.NotOwner, "credential 不属于当前用户");
            }

            agent.CredentialId = newCredId;
        }

        if (request.MaxConcurrency is { } mc && mc > 0)
        {
            agent.MaxConcurrency = mc;
        }

        if (request.DailyLimitUsd is { } daily)
        {
            agent.DailyLimitUsd = daily;
        }

        if (request.MonthlyBudgetUsd is { } monthly)
        {
            agent.MonthlyBudgetUsd = monthly;
        }

        agent.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.AgentUpdated,
            TargetType: "agent", TargetId: id));

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToSummary(agent));
    }

    // ── Lifecycle 操作（E2 §5.3）──

    private static async Task<IResult> PauseAgentAsync(
        Guid id, MateOSDbContext db, AuditWriter audit, HttpContext http, CancellationToken ct)
        => await ChangeLifecycleAsync(id, AgentLifecycle.Paused, "lifecycle_paused", db, audit, http, ct);

    private static async Task<IResult> ActivateAgentAsync(
        Guid id, MateOSDbContext db, AuditWriter audit, HttpContext http, CancellationToken ct)
        => await ChangeLifecycleAsync(id, AgentLifecycle.Active, null, db, audit, http, ct);

    private static async Task<IResult> DisableAgentAsync(
        Guid id, MateOSDbContext db, AuditWriter audit, HttpContext http, CancellationToken ct)
        => await ChangeLifecycleAsync(id, AgentLifecycle.Disabled, "lifecycle_disabled", db, audit, http, ct);

    private static async Task<IResult> ChangeLifecycleAsync(
        Guid id,
        AgentLifecycle target,
        string? activityReason,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);

        if (agent is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不存在");
        }

        if (agent.OwnerUserId != userId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有所有者可以改变 lifecycle");
        }

        if (!AgentLifecycleMap.TryParse(agent.Lifecycle, out AgentLifecycle current))
        {
            current = AgentLifecycle.Active;
        }

        if (current == target)
        {
            return Results.NoContent();
        }

        agent.Lifecycle = target.ToDbValue();
        // E2 §5.3：lifecycle ≠ Active 时强制 activity = Offline
        agent.Activity = AgentActivityMap.OfflineDbValue;
        agent.ActivityReason = activityReason;
        agent.ActivityUpdatedAt = DateTimeOffset.UtcNow;
        agent.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.AgentLifecycleChanged,
            TargetType: "agent", TargetId: id,
            Detail: new { from = current.ToDbValue(), to = target.ToDbValue() }));

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToSummary(agent));
    }

    // ──────────────────────────── Agent Tokens ────────────────────────────

    private static void MapAgentTokens(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/agents/{id:guid}/tokens")
            .WithTags("AgentTokens")
            .RequireAuthorization();

        group.MapPost("", IssueTokenAsync);
        group.MapGet("", ListTokensAsync);
        group.MapDelete("/{tokenId:guid}", RevokeTokenAsync);
    }

    private static async Task<IResult> IssueTokenAsync(
        Guid id,
        IssueTokenRequest request,
        MateOSDbContext db,
        TokenService tokenService,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);

        if (agent is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不存在");
        }

        if (agent.OwnerUserId != userId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有所有者可以签发 agent token");
        }

        int lifetimeDays = Math.Clamp(request.LifetimeDays ?? 30, 1, 365);
        TimeSpan lifetime = TimeSpan.FromDays(lifetimeDays);

        // M3b 改造：agent_token 走 JWT（sub=agent_id, token_type=agent），与 user access JWT
        // 共享密钥 + 同一中间件；通过 token_type claim 区分上下文。
        AgentTokenResult jwt = tokenService.IssueAgentToken(agent.Id, lifetime);
        string hashHex = Domain.Agent.AgentToken.ComputeHashHex(jwt.AccessToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid tokenId = Guid.NewGuid();
        DateTimeOffset expiresAt = jwt.ExpiresAt;

        var agentToken = new DbAgentToken
        {
            Id = tokenId,
            AgentId = agent.Id,
            TokenHash = hashHex,
            Label = request.Label,
            ExpiresAt = expiresAt,
            Revoked = false,
            CreatedAt = now,
        };

        db.AgentTokens.Add(agentToken);

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/agents/{id}/tokens/{tokenId}",
            new AgentTokenIssuanceResponse(
                TokenId: tokenId,
                AgentId: agent.Id,
                JwtToken: jwt.AccessToken,
                JwtJti: jwt.Jti,
                ExpiresAtMs: expiresAt.ToUnixTimeMilliseconds(),
                Label: request.Label));
    }

    private static async Task<IResult> ListTokensAsync(
        Guid id,
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);

        if (agent is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不存在");
        }

        if (agent.OwnerUserId != userId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有所有者可以查看 agent token");
        }

        // F2：永不返回 token_hash
        var tokens = await db.AgentTokens
            .Where(t => t.AgentId == id)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new
            {
                t.Id,
                t.AgentId,
                t.Label,
                t.LastSeenAt,
                t.ExpiresAt,
                t.CreatedAt,
                t.Revoked,
            })
            .ToListAsync(ct);

        return Results.Ok(tokens.Select(t => new AgentTokenSummary(
            t.Id, t.AgentId, t.Label,
            t.LastSeenAt?.ToUnixTimeMilliseconds(),
            t.ExpiresAt?.ToUnixTimeMilliseconds(),
            t.CreatedAt.ToUnixTimeMilliseconds())));
    }

    private static async Task<IResult> RevokeTokenAsync(
        Guid id,
        Guid tokenId,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);

        if (agent is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不存在");
        }

        if (agent.OwnerUserId != userId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有所有者可以撤销 agent token");
        }

        DbAgentToken? token = await db.AgentTokens.FirstOrDefaultAsync(t => t.Id == tokenId && t.AgentId == id, ct);

        if (token is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "token 不存在");
        }

        if (token.Revoked)
        {
            return Results.NoContent();
        }

        token.Revoked = true;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.AgentTokenRevoked,
            TargetType: "agent_token", TargetId: tokenId,
            Detail: new { agent_id = id }));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ──────────────────────── Project Membership ────────────────────────

    private static void MapAgentProjectMembership(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/projects/{projectId:guid}/agents")
            .WithTags("AgentProjectMembership")
            .RequireAuthorization();

        group.MapGet("", ListProjectAgentsAsync);
        group.MapPost("", AddAgentToProjectAsync);
        group.MapDelete("/{agentId:guid}", RemoveAgentFromProjectAsync);
    }

    private static async Task<IResult> ListProjectAgentsAsync(
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

        var rows = await db.AgentProjectMembers
            .Where(m => m.ProjectId == projectId)
            .Join(db.Agents, m => m.AgentId, a => a.Id, (m, a) => new { m, a })
            .Select(x => new
            {
                x.a.Id,
                x.a.OwnerUserId,
                x.a.CredentialId,
                x.a.Name,
                x.a.Role,
                x.a.Capabilities,
                x.a.Lifecycle,
                x.a.Activity,
                x.a.ActivityReason,
                x.a.ActivityUpdatedAt,
                x.a.MaxConcurrency,
                x.a.DailyLimitUsd,
                x.a.MonthlyBudgetUsd,
                x.a.LastHeartbeatAt,
                x.a.CreatedAt,
                x.a.UpdatedAt,
                x.m.CanReadHistory,
                x.m.JoinedAt,
            })
            .ToListAsync(ct);

        return Results.Ok(rows.Select(x => new AgentSummary(
            x.Id, x.OwnerUserId, x.CredentialId, x.Name, x.Role,
            JsonSerializer.Deserialize<List<string>>(x.Capabilities) ?? new(),
            x.Lifecycle, x.Activity, x.ActivityReason,
            x.ActivityUpdatedAt?.ToUnixTimeMilliseconds(),
            x.MaxConcurrency, x.DailyLimitUsd, x.MonthlyBudgetUsd,
            x.LastHeartbeatAt?.ToUnixTimeMilliseconds(),
            x.CreatedAt.ToUnixTimeMilliseconds(),
            x.UpdatedAt.ToUnixTimeMilliseconds())));
    }

    private static async Task<IResult> AddAgentToProjectAsync(
        Guid projectId,
        AddAgentToProjectRequest request,
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
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以邀请 agent");
        }

        DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == request.AgentId, ct);

        if (agent is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不存在");
        }

        bool already = await db.AgentProjectMembers
            .AnyAsync(m => m.AgentId == request.AgentId && m.ProjectId == projectId, ct);

        if (already)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, "agent 已在该项目中");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.AgentProjectMembers.Add(new AgentProjectMember
        {
            AgentId = request.AgentId,
            ProjectId = projectId,
            InvitedBy = userId,
            CanReadHistory = request.CanReadHistory ?? false,
            JoinedAt = now,
        });

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.AgentAddedToProject,
            TargetType: "project_agent", TargetId: request.AgentId,
            Detail: new { project_id = projectId, agent_id = request.AgentId }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/projects/{projectId}/agents/{request.AgentId}",
            new { agent_id = request.AgentId, project_id = projectId, joined_at_ms = now.ToUnixTimeMilliseconds() });
    }

    private static async Task<IResult> RemoveAgentFromProjectAsync(
        Guid projectId,
        Guid agentId,
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
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以移除 agent");
        }

        AgentProjectMember? member = await db.AgentProjectMembers.FirstOrDefaultAsync(
            m => m.AgentId == agentId && m.ProjectId == projectId, ct);

        if (member is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不在该项目中");
        }

        db.AgentProjectMembers.Remove(member);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.AgentRemovedFromProject,
            TargetType: "project_agent", TargetId: agentId,
            Detail: new { project_id = projectId, agent_id = agentId }));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ──────────────────────────── Activity 上报 ────────────────────────────

    private static void MapAgentActivity(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/agents/{id:guid}/activity")
            .WithTags("AgentActivity")
            .RequireAuthorization();

        group.MapPost("", ReportActivityAsync);
    }

    private static async Task<IResult> ReportActivityAsync(
        Guid id,
        ReportActivityRequest request,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        // 两条合法主体：
        //  ① Agent 自己上报（agent_token，sub = agent_id）—— activity 的真实来源，
        //     Agent 进程自己最清楚「我在想 / 我在干 / 我空闲」；
        //  ② owner 代报（access token）—— 保留给调试与人工置位。
        // M3a 阶段只实现了 ②，于是 stub 一接上就会 403：它是 agent_token 主体，
        // 而 owner 校验只认 user id。
        Guid? tokenAgentId = http.GetAgentId();
        Guid? userId = http.GetUserId();

        if (tokenAgentId is null && userId is null)
        {
            return ApiErrors.Unauthorized(ApiErrors.InvalidToken, "需要 access_token 或 agent_token");
        }

        if (!AgentActivityMap.TryParse(request.Activity, out AgentActivity activity))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "activity 不合法");
        }

        DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);

        if (agent is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "agent 不存在");
        }

        if (tokenAgentId is { } selfAgentId)
        {
            if (selfAgentId != id)
            {
                return ApiErrors.Forbidden(ApiErrors.NotOwner, "agent_token 与路径 agentId 不一致");
            }
        }
        else if (agent.OwnerUserId != userId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有所有者或 agent 自己可以上报 activity");
        }

        if (!AgentLifecycleMap.TryParse(agent.Lifecycle, out AgentLifecycle lifecycle))
        {
            lifecycle = AgentLifecycle.Active;
        }

        string? transitionError = AgentLifecycleActivity.CanTransitionTo(lifecycle, activity);
        if (transitionError is not null)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, transitionError);
        }

        agent.Activity = activity.ToDbValue();
        agent.ActivityReason = request.Reason;
        agent.ActivityUpdatedAt = DateTimeOffset.UtcNow;
        agent.LastHeartbeatAt = DateTimeOffset.UtcNow;
        agent.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(http, new AuditEntry(
            tokenAgentId is not null ? AuditActorTypes.Agent : AuditActorTypes.User,
            tokenAgentId ?? userId!.Value,
            AuditActions.AgentActivityReported,
            TargetType: "agent", TargetId: id,
            Detail: new { activity = agent.Activity, reason = request.Reason }));

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToSummary(agent));
    }

    // ── helpers ──

    private static AgentSummary ToSummary(DbAgent a) => new(
        a.Id, a.OwnerUserId, a.CredentialId, a.Name, a.Role,
        JsonSerializer.Deserialize<List<string>>(a.Capabilities) ?? new(),
        a.Lifecycle, a.Activity, a.ActivityReason,
        a.ActivityUpdatedAt?.ToUnixTimeMilliseconds(),
        a.MaxConcurrency, a.DailyLimitUsd, a.MonthlyBudgetUsd,
        a.LastHeartbeatAt?.ToUnixTimeMilliseconds(),
        a.CreatedAt.ToUnixTimeMilliseconds(),
        a.UpdatedAt.ToUnixTimeMilliseconds());
}
