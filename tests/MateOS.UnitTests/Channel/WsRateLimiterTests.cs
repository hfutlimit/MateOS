using MateOS.Api.Channels;
using MateOS.Domain.Channel;

namespace MateOS.UnitTests.Channel;

/// <summary>
/// 覆盖 detailed/03 §6 单连接限速（10 msgs/sec 滑动窗口）。
/// </summary>
public sealed class WsRateLimiterTests
{
    [Fact]
    public void 默认上限应等于MaxFramesPerSec常量()
    {
        WsRateLimiter limiter = new();

        Assert.Equal(WsEnvelope.MaxFramesPerSec, limiter.MaxFramesPerSec);
        Assert.Equal(10, limiter.MaxFramesPerSec);
    }

    [Fact]
    public void 前N帧应通过_第N加1帧应被拒()
    {
        WsRateLimiter limiter = new(maxFramesPerSec: 5);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAccept(), $"第 {i + 1} 帧应通过");
        }

        Assert.False(limiter.TryAccept());
    }

    [Fact]
    public void Reset后应重新通过()
    {
        WsRateLimiter limiter = new(maxFramesPerSec: 2);

        Assert.True(limiter.TryAccept());
        Assert.True(limiter.TryAccept());
        Assert.False(limiter.TryAccept());

        limiter.Reset();

        Assert.True(limiter.TryAccept());
    }

    [Fact]
    public void 自定义上限应被采用()
    {
        WsRateLimiter limiter = new(maxFramesPerSec: 100);

        for (int i = 0; i < 100; i++)
        {
            Assert.True(limiter.TryAccept());
        }

        Assert.False(limiter.TryAccept());
    }
}
