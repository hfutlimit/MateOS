using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Channels;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Persistence;
using MateOS.Api.Workspace;
using MateOS.Domain.Agent;
using MateOS.Domain.Channel;
using MateOS.Domain.Identity;
using MateOS.Domain.Memory;
using Microsoft.EntityFrameworkCore;
using Project = MateOS.Api.Persistence.Project;
using DbMemoryProposal = MateOS.Api.Persistence.MemoryProposal;
using DbMemoryItem = MateOS.Api.Persistence.MemoryItem;
using DbMessage = MateOS.Api.Persistence.Message;
using DbChannel = MateOS.Api.Persistence.Channel;

namespace MateOS.Api.Memory;

public sealed record ProposeMemoryRequest(
    string Type,
    string Title,
    string Content,
    string SourceType,
    Guid? SourceChannelId,
    long? SourceMessageSeq,
    Guid? SourceMessageId,
    Guid? ProposedByAgentId,
    string IdempotencyKey);

public sealed record ApproveMemoryRequest(string? Note);

public sealed record RejectMemoryRequest(string Reason);

public sealed record MemoryProposalSummary(
    Guid Id,
    Guid? ProjectId,
    string Type,
    string Title,
    string Content,
    string Status,
    string SourceType,
    Guid? SourceChannelId,
    long? SourceMessageSeq,
    Guid? SourceMessageId,
    Guid? ProposedByUserId,
    Guid? ProposedByAgentId,
    string IdempotencyKey,
    long CreatedAtMs,
    long? ApprovedAtMs,
    string? RejectReason);

public sealed record MemoryItemSummary(
    Guid Id,
    Guid ProposalId,
    Guid? ProjectId,
    string Type,
    string Title,
    string Content,
    string SourceType,
    Guid? SourceChannelId,
    long? SourceMessageSeq,
    Guid ApprovedBy,
    int Version,
    long CreatedAtMs,
    long UpdatedAtMs);

/// <summary>
/// E5 §3 Memory Proposal + Item CRUD + 全文检索（V1 简化：GIN tsvector；V2 升 pgvector）。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Source 三件套：CHANNEL_MESSAGE 必填 channel_id + seq（DB CHECK 兜底）</item>
///   <item>scope_target 不变量：PERSONAL 不挂 project；其他必须挂 project</item>
///   <item>PERSONAL：仅 user owner 申请</item>
///   <item>approve 时 E5 §3 转 memory_items（V1 stub：直接复制 content；V2 加 embedding）</item>
///   <item>search 用 to_tsvector GIN 索引</item>
/// </list>
/// </remarks>
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/memory")
            .WithTags("Memory")
            .RequireAuthorization();

        group.MapPost("/proposals", ProposeAsync);
        group.MapGet("/proposals", ListProposalsAsync);
        group.MapPost("/proposals/{proposalId:guid}/approve", ApproveAsync);
        group.MapPost("/proposals/{proposalId:guid}/reject", RejectAsync);
        group.MapGet("/items", ListItemsAsync);
        group.MapGet("/search", SearchAsync);
    }

    // ───────────────────────── Propose ─────────────────────────

    private static async Task<IResult> ProposeAsync(
        ProposeMemoryRequest request,
        MateOSDbContext db,
        WsSender wsSender,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (!MemoryTypeMap.TryParse(request.Type, out MemoryType type))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "type 必须是 PERSONAL / PROJECT / DECISION / KNOWLEDGE");
        }

        if (!MemorySourceTypeMap.TryParse(request.SourceType, out MemorySourceType sourceType))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "source_type 非法");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "title 不能为空");
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "content 不能为空");
        }

        if (request.Content.Length > 64 * 1024)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "content 超过 64KB 上限");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "idempotency_key 不能为空");
        }

        // Source 三件套
        string? sourceError = MemoryProposalInvariant.ValidateSource(
            sourceType, request.SourceChannelId, request.SourceMessageSeq);
        if (sourceError is not null)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, sourceError);
        }

        // 解析 proposed_by_agent_id 与 project_id 关系
        Guid? proposerUserId = userId;
        Guid? proposerAgentId = null;

        if (request.ProposedByAgentId is { } aid)
        {
            // 必须有 propose_memory 权限（V1 简化：仅校验 agent 属于 user）
            bool isMyAgent = await db.Agents
                .AnyAsync(a => a.Id == aid && a.OwnerUserId == userId, ct);

            if (!isMyAgent)
            {
                return ApiErrors.Forbidden(ApiErrors.NotOwner, "agent 不属于当前用户");
            }

            // V1 简化：agent 申请时仍记 user 为申请人（audit 主体）
            proposerAgentId = aid;
        }

        // scope_target 不变量
        string? scopeError = MemoryProposalInvariant.ValidateScopeTarget(
            type, request.SourceChannelId is { } ? null : null, proposerUserId, proposerAgentId);
        // 注：项目 ID 暂从 SourceChannelId 推导（V1 简化：source 是 channel 时取 channel.project_id）
        Guid? projectId = null;
        if (request.SourceChannelId is { } chId)
        {
            projectId = await db.Channels
                .Where(c => c.Id == chId)
                .Select(c => (Guid?)c.ProjectId)
                .FirstOrDefaultAsync(ct);
        }
        if (projectId is null && type is not MemoryType.PERSONAL)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed,
                "type ≠ PERSONAL 必须有 source_channel_id（用于推导 project_id）");
        }

        // 重新跑一次 scope_target 校验（这次 projectId 已知）
        scopeError = MemoryProposalInvariant.ValidateScopeTarget(
            type, projectId, proposerUserId, proposerAgentId);
        if (scopeError is not null)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, scopeError);
        }

        // 幂等
        DbMemoryProposal? existing = await db.MemoryProposals
            .FirstOrDefaultAsync(p => p.IdempotencyKey == request.IdempotencyKey, ct);

        if (existing is not null)
        {
            return Results.Ok(ToProposalSummary(existing, idempotent: true));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid proposalId = Guid.NewGuid();

        var proposal = new DbMemoryProposal
        {
            Id = proposalId,
            ProjectId = projectId,
            Type = type.ToDbValue(),
            Title = request.Title.Trim(),
            Content = request.Content,
            Status = MemoryStatusMap.ProposedValue,
            SourceType = sourceType.ToDbValue(),
            SourceChannelId = request.SourceChannelId,
            SourceMessageSeq = request.SourceMessageSeq,
            SourceMessageId = request.SourceMessageId,
            ProposedByUserId = proposerUserId,
            ProposedByAgentId = proposerAgentId,
            IdempotencyKey = request.IdempotencyKey,
            CreatedAt = now,
        };

        db.MemoryProposals.Add(proposal);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.MemoryProposalCreated,
            TargetType: "memory_proposal", TargetId: proposalId,
            Detail: new
            {
                type = type.ToDbValue(),
                source_type = sourceType.ToDbValue(),
                project_id = projectId,
            }));

        await db.SaveChangesAsync(ct);

        // T+5（detailed/05 §3.3）：写消息流 MEMORY_REQUEST 投影 + WS 推 message.created
        // V1 简化：仅 source_channel_id 非空时（project memory）写；PERSONAL 无 channel 跳过
        if (proposal.SourceChannelId is { } srcChannelId)
        {
            long proposalMessageSeq = await AllocateChannelSeqAsync(db, srcChannelId, ct);
            string proposalContentJson = JsonSerializer.Serialize(new
            {
                memory_proposal_ref = proposalId,
            });
            DateTimeOffset proposalMsgNow = DateTimeOffset.UtcNow;

            var proposalMessage = new DbMessage
            {
                Id = Guid.NewGuid(),
                ChannelId = srcChannelId,
                Seq = proposalMessageSeq,
                SenderType = MessageSenderTypeMap.HumanDbValue,
                SenderId = proposerUserId,
                ContentType = MessageContentTypeMap.MemoryRequestDbValue,
                Content = proposalContentJson,
                Mentions = null,
                ParentSeq = null,
                ClientMsgId = null,
                TraceId = http.GetTraceId(),
                CreatedAt = proposalMsgNow,
            };

            db.Messages.Add(proposalMessage);
            await db.SaveChangesAsync(ct);

            await wsSender.BroadcastToChannelAsync(srcChannelId, new
            {
                id = Guid.NewGuid().ToString("N"),
                type = WsMessageTypeMap.MessageCreatedValue,
                ts = proposalMsgNow.ToUnixTimeMilliseconds(),
                payload = new
                {
                    channel_id = srcChannelId,
                    message = new
                    {
                        proposalMessage.Id,
                        proposalMessage.Seq,
                        proposalMessage.SenderType,
                        proposalMessage.SenderId,
                        proposalMessage.ContentType,
                        content = JsonDocument.Parse(proposalMessage.Content).RootElement,
                        proposalMessage.ParentSeq,
                        proposalMessage.ClientMsgId,
                        proposalMessage.CreatedAt,
                    },
                },
            }, ct);
        }

        return Results.Created($"/memory/proposals/{proposalId}", ToProposalSummary(proposal, idempotent: false));
    }

    /// <summary>
    /// 事务内原子分配 channel seq（与 M2 Channel 投影保持同口径；F2 走 UPDATE ... RETURNING）。
    /// </summary>
    private static async Task<long> AllocateChannelSeqAsync(
        MateOSDbContext db, Guid channelId, CancellationToken ct)
    {
        long nextSeq = await db.Database
            .SqlQuery<long>(
                $@"UPDATE channel_seq_counters
                   SET next_seq = next_seq + 1
                   WHERE channel_id = {channelId}
                   RETURNING next_seq - 1")
            .SingleAsync(ct);

        return nextSeq;
    }

    // ───────────────────────── List proposals ─────────────────────────

    private static async Task<IResult> ListProposalsAsync(
        string? status,
        Guid? project_id,
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        MemoryStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!MemoryStatusMap.TryParse(status, out MemoryStatus s))
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "status 非法");
            }

            statusFilter = s;
        }

        var query = db.MemoryProposals.AsQueryable();

        // PERSONAL：仅自己看；其他：project member 可看
        query = query.Where(p => p.Type != MemoryTypeMap.PersonalValue
            || p.ProposedByUserId == userId);

        if (statusFilter.HasValue)
        {
            query = query.Where(p => p.Status == statusFilter.Value.ToDbValue());
        }

        if (project_id is { } pid)
        {
            // 必须是 project member
            bool isMember = await db.ProjectMembers
                .AnyAsync(pm => pm.ProjectId == pid && pm.UserId == userId, ct);

            if (!isMember)
            {
                return ApiErrors.Forbidden(ApiErrors.NotMember, "不是项目成员");
            }

            query = query.Where(p => p.ProjectId == pid);
        }

        var rows = await query.OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(ct);
        return Results.Ok(rows.Select(p => ToProposalSummary(p, idempotent: false)));
    }

    // ───────────────────────── Approve ─────────────────────────

    private static async Task<IResult> ApproveAsync(
        Guid proposalId,
        ApproveMemoryRequest request,
        MateOSDbContext db,
        WsSender wsSender,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        DbMemoryProposal? proposal = await db.MemoryProposals
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct);

        if (proposal is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "memory_proposal 不存在");
        }

        if (!MemoryStatusMap.TryParse(proposal.Status, out MemoryStatus current))
        {
            current = MemoryStatus.PROPOSED;
        }

        if (current != MemoryStatus.PROPOSED)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"memory_proposal 已处于 {proposal.Status} 终态");
        }

        // 权限校验：project_owner 批准（V1 简化）
        if (proposal.ProjectId is { } pid)
        {
            Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == pid, ct);

            if (project is null)
            {
                return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
            }

            WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

            if (!roles.CanManageProject)
            {
                return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以批准 memory");
            }
        }
        else
        {
            // PERSONAL：申请人本人可批准（自己的私人记忆）
            if (proposal.ProposedByUserId != userId)
            {
                return ApiErrors.Forbidden(ApiErrors.NotOwner, "PERSONAL memory 只能由申请人本人批准");
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // CAS：状态从 PROPOSED → APPROVED
        int rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE memory_proposals
               SET status = {MemoryStatusMap.ApprovedValue},
                   approved_by = {userId},
                   approved_at = {now}
               WHERE id = {proposalId} AND status = {MemoryStatusMap.ProposedValue}", ct);

        if (rows == 0)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, "proposal 已被其他操作覆盖");
        }

        // 写 memory_items（proposal_id UNIQUE 保证 v0.4.3 强约束）
        // search_text：title + content 合并（V1 LIKE/全文检索用）
        string searchText = $"{proposal.Title} {proposal.Content}";

        var item = new DbMemoryItem
        {
            Id = Guid.NewGuid(),
            ProposalId = proposalId,
            ProjectId = proposal.ProjectId,
            OwnerUserId = proposal.Type == MemoryTypeMap.PersonalValue ? proposal.ProposedByUserId : null,
            Type = proposal.Type,
            Title = proposal.Title,
            Content = proposal.Content,
            SearchText = searchText,
            SourceType = proposal.SourceType,
            SourceChannelId = proposal.SourceChannelId,
            SourceMessageSeq = proposal.SourceMessageSeq,
            ApprovedBy = userId,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MemoryItems.Add(item);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.MemoryApproved,
            TargetType: "memory_proposal", TargetId: proposalId,
            Detail: new { note = request.Note }));

        await db.SaveChangesAsync(ct);

        // T+12（detailed/05 §3.3）：写 SYSTEM 通知消息到 source_channel
        if (proposal.SourceChannelId is { } srcChannelId)
        {
            long notificationSeq = await AllocateChannelSeqAsync(db, srcChannelId, ct);
            DateTimeOffset msgNow = DateTimeOffset.UtcNow;
            string contentJson = JsonSerializer.Serialize(new
            {
                text = $"✅ 已写入项目记忆：{proposal.Title}",
            });

            var notification = new DbMessage
            {
                Id = Guid.NewGuid(),
                ChannelId = srcChannelId,
                Seq = notificationSeq,
                SenderType = MessageSenderTypeMap.SystemDbValue,
                SenderId = null,
                ContentType = MessageContentTypeMap.SystemDbValue,
                Content = contentJson,
                Mentions = null,
                ParentSeq = null,
                ClientMsgId = null,
                TraceId = http.GetTraceId(),
                CreatedAt = msgNow,
            };

            db.Messages.Add(notification);
            await db.SaveChangesAsync(ct);

            await wsSender.BroadcastToChannelAsync(srcChannelId, new
            {
                id = Guid.NewGuid().ToString("N"),
                type = WsMessageTypeMap.MessageCreatedValue,
                ts = msgNow.ToUnixTimeMilliseconds(),
                payload = new
                {
                    channel_id = srcChannelId,
                    message = new
                    {
                        notification.Id,
                        notification.Seq,
                        notification.SenderType,
                        notification.SenderId,
                        notification.ContentType,
                        content = JsonDocument.Parse(notification.Content).RootElement,
                        notification.ParentSeq,
                        notification.ClientMsgId,
                        notification.CreatedAt,
                    },
                },
            }, ct);
        }

        await db.Entry(proposal).ReloadAsync(ct);
        return Results.Ok(ToProposalSummary(proposal, idempotent: false));
    }

    // ───────────────────────── Reject ─────────────────────────

    private static async Task<IResult> RejectAsync(
        Guid proposalId,
        RejectMemoryRequest request,
        MateOSDbContext db,
        WsSender wsSender,
        WorkspaceAuthorizer authorizer,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "reason 不能为空");
        }

        DbMemoryProposal? proposal = await db.MemoryProposals
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct);

        if (proposal is null)
        {
            return ApiErrors.NotFoundResult(ApiErrors.NotFound, "memory_proposal 不存在");
        }

        if (!MemoryStatusMap.TryParse(proposal.Status, out MemoryStatus current))
        {
            current = MemoryStatus.PROPOSED;
        }

        if (current != MemoryStatus.PROPOSED)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict,
                $"memory_proposal 已处于 {proposal.Status} 终态");
        }

        // 权限同 approve
        if (proposal.ProjectId is { } pid)
        {
            Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == pid, ct);

            if (project is null)
            {
                return ApiErrors.NotFoundResult(ApiErrors.NotFound, "项目不存在");
            }

            WorkspaceRoles roles = await authorizer.ForProjectAsync(userId, project, ct);

            if (!roles.CanManageProject)
            {
                return ApiErrors.Forbidden(ApiErrors.NotOwner, "只有项目 owner 可以 reject memory");
            }
        }
        else
        {
            if (proposal.ProposedByUserId != userId)
            {
                return ApiErrors.Forbidden(ApiErrors.NotOwner, "PERSONAL memory 只能由申请人本人 reject");
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE memory_proposals
               SET status = {MemoryStatusMap.RejectedValue},
                   rejected_by = {userId},
                   reject_reason = {request.Reason}
               WHERE id = {proposalId} AND status = {MemoryStatusMap.ProposedValue}", ct);

        if (rows == 0)
        {
            return ApiErrors.ConflictResult(ApiErrors.Conflict, "proposal 已被其他操作覆盖");
        }

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, userId, AuditActions.MemoryRejected,
            TargetType: "memory_proposal", TargetId: proposalId,
            Detail: new { reason = request.Reason }));

        await db.SaveChangesAsync(ct);

        // T+12 同样写 SYSTEM 通知消息
        if (proposal.SourceChannelId is { } srcChannelId)
        {
            long notificationSeq = await AllocateChannelSeqAsync(db, srcChannelId, ct);
            DateTimeOffset msgNow = DateTimeOffset.UtcNow;
            string contentJson = JsonSerializer.Serialize(new
            {
                text = $"❌ 已拒绝记忆申请：{proposal.Title}（{request.Reason}）",
            });

            var notification = new DbMessage
            {
                Id = Guid.NewGuid(),
                ChannelId = srcChannelId,
                Seq = notificationSeq,
                SenderType = MessageSenderTypeMap.SystemDbValue,
                SenderId = null,
                ContentType = MessageContentTypeMap.SystemDbValue,
                Content = contentJson,
                Mentions = null,
                ParentSeq = null,
                ClientMsgId = null,
                TraceId = http.GetTraceId(),
                CreatedAt = msgNow,
            };

            db.Messages.Add(notification);
            await db.SaveChangesAsync(ct);

            await wsSender.BroadcastToChannelAsync(srcChannelId, new
            {
                id = Guid.NewGuid().ToString("N"),
                type = WsMessageTypeMap.MessageCreatedValue,
                ts = msgNow.ToUnixTimeMilliseconds(),
                payload = new
                {
                    channel_id = srcChannelId,
                    message = new
                    {
                        notification.Id,
                        notification.Seq,
                        notification.SenderType,
                        notification.SenderId,
                        notification.ContentType,
                        content = JsonDocument.Parse(notification.Content).RootElement,
                        notification.ParentSeq,
                        notification.ClientMsgId,
                        notification.CreatedAt,
                    },
                },
            }, ct);
        }

        await db.Entry(proposal).ReloadAsync(ct);
        return Results.Ok(ToProposalSummary(proposal, idempotent: false));
    }

    // ───────────────────────── List items + search ─────────────────────────

    private static async Task<IResult> ListItemsAsync(
        Guid? project_id,
        string? type,
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        var query = db.MemoryItems.AsQueryable();

        // PERSONAL：仅 owner 可见
        query = query.Where(i => i.Type != MemoryTypeMap.PersonalValue
            || i.OwnerUserId == userId);

        if (project_id is { } pid)
        {
            bool isMember = await db.ProjectMembers
                .AnyAsync(pm => pm.ProjectId == pid && pm.UserId == userId, ct);

            if (!isMember)
            {
                return ApiErrors.Forbidden(ApiErrors.NotMember, "不是项目成员");
            }

            query = query.Where(i => i.ProjectId == pid);
        }

        if (!string.IsNullOrEmpty(type))
        {
            if (!MemoryTypeMap.TryParse(type, out _))
            {
                return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "type 非法");
            }

            query = query.Where(i => i.Type == type);
        }

        var rows = await query.OrderByDescending(i => i.CreatedAt).Take(100).ToListAsync(ct);
        return Results.Ok(rows.Select(ToItemSummary));
    }

    private static async Task<IResult> SearchAsync(
        string q,
        Guid? project_id,
        MateOSDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        Guid userId = http.RequireUserId();

        if (string.IsNullOrWhiteSpace(q))
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "q 不能为空");
        }

        if (q.Length > 256)
        {
            return ApiErrors.BadRequest(ApiErrors.ValidationFailed, "q 超过 256 字符");
        }

        var query = db.MemoryItems.AsQueryable();

        // 可见性过滤
        query = query.Where(i => i.Type != MemoryTypeMap.PersonalValue
            || i.OwnerUserId == userId);

        if (project_id is { } pid)
        {
            bool isMember = await db.ProjectMembers
                .AnyAsync(pm => pm.ProjectId == pid && pm.UserId == userId, ct);

            if (!isMember)
            {
                return ApiErrors.Forbidden(ApiErrors.NotMember, "不是项目成员");
            }

            query = query.Where(i => i.ProjectId == pid);
        }

        // V1 简化：LIKE 全文（数据量小，GIN 索引做后备）
        // V2 升级：to_tsquery + ts_rank + websearch_to_tsquery
        var hits = await query
            .Where(i => EF.Functions.ILike(i.SearchText, $"%{q}%")
                || EF.Functions.ILike(i.Title, $"%{q}%"))
            .OrderByDescending(i => i.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        return Results.Ok(hits.Select(ToItemSummary));
    }

    // ── helpers ──

    private static MemoryProposalSummary ToProposalSummary(DbMemoryProposal p, bool idempotent) => new(
        p.Id, p.ProjectId, p.Type, p.Title, p.Content, p.Status,
        p.SourceType, p.SourceChannelId, p.SourceMessageSeq, p.SourceMessageId,
        p.ProposedByUserId, p.ProposedByAgentId,
        p.IdempotencyKey,
        p.CreatedAt.ToUnixTimeMilliseconds(),
        p.ApprovedAt?.ToUnixTimeMilliseconds(),
        p.RejectReason);

    private static MemoryItemSummary ToItemSummary(DbMemoryItem i) => new(
        i.Id, i.ProposalId, i.ProjectId, i.Type, i.Title, i.Content,
        i.SourceType, i.SourceChannelId, i.SourceMessageSeq,
        i.ApprovedBy, i.Version,
        i.CreatedAt.ToUnixTimeMilliseconds(),
        i.UpdatedAt.ToUnixTimeMilliseconds());
}
