using System.Net;
using System.Text.Json;
using MateOS.Api.Persistence;

namespace MateOS.Api.Observability;

/// <summary><c>audit_logs.actor_type</c> 的合法取值（E10 §3.1 DDL CHECK）。</summary>
public static class AuditActorTypes
{
    public const string User = "USER";
    public const string Agent = "AGENT";
    public const string System = "SYSTEM";
    public const string Runtime = "RUNTIME";
    public const string Provider = "PROVIDER";
}

/// <summary>审计动作名（<c>audit_logs.action</c>）。</summary>
public static class AuditActions
{
    public const string UserRegistered = "USER_REGISTERED";
    public const string UserLoggedIn = "USER_LOGGED_IN";
    public const string TokenRefreshed = "TOKEN_REFRESHED";
    public const string UserLoggedOut = "USER_LOGGED_OUT";
    public const string PasswordChanged = "PASSWORD_CHANGED";
    public const string ProfileUpdated = "PROFILE_UPDATED";

    public const string OrganizationCreated = "ORGANIZATION_CREATED";
    public const string OrganizationUpdated = "ORGANIZATION_UPDATED";
    public const string OrganizationDeleted = "ORGANIZATION_DELETED";

    /// <summary>I10 明确点名：权限变更必审计。</summary>
    public const string MembershipGranted = "MEMBERSHIP_GRANTED";
    public const string MembershipRoleChanged = "MEMBERSHIP_ROLE_CHANGED";
    public const string MembershipRevoked = "MEMBERSHIP_REVOKED";

    public const string TeamCreated = "TEAM_CREATED";
    public const string TeamUpdated = "TEAM_UPDATED";
    public const string ProjectCreated = "PROJECT_CREATED";
    public const string ProjectUpdated = "PROJECT_UPDATED";
    public const string ProjectDeleted = "PROJECT_DELETED";

    // ── E3 Channel & Messaging ──
    public const string ChannelCreated = "CHANNEL_CREATED";
    public const string ChannelUpdated = "CHANNEL_UPDATED";
    public const string ChannelArchived = "CHANNEL_ARCHIVED";
    public const string ChannelMemberAdded = "CHANNEL_MEMBER_ADDED";
    public const string ChannelMemberRemoved = "CHANNEL_MEMBER_REMOVED";
    public const string MessagePosted = "MESSAGE_POSTED";
    public const string MessageDeleted = "MESSAGE_DELETED";
    public const string AttachmentPresigned = "ATTACHMENT_PRESIGNED";
    public const string AttachmentConfirmed = "ATTACHMENT_CONFIRMED";

    // ── E2 Agent Registry ──
    public const string CredentialCreated = "CREDENTIAL_CREATED";
    public const string CredentialDeleted = "CREDENTIAL_DELETED";
    public const string AgentCreated = "AGENT_CREATED";
    public const string AgentUpdated = "AGENT_UPDATED";
    public const string AgentLifecycleChanged = "AGENT_LIFECYCLE_CHANGED";
    public const string AgentActivityReported = "AGENT_ACTIVITY_REPORTED";
    public const string AgentTokenIssued = "AGENT_TOKEN_ISSUED";
    public const string AgentTokenRevoked = "AGENT_TOKEN_REVOKED";
    public const string AgentAddedToProject = "AGENT_ADDED_TO_PROJECT";
    public const string AgentRemovedFromProject = "AGENT_REMOVED_FROM_PROJECT";

    // ── E7 Agent Execution & Dispatch ──
    public const string ExecutionCreated = "EXECUTION_CREATED";
    public const string ExecutionDispatchAcked = "EXECUTION_DISPATCH_ACKED";
    public const string ExecutionResult = "EXECUTION_RESULT";
    public const string ExecutionEvent = "EXECUTION_EVENT";

    // ── E6 Authorization & Approval ──
    public const string PermissionCreated = "PERMISSION_CREATED";
    public const string PermissionDeleted = "PERMISSION_DELETED";

    // ── E4 Collaboration & Routing ──
    public const string TriggerCreated = "TRIGGER_CREATED";
    public const string CollaborationRequestCreated = "COLLABORATION_REQUEST_CREATED";
    public const string DecisionRecorded = "DECISION_RECORDED";
    public const string ExecutionDispatchedFromCr = "EXECUTION_DISPATCHED_FROM_CR";

    // ── M7 Outbox / 主干 ──
    public const string OutboxEventEnqueued = "OUTBOX_EVENT_ENQUEUED";
    public const string OutboxEventPublished = "OUTBOX_EVENT_PUBLISHED";
    public const string OutboxEventDead = "OUTBOX_EVENT_DEAD";
    public const string CapacityInvariantViolation = "CAPACITY_INVARIANT_VIOLATION";
}

/// <summary>一条待写入的审计记录。</summary>
public sealed record AuditEntry(
    string ActorType,
    Guid? ActorId,
    string Action,
    string? TargetType = null,
    Guid? TargetId = null,
    object? Detail = null);

/// <summary>
/// 审计写入器（I10）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只向 ChangeTracker 添加实体，不调用 SaveChanges</b> —— 审计必须与它记录的业务写落在
/// <b>同一个事务</b>里。否则会出现「业务成功但审计丢失」或反之的双写不一致。
/// 调用方在业务 SaveChanges 时一并提交。
/// </para>
/// <para>表是 append-only：本类只提供新增，不提供修改与删除。</para>
/// </remarks>
public sealed class AuditWriter(MateOSDbContext db)
{
    private const int UserAgentMaxLength = 512;

    public void Record(HttpContext httpContext, AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(entry);

        db.AuditLogs.Add(new AuditLog
        {
            ActorType = entry.ActorType,
            ActorId = entry.ActorId,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            Detail = entry.Detail is null ? null : JsonSerializer.Serialize(entry.Detail),
            TraceId = httpContext.GetTraceId(),
            Ip = httpContext.Connection.RemoteIpAddress,
            UserAgent = Truncate(httpContext.Request.Headers.UserAgent.ToString()),
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>无 HTTP 上下文的后台场景（迁移、relay 等）。</summary>
    public void RecordSystem(string action, string? targetType = null, Guid? targetId = null, object? detail = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            ActorType = AuditActorTypes.System,
            ActorId = null,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Detail = detail is null ? null : JsonSerializer.Serialize(detail),
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static string? Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? null
        : value.Length <= UserAgentMaxLength ? value
        : value[..UserAgentMaxLength];
}
