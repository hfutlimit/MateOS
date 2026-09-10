namespace MateOS.Contracts.Protocol;

/// <summary>execution.event 的 event_type 取值（03 §2；SYSTEM_DESIGN §5.2）。</summary>
public static class EventTypes
{
    public const string Stdout = "STDOUT";
    public const string Progress = "PROGRESS";
    public const string ToolCall = "TOOL_CALL";
    public const string LlmTick = "LLM_TICK";
    public const string Artifact = "ARTIFACT";
    public const string Error = "ERROR";
}

/// <summary>execution.error 的 code 取值（03 §2）。</summary>
public static class ErrorCodes
{
    public const string Provider5xx = "PROVIDER_5XX";
    public const string Provider401 = "PROVIDER_401";
    public const string RateLimit = "RATE_LIMIT";
    public const string SandboxInitFailed = "SANDBOX_INIT_FAILED";
    public const string DeadlineExceeded = "DEADLINE_EXCEEDED";
}

/// <summary>execution.cancel 的 reason 取值（03 §2）。</summary>
public static class CancelReasons
{
    public const string UserCancel = "USER_CANCEL";
    public const string LifecyclePaused = "LIFECYCLE_PAUSED";
    public const string LifecycleDisabled = "LIFECYCLE_DISABLED";
    public const string DeadlineExceeded = "DEADLINE_EXCEEDED";
    public const string TimeoutNoResume = "TIMEOUT_NO_RESUME";
}

/// <summary>
/// execution.dispatch_ack 的 protocol_error 取值（03 §2 / §7.2）。
/// </summary>
/// <remarks>
/// v0.7 语义定型：ACK 是 <b>transport 收据</b>，只回填 <c>dispatch_acked_at</c>；
/// protocol_error 仅表达协议层异常（未知 execution / 过期 attempt），
/// <b>不表达业务接受与否</b>，因此没有 <c>accepted=false</c> 分支，也不触发重路由。
/// </remarks>
public static class ProtocolErrors
{
    public const string UnknownExecution = "UNKNOWN_EXECUTION";
    public const string StaleAttempt = "STALE_ATTEMPT";
}

/// <summary>Agent activity（status envelope 的 status 字段，03 §2）。</summary>
public static class AgentActivities
{
    public const string Offline = "OFFLINE";
    public const string Available = "AVAILABLE";
    public const string Thinking = "THINKING";
    public const string Working = "WORKING";
    public const string WaitingContext = "WAITING_CONTEXT";
    public const string Error = "ERROR";
}

/// <summary>collaboration.decision 的 decision 取值（03 §2；01 §T+11）。</summary>
/// <remarks>
/// <c>DELEGATE</c> 仅保留枚举位：S1 非目标，V1 不做多 Agent 委派（detailed/10 §6）。
/// </remarks>
public static class DecisionKinds
{
    public const string Accept = "ACCEPT";
    public const string Reject = "REJECT";
    public const string NeedContext = "NEED_CONTEXT";
    public const string Delegate = "DELEGATE";
}

/// <summary>collaboration_requests.status 取值（detailed/00「三条 lifecycle 边界」）。</summary>
public static class CollaborationStatuses
{
    public const string Pending = "PENDING";
    public const string Accepted = "ACCEPTED";
    public const string Rejected = "REJECTED";
    public const string NeedContext = "NEED_CONTEXT";
    public const string Unresolved = "UNRESOLVED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>agent_executions.status 取值（SYSTEM_DESIGN §5.2 DDL CHECK）。</summary>
public static class ExecutionStatuses
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
    public const string Timeout = "TIMEOUT";
}

/// <summary>execution_attempts.status 取值（SYSTEM_DESIGN §5.2 DDL CHECK，5 值）。</summary>
/// <remarks>
/// E7 DDL 明确定型：执行终态 <c>CANCELLED</c> / <c>TIMEOUT</c> 映射到 attempt 的 <c>INTERRUPTED</c>，
/// 因此 attempt 没有独立的 CANCELLED / TIMEOUT。
/// </remarks>
public static class AttemptStatuses
{
    public const string Started = "STARTED";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Interrupted = "INTERRUPTED";
}
