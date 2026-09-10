using MateOS.Contracts.Protocol;

namespace MateOS.Domain.Lifecycles;

/// <summary>
/// Collaboration lifecycle：起点 Trigger，终点事实源 <c>collaboration_requests</c>。
/// </summary>
/// <remarks>依据 detailed/00「三条 lifecycle 边界」。</remarks>
public enum CollaborationRequestStatus
{
    Pending,
    Accepted,
    Rejected,
    NeedContext,
    Unresolved,
    Cancelled,
}

/// <summary>
/// Agent Execution lifecycle：起点为 E4 调 E7 API 创建，终点事实源 <c>agent_executions</c>。
/// </summary>
/// <remarks>
/// 依据 detailed/00 与 SYSTEM_DESIGN §5.2 DDL CHECK。
/// UI 通过拉取本状态得到 <c>display_state="EXECUTING"</c> 投影，
/// 而<b>不是</b>让 CollaborationRequest.status 镜像 Execution status。
/// </remarks>
public enum ExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Timeout,
}

/// <summary>
/// Attempt lifecycle：Execution 内的 logical 重试单位（<b>≠ WS session</b>）。
/// </summary>
/// <remarks>
/// 依据 SYSTEM_DESIGN §5.2：v0.5 把三处口径（STARTED / RUNNING / INTERRUPTED）合并为唯一 5 值枚举；
/// 执行终态 <c>CANCELLED</c> / <c>TIMEOUT</c> 映射到 <see cref="AttemptStatus.Interrupted"/>。
/// </remarks>
public enum AttemptStatus
{
    /// <summary>已创建、dispatch 已落库（<c>dispatch_sent_at</c>），尚未确认送达。</summary>
    Started,

    /// <summary>Agent 已回 ACK 并开始执行（<c>dispatch_acked_at</c> 有值）。</summary>
    Running,

    /// <summary>正常收尾。</summary>
    Completed,

    /// <summary>执行失败（含重试耗尽）。</summary>
    Failed,

    /// <summary>被中断：取消、超时、或未启动即终止。</summary>
    Interrupted,
}

/// <summary>DB 值 ↔ 强类型枚举的双向映射。枚举名按 .NET 惯例 PascalCase，DB 值为 SCREAMING_SNAKE。</summary>
public static class LifecycleMap
{
    public static string ToDbValue(this CollaborationRequestStatus status) => status switch
    {
        CollaborationRequestStatus.Pending => CollaborationStatuses.Pending,
        CollaborationRequestStatus.Accepted => CollaborationStatuses.Accepted,
        CollaborationRequestStatus.Rejected => CollaborationStatuses.Rejected,
        CollaborationRequestStatus.NeedContext => CollaborationStatuses.NeedContext,
        CollaborationRequestStatus.Unresolved => CollaborationStatuses.Unresolved,
        CollaborationRequestStatus.Cancelled => CollaborationStatuses.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未映射的 CollaborationRequestStatus"),
    };

    public static string ToDbValue(this ExecutionStatus status) => status switch
    {
        ExecutionStatus.Pending => ExecutionStatuses.Pending,
        ExecutionStatus.Running => ExecutionStatuses.Running,
        ExecutionStatus.Succeeded => ExecutionStatuses.Succeeded,
        ExecutionStatus.Failed => ExecutionStatuses.Failed,
        ExecutionStatus.Cancelled => ExecutionStatuses.Cancelled,
        ExecutionStatus.Timeout => ExecutionStatuses.Timeout,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未映射的 ExecutionStatus"),
    };

    public static string ToDbValue(this AttemptStatus status) => status switch
    {
        AttemptStatus.Started => AttemptStatuses.Started,
        AttemptStatus.Running => AttemptStatuses.Running,
        AttemptStatus.Completed => AttemptStatuses.Completed,
        AttemptStatus.Failed => AttemptStatuses.Failed,
        AttemptStatus.Interrupted => AttemptStatuses.Interrupted,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未映射的 AttemptStatus"),
    };

    public static bool TryParseCollaboration(string? value, out CollaborationRequestStatus status)
    {
        status = value switch
        {
            CollaborationStatuses.Pending => CollaborationRequestStatus.Pending,
            CollaborationStatuses.Accepted => CollaborationRequestStatus.Accepted,
            CollaborationStatuses.Rejected => CollaborationRequestStatus.Rejected,
            CollaborationStatuses.NeedContext => CollaborationRequestStatus.NeedContext,
            CollaborationStatuses.Unresolved => CollaborationRequestStatus.Unresolved,
            CollaborationStatuses.Cancelled => CollaborationRequestStatus.Cancelled,
            _ => (CollaborationRequestStatus)(-1),
        };
        return (int)status >= 0;
    }

    public static bool TryParseExecution(string? value, out ExecutionStatus status)
    {
        status = value switch
        {
            ExecutionStatuses.Pending => ExecutionStatus.Pending,
            ExecutionStatuses.Running => ExecutionStatus.Running,
            ExecutionStatuses.Succeeded => ExecutionStatus.Succeeded,
            ExecutionStatuses.Failed => ExecutionStatus.Failed,
            ExecutionStatuses.Cancelled => ExecutionStatus.Cancelled,
            ExecutionStatuses.Timeout => ExecutionStatus.Timeout,
            _ => (ExecutionStatus)(-1),
        };
        return (int)status >= 0;
    }

    public static bool TryParseAttempt(string? value, out AttemptStatus status)
    {
        status = value switch
        {
            AttemptStatuses.Started => AttemptStatus.Started,
            AttemptStatuses.Running => AttemptStatus.Running,
            AttemptStatuses.Completed => AttemptStatus.Completed,
            AttemptStatuses.Failed => AttemptStatus.Failed,
            AttemptStatuses.Interrupted => AttemptStatus.Interrupted,
            _ => (AttemptStatus)(-1),
        };
        return (int)status >= 0;
    }

    /// <summary>
    /// Execution 是否已进入终态。I6 的 CAS（<c>WHERE status IN ('PENDING','RUNNING')</c>）等价于「非终态才可写」。
    /// </summary>
    public static bool IsTerminal(this ExecutionStatus status) =>
        status is ExecutionStatus.Succeeded
                or ExecutionStatus.Failed
                or ExecutionStatus.Cancelled
                or ExecutionStatus.Timeout;

    /// <summary>Attempt 是否已收尾（终态不可再迁移）。</summary>
    public static bool IsTerminal(this AttemptStatus status) =>
        status is AttemptStatus.Completed
                or AttemptStatus.Failed
                or AttemptStatus.Interrupted;

    /// <summary>CollaborationRequest 是否已终结。ACCEPTED 是终态（01 §T+13：失败不回滚 CR）。</summary>
    public static bool IsTerminal(this CollaborationRequestStatus status) =>
        status is CollaborationRequestStatus.Accepted
                or CollaborationRequestStatus.Rejected
                or CollaborationRequestStatus.NeedContext
                or CollaborationRequestStatus.Unresolved
                or CollaborationRequestStatus.Cancelled;
}
