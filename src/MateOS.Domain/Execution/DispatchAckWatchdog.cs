namespace MateOS.Domain.Execution;

/// <summary>dispatch 送达失败时，Runtime 下一步该做什么。</summary>
public enum AckWatchdogAction
{
    /// <summary>还在等待窗口内，什么都不做。</summary>
    KeepWaiting,

    /// <summary>先推 <c>execution.resume_request</c> 探测——Agent 可能其实已在执行，只是 ACK 丢了（03 §7.1）。</summary>
    SendResumeProbe,

    /// <summary>探测也无响应 → 重派 <b>同一</b> <c>(execution_id, attempt_no)</c>（B2）。</summary>
    RedispatchSameAttempt,

    /// <summary>重派次数超阈值 → Execution 转 FAILED 并进 Inbox（<c>execution.failed</c> 分类）。</summary>
    GiveUpAndFail,
}

/// <summary>
/// dispatch ACK 超时看门狗（纯函数）。
/// </summary>
/// <remarks>
/// <para>依据 detailed/03 §7.1 与 detailed/10 §2 B2。</para>
/// <para>
/// 硬约束（B2 断言点）：无论走到哪一步，<b>都不新建 attempt、都不回 Resolver 重路由</b>。
/// CR 在 ACCEPT 那一刻已是终态，容量也已由 Resolver 的 lease 预占
/// （SYSTEM_DESIGN §4.2.1）——dispatch 阶段再出现「本地已满」属 invariant 破坏。
/// </para>
/// </remarks>
public static class DispatchAckWatchdog
{
    /// <summary>
    /// 等待 <c>dispatch_ack</c> 的默认窗口：<b>15 秒</b>。
    /// 该默认值由 detailed/10 §2 B2 明确给出（<c>ack_wait_s</c>，默认 15s）。
    /// </summary>
    public static readonly TimeSpan DefaultAckWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 规划下一步动作。
    /// </summary>
    /// <param name="sinceDispatchSent">自 <c>dispatch_sent_at</c> 起经过的时长。</param>
    /// <param name="sinceResumeProbe">
    /// 自上次发出 <c>resume_request</c> 探测起经过的时长；尚未探测过则传 <c>null</c>。
    /// </param>
    /// <param name="redispatchCount">该 attempt 已重派的次数。</param>
    /// <param name="maxRedispatch">
    /// 重派次数上限。此阈值<b>未在 detailed 中硬编码</b>，由 08（Error &amp; Retry）的退避策略注入；
    /// 保持参数化以免在领域层制造第二处事实源。
    /// </param>
    /// <param name="ackWait">等待窗口，默认 <see cref="DefaultAckWait"/>（15s）。</param>
    public static AckWatchdogAction Plan(
        TimeSpan sinceDispatchSent,
        TimeSpan? sinceResumeProbe,
        int redispatchCount,
        int maxRedispatch,
        TimeSpan? ackWait = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(redispatchCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRedispatch);

        TimeSpan wait = ackWait ?? DefaultAckWait;

        if (sinceDispatchSent < wait)
        {
            return AckWatchdogAction.KeepWaiting;
        }

        // 到点后第一步永远是「探测」而不是「重派」：重派有重复执行风险，探测没有。
        if (sinceResumeProbe is null)
        {
            return AckWatchdogAction.SendResumeProbe;
        }

        if (sinceResumeProbe < wait)
        {
            return AckWatchdogAction.KeepWaiting;
        }

        return redispatchCount >= maxRedispatch
            ? AckWatchdogAction.GiveUpAndFail
            : AckWatchdogAction.RedispatchSameAttempt;
    }
}
