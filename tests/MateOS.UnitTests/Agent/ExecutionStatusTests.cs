using MateOS.Domain.Agent;

namespace MateOS.UnitTests.Agent;

/// <summary>
/// 覆盖 E7 ExecutionStatus / AttemptStatus 5+6 态 + 终态判定。
/// </summary>
public sealed class ExecutionStatusTests
{
    [Theory]
    [InlineData("PENDING", ExecutionStatus.PENDING)]
    [InlineData("RUNNING", ExecutionStatus.RUNNING)]
    [InlineData("SUCCEEDED", ExecutionStatus.SUCCEEDED)]
    [InlineData("FAILED", ExecutionStatus.FAILED)]
    [InlineData("CANCELLED", ExecutionStatus.CANCELLED)]
    [InlineData("invalid", null)]
    public void ExecutionStatusMap应正确解析(string raw, ExecutionStatus? expected)
    {
        bool ok = ExecutionStatusMap.TryParse(raw, out ExecutionStatus status);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, status);
        }
    }

    [Theory]
    [InlineData(ExecutionStatus.SUCCEEDED, true)]
    [InlineData(ExecutionStatus.FAILED, true)]
    [InlineData(ExecutionStatus.CANCELLED, true)]
    [InlineData(ExecutionStatus.PENDING, false)]
    [InlineData(ExecutionStatus.RUNNING, false)]
    public void IsTerminal应正确判定(ExecutionStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsTerminal());
    }

    [Theory]
    [InlineData("PENDING", AttemptStatus.PENDING)]
    [InlineData("DISPATCHED", AttemptStatus.DISPATCHED)]
    [InlineData("RUNNING", AttemptStatus.RUNNING)]
    [InlineData("COMPLETED", AttemptStatus.COMPLETED)]
    [InlineData("FAILED", AttemptStatus.FAILED)]
    [InlineData("CANCELLED", AttemptStatus.CANCELLED)]
    [InlineData("invalid", null)]
    public void AttemptStatusMap应正确解析(string raw, AttemptStatus? expected)
    {
        bool ok = AttemptStatusMap.TryParse(raw, out AttemptStatus status);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, status);
        }
    }

    [Theory]
    [InlineData(AttemptStatus.COMPLETED, true)]
    [InlineData(AttemptStatus.FAILED, true)]
    [InlineData(AttemptStatus.CANCELLED, true)]
    [InlineData(AttemptStatus.PENDING, false)]
    [InlineData(AttemptStatus.DISPATCHED, false)]
    [InlineData(AttemptStatus.RUNNING, false)]
    public void AttemptIsTerminal应正确判定(AttemptStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsTerminal());
    }
}

/// <summary>
/// 覆盖 v0.4.3 contiguous cursor（detailed/03 §3.2）。
/// </summary>
public sealed class AttemptEventCursorTests
{
    [Fact]
    public void FirstSeq应从1开始()
    {
        Assert.Equal(1L, AttemptEventCursor.ExpectedSeq(0));
    }

    [Fact]
    public void ExpectedSeq应返lastPersistedSeq加1()
    {
        Assert.Equal(42L, AttemptEventCursor.ExpectedSeq(41));
    }

    [Fact]
    public void Decide应接受expectedSeq()
    {
        Assert.Equal(AttemptEventCursor.AcceptDecision.Accept,
            AttemptEventCursor.Decide(seq: 1, lastPersistedSeq: 0));
    }

    [Fact]
    public void Decide应识别duplicate_seqs_小于expected()
    {
        Assert.Equal(AttemptEventCursor.AcceptDecision.Duplicate,
            AttemptEventCursor.Decide(seq: 5, lastPersistedSeq: 10));
    }

    [Fact]
    public void Decide应识别gap_seqs_大于expected()
    {
        Assert.Equal(AttemptEventCursor.AcceptDecision.Gap,
            AttemptEventCursor.Decide(seq: 12, lastPersistedSeq: 10));
    }

    [Fact]
    public void Decide应拒绝非法seq_0或负()
    {
        Assert.Equal(AttemptEventCursor.AcceptDecision.Gap,
            AttemptEventCursor.Decide(seq: 0, lastPersistedSeq: 0));
        Assert.Equal(AttemptEventCursor.AcceptDecision.Gap,
            AttemptEventCursor.Decide(seq: -1, lastPersistedSeq: 0));
    }

    [Fact]
    public void Advance应严格contiguous()
    {
        Assert.Equal(5L, AttemptEventCursor.Advance(lastPersistedSeq: 4, writtenSeq: 5));
    }

    [Fact]
    public void Advance应拒绝跳号()
    {
        // 写入了 5 但 lastPersistedSeq 还是 4，OK
        // 如果试图从 4 跳到 6，应该抛错
        Assert.Throws<InvalidOperationException>(() =>
            AttemptEventCursor.Advance(lastPersistedSeq: 4, writtenSeq: 6));
    }
}

/// <summary>
/// 覆盖 detailed/01 §1 T+20 终态 CAS 守卫 + idempotency 三层。
/// </summary>
public sealed class ExecutionTerminalGuardTests
{
    [Theory]
    [InlineData(ExecutionStatus.PENDING, true)]
    [InlineData(ExecutionStatus.RUNNING, true)]
    [InlineData(ExecutionStatus.SUCCEEDED, false)]
    [InlineData(ExecutionStatus.FAILED, false)]
    [InlineData(ExecutionStatus.CANCELLED, false)]
    public void CanTransitionToTerminal应正确判定(ExecutionStatus current, bool expected)
    {
        Assert.Equal(expected, ExecutionTerminalGuard.CanTransitionToTerminal(current));
    }

    [Theory]
    [InlineData(AttemptStatus.PENDING, true)]
    [InlineData(AttemptStatus.DISPATCHED, true)]
    [InlineData(AttemptStatus.RUNNING, true)]
    [InlineData(AttemptStatus.COMPLETED, false)]
    [InlineData(AttemptStatus.FAILED, false)]
    [InlineData(AttemptStatus.CANCELLED, false)]
    public void CanAttemptTransitionToTerminal应正确判定(AttemptStatus current, bool expected)
    {
        Assert.Equal(expected, ExecutionTerminalGuard.CanAttemptTransitionToTerminal(current));
    }

    [Fact]
    public void WhyIgnoringIdempotentResult应返null_当非终态()
    {
        Assert.Null(ExecutionTerminalGuard.WhyIgnoringIdempotentResult(ExecutionStatus.RUNNING, "env-1"));
    }

    [Fact]
    public void WhyIgnoringIdempotentResult应返说明_当终态()
    {
        string? msg = ExecutionTerminalGuard.WhyIgnoringIdempotentResult(ExecutionStatus.SUCCEEDED, "env-1");
        Assert.NotNull(msg);
        Assert.Contains("SUCCEEDED", msg);
        Assert.Contains("env-1", msg);
    }
}
