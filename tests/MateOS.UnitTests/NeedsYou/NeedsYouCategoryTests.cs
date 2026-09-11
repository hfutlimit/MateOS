using MateOS.Domain.NeedsYou;

namespace MateOS.UnitTests.NeedsYou;

/// <summary>
/// 覆盖 Needs You 4 分类 + 4 源 双向映射 + 排序优先级。
/// </summary>
public sealed class NeedsYouCategoryTests
{
    [Theory]
    [InlineData("PROBLEMS", NeedsYouCategory.PROBLEMS)]
    [InlineData("APPROVAL", NeedsYouCategory.APPROVAL)]
    [InlineData("DECISION", NeedsYouCategory.DECISION)]
    [InlineData("INFORMATION", NeedsYouCategory.INFORMATION)]
    [InlineData("invalid", null)]
    public void NeedsYouCategoryMap应正确解析(string raw, NeedsYouCategory? expected)
    {
        bool ok = NeedsYouCategoryMap.TryParse(raw, out NeedsYouCategory category);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, category);
        }
    }

    [Theory]
    [InlineData(NeedsYouSource.CollaborationRequest, "collaboration_request")]
    [InlineData(NeedsYouSource.MemoryProposal, "memory_proposal")]
    [InlineData(NeedsYouSource.Execution, "execution")]
    [InlineData(NeedsYouSource.Agent, "agent")]
    public void NeedsYouSourceMap应正确序列化(NeedsYouSource source, string expected)
    {
        Assert.Equal(expected, source.ToWireValue());
    }

    [Fact]
    public void Priority应为PROBLEMS最低_最靠前()
    {
        Assert.True(NeedsYouCategory.PROBLEMS.Priority() < NeedsYouCategory.APPROVAL.Priority());
        Assert.True(NeedsYouCategory.APPROVAL.Priority() < NeedsYouCategory.DECISION.Priority());
        Assert.True(NeedsYouCategory.DECISION.Priority() < NeedsYouCategory.INFORMATION.Priority());
    }
}
