using MateOS.Domain.Lifecycles;

namespace MateOS.Domain.Execution;

/// <summary>
/// Attempt 状态机（纯函数）。
/// </summary>
/// <remarks>
/// <para>依据 detailed/01 §T+15 / §T+16 / §T+20 与 SYSTEM_DESIGN §5.2 的 5 值枚举。</para>
/// <para>
/// 两个易错点：
/// <list type="number">
/// <item>
/// <b>STARTED → RUNNING 不是自动的</b>：只有收到 <c>execution.dispatch_ack</c> 后才迁移（01 §T+16）。
/// 在此之前 attempt 停在 STARTED，这正是 Runtime 重启时「该重派还是该等 resume」的判据来源（03 §4.2）。
/// </item>
/// <item>
/// <b>Attempt 没有 CANCELLED / TIMEOUT</b>：执行层这两个终态都映射到 <see cref="AttemptStatus.Interrupted"/>，
/// 靠 <c>agent_executions.status</c> 保留区分度。
/// </item>
/// </list>
/// </para>
/// </remarks>
public static class AttemptLifecycle
{
    /// <summary>判断一次状态迁移是否合法。</summary>
    public static bool CanTransition(AttemptStatus from, AttemptStatus to) => (from, to) switch
    {
        // 收到 dispatch_ack 后开始执行
        (AttemptStatus.Started, AttemptStatus.Running) => true,

        // 未开始即失败：dispatch 始终送不到（B2 的送达失败路径，重试阈值耗尽）
        (AttemptStatus.Started, AttemptStatus.Failed) => true,

        // 未开始即被中断：取消 / 超时发生在 ACK 之前
        (AttemptStatus.Started, AttemptStatus.Interrupted) => true,

        // 正常收尾
        (AttemptStatus.Running, AttemptStatus.Completed) => true,

        // 执行失败（含重试耗尽）
        (AttemptStatus.Running, AttemptStatus.Failed) => true,

        // 取消 / 超时 / 长断线
        (AttemptStatus.Running, AttemptStatus.Interrupted) => true,

        // 其余一律非法：含所有「终态 → 任意」以及 Started → Completed 的跳步
        _ => false,
    };

    /// <summary>校验并返回目标状态；非法迁移抛 <see cref="InvalidOperationException"/>。</summary>
    public static AttemptStatus Transition(AttemptStatus from, AttemptStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException(
                $"非法的 attempt 状态迁移：{from.ToDbValue()} → {to.ToDbValue()}");
        }

        return to;
    }

    /// <summary>
    /// Execution 终态 → Attempt 终态的映射。
    /// </summary>
    /// <remarks>
    /// <c>CANCELLED</c> 与 <c>TIMEOUT</c> 都收敛到 <see cref="AttemptStatus.Interrupted"/>：
    /// attempt 层不保留取消 / 超时的区分，两者在 <c>agent_executions.status</c> 上才有别。
    /// </remarks>
    public static AttemptStatus ForExecutionTerminal(ExecutionStatus terminal) => terminal switch
    {
        ExecutionStatus.Succeeded => AttemptStatus.Completed,
        ExecutionStatus.Failed => AttemptStatus.Failed,
        ExecutionStatus.Cancelled => AttemptStatus.Interrupted,
        ExecutionStatus.Timeout => AttemptStatus.Interrupted,
        _ => throw new ArgumentOutOfRangeException(
            nameof(terminal), terminal, "只有终态 execution 才能映射到 attempt 终态。"),
    };
}
