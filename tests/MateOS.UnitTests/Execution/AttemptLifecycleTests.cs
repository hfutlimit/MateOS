using MateOS.Domain.Execution;
using MateOS.Domain.Lifecycles;

namespace MateOS.UnitTests.Execution;

/// <summary>
/// 覆盖 SYSTEM_DESIGN §5.2 的 attempt 5 值枚举与 detailed/01 §T+16 / §T+20 的迁移条件。
/// </summary>
public sealed class AttemptLifecycleTests
{
    public static TheoryData<AttemptStatus, AttemptStatus> LegalTransitions => new()
    {
        // STARTED → RUNNING 只在收到 dispatch_ack 之后（01 §T+16）
        { AttemptStatus.Started, AttemptStatus.Running },
        // 送达失败重试耗尽（B2）
        { AttemptStatus.Started, AttemptStatus.Failed },
        // 未启动即被取消 / 超时
        { AttemptStatus.Started, AttemptStatus.Interrupted },
        { AttemptStatus.Running, AttemptStatus.Completed },
        { AttemptStatus.Running, AttemptStatus.Failed },
        { AttemptStatus.Running, AttemptStatus.Interrupted },
    };

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void 合法迁移应被允许(AttemptStatus from, AttemptStatus to)
    {
        Assert.True(AttemptLifecycle.CanTransition(from, to));
        Assert.Equal(to, AttemptLifecycle.Transition(from, to));
    }

    [Theory]
    [InlineData(AttemptStatus.Started, AttemptStatus.Completed)]   // 跳步：没有 RUNNING 就不能收尾
    [InlineData(AttemptStatus.Started, AttemptStatus.Started)]
    [InlineData(AttemptStatus.Running, AttemptStatus.Started)]    // 不能回退
    [InlineData(AttemptStatus.Completed, AttemptStatus.Running)]  // 终态不可迁出
    [InlineData(AttemptStatus.Completed, AttemptStatus.Failed)]
    [InlineData(AttemptStatus.Failed, AttemptStatus.Running)]
    [InlineData(AttemptStatus.Interrupted, AttemptStatus.Running)]
    public void 非法迁移应被拒绝(AttemptStatus from, AttemptStatus to)
    {
        Assert.False(AttemptLifecycle.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => AttemptLifecycle.Transition(from, to));
    }

    [Theory]
    [InlineData(AttemptStatus.Completed)]
    [InlineData(AttemptStatus.Failed)]
    [InlineData(AttemptStatus.Interrupted)]
    public void 终态attempt不可再迁移(AttemptStatus terminal)
    {
        Assert.True(terminal.IsTerminal());

        foreach (AttemptStatus target in Enum.GetValues<AttemptStatus>())
        {
            Assert.False(AttemptLifecycle.CanTransition(terminal, target));
        }
    }

    [Theory]
    [InlineData(AttemptStatus.Started)]
    [InlineData(AttemptStatus.Running)]
    public void 在途attempt不是终态(AttemptStatus inFlight)
    {
        Assert.False(inFlight.IsTerminal());
    }

    [Theory]
    [InlineData(ExecutionStatus.Succeeded, AttemptStatus.Completed)]
    [InlineData(ExecutionStatus.Failed, AttemptStatus.Failed)]
    [InlineData(ExecutionStatus.Cancelled, AttemptStatus.Interrupted)]
    [InlineData(ExecutionStatus.Timeout, AttemptStatus.Interrupted)]
    public void 执行终态应正确映射到attempt终态(ExecutionStatus terminal, AttemptStatus expected)
    {
        Assert.Equal(expected, AttemptLifecycle.ForExecutionTerminal(terminal));
    }

    [Fact]
    public void 取消与超时在attempt层不可区分()
    {
        // SYSTEM_DESIGN §5.2：attempt 没有 CANCELLED / TIMEOUT，两者都收敛到 INTERRUPTED，
        // 区分度由 agent_executions.status 提供。
        Assert.Equal(
            AttemptLifecycle.ForExecutionTerminal(ExecutionStatus.Cancelled),
            AttemptLifecycle.ForExecutionTerminal(ExecutionStatus.Timeout));
    }

    [Theory]
    [InlineData(ExecutionStatus.Pending)]
    [InlineData(ExecutionStatus.Running)]
    public void 非终态执行不可映射到attempt终态(ExecutionStatus inFlight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AttemptLifecycle.ForExecutionTerminal(inFlight));
    }
}
