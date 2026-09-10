using MateOS.Domain.Execution;

namespace MateOS.UnitTests.Execution;

/// <summary>
/// 覆盖 detailed/10 §2 B2（dispatch 未 ACK）与 detailed/03 §7.1 的补洞路径。
/// 关键断言：重试始终落在<b>同一</b> attempt 上，不新建 attempt、不回 Resolver 重路由。
/// </summary>
public sealed class DispatchAckWatchdogTests
{
    [Fact]
    public void 等待窗口内应保持静默()
    {
        AckWatchdogAction action = DispatchAckWatchdog.Plan(
            sinceDispatchSent: TimeSpan.FromSeconds(5),
            sinceResumeProbe: null,
            redispatchCount: 0,
            maxRedispatch: 4);

        Assert.Equal(AckWatchdogAction.KeepWaiting, action);
    }

    [Fact]
    public void 超过等待窗口应先探测续传而非直接重派()
    {
        AckWatchdogAction action = DispatchAckWatchdog.Plan(
            sinceDispatchSent: TimeSpan.FromSeconds(16),
            sinceResumeProbe: null,
            redispatchCount: 0,
            maxRedispatch: 4);

        Assert.Equal(AckWatchdogAction.SendResumeProbe, action);
    }

    [Fact]
    public void 探测发出后窗口内应继续等待()
    {
        AckWatchdogAction action = DispatchAckWatchdog.Plan(
            sinceDispatchSent: TimeSpan.FromSeconds(20),
            sinceResumeProbe: TimeSpan.FromSeconds(3),
            redispatchCount: 0,
            maxRedispatch: 4);

        Assert.Equal(AckWatchdogAction.KeepWaiting, action);
    }

    [Fact]
    public void 探测亦无响应才重派同一attempt()
    {
        AckWatchdogAction action = DispatchAckWatchdog.Plan(
            sinceDispatchSent: TimeSpan.FromSeconds(40),
            sinceResumeProbe: TimeSpan.FromSeconds(20),
            redispatchCount: 1,
            maxRedispatch: 4);

        Assert.Equal(AckWatchdogAction.RedispatchSameAttempt, action);
    }

    [Fact]
    public void 重派次数达上限应收敛为失败()
    {
        // B2：超阈值 → Execution FAILED + 进 Inbox；CR 保持 ACCEPTED、不重路由、不新建 attempt
        AckWatchdogAction action = DispatchAckWatchdog.Plan(
            sinceDispatchSent: TimeSpan.FromMinutes(5),
            sinceResumeProbe: TimeSpan.FromSeconds(30),
            redispatchCount: 4,
            maxRedispatch: 4);

        Assert.Equal(AckWatchdogAction.GiveUpAndFail, action);
    }

    [Fact]
    public void 默认等待窗口应为十五秒()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), DispatchAckWatchdog.DefaultAckWait);

        // 边界：刚好 15s 视为到点（触发探测）
        Assert.Equal(
            AckWatchdogAction.SendResumeProbe,
            DispatchAckWatchdog.Plan(TimeSpan.FromSeconds(15), null, 0, 4));
    }

    [Fact]
    public void 等待窗口应可注入覆盖()
    {
        AckWatchdogAction action = DispatchAckWatchdog.Plan(
            sinceDispatchSent: TimeSpan.FromSeconds(4),
            sinceResumeProbe: null,
            redispatchCount: 0,
            maxRedispatch: 4,
            ackWait: TimeSpan.FromSeconds(3));

        Assert.Equal(AckWatchdogAction.SendResumeProbe, action);
    }

    [Fact]
    public void 负的计数应被拒绝()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DispatchAckWatchdog.Plan(TimeSpan.Zero, null, redispatchCount: -1, maxRedispatch: 4));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DispatchAckWatchdog.Plan(TimeSpan.Zero, null, redispatchCount: 0, maxRedispatch: -1));
    }
}
