using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Persistence;
using MateOS.Api.Workspace;
using MateOS.Domain.Channel;
using MateOS.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Project = MateOS.Api.Persistence.Project;

namespace MateOS.Api.Channels;

public sealed record CreateChannelRequest(string Name, string? Description);

public sealed record UpdateChannelRequest(string? Name, string? Description);

public sealed record AddChannelMemberRequest(string MemberType, Guid MemberId, string? Role);

public sealed record PostMessageRequest(
    string ContentType,
    JsonElement Content,
    Guid? ClientMsgId,
    long? ParentSeq);

public sealed record PresignAttachmentRequest(string FileName, string? MimeType, long SizeBytes);

public sealed record ChannelSummary(
    Guid Id,
    Guid ProjectId,
    string Name,
    string? Description,
    long LastSeq,
    long? ArchivedAtMs,
    string Role,
    long CreatedAtMs);

public sealed record ChannelMemberSummary(
    string MemberType,
    Guid MemberId,
    string Role,
    long JoinedAtMs);

public sealed record MessageSummary(
    Guid Id,
    long Seq,
    string SenderType,
    Guid? SenderId,
    string ContentType,
    JsonElement Content,
    long? ParentSeq,
    Guid? ClientMsgId,
    long CreatedAtMs);

public sealed record MessagesPage(long LastSeq, IReadOnlyList<MessageSummary> Messages);

public sealed record AttachmentSummary(
    Guid Id,
    string S3Key,
    string FileName,
    string? MimeType,
    long? SizeBytes,
    string Status,
    long CreatedAtMs);

public sealed record PresignAttachmentResponse(
    Guid AttachmentId,
    string S3Key,
    string UploadUrl,
    DateTimeOffset ExpiresAt);

/// <summary>
/// E3 §4 的 <c>/projects/:pid/channels</c> · <c>/channels/:id</c> · <c>/channels/:id/messages</c> · <c>/attachments/presign</c>。
/// </summary>
/// <remarks>
/// <para>
/// V1 阶段权限：所有 channel 操作都走 <see cref="WorkspaceAuthorizer"/> 取得的
/// project 角色（<c>channel_members</c> 表是 V1 数据模型，但访问控制简化到 project 层，
/// 完整 channel 维度权限等 E6 Permission 落地后收紧）。
/// </para>
/// <para>
/// seq 分配走 SQL 事务：<c>UPDATE channel_seq_counters SET next_seq = next_seq + 1
/// WHERE channel_id = ? RETURNING next_seq - 1</c>，与 message 写入同事务提交。
/// 客户端幂等：<c>client_msg_id</c> 命中已存在行则返该行（不分配新 seq）。
/// </para>
/// </remarks>
public static class ChannelEndpoints
{
    public static void MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        MapProjectChannels(app);
        MapChannels(app);
        MapChannelMembers(app);
        MapChannelMessages(app);
        MapAttachments(app);
    }

    // ───────────────────────── Project 下的 Channel ─────────────────────────

    private static void MapProjectChannels(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/projects/{projectId:guid}/channels")
            .WithTags("Channels")
            .RequireAuthorization();

        group.MapPost("", CreateChannelAsync);
        group.MapGet("", ListProjectChannelsAsync);
    }

    private static async Task<IResult> CreateChannelAsync(
        Guid projectId,
        CreateChannelRequest request,
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
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "channel 名称不能为空");
        }

        if (name.Length > 64)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "channel 名称不能超过 64 字符");
        }

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

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid channelId = Guid.NewGuid();

        var channel = new Channel
        {
            Id = channelId,
            ProjectId = projectId,
            Name = name,
            Description = request.Description?.Trim(),
            LastSeq = 0,
            CreatedAt = now,
        };

        var counter = new ChannelSeqCounter
        {
            ChannelId = channelId,
            NextSeq = ChannelSeq.FirstSeq,
        };

        // 创建者自动成为 channel owner（E3 §2.1）
        var creatorMember = new ChannelMember
        {
            ChannelId = channelId,
            MemberType = ChannelMemberTypeMap.HumanDbValue,
            MemberId = userId,
            Role = ChannelMemberRoleMap.OwnerDbValue,
            JoinedAt = now,
        };

        db.Channels.Add(channel);
        db.ChannelSeqCounters.Add(counter);
        db.ChannelMembers.Add(creatorMember);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.ChannelCreated,
            TargetType: "channel", TargetId: channelId,
            Detail: new { project_id = projectId, name }));

        await db.SaveChangesAsync(ct);

        string role = roles.IsProjectOwner
            ? MembershipRoleMap.OwnerDbValue
            : MembershipRoleMap.MemberDbValue;

        return Results.Created(
            $"/channels/{channelId}",
            new ChannelSummary(channel.Id, channel.ProjectId, channel.Name, channel.Description,
                channel.LastSeq, channel.ArchivedAt?.ToUnixTimeMilliseconds(), role,
                channel.CreatedAt.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> ListProjectChannelsAsync(
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

        // V1 简化：返回 project 下全部 active channel（含 private），不做 channel 维度权限过滤
        // （E6 落地后这里再收紧为「只返我在 channel_members 表里有的」）
        var rows = await db.Channels
            .Where(c => c.ProjectId == projectId && c.DeletedAt == null)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.ProjectId,
                c.Name,
                c.Description,
                c.LastSeq,
                c.ArchivedAt,
                c.CreatedAt,
            })
            .ToListAsync(ct);

        string myRole = roles.IsProjectOwner
            ? MembershipRoleMap.OwnerDbValue
            : MembershipRoleMap.MemberDbValue;

        return Results.Ok(rows.Select(c => new ChannelSummary(
            c.Id, c.ProjectId, c.Name, c.Description, c.LastSeq,
            c.ArchivedAt?.ToUnixTimeMilliseconds(),
            myRole,
            c.CreatedAt.ToUnixTimeMilliseconds())));
    }

    // ───────────────────────────── 单个 Channel ─────────────────────────────

    private static void MapChannels(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/channels/{id:guid}")
            .WithTags("Channels")
            .RequireAuthorization();

        group.MapGet("", GetChannelAsync);
        group.MapPatch("", UpdateChannelAsync);
        group.MapDelete("", ArchiveChannelAsync);
    }

    private static async Task<IResult> GetChannelAsync(
        Guid id,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct);

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

        string myRole = roles.IsProjectOwner
            ? MembershipRoleMap.OwnerDbValue
            : MembershipRoleMap.MemberDbValue;

        return Results.Ok(new ChannelSummary(
            channel.Id, channel.ProjectId, channel.Name, channel.Description, channel.LastSeq,
            channel.ArchivedAt?.ToUnixTimeMilliseconds(), myRole,
            channel.CreatedAt.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> UpdateChannelAsync(
        Guid id,
        UpdateChannelRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct);

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
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以修改 channel");
        }

        string? name = request.Name?.Trim();

        if (name is not null)
        {
            if (name.Length == 0)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "channel 名称不能为空");
            }

            if (name.Length > 64)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "channel 名称不能超过 64 字符");
            }

            channel.Name = name;
        }

        if (request.Description is not null)
        {
            channel.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        }

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.ChannelUpdated,
            TargetType: "channel", TargetId: id));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ArchiveChannelAsync(
        Guid id,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct);

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
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以归档 channel");
        }

        // V1 用软删（archived_at）保留消息完整性；E6 之后再考虑硬删
        channel.ArchivedAt = DateTimeOffset.UtcNow;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.ChannelArchived,
            TargetType: "channel", TargetId: id));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ──────────────────────────── Channel 成员 ────────────────────────────

    private static void MapChannelMembers(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/channels/{id:guid}/members")
            .WithTags("Channels")
            .RequireAuthorization();

        group.MapGet("", ListChannelMembersAsync);
        group.MapPost("", AddChannelMemberAsync);
        group.MapDelete("/{memberType}/{memberId:guid}", RemoveChannelMemberAsync);
    }

    private static async Task<IResult> ListChannelMembersAsync(
        Guid id,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct);

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

        var members = await db.ChannelMembers
            .Where(m => m.ChannelId == id)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new
            {
                m.MemberType,
                m.MemberId,
                m.Role,
                m.JoinedAt,
            })
            .ToListAsync(ct);

        return Results.Ok(members.Select(m => new ChannelMemberSummary(
            m.MemberType, m.MemberId, m.Role, m.JoinedAt.ToUnixTimeMilliseconds())));
    }

    private static async Task<IResult> AddChannelMemberAsync(
        Guid id,
        AddChannelMemberRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct);

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
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以管理 channel 成员");
        }

        if (!ChannelMemberTypeMap.TryParse(request.MemberType, out ChannelMemberType memberType))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "member_type 只能是 HUMAN 或 AGENT");
        }

        string? roleInput = string.IsNullOrWhiteSpace(request.Role)
            ? ChannelMemberRoleMap.MemberDbValue
            : request.Role.Trim().ToLowerInvariant();

        if (!ChannelMemberRoleMap.TryParse(roleInput, out ChannelMemberRole memberRole))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "role 只能是 owner 或 member");
        }

        // HUMAN 必须是 project member（防止幽灵成员）；AGENT 暂不校验（待 E2 上线后加）
        if (memberType is ChannelMemberType.Human)
        {
            bool isProjectMember = await db.ProjectMembers
                .AnyAsync(pm => pm.ProjectId == channel.ProjectId && pm.UserId == request.MemberId, ct);

            if (!isProjectMember)
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                    "被邀请的 HUMAN 必须是该项目的成员");
            }
        }

        bool alreadyMember = await db.ChannelMembers
            .AnyAsync(m => m.ChannelId == id && m.MemberType == memberType.ToDbValue()
                && m.MemberId == request.MemberId, ct);

        if (alreadyMember)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, "该成员已在 channel 内");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        db.ChannelMembers.Add(new ChannelMember
        {
            ChannelId = id,
            MemberType = memberType.ToDbValue(),
            MemberId = request.MemberId,
            Role = memberRole.ToDbValue(),
            JoinedAt = now,
        });

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.ChannelMemberAdded,
            TargetType: "channel", TargetId: id,
            Detail: new { member_type = memberType.ToDbValue(), member_id = request.MemberId, role = memberRole.ToDbValue() }));

        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/channels/{id}/members/{memberType.ToDbValue()}/{request.MemberId}",
            new ChannelMemberSummary(
                memberType.ToDbValue(), request.MemberId, memberRole.ToDbValue(),
                now.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> RemoveChannelMemberAsync(
        Guid id,
        string memberType,
        Guid memberId,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct);

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

        // 自己退自己 OR project owner 踢人
        bool isSelf = string.Equals(memberType, ChannelMemberTypeMap.HumanDbValue, StringComparison.Ordinal)
                      && memberId == userId;

        if (!isSelf && !roles.CanManageProject)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以移除其他成员");
        }

        if (!ChannelMemberTypeMap.TryParse(memberType, out ChannelMemberType parsedType))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "member_type 只能是 HUMAN 或 AGENT");
        }

        ChannelMember? member = await db.ChannelMembers.FirstOrDefaultAsync(
            m => m.ChannelId == id && m.MemberType == parsedType.ToDbValue() && m.MemberId == memberId,
            ct);

        if (member is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "该成员不在 channel 内");
        }

        // 不能移除最后一个 owner
        if (member.Role == ChannelMemberRoleMap.OwnerDbValue)
        {
            int ownerCount = await db.ChannelMembers
                .CountAsync(m => m.ChannelId == id && m.Role == ChannelMemberRoleMap.OwnerDbValue, ct);

            if (ownerCount <= 1)
            {
                return ApiErrors.ConflictResult(ApiErrors.Conflict, "不能移除 channel 的最后一个 owner");
            }
        }

        db.ChannelMembers.Remove(member);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.ChannelMemberRemoved,
            TargetType: "channel", TargetId: id,
            Detail: new { member_type = parsedType.ToDbValue(), member_id = memberId }));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ──────────────────────────── Messages ────────────────────────────

    private static void MapChannelMessages(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/channels/{id:guid}/messages")
            .WithTags("Messages")
            .RequireAuthorization();

        group.MapGet("", ListMessagesAsync);
        group.MapPost("", PostMessageAsync);
        group.MapDelete("/{seq:long}", DeleteMessageAsync);
    }

    private static async Task<IResult> ListMessagesAsync(
        Guid id,
        long? since_seq,
        int? limit,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        string? sinceSeqError = ChannelSeq.ValidateSinceSeq(since_seq);
        if (sinceSeqError is not null)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, sinceSeqError);
        }

        string? limitError = ChannelSeq.ValidateLimit(limit);
        if (limitError is not null)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, limitError);
        }

        int effectiveLimit = limit ?? 50;

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct);

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

        long startSeq = since_seq.HasValue ? ChannelSeq.NextSeqToReturn(since_seq.Value) : ChannelSeq.FirstSeq;

        var rows = await db.Messages
            .Where(m => m.ChannelId == id && m.DeletedAt == null && m.Seq >= startSeq)
            .OrderBy(m => m.Seq)
            .Take(effectiveLimit)
            .Select(m => new
            {
                m.Id,
                m.Seq,
                m.SenderType,
                m.SenderId,
                m.ContentType,
                m.Content,
                m.ParentSeq,
                m.ClientMsgId,
                m.CreatedAt,
            })
            .ToListAsync(ct);

        var messages = rows.Select(m => new MessageSummary(
            m.Id,
            m.Seq,
            m.SenderType,
            m.SenderId,
            m.ContentType,
            JsonDocument.Parse(m.Content).RootElement,
            m.ParentSeq,
            m.ClientMsgId,
            m.CreatedAt.ToUnixTimeMilliseconds())).ToList();

        return Results.Ok(new MessagesPage(channel.LastSeq, messages));
    }

    private static async Task<IResult> PostMessageAsync(
        Guid id,
        PostMessageRequest request,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        WsSender wsSender,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (!MessageContentTypeMap.TryParse(request.ContentType, out MessageContentType contentType))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "content_type 必须是 HUMAN / DECISION / AGENT_OUTPUT / SYSTEM / MEMORY_REQUEST");
        }

        // V1 简化：只允许人类在 channel 发 HUMAN 形态；其他 3 形态由各自域（E4/E5/E7）写入
        // SYSTEM 由 SERVER actor 写入（V1 暂不开）
        if (contentType is not MessageContentType.Human)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                $"V1 阶段人类只能发 HUMAN 形态消息（{request.ContentType} 由对应业务域写入）");
        }

        string? validationError = MessageProjection.Validate(contentType, request.Content);
        if (validationError is not null)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, validationError);
        }

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct);

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

        // F2 客户端幂等：同 client_msg_id 已存在则返该行（不分配新 seq）
        if (request.ClientMsgId is { } clientMsgId)
        {
            Message? existing = await db.Messages
                .FirstOrDefaultAsync(m => m.ChannelId == id && m.ClientMsgId == clientMsgId && m.DeletedAt == null, ct);

            if (existing is not null)
            {
                return Results.Ok(new MessageSummary(
                    existing.Id, existing.Seq, existing.SenderType, existing.SenderId,
                    existing.ContentType,
                    JsonDocument.Parse(existing.Content).RootElement,
                    existing.ParentSeq, existing.ClientMsgId,
                    existing.CreatedAt.ToUnixTimeMilliseconds()));
            }
        }

        // seq 分配：UPDATE channel_seq_counters ... RETURNING next_seq - 1
        // 用 raw SQL 走事务（EF SaveChanges 内联执行），与 message INSERT 同事务
        long allocatedSeq = await AllocateSeqAsync(db, id, ct);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid messageId = Guid.NewGuid();
        string contentJson = request.Content.GetRawText();

        var message = new Message
        {
            Id = messageId,
            ChannelId = id,
            Seq = allocatedSeq,
            SenderType = MessageSenderTypeMap.HumanDbValue,
            SenderId = userId,
            ContentType = MessageContentTypeMap.HumanDbValue,
            Content = contentJson,
            ParentSeq = request.ParentSeq,
            ClientMsgId = request.ClientMsgId,
            TraceId = http.GetTraceId(),
            CreatedAt = now,
        };

        db.Messages.Add(message);

        // 同步更新 channels.last_seq
        channel.LastSeq = Math.Max(channel.LastSeq, allocatedSeq);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.MessagePosted,
            TargetType: "message", TargetId: messageId,
            Detail: new { channel_id = id, seq = allocatedSeq, content_type = MessageContentTypeMap.HumanDbValue }));

        await db.SaveChangesAsync(ct);

        // 推 message.created 给该 channel 的全部 WS 订阅者（E3 §4.4）
        // 失败不阻塞 POST：HTTP 已 201，WS 漏推靠客户端的 resume(last_seq) 兜底
        try
        {
            await wsSender.BroadcastToChannelAsync(id, new
            {
                id = Guid.NewGuid().ToString("N"),
                type = WsMessageTypeMap.MessageCreatedValue,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payload = new
                {
                    channel_id = id,
                    message = new
                    {
                        message.Id,
                        message.Seq,
                        message.SenderType,
                        message.SenderId,
                        message.ContentType,
                        content = JsonDocument.Parse(message.Content).RootElement,
                        message.ParentSeq,
                        message.ClientMsgId,
                        message.CreatedAt,
                    },
                },
            }, ct);
        }
        catch (Exception ex)
        {
            // 推送失败不能让 HTTP 请求失败
            // （数据库已落库，客户端可走 GET since_seq= 兜底）
        }

        return Results.Created(
            $"/channels/{id}/messages/{allocatedSeq}",
            new MessageSummary(
                message.Id, message.Seq, message.SenderType, message.SenderId,
                message.ContentType,
                JsonDocument.Parse(message.Content).RootElement,
                message.ParentSeq, message.ClientMsgId,
                message.CreatedAt.ToUnixTimeMilliseconds()));
    }

    private static async Task<IResult> DeleteMessageAsync(
        Guid id,
        long seq,
        MateOSDbContext db,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Channel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null, ct);

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
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以删除消息");
        }

        Message? message = await db.Messages.FirstOrDefaultAsync(
            m => m.ChannelId == id && m.Seq == seq && m.DeletedAt == null, ct);

        if (message is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "消息不存在");
        }

        message.DeletedAt = DateTimeOffset.UtcNow;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.MessageDeleted,
            TargetType: "message", TargetId: message.Id,
            Detail: new { channel_id = id, seq }));

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>
    /// 事务内原子分配 seq（E3 §3 channel_seq_counters）。
    /// </summary>
    private static async Task<long> AllocateSeqAsync(MateOSDbContext db, Guid channelId, CancellationToken ct)
    {
        // EnsureCreated for new channel 由 CreateChannelAsync 走 SaveChanges 提交
        // 但 seq 分配必须 in-transaction：先 SELECT FOR UPDATE，再 UPDATE + INSERT message
        long nextSeq = await db.Database
            .SqlQuery<long>(
                $@"UPDATE channel_seq_counters
                   SET next_seq = next_seq + 1
                   WHERE channel_id = {channelId}
                   RETURNING next_seq - 1")
            .SingleAsync(ct);

        return nextSeq;
    }

    // ──────────────────────────── Attachments ────────────────────────────

    private static void MapAttachments(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/attachments")
            .WithTags("Attachments")
            .RequireAuthorization();

        group.MapPost("/presign", PresignAttachmentAsync);
        group.MapPost("/{id:guid}/confirm", ConfirmAttachmentAsync);
    }

    private static async Task<IResult> PresignAttachmentAsync(
        PresignAttachmentRequest request,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "file_name 不能为空");
        }

        if (request.SizeBytes <= 0)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "size_bytes 必须为正数");
        }

        // V1 简化：50 MB 上限（待 V2 接入 S3 SDK 改为服务端策略）
        const long MaxSizeBytes = 50L * 1024 * 1024;
        if (request.SizeBytes > MaxSizeBytes)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, $"附件超过 {MaxSizeBytes} 字节上限");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid attachmentId = Guid.NewGuid();
        string s3Key = $"attachments/{userId:N}/{attachmentId:N}/{request.FileName}";

        db.Attachments.Add(new Attachment
        {
            Id = attachmentId,
            UploaderType = MessageSenderTypeMap.HumanDbValue,
            UploaderId = userId,
            S3Key = s3Key,
            Status = "PRESIGNED",
            FileName = request.FileName,
            MimeType = request.MimeType,
            SizeBytes = request.SizeBytes,
            RefCount = 0,
            CreatedAt = now,
        });

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.AttachmentPresigned,
            TargetType: "attachment", TargetId: attachmentId,
            Detail: new { file_name = request.FileName, size_bytes = request.SizeBytes }));

        await db.SaveChangesAsync(ct);

        // V1 stub：上传 URL 直接指向本地 API（真实 S3 走 S3 SDK 生成 presigned URL）
        // 这是占位实现，等 S3 SDK 接入时改回真 presign
        string uploadUrl = $"/attachments/{attachmentId}/upload-stub";
        DateTimeOffset expiresAt = now.AddHours(24);

        return Results.Created(
            $"/attachments/{attachmentId}",
            new PresignAttachmentResponse(attachmentId, s3Key, uploadUrl, expiresAt));
    }

    private static async Task<IResult> ConfirmAttachmentAsync(
        Guid id,
        MateOSDbContext db,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        Attachment? attachment = await db.Attachments.FirstOrDefaultAsync(a => a.Id == id, ct);

        if (attachment is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "附件不存在");
        }

        if (attachment.UploaderId != userId)
        {
            return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有上传者可以确认附件");
        }

        if (attachment.Status != "PRESIGNED")
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"附件状态 {attachment.Status}，无法再 confirm");
        }

        attachment.Status = "UPLOADED";
        attachment.ConfirmedAt = DateTimeOffset.UtcNow;

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.AttachmentConfirmed,
            TargetType: "attachment", TargetId: id));

        await db.SaveChangesAsync(ct);

        return Results.Ok(new AttachmentSummary(
            attachment.Id, attachment.S3Key, attachment.FileName, attachment.MimeType,
            attachment.SizeBytes, attachment.Status, attachment.CreatedAt.ToUnixTimeMilliseconds()));
    }
}
