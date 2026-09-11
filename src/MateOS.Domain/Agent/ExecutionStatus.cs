namespace MateOS.Domain.Agent;

/// <summary>
/// Agent Execution 顶层 5 态（v0.4.3 DDL <c>agent_executions.status</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 状态机：
/// <code>
///   PENDING ──dispatch──▶ RUNNING ──result/error──▶ SUCCEEDED / FAILED / CANCELLED
///      │                   │
///      └──────cancel───────┴──▶ CANCELLED
/// </code>
/// </para>
/// <para>
/// 终态：<see cref="Succeeded"/> / <see cref="Failed"/> / <see cref="Cancelled"/>。
/// 终态用 CAS 守卫（detailed/01 §1 T+20：<c>WHERE status IN ('PENDING','RUNNING')</c>）。
/// </para>
/// </remarks>
public enum ExecutionStatus
{
    PENDING,
    RUNNING,
    SUCCEEDED,
    FAILED,
    CANCELLED,
}

public static class ExecutionStatusMap
{
    public const string PendingDbValue = "PENDING";
    public const string RunningDbValue = "RUNNING";
    public const string SucceededDbValue = "SUCCEEDED";
    public const string FailedDbValue = "FAILED";
    public const string CancelledDbValue = "CANCELLED";

    public static string ToDbValue(this ExecutionStatus status) => status switch
    {
        ExecutionStatus.PENDING => PendingDbValue,
        ExecutionStatus.RUNNING => RunningDbValue,
        ExecutionStatus.SUCCEEDED => SucceededDbValue,
        ExecutionStatus.FAILED => FailedDbValue,
        ExecutionStatus.CANCELLED => CancelledDbValue,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未映射的 ExecutionStatus"),
    };

    public static bool TryParse(string? value, out ExecutionStatus status)
    {
        switch (value)
        {
            case PendingDbValue: status = ExecutionStatus.PENDING; return true;
            case RunningDbValue: status = ExecutionStatus.RUNNING; return true;
            case SucceededDbValue: status = ExecutionStatus.SUCCEEDED; return true;
            case FailedDbValue: status = ExecutionStatus.FAILED; return true;
            case CancelledDbValue: status = ExecutionStatus.CANCELLED; return true;
            default:
                status = (ExecutionStatus)(-1);
                return false;
        }
    }

    public static bool IsTerminal(this ExecutionStatus status) =>
        status is ExecutionStatus.SUCCEEDED
              or ExecutionStatus.FAILED
              or ExecutionStatus.CANCELLED;
}

/// <summary>
/// Execution Attempt 6 态（DDL <c>execution_attempts.status</c>）。
/// </summary>
/// <remarks>
/// 与 ExecutionStatus 类似但更细：
/// <list type="bullet">
///   <item>PENDING：已创建但未 dispatch</item>
///   <item>DISPATCHED：已 dispatch 但 Agent 未回 ack</item>
///   <item>RUNNING：Agent 已 ack，开始跑任务</item>
///   <item>COMPLETED：终态 succeeded</item>
///   <item>FAILED：终态 failed</item>
///   <item>CANCELLED：终态 cancelled</item>
/// </list>
/// v0.4.3 §4.2：Runtime restart 时按 <c>dispatch_acked_at</c> 区分：
/// <list type="bullet">
///   <item><c>dispatch_acked_at IS NULL</c>（PENDING/DISPATCHED）→ 可重派同一 attempt</item>
///   <item><c>dispatch_acked_at IS NOT NULL</c>（RUNNING）→ 等 resume_request，<b>绝不</b> 重派</item>
/// </list>
/// </remarks>
public enum AttemptStatus
{
    PENDING,
    DISPATCHED,
    RUNNING,
    COMPLETED,
    FAILED,
    CANCELLED,
}

public static class AttemptStatusMap
{
    public const string PendingDbValue = "PENDING";
    public const string DispatchedDbValue = "DISPATCHED";
    public const string RunningDbValue = "RUNNING";
    public const string CompletedDbValue = "COMPLETED";
    public const string FailedDbValue = "FAILED";
    public const string CancelledDbValue = "CANCELLED";

    public static string ToDbValue(this AttemptStatus status) => status switch
    {
        AttemptStatus.PENDING => PendingDbValue,
        AttemptStatus.DISPATCHED => DispatchedDbValue,
        AttemptStatus.RUNNING => RunningDbValue,
        AttemptStatus.COMPLETED => CompletedDbValue,
        AttemptStatus.FAILED => FailedDbValue,
        AttemptStatus.CANCELLED => CancelledDbValue,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未映射的 AttemptStatus"),
    };

    public static bool TryParse(string? value, out AttemptStatus status)
    {
        switch (value)
        {
            case PendingDbValue: status = AttemptStatus.PENDING; return true;
            case DispatchedDbValue: status = AttemptStatus.DISPATCHED; return true;
            case RunningDbValue: status = AttemptStatus.RUNNING; return true;
            case CompletedDbValue: status = AttemptStatus.COMPLETED; return true;
            case FailedDbValue: status = AttemptStatus.FAILED; return true;
            case CancelledDbValue: status = AttemptStatus.CANCELLED; return true;
            default:
                status = (AttemptStatus)(-1);
                return false;
        }
    }

    public static bool IsTerminal(this AttemptStatus status) =>
        status is AttemptStatus.COMPLETED
              or AttemptStatus.FAILED
              or AttemptStatus.CANCELLED;
}
