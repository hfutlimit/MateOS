using MateOS.Domain.Execution;
using MateOS.Domain.Lifecycles;

namespace MateOS.UnitTests.Execution;

/// <summary>
/// 覆盖 detailed/03 §4.2 / §9 的 test_006（restart 不重派）与 test_007（restart 重派未 ACK 者）。
/// </summary>
public sealed class RuntimeRestartPlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T12:00:00Z");

    [Fact]
    public void 已ACK的在途attempt重启后绝不重派()
    {
        // 03 §9 test_006：Given 3 in-flight executions, dispatch_acked_at all set,
        //                  When Runtime restarts, Then no re-dispatch, all 3 wait for resume
        var execution = new ExecutionState(ExecutionStatus.Running, ActiveAttemptNo: 1, TerminalEnvelopeId: null);
        var attempt = new AttemptRecoverySnapshot(
            AttemptNo: 1,
            Status: AttemptStatus.Running,
            DispatchSentAt: Now.AddSeconds(-30),
            DispatchAckedAt: Now.AddSeconds(-25),
            LastPersistedSeq: 7);

        RestartRecoveryAction action = RuntimeRestartPlanner.Plan(execution, attempt);

        Assert.Equal(RestartRecoveryAction.WaitForResume, action);
    }

    [Fact]
    public void 未ACK的自洽attempt重启后应重派同一attempt()
    {
        // 03 §9 test_007：Given 2 in-flight, dispatch_acked_at both null,
        //                  When Runtime restarts, Then both re-dispatched
        var execution = new ExecutionState(ExecutionStatus.Pending, ActiveAttemptNo: 1, TerminalEnvelopeId: null);
        var attempt = new AttemptRecoverySnapshot(
            AttemptNo: 1,
            Status: AttemptStatus.Started,
            DispatchSentAt: Now.AddSeconds(-30),
            DispatchAckedAt: null,
            LastPersistedSeq: 0);

        RestartRecoveryAction action = RuntimeRestartPlanner.Plan(execution, attempt);

        Assert.Equal(RestartRecoveryAction.ReDispatch, action);
    }

    [Fact]
    public void 运行中却未ACK的矛盾态应先探测续传而非直接重派()
    {
        // 03 §7.1 补洞行：execution=RUNNING 但 dispatch_acked_at IS NULL
        //                 → 先发 resume_request，ack_wait_s 内无响应才重派同一 attempt_no
        var execution = new ExecutionState(ExecutionStatus.Running, ActiveAttemptNo: 1, TerminalEnvelopeId: null);
        var attempt = new AttemptRecoverySnapshot(
            AttemptNo: 1,
            Status: AttemptStatus.Running,
            DispatchSentAt: Now.AddSeconds(-30),
            DispatchAckedAt: null,
            LastPersistedSeq: 3);

        RestartRecoveryAction action = RuntimeRestartPlanner.Plan(execution, attempt);

        Assert.Equal(RestartRecoveryAction.ProbeResumeThenRedispatch, action);
    }

    [Fact]
    public void 无activeAttempt的在途执行应被隔离()
    {
        // 03 §4.2 第三行：无 attempt（异常）→ cancel + 通知 owner
        var execution = new ExecutionState(ExecutionStatus.Running, ActiveAttemptNo: null, TerminalEnvelopeId: null);

        RestartRecoveryAction action = RuntimeRestartPlanner.Plan(execution, activeAttempt: null);

        Assert.Equal(RestartRecoveryAction.QuarantineNoActiveAttempt, action);
    }

    [Theory]
    [InlineData(ExecutionStatus.Succeeded)]
    [InlineData(ExecutionStatus.Failed)]
    [InlineData(ExecutionStatus.Cancelled)]
    [InlineData(ExecutionStatus.Timeout)]
    public void 终态执行不进入恢复流程(ExecutionStatus status)
    {
        var execution = new ExecutionState(status, ActiveAttemptNo: null, TerminalEnvelopeId: Guid.NewGuid());
        var attempt = new AttemptRecoverySnapshot(1, AttemptStatus.Completed, Now, Now, 9);

        RestartRecoveryAction action = RuntimeRestartPlanner.Plan(execution, attempt);

        Assert.Equal(RestartRecoveryAction.SkipAlreadyTerminal, action);
    }

    [Theory]
    [InlineData(AttemptStatus.Completed, true)]
    [InlineData(AttemptStatus.Completed, false)]
    [InlineData(AttemptStatus.Failed, true)]
    [InlineData(AttemptStatus.Failed, false)]
    [InlineData(AttemptStatus.Interrupted, true)]
    [InlineData(AttemptStatus.Interrupted, false)]
    public void 在途执行上出现终态attempt应按数据异常隔离(AttemptStatus terminal, bool acknowledged)
    {
        // 终态 attempt 的判定优先于 dispatch_acked_at：
        // 重派会让已结束的 attempt 复活，等 resume 则永远等不到（Agent 不会再主动续传），
        // 因此无论 ACK 是否存在都必须隔离并通知 owner。
        var execution = new ExecutionState(ExecutionStatus.Running, ActiveAttemptNo: 1, TerminalEnvelopeId: null);
        var inconsistent = new AttemptRecoverySnapshot(
            AttemptNo: 1,
            Status: terminal,
            DispatchSentAt: Now,
            DispatchAckedAt: acknowledged ? Now : null,
            LastPersistedSeq: 9);

        RestartRecoveryAction action = RuntimeRestartPlanner.Plan(execution, inconsistent);

        Assert.Equal(RestartRecoveryAction.QuarantineNoActiveAttempt, action);
    }

    [Fact]
    public void 已ACK判定只依据ack时间戳而非状态()
    {
        var acknowledged = new AttemptRecoverySnapshot(1, AttemptStatus.Running, Now, Now, 0);
        var notAcknowledged = new AttemptRecoverySnapshot(1, AttemptStatus.Running, Now, null, 0);

        Assert.True(acknowledged.IsAcknowledged);
        Assert.False(notAcknowledged.IsAcknowledged);
    }
}
