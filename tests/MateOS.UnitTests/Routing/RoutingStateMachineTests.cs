using MateOS.Domain.Routing;

namespace MateOS.UnitTests.Routing;

/// <summary>
/// 覆盖 E4 §2.1 CR 6 态 + Decision 4 态 状态机（v0.4.1 收敛口径）。
/// </summary>
public sealed class CrStatusTests
{
    [Theory]
    [InlineData("PENDING", CrStatus.PENDING)]
    [InlineData("ACCEPTED", CrStatus.ACCEPTED)]
    [InlineData("REJECTED", CrStatus.REJECTED)]
    [InlineData("NEED_CONTEXT", CrStatus.NEED_CONTEXT)]
    [InlineData("UNRESOLVED", CrStatus.UNRESOLVED)]
    [InlineData("CANCELLED", CrStatus.CANCELLED)]
    [InlineData("invalid", null)]
    public void CrStatusMap应正确解析(string raw, CrStatus? expected)
    {
        bool ok = CrStatusMap.TryParse(raw, out CrStatus status);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, status);
        }
    }

    [Theory]
    [InlineData(CrStatus.PENDING, false)]
    [InlineData(CrStatus.ACCEPTED, true)]
    [InlineData(CrStatus.REJECTED, true)]
    [InlineData(CrStatus.NEED_CONTEXT, true)]
    [InlineData(CrStatus.UNRESOLVED, true)]
    [InlineData(CrStatus.CANCELLED, true)]
    public void IsTerminal应正确判定(CrStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsTerminal());
    }
}

public sealed class CrDecisionTests
{
    [Theory]
    [InlineData("ACCEPT", CrDecision.ACCEPT)]
    [InlineData("REJECT", CrDecision.REJECT)]
    [InlineData("NEED_CONTEXT", CrDecision.NEED_CONTEXT)]
    [InlineData("CANCEL", CrDecision.CANCEL)]
    [InlineData("invalid", null)]
    public void CrDecisionMap应正确解析(string raw, CrDecision? expected)
    {
        bool ok = CrDecisionMap.TryParse(raw, out CrDecision decision);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, decision);
        }
    }
}

public sealed class CrStateMachineTests
{
    [Theory]
    [InlineData(CrStatus.PENDING, CrStatus.ACCEPTED, true)]
    [InlineData(CrStatus.PENDING, CrStatus.REJECTED, true)]
    [InlineData(CrStatus.PENDING, CrStatus.NEED_CONTEXT, true)]
    [InlineData(CrStatus.PENDING, CrStatus.UNRESOLVED, true)]
    [InlineData(CrStatus.PENDING, CrStatus.CANCELLED, true)]
    [InlineData(CrStatus.PENDING, CrStatus.PENDING, false)]   // 自环不允许
    public void PENDING应能迁到5种终态(CrStatus from, CrStatus to, bool expected)
    {
        Assert.Equal(expected, CrStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(CrStatus.ACCEPTED)]
    [InlineData(CrStatus.REJECTED)]
    [InlineData(CrStatus.NEED_CONTEXT)]
    [InlineData(CrStatus.UNRESOLVED)]
    [InlineData(CrStatus.CANCELLED)]
    public void 终态不应再迁(CrStatus terminal)
    {
        Assert.False(CrStateMachine.CanTransition(terminal, CrStatus.ACCEPTED));
        Assert.False(CrStateMachine.CanTransition(terminal, CrStatus.REJECTED));
        Assert.NotNull(CrStateMachine.WhyCannotTransition(terminal, CrStatus.ACCEPTED));
    }

    [Fact]
    public void WhyCannotTransition应包含详细说明()
    {
        string? error = CrStateMachine.WhyCannotTransition(CrStatus.ACCEPTED, CrStatus.REJECTED);
        Assert.NotNull(error);
        Assert.Contains("ACCEPTED", error);
    }
}

public sealed class TriggerAndKindTests
{
    [Theory]
    [InlineData("MENTION", TriggerType.MENTION)]
    [InlineData("WORK_ITEM", TriggerType.WORK_ITEM)]
    [InlineData("API", TriggerType.API)]
    [InlineData("AUTOMATION", TriggerType.AUTOMATION)]
    public void TriggerTypeMap应正确解析(string raw, TriggerType expected)
    {
        Assert.True(TriggerTypeMap.TryParse(raw, out TriggerType t));
        Assert.Equal(expected, t);
    }

    [Theory]
    [InlineData("MESSAGE_RESPONSE", CrRequestKind.MESSAGE_RESPONSE)]
    [InlineData("WORK_ITEM_EXECUTION", CrRequestKind.WORK_ITEM_EXECUTION)]
    [InlineData("API_CALL", CrRequestKind.API_CALL)]
    [InlineData("AUTOMATION_RUN", CrRequestKind.AUTOMATION_RUN)]
    public void CrRequestKindMap应正确解析(string raw, CrRequestKind expected)
    {
        Assert.True(CrRequestKindMap.TryParse(raw, out CrRequestKind k));
        Assert.Equal(expected, k);
    }

    [Theory]
    [InlineData("USER", TriggerActorType.USER)]
    [InlineData("AGENT", TriggerActorType.AGENT)]
    [InlineData("SYSTEM", TriggerActorType.SYSTEM)]
    public void TriggerActorTypeMap应正确解析(string raw, TriggerActorType expected)
    {
        Assert.True(TriggerActorTypeMap.TryParse(raw, out TriggerActorType t));
        Assert.Equal(expected, t);
    }
}
