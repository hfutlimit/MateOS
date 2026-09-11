using MateOS.Domain.Memory;

namespace MateOS.UnitTests.Memory;

/// <summary>
/// 覆盖 E5 §3 4 类型 + 4 状态 + Source 6 类型 + 不变量。
/// </summary>
public sealed class MemoryTypeTests
{
    [Theory]
    [InlineData("PERSONAL", MemoryType.PERSONAL)]
    [InlineData("PROJECT", MemoryType.PROJECT)]
    [InlineData("DECISION", MemoryType.DECISION)]
    [InlineData("KNOWLEDGE", MemoryType.KNOWLEDGE)]
    [InlineData("invalid", null)]
    public void MemoryTypeMap应正确解析(string raw, MemoryType? expected)
    {
        bool ok = MemoryTypeMap.TryParse(raw, out MemoryType type);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, type);
        }
    }

    [Theory]
    [InlineData("PROPOSED", MemoryStatus.PROPOSED)]
    [InlineData("APPROVED", MemoryStatus.APPROVED)]
    [InlineData("REJECTED", MemoryStatus.REJECTED)]
    [InlineData("WITHDRAWN", MemoryStatus.WITHDRAWN)]
    [InlineData("invalid", null)]
    public void MemoryStatusMap应正确解析(string raw, MemoryStatus? expected)
    {
        bool ok = MemoryStatusMap.TryParse(raw, out MemoryStatus status);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, status);
        }
    }

    [Theory]
    [InlineData(MemoryStatus.PROPOSED, false)]
    [InlineData(MemoryStatus.APPROVED, true)]
    [InlineData(MemoryStatus.REJECTED, true)]
    [InlineData(MemoryStatus.WITHDRAWN, true)]
    public void IsTerminal应正确判定(MemoryStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsTerminal());
    }

    [Theory]
    [InlineData("CHANNEL_MESSAGE", MemorySourceType.CHANNEL_MESSAGE)]
    [InlineData("EXECUTION_RESULT", MemorySourceType.EXECUTION_RESULT)]
    [InlineData("DECISION", MemorySourceType.DECISION)]
    [InlineData("WORK_ITEM", MemorySourceType.WORK_ITEM)]
    [InlineData("API", MemorySourceType.API)]
    [InlineData("MANUAL", MemorySourceType.MANUAL)]
    public void MemorySourceTypeMap应正确解析(string raw, MemorySourceType expected)
    {
        Assert.True(MemorySourceTypeMap.TryParse(raw, out MemorySourceType t));
        Assert.Equal(expected, t);
    }
}

/// <summary>
/// 覆盖 E5 §3 Source 三件套 + scope_target 不变量。
/// </summary>
public sealed class MemoryProposalInvariantTests
{
    [Fact]
    public void ValidateSource_CHANNE_MESSAGE缺channel_id应拒()
    {
        string? err = MemoryProposalInvariant.ValidateSource(
            MemorySourceType.CHANNEL_MESSAGE, null, 1L);
        Assert.NotNull(err);
        Assert.Contains("source_channel_id", err);
    }

    [Fact]
    public void ValidateSource_CHANNE_MESSAGE缺seq应拒()
    {
        string? err = MemoryProposalInvariant.ValidateSource(
            MemorySourceType.CHANNEL_MESSAGE, Guid.NewGuid(), null);
        Assert.NotNull(err);
        Assert.Contains("source_message_seq", err);
    }

    [Fact]
    public void ValidateSource_CHANNE_MESSAGE_seq为0应拒()
    {
        string? err = MemoryProposalInvariant.ValidateSource(
            MemorySourceType.CHANNEL_MESSAGE, Guid.NewGuid(), 0L);
        Assert.NotNull(err);
    }

    [Fact]
    public void ValidateSource_CHANNE_MESSAGE三件套齐应通过()
    {
        Assert.Null(MemoryProposalInvariant.ValidateSource(
            MemorySourceType.CHANNEL_MESSAGE, Guid.NewGuid(), 42L));
    }

    [Theory]
    [InlineData(MemorySourceType.EXECUTION_RESULT)]
    [InlineData(MemorySourceType.DECISION)]
    [InlineData(MemorySourceType.WORK_ITEM)]
    [InlineData(MemorySourceType.API)]
    [InlineData(MemorySourceType.MANUAL)]
    public void ValidateSource_非CHANNE_MESSAGE类型无需三件套(MemorySourceType sourceType)
    {
        Assert.Null(MemoryProposalInvariant.ValidateSource(sourceType, null, null));
    }

    [Fact]
    public void ValidateScopeTarget_PERSONAL不应挂project()
    {
        string? err = MemoryProposalInvariant.ValidateScopeTarget(
            MemoryType.PERSONAL, projectId: Guid.NewGuid(), proposerUserId: Guid.NewGuid(), proposerAgentId: null);
        Assert.NotNull(err);
        Assert.Contains("PERSONAL", err);
    }

    [Fact]
    public void ValidateScopeTarget_PERSONAL必须由user申请()
    {
        string? err = MemoryProposalInvariant.ValidateScopeTarget(
            MemoryType.PERSONAL, projectId: null, proposerUserId: null, proposerAgentId: null);
        Assert.NotNull(err);
    }

    [Fact]
    public void ValidateScopeTarget_PERSONAL不允许agent申请()
    {
        string? err = MemoryProposalInvariant.ValidateScopeTarget(
            MemoryType.PERSONAL, projectId: null, proposerUserId: Guid.NewGuid(), proposerAgentId: Guid.NewGuid());
        Assert.NotNull(err);
    }

    [Fact]
    public void ValidateScopeTarget_PERSONAL合法应通过()
    {
        Assert.Null(MemoryProposalInvariant.ValidateScopeTarget(
            MemoryType.PERSONAL, projectId: null, proposerUserId: Guid.NewGuid(), proposerAgentId: null));
    }

    [Theory]
    [InlineData(MemoryType.PROJECT)]
    [InlineData(MemoryType.DECISION)]
    [InlineData(MemoryType.KNOWLEDGE)]
    public void ValidateScopeTarget_非PERSONAL必须挂project(MemoryType type)
    {
        string? err = MemoryProposalInvariant.ValidateScopeTarget(
            type, projectId: null, proposerUserId: Guid.NewGuid(), proposerAgentId: null);
        Assert.NotNull(err);
        Assert.Contains("project_id", err);
    }

    [Theory]
    [InlineData(MemoryType.PROJECT)]
    [InlineData(MemoryType.DECISION)]
    [InlineData(MemoryType.KNOWLEDGE)]
    public void ValidateScopeTarget_非PERSONAL必须有申请人(MemoryType type)
    {
        string? err = MemoryProposalInvariant.ValidateScopeTarget(
            type, projectId: Guid.NewGuid(), proposerUserId: null, proposerAgentId: null);
        Assert.NotNull(err);
    }

    [Fact]
    public void ValidateScopeTarget_PROJECT_合法应通过()
    {
        Assert.Null(MemoryProposalInvariant.ValidateScopeTarget(
            MemoryType.PROJECT, projectId: Guid.NewGuid(),
            proposerUserId: Guid.NewGuid(), proposerAgentId: null));
    }
}
