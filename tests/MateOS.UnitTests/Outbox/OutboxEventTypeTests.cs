using MateOS.Domain.Outbox;

namespace MateOS.UnitTests.Outbox;

/// <summary>
/// 覆盖 M7 Outbox 5 个 canonical event_type + 3 态 + 重试策略 + CapacityInvariant。
/// </summary>
public sealed class OutboxEventTypeTests
{
    [Theory]
    [InlineData("collaboration.accepted")]
    [InlineData("collaboration.timeout")]
    [InlineData("execution.created")]
    [InlineData("execution.completed")]
    [InlineData("execution.cancelled")]
    public void Validate应接受canonical_event_type(string eventType)
    {
        Assert.Null(OutboxEventType.Validate(eventType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown.event")]
    [InlineData(null)]
    public void Validate应拒绝非法event_type(string? eventType)
    {
        Assert.NotNull(OutboxEventType.Validate(eventType));
    }

    [Theory]
    [InlineData("PENDING", OutboxStatus.PENDING)]
    [InlineData("PUBLISHED", OutboxStatus.PUBLISHED)]
    [InlineData("DEAD", OutboxStatus.DEAD)]
    [InlineData("invalid", null)]
    public void OutboxStatusMap应正确解析(string raw, OutboxStatus? expected)
    {
        bool ok = OutboxStatusMap.TryParse(raw, out OutboxStatus status);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, status);
        }
    }
}

/// <summary>
/// 覆盖 M7 指数退避 + CapacityInvariant。
/// </summary>
public sealed class OutboxRetryAndCapacityTests
{
    [Fact]
    public void NextDelay应单调递增且封顶5min()
    {
        TimeSpan d1 = OutboxRetryPolicy.NextDelay(1);
        TimeSpan d2 = OutboxRetryPolicy.NextDelay(2);
        TimeSpan d3 = OutboxRetryPolicy.NextDelay(3);
        TimeSpan d4 = OutboxRetryPolicy.NextDelay(4);
        TimeSpan d10 = OutboxRetryPolicy.NextDelay(10);

        Assert.True(d2 > d1, $"d2({d2}) 应 > d1({d1})");
        Assert.True(d3 > d2, $"d3({d3}) 应 > d2({d2})");
        Assert.True(d4 > d3, $"d4({d4}) 应 > d3({d3})");
        // attempt >= 5 时封顶 5min（300s）
        Assert.Equal(300.0, d10.TotalSeconds, 0);
    }

    [Fact]
    public void ShouldMarkDead第5次应true()
    {
        Assert.False(OutboxRetryPolicy.ShouldMarkDead(1));
        Assert.False(OutboxRetryPolicy.ShouldMarkDead(4));
        Assert.True(OutboxRetryPolicy.ShouldMarkDead(5));
        Assert.True(OutboxRetryPolicy.ShouldMarkDead(6));
    }

    [Fact]
    public void CapacityInvariant应正确判定超限()
    {
        // max_concurrency=2，in-flight=0/1 OK，in-flight=2 拒
        Assert.False(CapacityInvariant.WouldExceed(currentInFlight: 0, maxConcurrency: 2));
        Assert.False(CapacityInvariant.WouldExceed(currentInFlight: 1, maxConcurrency: 2));
        Assert.True(CapacityInvariant.WouldExceed(currentInFlight: 2, maxConcurrency: 2));
    }

    [Fact]
    public void CapacityInvariant全局封顶32应强于max_concurrency()
    {
        // max_concurrency=100 但全局上限 32，in-flight=32 应拒
        Assert.True(CapacityInvariant.WouldExceed(currentInFlight: 32, maxConcurrency: 100));
        Assert.False(CapacityInvariant.WouldExceed(currentInFlight: 31, maxConcurrency: 100));
    }
}
