using System.Net;

using MateOS.Domain.Agent;
using MateOS.Domain.Memory;
using MateOS.Domain.Routing;

namespace MateOS.Api.Persistence;

/// <summary>鑷劧浜猴紱Agent 鐨?owner锛圗1 搂3锛夈€?/summary>
public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;

    /// <summary>argon2id 缂栫爜涓诧紙鍚?salt 涓庡弬鏁帮級锛岀粷涓嶅洖鏄撅紙README 宸ヤ綔绾﹀畾锛夈€?/summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>椤跺眰绉熸埛锛涗笉鏄潈闄愪綔鐢ㄥ煙锛堟潈闄愭渶灏忓埌 Project锛夈€?/summary>
public sealed class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>浠呬俊鎭€э紝涓嶅弬涓庢潈闄愬垽瀹氾紙v0.4.2 鍒犻櫎 owner_id 鍙屼簨瀹炴簮锛夈€?/summary>
    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Org owner 瑙掕壊鐨?b>鍞竴</b>浜嬪疄婧愶紙E1 搂3.1锛夈€?/summary>
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

/// <summary>Context Boundary锛氭垚鍛?/ 璁板繂 / 宸ヤ綔 / Provider 缁戝畾鐨勪綔鐢ㄥ煙銆?/summary>
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

/// <summary>I10锛氭墍鏈夊啓鎿嶄綔鐣欏璁°€?/summary>
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

    /// <summary>鍒楃被鍨?<c>inet</c>銆侼pgsql 瀵?inet 鐨勫師鐢熸槧灏勭洰鏍囨槸 <see cref="IPAddress"/>锛屼笉鏄?string銆?/summary>
    public IPAddress? Ip { get; set; }

    public string? UserAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Transactional outbox锛圖7锛夈€傚繀椤讳笌涓氬姟鍐欏湪鍚屼竴浜嬪姟鍐呫€?/summary>
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

/// <summary>鏄惧紡 SQL 杩佺Щ鐨勮璐﹁〃锛堢敱杩佺Щ鍣ㄨ嚜琛?bootstrap锛屼笉灞炰换浣曚笟鍔¤縼绉伙級銆?/summary>
public sealed class SchemaMigration
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; set; }
}

// ============================================================================
// E3 Channel & Messaging锛堣縼绉?002锛?
// ============================================================================

/// <summary>閫氫俊杈圭晫锛圗3 搂1锛夈€傛瘡涓?channel 灞炰簬涓€涓?project銆?/summary>
public sealed class Channel
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>褰撳墠鏈€澶?seq锛岀敤浜庢柇鐐圭画浼狅紙DDL锛?c>last_seq BIGINT NOT NULL DEFAULT 0</c>锛夈€?/summary>
    public long LastSeq { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Channel 鎴愬憳锛圚UMAN 鎴?AGENT 鐨嗗彲鍔犲叆锛孍3 搂2.1锛夈€?/summary>
public sealed class ChannelMember
{
    public Guid ChannelId { get; set; }
    public string MemberType { get; set; } = string.Empty;
    public Guid MemberId { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>Channel seq 璁℃暟鍣紙姣?channel 涓€琛岋紱浜嬪姟鍐?UPDATE ... RETURNING 鍒嗛厤锛夈€?/summary>
public sealed class ChannelSeqCounter
{
    public Guid ChannelId { get; set; }

    /// <summary>涓嬩竴涓彲鍒嗛厤鐨?seq锛圖DL锛?c>next_seq BIGINT NOT NULL DEFAULT 1</c>锛夈€?/summary>
    public long NextSeq { get; set; }
}

/// <summary>娑堟伅娴侊紙5 褰㈡€?projection锛孍3 搂3 / 搂3.1锛夈€?/summary>
public sealed class Message
{
    public Guid Id { get; set; }
    public Guid ChannelId { get; set; }
    public long Seq { get; set; }
    public string SenderType { get; set; } = string.Empty;
    public Guid? SenderId { get; set; }
    public string ContentType { get; set; } = string.Empty;

    /// <summary>5 褰㈡€佹姇褰卞唴瀹癸紙jsonb锛夈€侱ECISION / AGENT_OUTPUT / MEMORY_REQUEST 鍙紩 entity_ref銆?/summary>
    public string Content { get; set; } = "{}";

    /// <summary>棰勬彁鍙?mentions锛圗4 mention 瑙ｆ瀽鍚庡洖濉紝E3 浠呭啓锛夈€?/summary>
    public string? Mentions { get; set; }

    /// <summary>thread 鐖舵秷鎭?seq锛坴0.4 绠€鍖栵紝V2 鐙珛鎴愯〃锛夈€?/summary>
    public long? ParentSeq { get; set; }

    /// <summary>瀹㈡埛绔箓绛夐敭锛團2 閲嶅彂鍘婚噸锛夈€?/summary>
    public Guid? ClientMsgId { get; set; }
    public string? TraceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>闄勪欢鍏冩暟鎹紙鐙珛琛紝璺ㄦ秷鎭彲澶嶇敤锛孍3 搂4 attachments锛夈€?/summary>
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

// ============================================================================
// E2 Agent Registry锛堣縼绉?003锛?
// ============================================================================

/// <summary>鍔犲瘑鍑嵁锛圗2 搂3.3 AES-256-GCM 淇″皝锛夈€?/summary>
public sealed class Credential
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>瀵嗘枃 = nonce(12) || ciphertext || tag(16)銆傚簲鐢ㄥ眰鍔犺В瀵嗐€?/summary>
    public byte[] SecretEncrypted { get; set; } = Array.Empty<byte>();

    public string? Meta { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Agent 瀹炰綋锛圗2 搂3 lifecycle 脳 activity 姝ｄ氦锛夈€?/summary>
public sealed class Agent
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid CredentialId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;

    /// <summary>canonical key JSON 鏁扮粍锛圗2 搂3.2 5 keys锛夈€?/summary>
    public string Capabilities { get; set; } = "[]";

    public string Lifecycle { get; set; } = AgentLifecycleMap.ActiveDbValue;
    public string Activity { get; set; } = AgentActivityMap.OfflineDbValue;
    public string? ActivityReason { get; set; }
    public DateTimeOffset? ActivityUpdatedAt { get; set; }

    public int MaxConcurrency { get; set; } = 1;
    public decimal DailyLimitUsd { get; set; } = 5.00m;
    public decimal MonthlyBudgetUsd { get; set; } = 50.00m;
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Agent 涓?Project 鐨勫彲瑙佹€х粦瀹氾紙E2 搂3锛夈€?/summary>
public sealed class AgentProjectMember
{
    public Guid AgentId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid InvitedBy { get; set; }
    public bool CanReadHistory { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>Agent 绛惧彂 token 鐨勪笉鍙€嗙储寮曪紙E2 搂3锛夈€?/summary>
public sealed class AgentToken
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    /// <summary>SHA-256 hex锛堟槑鏂囦粎鍦ㄥ垱寤哄搷搴旈噷杩斿洖涓€娆★級銆?/summary>
    public string TokenHash { get; set; } = string.Empty;

    public string? Label { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ============================================================================
// E7 Agent Execution & Dispatch（迁移 004）
// ============================================================================

/// <summary>Agent Execution 顶层（E7）。</summary>
public sealed class AgentExecution
{
    public Guid Id { get; set; }
    public Guid? CollaborationRequestId { get; set; }
    public Guid AgentId { get; set; }
    public Guid? WorkItemRef { get; set; }
    public string Status { get; set; } = ExecutionStatusMap.PendingDbValue;
    public DateTimeOffset? DispatchSentAt { get; set; }
    public DateTimeOffset? DispatchAckedAt { get; set; }

    /// <summary>contiguous cursor（v0.4.3 修复：严格不跳号）。</summary>
    public long LastPersistedSeq { get; set; } = 0;

    public string? TerminalEnvelopeId { get; set; }
    public int? ActiveAttemptNo { get; set; }
    public int AttemptCount { get; set; } = 0;
    public string Input { get; set; } = "{}";
    public string? ContextRefs { get; set; }
    public string? ResultOutput { get; set; }
    public string? ResultUsage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
}

/// <summary>Execution Attempt（一次执行可有多次 attempt；V1 简化为单 attempt）。</summary>
public sealed class ExecutionAttempt
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public int AttemptNo { get; set; } = 1;
    public string Status { get; set; } = AttemptStatusMap.PendingDbValue;
    public DateTimeOffset? DispatchSentAt { get; set; }
    public DateTimeOffset? DispatchAckedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>contiguous cursor（attempt 级）。</summary>
    public long LastPersistedSeq { get; set; } = 0;

    public string? RuntimeSessionId { get; set; }
    public string? TerminalErrorCode { get; set; }
    public string? TerminalMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Execution Event（agent 产出事件流；cursor 强制 contiguous）。</summary>
public sealed class ExecutionEvent
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string ProviderEventId { get; set; } = string.Empty;
    public long Seq { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>E4 → E7 dispatch 重发去重（v0.5 §7.1）。</summary>
public sealed class AgentDispatchInbox
{
    public Guid Id { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid AgentId { get; set; }
    public Guid ExecutionId { get; set; }
    public int AttemptNo { get; set; }
    public DateTimeOffset DispatchedAt { get; set; }
    public DateTimeOffset? AckedAt { get; set; }
}

// ============================================================================
// E6 Authorization & Approval（迁移 005）
// ============================================================================

/// <summary>权限 override（E6 §3）。</summary>
public sealed class Permission
{
    public Guid Id { get; set; }
    public string ScopeType { get; set; } = string.Empty;
    public Guid ScopeId { get; set; }
    public string SubjectType { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public string PermKey { get; set; } = string.Empty;
    public string Effect { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

// ============================================================================
// E4 Collaboration & Routing（迁移 006）
// ============================================================================

/// <summary>Trigger（E4 §3）—— 4 态：MENTION / WORK_ITEM / API / AUTOMATION。</summary>
public sealed class Trigger
{
    public Guid Id { get; set; }
    public string TriggerType { get; set; } = string.Empty;
    public string TriggerRef { get; set; } = "{}";
    public string FromActorType { get; set; } = string.Empty;
    public Guid FromActorId { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

/// <summary>Collaboration Request（E4 §3）—— 6 态状态机。</summary>
public sealed class CollaborationRequest
{
    public Guid Id { get; set; }
    public Guid TriggerId { get; set; }
    public string TriggerType { get; set; } = string.Empty;
    public string TriggerRef { get; set; } = "{}";
    public string RequestKind { get; set; } = string.Empty;
    public string FromActorType { get; set; } = string.Empty;
    public Guid FromActorId { get; set; }
    public Guid? TargetAgentId { get; set; }
    public string RequiredCapabilities { get; set; } = "[]";
    public string ContextRefs { get; set; } = "{}";
    public string Status { get; set; } = CrStatusMap.PendingValue;
    public string? SlotLeaseId { get; set; }
    public Guid? TargetExecutionId { get; set; }
    public int DeadlineS { get; set; } = 600;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>Decision Record（E4 §3）—— 一个 CR 一次决策（UNIQUE）。</summary>
public sealed class DecisionRecord
{
    public Guid Id { get; set; }
    public Guid CollaborationRequestId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? Needs { get; set; }
    public bool? AnalysisCapability { get; set; }
    public int? AnalysisContextScore { get; set; }
    public bool? AnalysisPermission { get; set; }
    public Guid? AcceptedExecutionId { get; set; }
    public string ActorType { get; set; } = string.Empty;
    public Guid ActorId { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

// ============================================================================
// E5 Shared Memory（迁移 007）
// ============================================================================

/// <summary>Memory Proposal（E5 §3）—— 申请阶段事实源。</summary>
public sealed class MemoryProposal
{
    public Guid Id { get; set; }
    public Guid? ProjectId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? ScopeType { get; set; }  // generated column
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = MemoryStatusMap.ProposedValue;
    public string SourceType { get; set; } = string.Empty;
    public Guid? SourceChannelId { get; set; }
    public long? SourceMessageSeq { get; set; }
    public Guid? SourceMessageId { get; set; }
    public Guid? ProposedByAgentId { get; set; }
    public Guid? ProposedByUserId { get; set; }
    public Guid? ApprovedBy { get; set; }
    public Guid? RejectedBy { get; set; }
    public string? RejectReason { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

/// <summary>Memory Item（E5 §3）—— 已批准事实源（proposal_id UNIQUE）。</summary>
public sealed class MemoryItem
{
    public Guid Id { get; set; }
    public Guid ProposalId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? ScopeType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string SearchText { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public Guid? SourceChannelId { get; set; }
    public long? SourceMessageSeq { get; set; }
    public Guid ApprovedBy { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ============================================================================
// E8 Work Management Core（迁移 008）
// ============================================================================

/// <summary>
/// Provider Connection（E8 §3 v0.4.2：Org/Owner 级，多 Project 复用同一 Jira Site）。
/// </summary>
/// <remarks>
/// v0.4.3 起 webhook_* 字段不再挂在此表（见 <see cref="WorkManagementWebhook"/>）：
/// connection 是 tenant + OAuth 维度，webhook 是 subscription 维度。
/// </remarks>
public sealed class WorkManagementConnection
{
    public Guid Id { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public Guid OrgId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string? DisplayLabel { get; set; }

    /// <summary>密文 = nonce(12) || ciphertext || tag(16)，与应用层 credential 同口径。</summary>
    public byte[]? AccessTokenEncrypted { get; set; }

    public byte[]? RefreshTokenEncrypted { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>site URL / scopes 等。</summary>
    public string? Meta { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Provider Binding（Project × Provider）。<b>路由的唯一事实源</b>（E8 §5.1）。
/// </summary>
public sealed class WorkItemBinding
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public Guid? ConnectionId { get; set; }
    public string? ExternalProjectRef { get; set; }

    /// <summary>status mapping 等 Provider 私有配置。</summary>
    public string? Settings { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// WorkItem（E8 §3 单一表）。Built-in 时是事实源；Jira 时是本地同步表示。
/// </summary>
public sealed class WorkItem
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>由 status 派生（F8），绝不手填。</summary>
    public string CanonicalStatusCategory { get; set; } = string.Empty;

    public string? AssigneeType { get; set; }
    public Guid? AssigneeId { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public string CreatedByType { get; set; } = string.Empty;
    public Guid CreatedById { get; set; }

    /// <summary>v0.4.3：路由事实源。UPDATE 一律用它，不看 active binding。</summary>
    public Guid BindingId { get; set; }

    /// <summary>冗余字段，便于按 provider 过滤（identity 仍以 binding_id 为准）。</summary>
    public string ProviderKey { get; set; } = string.Empty;

    public string? ExternalRef { get; set; }
    public string? ExternalUrl { get; set; }
    public string? ProviderStatus { get; set; }
    public DateTimeOffset? ProviderUpdatedAt { get; set; }
    public string? ProviderMeta { get; set; }

    /// <summary>V1 简化：TEXT + 表达式 GIN 索引（与 memory_items 同口径）。</summary>
    public string SearchText { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>WorkComment（E8 §3）。</summary>
public sealed class WorkComment
{
    public Guid Id { get; set; }
    public Guid WorkItemId { get; set; }
    public string AuthorType { get; set; } = string.Empty;
    public Guid? AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>WorkRelation（E8 §3）。写入时同步写反向行，两端视图才不矛盾。</summary>
public sealed class WorkRelation
{
    public Guid Id { get; set; }
    public Guid FromWorkItemId { get; set; }
    public Guid ToWorkItemId { get; set; }
    public string RelationType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Webhook 注册（E8 §3 v0.4.3 独立表，挂 binding 维度；E9 Jira 消费，S3 只落 schema）。
/// </summary>
public sealed class WorkManagementWebhook
{
    public Guid Id { get; set; }
    public Guid BindingId { get; set; }
    public Guid ConnectionId { get; set; }
    public string ExternalWebhookId { get; set; } = string.Empty;
    public string? FilterJql { get; set; }
    public string? FilterEvents { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }
    public string? RefreshStatus { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
