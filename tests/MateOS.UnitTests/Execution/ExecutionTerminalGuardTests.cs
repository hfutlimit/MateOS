using MateOS.Domain.Execution;
using MateOS.Domain.Lifecycles;

namespace MateOS.UnitTests.Execution;

/// <summary>
/// 覆盖 I6（终态 CAS）与 detailed/10 §2 的 B7（重复投递）、B8（迟到结果）。
/// </summary>
public sealed class ExecutionTerminalGuardTests
{
    private static readonly Guid EnvelopeA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EnvelopeB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void 在途执行收到匹配attempt的终态应成功收敛()
    {
        // 01 §T+20：CAS UPDATE ... WHERE status IN ('PENDING','RUNNING')，成功后 active_attempt_no 置 NULL
        var running = new ExecutionState(ExecutionStatus.Running, ActiveAttemptNo: 1, TerminalEnvelopeId: null);

        TerminalIngestResult result = ExecutionTerminalGuard.Ingest(
            running, attemptNo: 1, envelopeId: EnvelopeA, incoming: ExecutionStatus.Succeeded);

        Assert.True(result.WasApplied);
        Assert.Equal(ExecutionStatus.Succeeded, result.State.Status);
        Assert.Null(result.State.ActiveAttemptNo);
        Assert.Equal(EnvelopeA, result.State.TerminalEnvelopeId);
    }

    [Fact]
    public void 在Pending态也应能直接收敛()
    {
        var pending = ExecutionState.Opened();

        TerminalIngestResult result = ExecutionTerminalGuard.Ingest(
            pending, attemptNo: 1, envelopeId: EnvelopeA, incoming: ExecutionStatus.Failed);

        Assert.True(result.WasApplied);
        Assert.Equal(ExecutionStatus.Failed, result.State.Status);
    }

    [Fact]
    public void 同一终态信封重复到达应判为重放()
    {
        // B7：Agent 重发 result，envelope.id 相同 → terminal_envelope_id 已存在 → 忽略
        var terminal = new ExecutionState(ExecutionStatus.Succeeded, ActiveAttemptNo: null, TerminalEnvelopeId: EnvelopeA);

        TerminalIngestResult result = ExecutionTerminalGuard.Ingest(
            terminal, attemptNo: 1, envelopeId: EnvelopeA, incoming: ExecutionStatus.Succeeded);

        Assert.Equal(TerminalIngestOutcome.DiscardedTerminalEnvelopeReplay, result.Outcome);
        Assert.False(result.WasApplied);
        Assert.Equal(ExecutionStatus.Succeeded, result.State.Status);
    }

    [Fact]
    public void 迟到结果应在attempt已轮换时被丢弃()
    {
        // B8：attempt 1 的结果在 retry 到 attempt 2 之后才到。
        // 此时 execution 仍在 RUNNING，active_attempt_no=2 → 结果必须丢弃，attempt 2 的收尾不受影响。
        var runningAttempt2 = new ExecutionState(ExecutionStatus.Running, ActiveAttemptNo: 2, TerminalEnvelopeId: null);

        TerminalIngestResult late = ExecutionTerminalGuard.Ingest(
            runningAttempt2, attemptNo: 1, envelopeId: EnvelopeB, incoming: ExecutionStatus.Succeeded);

        Assert.Equal(TerminalIngestOutcome.DiscardedStaleAttempt, late.Outcome);
        Assert.False(late.WasApplied);

        // attempt 2 自己的结果照常生效
        TerminalIngestResult own = ExecutionTerminalGuard.Ingest(
            late.State, attemptNo: 2, envelopeId: EnvelopeB, incoming: ExecutionStatus.Succeeded);

        Assert.True(own.WasApplied);
    }

    [Fact]
    public void 无activeAttempt时应拒绝任何终态摄入()
    {
        var inFlightButNoAttempt = new ExecutionState(ExecutionStatus.Running, ActiveAttemptNo: null, TerminalEnvelopeId: null);

        TerminalIngestResult result = ExecutionTerminalGuard.Ingest(
            inFlightButNoAttempt, attemptNo: 1, envelopeId: EnvelopeA, incoming: ExecutionStatus.Succeeded);

        Assert.Equal(TerminalIngestOutcome.DiscardedStaleAttempt, result.Outcome);
    }

    [Fact]
    public void 已收敛后再来新信封应被忽略()
    {
        // 03 §3.5：Agent 重发 result with new envelope.id + status=SUCCEEDED → CAS 失败（已是 SUCCEEDED）→ 忽略
        var terminal = new ExecutionState(ExecutionStatus.Succeeded, ActiveAttemptNo: null, TerminalEnvelopeId: EnvelopeA);

        TerminalIngestResult result = ExecutionTerminalGuard.Ingest(
            terminal, attemptNo: 1, envelopeId: EnvelopeB, incoming: ExecutionStatus.Succeeded);

        Assert.Equal(TerminalIngestOutcome.DiscardedAfterTerminal, result.Outcome);
        Assert.Equal(EnvelopeA, result.State.TerminalEnvelopeId);
    }

    [Theory]
    [InlineData(ExecutionStatus.Succeeded)]
    [InlineData(ExecutionStatus.Failed)]
    [InlineData(ExecutionStatus.Cancelled)]
    [InlineData(ExecutionStatus.Timeout)]
    public void 四种终态都应被接受(ExecutionStatus incoming)
    {
        var running = new ExecutionState(ExecutionStatus.Running, ActiveAttemptNo: 1, TerminalEnvelopeId: null);

        TerminalIngestResult result = ExecutionTerminalGuard.Ingest(
            running, attemptNo: 1, envelopeId: EnvelopeA, incoming: incoming);

        Assert.True(result.WasApplied);
        Assert.Equal(incoming, result.State.Status);
    }

    [Theory]
    [InlineData(ExecutionStatus.Pending)]
    [InlineData(ExecutionStatus.Running)]
    public void 非终态摄入应被拒绝(ExecutionStatus incoming)
    {
        var running = new ExecutionState(ExecutionStatus.Running, ActiveAttemptNo: 1, TerminalEnvelopeId: null);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExecutionTerminalGuard.Ingest(running, attemptNo: 1, envelopeId: EnvelopeA, incoming: incoming));
    }

    [Fact]
    public void 非法attempt序号应被拒绝()
    {
        var running = new ExecutionState(ExecutionStatus.Running, ActiveAttemptNo: 1, TerminalEnvelopeId: null);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExecutionTerminalGuard.Ingest(running, attemptNo: 0, envelopeId: EnvelopeA, incoming: ExecutionStatus.Succeeded));
    }

    [Fact]
    public void 新开启的执行应处于Pending且已挂第一个attempt()
    {
        ExecutionState opened = ExecutionState.Opened();

        Assert.Equal(ExecutionStatus.Pending, opened.Status);
        Assert.Equal(1, opened.ActiveAttemptNo);
        Assert.Null(opened.TerminalEnvelopeId);
        Assert.False(opened.IsTerminal);
    }
}
