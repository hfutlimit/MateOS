namespace MateOS.Domain.Execution;

/// <summary>Agent SDK 收到一次 dispatch 后的处置方式。</summary>
public enum DispatchReceiptOutcome
{
    /// <summary>首次收到该 <c>(execution_id, attempt_no)</c>：建立 attempt 上下文、立即回 ACK、启动执行。</summary>
    FirstReceipt,

    /// <summary>
    /// 重复收到同一 <c>(execution_id, attempt_no)</c>：<b>只重新绑定 WS 会话并再回一次 ACK</b>，绝不重新执行。
    /// </summary>
    ReplayRebindOnly,
}

/// <summary>
/// Agent 侧的 dispatch 幂等账本（SDK 责任）。
/// </summary>
/// <remarks>
/// <para>
/// 依据 detailed/03 §7.1 与 detailed/10 §2 B7 / §4。
/// 「客户端按 <c>(execution_id, attempt_no)</c> 幂等」是 Runtime 能安全重派的<b>唯一前提</b>：
/// Runtime 重启、ACK 丢失、网络抖动都会导致同一条 dispatch 被送达两次，
/// SDK 若重复执行，等于重复写文件、重复计费。
/// </para>
/// <para>
/// 注意归属：这段逻辑属于 <b>Agent SDK / stub</b>，不属于 Runtime。
/// Runtime 侧代码不得出现任何 stub 分支（detailed/10 §3）。
/// 放在同一领域程序集只是因为它是「执行不变量」的客户端半边，共享同一套术语。
/// </para>
/// </remarks>
public sealed class AgentDispatchDeduplicator
{
    private readonly HashSet<(Guid ExecutionId, int AttemptNo)> _received = [];

    /// <summary>已建立上下文的 attempt 数量（诊断用）。</summary>
    public int TrackedAttemptCount => _received.Count;

    /// <summary>
    /// 记录一次 dispatch 送达，并返回 SDK 应采取的处置。
    /// 两种结果都必须回 <c>execution.dispatch_ack{received:true}</c>，差别只在「是否启动执行」。
    /// </summary>
    public DispatchReceiptOutcome Receive(Guid executionId, int attemptNo)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNo, 1);

        return _received.Add((executionId, attemptNo))
            ? DispatchReceiptOutcome.FirstReceipt
            : DispatchReceiptOutcome.ReplayRebindOnly;
    }

    /// <summary>判断某 attempt 是否已建立上下文（用于校验 <c>execution.event</c> 的合法性）。</summary>
    public bool HasContext(Guid executionId, int attemptNo) => _received.Contains((executionId, attemptNo));

    /// <summary>
    /// 在 attempt 收尾后清理上下文，避免长驻进程无限增长。
    /// 幂等：重复清理无副作用。
    /// </summary>
    public bool Forget(Guid executionId, int attemptNo) => _received.Remove((executionId, attemptNo));
}
