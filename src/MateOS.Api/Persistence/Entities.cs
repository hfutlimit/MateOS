using System.Net;

namespace MateOS.Api.Persistence;

/// <summary>自然人；Agent 的 owner（E1 §3）。</summary>
public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;

    /// <summary>argon2id 编码串（含 salt 与参数），绝不回显（README 工作约定）。</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>顶层租户；不是权限作用域（权限最小到 Project）。</summary>
public sealed class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>仅信息性，不参与权限判定（v0.4.2 删除 owner_id 双事实源）。</summary>
    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Org owner 角色的<b>唯一</b>事实源（E1 §3.1）。</summary>
public sealed class OrganizationMember
{
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
}

public sealed class Team
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TeamMember
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>Context Boundary：成员 / 记忆 / 工作 / Provider 绑定的作用域。</summary>
public sealed class Project
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? RepoUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProjectMember
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>I10：所有写操作留审计。</summary>
public sealed class AuditLog
{
    public long Id { get; set; }
    public string ActorType { get; set; } = string.Empty;
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public string? Detail { get; set; }
    public string? TraceId { get; set; }

    /// <summary>列类型 <c>inet</c>。Npgsql 对 inet 的原生映射目标是 <see cref="IPAddress"/>，不是 string。</summary>
    public IPAddress? Ip { get; set; }

    public string? UserAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Transactional outbox（D7）。必须与业务写在同一事务内。</summary>
public sealed class OutboxEvent
{
    public Guid Id { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public Guid AggregateId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public string Status { get; set; } = "PENDING";
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>显式 SQL 迁移的记账表（由迁移器自行 bootstrap，不属任何业务迁移）。</summary>
public sealed class SchemaMigration
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; set; }
}

// ============================================================================
// E3 Channel & Messaging（迁移 002）
// ============================================================================

/// <summary>通信边界（E3 §1）。每个 channel 属于一个 project。</summary>
public sealed class Channel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>当前最大 seq，用于断点续传（DDL：<c>last_seq BIGINT NOT NULL DEFAULT 0</c>）。</summary>
    public long LastSeq { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Channel 成员（HUMAN 或 AGENT 皆可加入，E3 §2.1）。</summary>
public sealed class ChannelMember
{
    public Guid ChannelId { get; set; }
    public string MemberType { get; set; } = string.Empty;
    public Guid MemberId { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>Channel seq 计数器（每 channel 一行；事务内 UPDATE ... RETURNING 分配）。</summary>
public sealed class ChannelSeqCounter
{
    public Guid ChannelId { get; set; }

    /// <summary>下一个可分配的 seq（DDL：<c>next_seq BIGINT NOT NULL DEFAULT 1</c>）。</summary>
    public long NextSeq { get; set; }
}

/// <summary>消息流（5 形态 projection，E3 §3 / §3.1）。</summary>
public sealed class Message
{
    public Guid Id { get; set; }
    public Guid ChannelId { get; set; }
    public long Seq { get; set; }
    public string SenderType { get; set; } = string.Empty;
    public Guid? SenderId { get; set; }
    public string ContentType { get; set; } = string.Empty;

    /// <summary>5 形态投影内容（jsonb）。DECISION / AGENT_OUTPUT / MEMORY_REQUEST 只引 entity_ref。</summary>
    public string Content { get; set; } = "{}";

    /// <summary>预提取 mentions（E4 mention 解析后回填，E3 仅写）。</summary>
    public string? Mentions { get; set; }

    /// <summary>thread 父消息 seq（v0.4 简化，V2 独立成表）。</summary>
    public long? ParentSeq { get; set; }

    /// <summary>客户端幂等键（F2 重发去重）。</summary>
    public Guid? ClientMsgId { get; set; }
    public string? TraceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>附件元数据（独立表，跨消息可复用，E3 §4 attachments）。</summary>
public sealed class Attachment
{
    public Guid Id { get; set; }
    public string UploaderType { get; set; } = string.Empty;
    public Guid UploaderId { get; set; }
    public string S3Key { get; set; } = string.Empty;
    public string Status { get; set; } = "PRESIGNED";
    public string FileName { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public long? SizeBytes { get; set; }
    public int RefCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
