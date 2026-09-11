using MateOS.Domain.Channel;

namespace MateOS.UnitTests.Channel;

/// <summary>
/// 覆盖 E3 §3 channel_seq_counters 的不变量（since_seq / limit 校验 + resume 续传起点计算）。
/// </summary>
/// <remarks>
/// SQL 路径（事务内 UPDATE ... RETURNING）由 <c>tests/MateOS.IntegrationTests</c> 覆盖，
/// 本类只覆盖纯函数不变量。
/// </remarks>
public sealed class ChannelSeqTests
{
    [Fact]
    public void FirstSeq应从1开始()
    {
        Assert.Equal(1L, ChannelSeq.FirstSeq);
    }

    [Theory]
    [InlineData(null, null)]    // null 表示从头开始
    [InlineData(0L, null)]      // 0 合法（表示"从第一条开始"）
    [InlineData(100L, null)]    // 任意正整数合法
    public void ValidateSinceSeq应接受合法值(long? sinceSeq, string? expectedErrorFragment)
    {
        Assert.Null(ChannelSeq.ValidateSinceSeq(sinceSeq));
    }

    [Fact]
    public void ValidateSinceSeq应拒绝负数()
    {
        string? error = ChannelSeq.ValidateSinceSeq(-1L);
        Assert.NotNull(error);
        Assert.Contains("since_seq", error);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1, null)]
    [InlineData(200, null)]
    [InlineData(0, "limit 必须为正整数")]
    [InlineData(-1, "limit 必须为正整数")]
    [InlineData(201, "limit 不能超过 200")]
    public void ValidateLimit应按边界拒绝(int? limit, string? expectedErrorFragment)
    {
        string? error = ChannelSeq.ValidateLimit(limit);

        if (expectedErrorFragment is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.NotNull(error);
            Assert.Contains(expectedErrorFragment, error);
        }
    }

    [Fact]
    public void NextSeqToReturn应返sinceSeq加1()
    {
        Assert.Equal(101L, ChannelSeq.NextSeqToReturn(100L));
        Assert.Equal(1L, ChannelSeq.NextSeqToReturn(0L));
    }

    [Theory]
    [InlineData(101L, 100L, true)]
    [InlineData(100L, 100L, false)]   // 等于 since_seq 不应返回
    [InlineData(50L, 100L, false)]
    [InlineData(1L, 0L, true)]
    public void IsAfterSince应仅返严格大于的seq(long messageSeq, long sinceSeq, bool expected)
    {
        Assert.Equal(expected, ChannelSeq.IsAfterSince(messageSeq, sinceSeq));
    }
}
