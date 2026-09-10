using MateOS.Domain.Lifecycles;

namespace MateOS.Domain.Execution;

/// <summary>
/// <c>agent_executions</c> 中与终态收敛相关的列快照。
/// </summary>
/// <param name="Status">execution status。</param>
/// <param name="ActiveAttemptNo">当前 attempt；终态时置 NULL（01 §T+20）。</param>
/// <param name="TerminalEnvelopeId">终态 envelope 去重键（SYSTEM_DESIGN §5.2 变更 #24）。</param>
public sealed record ExecutionState(
    ExecutionStatus Status,
    int? ActiveAttemptNo,
    Guid? TerminalEnvelopeId)
{
    /// <summary>是否已收敛。I6 的 CAS 条件「status IN (PENDING, RUNNING)」等价于本属性为 false。</summary>
    public bool IsTerminal => Status.IsTerminal();

    /// <summary>E7 创建 Execution 时的初始态（01 §T+15）。</summary>
    public static ExecutionState Opened() => new(ExecutionStatus.Pending, ActiveAttemptNo: 1, TerminalEnvelopeId: null);
}

/// <summary>终态 envelope 的摄入判定。</summary>
public enum TerminalIngestOutcome
{
    /// <summary>CAS 成功：状态收敛，位点清零。</summary>
    Applied,

    /// <summary>同一 <c>terminal_envelope_id</c> 重放（B7：同 envelope.id 的 result 再发一次）。</summary>
    DiscardedTerminalEnvelopeReplay,

    /// <summary>Execution 已是终态，又来了一条新的终态消息（03 §3.5：CAS 失败 → 忽略）。</summary>
    DiscardedAfterTerminal,

    /// <summary><c>active_attempt_no != attempt_no</c>：迟到结果，属于已被取代的 attempt（B8）。</summary>
    DiscardedStaleAttempt,
}

/// <summary>摄入结果。状态在 <see cref="TerminalIngestOutcome.Applied"/> 之外一律原样返回。</summary>
public sealed record TerminalIngestResult(TerminalIngestOutcome Outcome, ExecutionState State, string? Reason = null)
{
    public bool WasApplied => Outcome is TerminalIngestOutcome.Applied;
}

/// <summary>
/// Execution 终态收敛的守卫（纯函数）。
/// </summary>
/// <remarks>
/// <para>依据 I6 + detailed/03 §3.5 + detailed/10 §2 B7/B8。</para>
/// <para>
/// 判定顺序是<b>语义优先级</b>，不是任意选择：
/// <list type="number">
/// <item>同 <c>terminal_envelope_id</c> → 精确重放，最高优先级（保证 terminal 消息 exactly-once）；</item>
/// <item>已是终态 → CAS 失败，忽略（I6）；</item>
/// <item><c>active_attempt_no</c> 不匹配 → 迟到结果，丢弃（B8）；</item>
/// <item>否则 CAS 成功。</item>
/// </list>
/// 第 3 步与 B8 的差别在于执行时机：attempt 1 的结果若在 attempt 2 <b>进行中</b>到达，
/// 状态仍是 RUNNING、<c>active_attempt_no=2</c>，此时命中第 3 步；
/// 若在 attempt 2 <b>已收尾后</b>到达，则命中第 2 步。两种情形都必须丢弃。
/// </para>
/// </remarks>
public static class ExecutionTerminalGuard
{
    /// <summary>摄入一条终态消息（result / error / cancel 的最终归宿）。</summary>
    /// <param name="current">DB 当前状态（调用方需在事务内 <c>FOR UPDATE</c> 读取）。</param>
    /// <param name="attemptNo">消息自带的 attempt_no。</param>
    /// <param name="envelopeId">envelope.id，作为 terminal 去重键。</param>
    /// <param name="incoming">期望落入的终态；传入非终态属调用方 bug。</param>
    public static TerminalIngestResult Ingest(
        ExecutionState current,
        int attemptNo,
        Guid envelopeId,
        ExecutionStatus incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNo, 1);

        if (!incoming.IsTerminal())
        {
            throw new ArgumentOutOfRangeException(
                nameof(incoming), incoming, "终态摄入只接受 SUCCEEDED / FAILED / CANCELLED / TIMEOUT。");
        }

        // ① 精确重放：同 envelope.id 再来一次
        if (current.TerminalEnvelopeId is { } terminalId && terminalId == envelopeId)
        {
            return new TerminalIngestResult(
                TerminalIngestOutcome.DiscardedTerminalEnvelopeReplay, current, "duplicate terminal envelope.id");
        }

        // ② 已收敛：CAS 必然失败
        if (current.IsTerminal)
        {
            return new TerminalIngestResult(
                TerminalIngestOutcome.DiscardedAfterTerminal, current,
                $"execution already terminal ({current.Status.ToDbValue()})");
        }

        // ③ 迟到结果：不属于当前 active attempt
        if (current.ActiveAttemptNo != attemptNo)
        {
            return new TerminalIngestResult(
                TerminalIngestOutcome.DiscardedStaleAttempt, current,
                $"active_attempt_no={current.ActiveAttemptNo?.ToString() ?? "null"} != attempt_no={attemptNo}");
        }

        // ④ CAS 成功
        return new TerminalIngestResult(
            TerminalIngestOutcome.Applied,
            new ExecutionState(incoming, ActiveAttemptNo: null, TerminalEnvelopeId: envelopeId));
    }
}
