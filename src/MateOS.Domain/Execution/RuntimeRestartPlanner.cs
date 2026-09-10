using MateOS.Domain.Lifecycles;

namespace MateOS.Domain.Execution;

/// <summary>
/// Attempt 上与恢复判定相关的列快照（SYSTEM_DESIGN §5.2）。
/// </summary>
/// <param name="AttemptNo">logical 重试序号。</param>
/// <param name="Status">attempt 状态。</param>
/// <param name="DispatchSentAt">Runtime 推送 dispatch 的时刻。</param>
/// <param name="DispatchAckedAt">
/// 收到 <c>execution.dispatch_ack</c> 的时刻。此列是 v0.4.3 修复 P1-1 的关键：
/// 它把「Agent 到底有没有收到指令」变成可判定的事实。
/// </param>
/// <param name="LastPersistedSeq">连续事件位点（03 §3.2）。</param>
public sealed record AttemptRecoverySnapshot(
    int AttemptNo,
    AttemptStatus Status,
    DateTimeOffset? DispatchSentAt,
    DateTimeOffset? DispatchAckedAt,
    long LastPersistedSeq)
{
    /// <summary><c>dispatch_acked_at IS NOT NULL</c>。</summary>
    public bool IsAcknowledged => DispatchAckedAt is not null;
}

/// <summary>Runtime 启动时对一条在途 execution 应采取的动作（03 §4.2 / §7.1）。</summary>
public enum RestartRecoveryAction
{
    /// <summary>已收敛，不在恢复范围内。</summary>
    SkipAlreadyTerminal,

    /// <summary>没有 active attempt（数据异常）→ cancel + 通知 owner（03 §4.2 第三行）。</summary>
    QuarantineNoActiveAttempt,

    /// <summary>
    /// 未收到 ACK 且状态自洽（STARTED）→ 直接重派 <b>同一</b> <c>(execution_id, attempt_no)</c>。
    /// </summary>
    ReDispatch,

    /// <summary>
    /// 状态自相矛盾：<c>status=RUNNING</c> 但 <c>dispatch_acked_at IS NULL</c>。
    /// 03 §7.1 冻结的处理是「先 <c>resume_request</c> 探测，<c>ack_wait_s</c> 内无响应才重派同一 attempt」——
    /// 不直接重派，因为 Agent 可能其实已在跑（ACK 只是丢在网络上）。
    /// </summary>
    ProbeResumeThenRedispatch,

    /// <summary>
    /// 已 ACK 且正在执行 → <b>绝不</b>重派，等 Agent 重连后主动 <c>resume_request</c>。
    /// 这是 v0.4.3 修复的核心：盲目重派会让 Agent 重复执行、重复写文件、重复计费（03 §4.1）。
    /// </summary>
    WaitForResume,
}

/// <summary>
/// Runtime 重启（或进程换手）时的在途 execution 恢复决策（纯函数）。
/// </summary>
/// <remarks>
/// 对应 detailed/03 §4.2 的「Runtime restart 行为」判定表与 §4.3 的启动流程。
/// 判定的唯一权威依据是 <see cref="AttemptRecoverySnapshot.DispatchAckedAt"/>，而不是 execution/attempt 的 status。
/// </remarks>
public static class RuntimeRestartPlanner
{
    /// <summary>为一条在途 execution 规划恢复动作。</summary>
    /// <param name="execution">execution 状态快照。</param>
    /// <param name="activeAttempt">
    /// 当前 active attempt 的恢复快照；<c>null</c> 表示数据异常（execution 在途却无 active attempt）。
    /// </param>
    public static RestartRecoveryAction Plan(ExecutionState execution, AttemptRecoverySnapshot? activeAttempt)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (execution.IsTerminal)
        {
            return RestartRecoveryAction.SkipAlreadyTerminal;
        }

        if (activeAttempt is null)
        {
            return RestartRecoveryAction.QuarantineNoActiveAttempt;
        }

        // 结构性异常优先于 ACK 判定：在途 execution 上挂着「已终态」的 attempt，
        // 说明 01 §T+20 的状态收敛没走完。此时两边都走不通 ——
        // 重派会让已结束的 attempt 复活，等 resume 则永远等不到（Agent 不会再主动续传）。
        // 因此必须隔离并通知 owner，而不是静默挂起。
        if (activeAttempt.Status.IsTerminal())
        {
            return RestartRecoveryAction.QuarantineNoActiveAttempt;
        }

        if (activeAttempt.IsAcknowledged)
        {
            // Agent 已确认收到并（大概率）正在执行 —— 重派会造成重复执行。
            return RestartRecoveryAction.WaitForResume;
        }

        // 未 ACK：区分「自洽的未送达」与「RUNNING 却未 ACK 的矛盾态」。
        return activeAttempt.Status switch
        {
            AttemptStatus.Started => RestartRecoveryAction.ReDispatch,
            AttemptStatus.Running => RestartRecoveryAction.ProbeResumeThenRedispatch,
            _ => RestartRecoveryAction.QuarantineNoActiveAttempt,
        };
    }
}
