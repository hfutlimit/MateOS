namespace MateOS.Domain.Execution;

/// <summary>
/// watchdog worker 准备落库的执行摘要（纯数据，不依赖 ORM）。
/// </summary>
/// <remarks>
/// 把"决策（plan）"与"执行（commit）"切开：worker 负责按这份摘要写库，
/// 而不直接操作 <c>agent_executions</c> / <c>execution_attempts</c>。
/// 这样领域层不依赖 EF / 仓储，仍能单测。
/// </remarks>
public sealed record DispatchAckWatchdogPlan(
    Guid ExecutionId,
    int AttemptNo,
    AckWatchdogAction Action,
    int NewRedispatchCount,
    DateTimeOffset PlannedAt);

/// <summary>
/// 一次 watchdog scan 出来的"待处理 attempt"快照（worker 用）。
/// </summary>
/// <remarks>
/// 与 <see cref="AttemptRecoverySnapshot"/> 同源，但多带了：
/// <list type="bullet">
///   <item><c>CurrentRedispatchCount</c> —— 用于递增</item>
///   <item><c>LastResumeProbeAt</c> —— 留给 V2 探测路径（V1 简化版不动它）</item>
/// </list>
/// </remarks>
public sealed record DispatchAckCandidate(
    Guid ExecutionId,
    Guid AttemptId,
    int AttemptNo,
    Guid AgentId,
    DateTimeOffset DispatchSentAt,
    int CurrentRedispatchCount,
    DateTimeOffset? LastResumeProbeAt);

/// <summary>
/// dispatch ACK watchdog 的决策器 + 规划器（纯函数）。
/// </summary>
/// <remarks>
/// <para>
/// 依据 detailed/03 §7.1 + detailed/10 §2 B2 + SYSTEM_DESIGN §5.2。
/// 决策（<see cref="PlanForCandidate"/>）与执行（worker 写库）解耦：
/// 决策是纯函数，可单测；写库是 worker 的职责。
/// </para>
/// <para>
/// V1 简化（与 009 migration 的注释一致）：
/// <list type="bullet">
///   <item>不走 <c>SendResumeProbe</c> 分支 —— 让 <see cref="AckWatchdogAction.SendResumeProbe"/>
///     的语义降级为"再等一轮 ack_wait"，下次 cycle 才真正 <c>RedispatchSameAttempt</c></item>
///   <item>resume 探测由 Agent 侧 SDK 的 <see cref="AgentDispatchDeduplicator"/> 自然处理：
///     重复收到同 <c>(execution_id, attempt_no)</c> 只重绑会话再 ACK，不重新执行
///     （detailed/10 §4）</item>
/// </list>
/// 所以 worker 实际只面对 <c>KeepWaiting</c> / <c>RedispatchSameAttempt</c> /
/// <c>GiveUpAndFail</c> 三种结果。
/// </para>
/// </remarks>
public static class DispatchAckWatchdogRunner
{
    /// <summary>watchdog 默认 ack_wait（detailed/10 §2 B2：15s）。</summary>
    public static readonly TimeSpan DefaultAckWait = DispatchAckWatchdog.DefaultAckWait;

    /// <summary>watchdog 默认 redispatch 上限。</summary>
    /// <remarks>
    /// 阈值由 08（Error &amp; Retry）的退避策略注入；此处给出领域默认值。
    /// 阈值过小 → 抖动一次就 FAILED，伤害太大；过大 → Agent 永远不在线时
    /// Execution 要等很久才放弃。3 是 S1 阶段的经验起点。
    /// </remarks>
    public const int DefaultMaxRedispatch = 3;

    /// <summary>规划单条 attempt 的下一步。</summary>
    /// <param name="candidate">scan 时拿到的 attempt 快照。</param>
    /// <param name="now">当前时刻（注入便于测试）。</param>
    /// <param name="ackWait">等待窗口。</param>
    /// <param name="maxRedispatch">重派上限。</param>
    public static DispatchAckWatchdogPlan PlanForCandidate(
        DispatchAckCandidate candidate,
        DateTimeOffset now,
        TimeSpan? ackWait = null,
        int maxRedispatch = DefaultMaxRedispatch)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        TimeSpan wait = ackWait ?? DefaultAckWait;
        TimeSpan sinceDispatch = now - candidate.DispatchSentAt;

        AckWatchdogAction action = DispatchAckWatchdog.Plan(
            sinceDispatchSent: sinceDispatch,
            sinceResumeProbe: candidate.LastResumeProbeAt is { } lastProbe
                ? now - lastProbe
                : (TimeSpan?)null,
            redispatchCount: candidate.CurrentRedispatchCount,
            maxRedispatch: maxRedispatch,
            ackWait: wait);

        int newCount = action is AckWatchdogAction.RedispatchSameAttempt
            ? candidate.CurrentRedispatchCount + 1
            : candidate.CurrentRedispatchCount;

        return new DispatchAckWatchdogPlan(
            ExecutionId: candidate.ExecutionId,
            AttemptNo: candidate.AttemptNo,
            Action: action,
            NewRedispatchCount: newCount,
            PlannedAt: now);
    }
}
