using MateOS.Domain.Execution;

namespace MateOS.UnitTests.Execution;

/// <summary>
/// 覆盖 detailed/10 §2 B7 的客户端半边与 detailed/03 §7.1「重复 dispatch 只重绑 WS + 再回 ACK」。
/// </summary>
public sealed class AgentDispatchDeduplicatorTests
{
    private static readonly Guid ExecutionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void 首次收到dispatch应建立上下文并启动执行()
    {
        var ledger = new AgentDispatchDeduplicator();

        DispatchReceiptOutcome outcome = ledger.Receive(ExecutionId, attemptNo: 1);

        Assert.Equal(DispatchReceiptOutcome.FirstReceipt, outcome);
        Assert.True(ledger.HasContext(ExecutionId, attemptNo: 1));
        Assert.Equal(1, ledger.TrackedAttemptCount);
    }

    [Fact]
    public void 重复收到同一attempt只应重绑而不重新执行()
    {
        var ledger = new AgentDispatchDeduplicator();
        ledger.Receive(ExecutionId, attemptNo: 1);

        DispatchReceiptOutcome second = ledger.Receive(ExecutionId, attemptNo: 1);
        DispatchReceiptOutcome third = ledger.Receive(ExecutionId, attemptNo: 1);

        Assert.Equal(DispatchReceiptOutcome.ReplayRebindOnly, second);
        Assert.Equal(DispatchReceiptOutcome.ReplayRebindOnly, third);
        Assert.Equal(1, ledger.TrackedAttemptCount);
    }

    [Fact]
    public void 同一次执行的第二个attempt是独立上下文()
    {
        // B8 场景的前提：retry 产生 attempt 2，它与 attempt 1 是不同的执行上下文
        var ledger = new AgentDispatchDeduplicator();

        Assert.Equal(DispatchReceiptOutcome.FirstReceipt, ledger.Receive(ExecutionId, attemptNo: 1));
        Assert.Equal(DispatchReceiptOutcome.FirstReceipt, ledger.Receive(ExecutionId, attemptNo: 2));
        Assert.Equal(2, ledger.TrackedAttemptCount);
    }

    [Fact]
    public void 不同执行的同名attempt互不干扰()
    {
        var ledger = new AgentDispatchDeduplicator();
        var other = Guid.NewGuid();

        ledger.Receive(ExecutionId, attemptNo: 1);

        Assert.False(ledger.HasContext(other, attemptNo: 1));
        Assert.Equal(DispatchReceiptOutcome.FirstReceipt, ledger.Receive(other, attemptNo: 1));
    }

    [Fact]
    public void 收尾后清理上下文可避免长驻进程无限增长()
    {
        var ledger = new AgentDispatchDeduplicator();
        ledger.Receive(ExecutionId, attemptNo: 1);

        Assert.True(ledger.Forget(ExecutionId, attemptNo: 1));
        Assert.False(ledger.HasContext(ExecutionId, attemptNo: 1));
        Assert.Equal(0, ledger.TrackedAttemptCount);
    }

    [Fact]
    public void 重复清理应无副作用()
    {
        var ledger = new AgentDispatchDeduplicator();

        Assert.False(ledger.Forget(ExecutionId, attemptNo: 1));
    }

    [Fact]
    public void 非法attempt序号应被拒绝()
    {
        var ledger = new AgentDispatchDeduplicator();

        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Receive(ExecutionId, attemptNo: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Receive(ExecutionId, attemptNo: -1));
    }
}
