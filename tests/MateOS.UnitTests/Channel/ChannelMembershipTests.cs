using MateOS.Domain.Channel;

namespace MateOS.UnitTests.Channel;

/// <summary>
/// 覆盖 E3 §2.1 channel 成员类型 + §7 F6 非成员返 403。
/// </summary>
public sealed class ChannelMembershipTests
{
    [Fact]
    public void IsMember应正确判定HUMAN成员()
    {
        var types = new[] { ChannelMemberType.Human };

        Assert.True(ChannelMembership.IsMember(types, ChannelMemberType.Human));
        Assert.False(ChannelMembership.IsMember(types, ChannelMemberType.Agent));
    }

    [Fact]
    public void IsMember应正确判定AGENT成员()
    {
        var types = new[] { ChannelMemberType.Agent };

        Assert.False(ChannelMembership.IsMember(types, ChannelMemberType.Human));
        Assert.True(ChannelMembership.IsMember(types, ChannelMemberType.Agent));
    }

    [Fact]
    public void IsMember应支持同channel多类型成员()
    {
        var types = new[] { ChannelMemberType.Human, ChannelMemberType.Agent };

        Assert.True(ChannelMembership.IsMember(types, ChannelMemberType.Human));
        Assert.True(ChannelMembership.IsMember(types, ChannelMemberType.Agent));
    }

    [Fact]
    public void 空成员集合应判否()
    {
        var types = Array.Empty<ChannelMemberType>();

        Assert.False(ChannelMembership.IsMember(types, ChannelMemberType.Human));
        Assert.False(ChannelMembership.IsMember(types, ChannelMemberType.Agent));
    }

    [Fact]
    public void CanPost应仅要求非null角色()
    {
        Assert.True(ChannelMembership.CanPost(ChannelMemberRole.Owner));
        Assert.True(ChannelMembership.CanPost(ChannelMemberRole.Member));
        Assert.False(ChannelMembership.CanPost(null));
    }

    [Fact]
    public void CanManage应仅owner为真()
    {
        Assert.True(ChannelMembership.CanManage(ChannelMemberRole.Owner));
        Assert.False(ChannelMembership.CanManage(ChannelMemberRole.Member));
        Assert.False(ChannelMembership.CanManage(null));
    }
}

/// <summary>
/// 覆盖 E3 §3 channel_member / sender_type 双向映射（DB 值与强类型互转）。
/// </summary>
public sealed class ChannelEnumMapTests
{
    [Theory]
    [InlineData("HUMAN", ChannelMemberType.Human)]
    [InlineData("AGENT", ChannelMemberType.Agent)]
    [InlineData("human", null)]    // 大小写敏感
    [InlineData("invalid", null)]
    public void ChannelMemberTypeMap应正确解析(string raw, ChannelMemberType? expected)
    {
        bool ok = ChannelMemberTypeMap.TryParse(raw, out ChannelMemberType parsed);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, parsed);
        }
    }

    [Theory]
    [InlineData(ChannelMemberType.Human, "HUMAN")]
    [InlineData(ChannelMemberType.Agent, "AGENT")]
    public void ChannelMemberTypeMap应正确序列化(ChannelMemberType type, string expected)
    {
        Assert.Equal(expected, type.ToDbValue());
    }

    [Theory]
    [InlineData("owner", ChannelMemberRole.Owner)]
    [InlineData("member", ChannelMemberRole.Member)]
    [InlineData("OWNER", null)]   // 大小写敏感
    public void ChannelMemberRoleMap应正确解析(string raw, ChannelMemberRole? expected)
    {
        bool ok = ChannelMemberRoleMap.TryParse(raw, out ChannelMemberRole parsed);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, parsed);
        }
    }

    [Theory]
    [InlineData(MessageContentType.Human, "HUMAN")]
    [InlineData(MessageContentType.Decision, "DECISION")]
    [InlineData(MessageContentType.AgentOutput, "AGENT_OUTPUT")]
    [InlineData(MessageContentType.System, "SYSTEM")]
    [InlineData(MessageContentType.MemoryRequest, "MEMORY_REQUEST")]
    public void MessageContentTypeMap应正确序列化(MessageContentType type, string expected)
    {
        Assert.Equal(expected, type.ToDbValue());
    }

    [Theory]
    [InlineData("HUMAN", MessageContentType.Human)]
    [InlineData("DECISION", MessageContentType.Decision)]
    [InlineData("AGENT_OUTPUT", MessageContentType.AgentOutput)]
    [InlineData("SYSTEM", MessageContentType.System)]
    [InlineData("MEMORY_REQUEST", MessageContentType.MemoryRequest)]
    [InlineData("invalid", null)]
    public void MessageContentTypeMap应正确解析(string raw, MessageContentType? expected)
    {
        bool ok = MessageContentTypeMap.TryParse(raw, out MessageContentType parsed);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, parsed);
        }
    }

    [Theory]
    [InlineData("HUMAN", MessageSenderType.Human)]
    [InlineData("AGENT", MessageSenderType.Agent)]
    [InlineData("SYSTEM", MessageSenderType.System)]
    [InlineData("invalid", null)]
    public void MessageSenderTypeMap应正确解析(string raw, MessageSenderType? expected)
    {
        bool ok = MessageSenderTypeMap.TryParse(raw, out MessageSenderType parsed);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, parsed);
        }
    }
}
