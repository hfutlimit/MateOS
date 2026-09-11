using MateOS.Domain.Work;

namespace MateOS.UnitTests.Work;

/// <summary>
/// E8 WorkItem 状态机 + canonical_status_category 派生（F8）。
/// </summary>
/// <remarks>
/// 这两件事都在纯函数里，是「看板不说谎」的根基：
/// 状态机错 → 卡片能跳到不存在的状态；category 漂移 → 同一张卡在列表与详情里列不同。
/// </remarks>
public sealed class WorkItemStateMachineTests
{
    // ───────────────────── canonical category 派生 ─────────────────────

    [Theory]
    [InlineData(WorkItemStatus.OPEN, "TODO")]
    [InlineData(WorkItemStatus.IN_PROGRESS, "IN_PROGRESS")]
    [InlineData(WorkItemStatus.IN_REVIEW, "IN_PROGRESS")]
    [InlineData(WorkItemStatus.DONE, "DONE")]
    [InlineData(WorkItemStatus.CLOSED, "DONE")]
    public void Status应派生唯一的canonical_category(WorkItemStatus status, string expected)
    {
        Assert.Equal(expected, status.ToCanonicalCategoryDbValue());
    }

    [Fact]
    public void 五态都应能往返解析()
    {
        foreach (WorkItemStatus status in Enum.GetValues<WorkItemStatus>())
        {
            string dbValue = status.ToDbValue();

            Assert.True(WorkItemStatusMap.TryParse(dbValue, out WorkItemStatus parsed));
            Assert.Equal(status, parsed);
        }
    }

    [Theory]
    [InlineData("open")]      // 大小写敏感：DB 存 SCREAMING_SNAKE
    [InlineData("IN-PROGRESS")]
    [InlineData("")]
    [InlineData(null)]
    public void 非法状态字面值应解析失败(string? value)
    {
        Assert.False(WorkItemStatusMap.TryParse(value, out _));
    }

    [Fact]
    public void 终态判定应覆盖DONE与CLOSED()
    {
        Assert.False(WorkItemStatus.OPEN.IsTerminal());
        Assert.False(WorkItemStatus.IN_PROGRESS.IsTerminal());
        Assert.False(WorkItemStatus.IN_REVIEW.IsTerminal());
        Assert.True(WorkItemStatus.DONE.IsTerminal());
        Assert.True(WorkItemStatus.CLOSED.IsTerminal());
    }

    // ───────────────────── 状态机合法迁移 ─────────────────────

    [Theory]
    [InlineData(WorkItemStatus.OPEN, WorkItemStatus.IN_PROGRESS)]
    [InlineData(WorkItemStatus.OPEN, WorkItemStatus.CLOSED)]
    [InlineData(WorkItemStatus.IN_PROGRESS, WorkItemStatus.IN_REVIEW)]
    [InlineData(WorkItemStatus.IN_PROGRESS, WorkItemStatus.DONE)]
    [InlineData(WorkItemStatus.IN_PROGRESS, WorkItemStatus.OPEN)]
    [InlineData(WorkItemStatus.IN_REVIEW, WorkItemStatus.DONE)]
    [InlineData(WorkItemStatus.IN_REVIEW, WorkItemStatus.IN_PROGRESS)]
    [InlineData(WorkItemStatus.DONE, WorkItemStatus.IN_REVIEW)]
    [InlineData(WorkItemStatus.CLOSED, WorkItemStatus.OPEN)]
    public void 合法迁移应被允许(WorkItemStatus from, WorkItemStatus to)
    {
        Assert.True(WorkItemStateMachine.CanTransition(from, to));
        Assert.Null(WorkItemStateMachine.WhyCannotTransition(from, to));
    }

    [Theory]
    [InlineData(WorkItemStatus.OPEN, WorkItemStatus.IN_REVIEW)]   // 不能跳过 IN_PROGRESS
    [InlineData(WorkItemStatus.OPEN, WorkItemStatus.DONE)]        // 不能凭空宣布完成
    [InlineData(WorkItemStatus.CLOSED, WorkItemStatus.DONE)]      // CLOSED 只能回到 OPEN
    [InlineData(WorkItemStatus.CLOSED, WorkItemStatus.IN_PROGRESS)]
    public void 非法迁移应被拒绝并给出原因(WorkItemStatus from, WorkItemStatus to)
    {
        Assert.False(WorkItemStateMachine.CanTransition(from, to));

        string? reason = WorkItemStateMachine.WhyCannotTransition(from, to);

        Assert.NotNull(reason);
        Assert.Contains(from.ToDbValue(), reason);
        Assert.Contains(to.ToDbValue(), reason);
    }

    [Fact]
    public void 同状态迁移应被拒绝而不是静默成功()
    {
        foreach (WorkItemStatus status in Enum.GetValues<WorkItemStatus>())
        {
            Assert.False(WorkItemStateMachine.CanTransition(status, status));

            string? reason = WorkItemStateMachine.WhyCannotTransition(status, status);

            Assert.NotNull(reason);
            Assert.Contains("无需迁移", reason);
        }
    }

    [Fact]
    public void 每个状态都应可枚举出合法的下一步()
    {
        foreach (WorkItemStatus status in Enum.GetValues<WorkItemStatus>())
        {
            IReadOnlyList<WorkItemStatus> targets = WorkItemStateMachine.AllowedTargets(status);

            Assert.NotEmpty(targets);

            // 枚举出来的目标必须自洽：反向判定同样为 true
            foreach (WorkItemStatus target in targets)
            {
                Assert.True(WorkItemStateMachine.CanTransition(status, target));
                Assert.NotEqual(status, target);
            }
        }
    }

    [Fact]
    public void 只有CLOSED的唯一出口是重开为OPEN()
    {
        IReadOnlyList<WorkItemStatus> targets = WorkItemStateMachine.AllowedTargets(WorkItemStatus.CLOSED);

        Assert.Equal([WorkItemStatus.OPEN], targets);
    }

    // ───────────────────── 搜索文本 ─────────────────────

    [Fact]
    public void 搜索文本应拼接标题与描述()
    {
        Assert.Equal("修复登录 用户无法登录", WorkItemSearchText.Build("修复登录", "用户无法登录"));
    }

    [Fact]
    public void 搜索文本应容忍空描述()
    {
        Assert.Equal("只有标题", WorkItemSearchText.Build("只有标题", null));
        Assert.Equal("只有标题", WorkItemSearchText.Build("只有标题", "   "));
    }

    // ───────────────────── 关系反向 ─────────────────────

    [Theory]
    [InlineData(WorkRelationType.BLOCKS, WorkRelationType.BLOCKED_BY)]
    [InlineData(WorkRelationType.BLOCKED_BY, WorkRelationType.BLOCKS)]
    [InlineData(WorkRelationType.PARENT_OF, WorkRelationType.CHILD_OF)]
    [InlineData(WorkRelationType.CHILD_OF, WorkRelationType.PARENT_OF)]
    [InlineData(WorkRelationType.RELATES_TO, WorkRelationType.RELATES_TO)]
    public void 关系反向应成对(WorkRelationType type, WorkRelationType expected)
    {
        Assert.Equal(expected, type.Inverse());
    }

    [Fact]
    public void 关系反向应是对合运算()
    {
        foreach (WorkRelationType type in Enum.GetValues<WorkRelationType>())
        {
            Assert.Equal(type, type.Inverse().Inverse());
        }
    }
}
