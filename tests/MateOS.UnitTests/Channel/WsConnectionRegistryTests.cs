using System.Net.WebSockets;
using MateOS.Api.Channels;
using MateOS.Domain.Channel;

namespace MateOS.UnitTests.Channel;

/// <summary>
/// 覆盖 WS 连接注册表的不变量（注册 / 注销 / 多 channel 索引 / 幂等）。
/// </summary>
/// <remarks>
/// 不依赖真 WebSocket——<see cref="WsConnection.Socket"/> 字段是 <c>required</c>，
/// 测试用一个 no-op mock 站位（用 <see cref="WebSocket.CreateFromStream"/> 在 <c>Stream.Null</c> 上）。
/// </remarks>
public sealed class WsConnectionRegistryTests
{
    [Fact]
    public void 注册应加入session索引和actor索引()
    {
        WsConnectionRegistry registry = new();
        WsConnection connection = MakeConnection(Guid.NewGuid());

        registry.Register(connection);

        Assert.Same(connection, registry.GetBySession(connection.SessionId));
        Assert.Equal(1, registry.ActiveSessionCount);
    }

    [Fact]
    public void 重复注册应抛错()
    {
        WsConnectionRegistry registry = new();
        WsConnection connection = MakeConnection(Guid.NewGuid());
        registry.Register(connection);

        Assert.Throws<InvalidOperationException>(() => registry.Register(connection));
    }

    [Fact]
    public void 注销应清空session订阅并保留其他session()
    {
        WsConnectionRegistry registry = new();
        WsConnection a = MakeConnection(Guid.NewGuid());
        WsConnection b = MakeConnection(Guid.NewGuid());
        Guid channelId = Guid.NewGuid();

        registry.Register(a);
        registry.Register(b);
        registry.Subscribe(a.SessionId, channelId);
        registry.Subscribe(b.SessionId, channelId);

        WsConnection? removed = registry.Unregister(a.SessionId);

        Assert.Same(a, removed);
        Assert.Null(registry.GetBySession(a.SessionId));
        Assert.Single(registry.GetChannelSubscribers(channelId));
        Assert.Equal(b.SessionId, registry.GetChannelSubscribers(channelId).First());
    }

    [Fact]
    public void Subscribe应幂等()
    {
        WsConnectionRegistry registry = new();
        WsConnection connection = MakeConnection(Guid.NewGuid());
        Guid channelId = Guid.NewGuid();
        registry.Register(connection);

        registry.Subscribe(connection.SessionId, channelId);
        registry.Subscribe(connection.SessionId, channelId);

        Assert.Single(registry.GetChannelSubscribers(channelId));
    }

    [Fact]
    public void Unsubscribe应幂等且不抛()
    {
        WsConnectionRegistry registry = new();
        WsConnection connection = MakeConnection(Guid.NewGuid());
        Guid channelId = Guid.NewGuid();
        registry.Register(connection);

        registry.Unsubscribe(connection.SessionId, channelId); // 未订阅也应静默
        Assert.Empty(registry.GetChannelSubscribers(channelId));
    }

    [Fact]
    public void 多连接订阅同一channel应返回全部session()
    {
        WsConnectionRegistry registry = new();
        WsConnection a = MakeConnection(Guid.NewGuid());
        WsConnection b = MakeConnection(Guid.NewGuid());
        WsConnection c = MakeConnection(Guid.NewGuid());
        Guid channelId = Guid.NewGuid();

        registry.Register(a);
        registry.Register(b);
        registry.Register(c);
        registry.Subscribe(a.SessionId, channelId);
        registry.Subscribe(b.SessionId, channelId);

        IReadOnlyCollection<string> subs = registry.GetChannelSubscribers(channelId);
        Assert.Equal(2, subs.Count);
        Assert.Contains(a.SessionId, subs);
        Assert.Contains(b.SessionId, subs);
        Assert.DoesNotContain(c.SessionId, subs);
    }

    [Fact]
    public void 同一actor多session应都被追踪()
    {
        WsConnectionRegistry registry = new();
        Guid actorId = Guid.NewGuid();
        WsConnection a = MakeConnection(actorId);
        WsConnection b = MakeConnection(actorId);
        Guid channelId = Guid.NewGuid();

        registry.Register(a);
        registry.Register(b);
        registry.Subscribe(a.SessionId, channelId);
        registry.Subscribe(b.SessionId, channelId);

        Assert.Equal(2, registry.GetChannelSubscribers(channelId).Count);

        // 注销一个不应影响另一个
        registry.Unregister(a.SessionId);
        Assert.Single(registry.GetChannelSubscribers(channelId));
    }

    // ───────────────── 人 / Agent 会话隔离（M3b Phase 2）─────────────────

    [Fact]
    public void Agent会话应按agentId索引()
    {
        WsConnectionRegistry registry = new();
        Guid agentId = Guid.NewGuid();
        WsConnection agent = MakeConnection(agentId, WsActorType.AGENT);

        registry.Register(agent);

        Assert.True(registry.HasAgentSession(agentId));
        Assert.Equal([agent.SessionId], registry.GetAgentSubscribers(agentId));
        Assert.Equal(1, registry.ActiveAgentSessionCount);
    }

    /// <summary>
    /// 关键不变量：人 id 与 agent id 都是 UUID 且语义独立，
    /// 只按 id 索引会让 execution.dispatch 推给人的浏览器。
    /// </summary>
    [Fact]
    public void 同一id的人会话不得被人格化为Agent会话()
    {
        WsConnectionRegistry registry = new();
        Guid sharedId = Guid.NewGuid();
        WsConnection human = MakeConnection(sharedId, WsActorType.HUMAN);

        registry.Register(human);

        Assert.False(registry.HasAgentSession(sharedId));
        Assert.Empty(registry.GetAgentSubscribers(sharedId));
        Assert.Equal(0, registry.ActiveAgentSessionCount);
    }

    [Fact]
    public void 同一Agent多session应都被追踪且注销只移除自己()
    {
        WsConnectionRegistry registry = new();
        Guid agentId = Guid.NewGuid();
        WsConnection a = MakeConnection(agentId, WsActorType.AGENT);
        WsConnection b = MakeConnection(agentId, WsActorType.AGENT);

        registry.Register(a);
        registry.Register(b);

        Assert.Equal(2, registry.GetAgentSubscribers(agentId).Count);

        registry.Unregister(a.SessionId);

        Assert.Equal([b.SessionId], registry.GetAgentSubscribers(agentId));

        // 最后一个会话注销后索引应被清空，HasAgentSession 回到 false
        registry.Unregister(b.SessionId);

        Assert.False(registry.HasAgentSession(agentId));
        Assert.Equal(0, registry.ActiveAgentSessionCount);
    }

    [Fact]
    public void 人与Agent会话可并存且互不干扰()
    {
        WsConnectionRegistry registry = new();
        Guid humanId = Guid.NewGuid();
        Guid agentId = Guid.NewGuid();
        Guid channelId = Guid.NewGuid();

        WsConnection human = MakeConnection(humanId, WsActorType.HUMAN);
        WsConnection agent = MakeConnection(agentId, WsActorType.AGENT);

        registry.Register(human);
        registry.Register(agent);
        registry.Subscribe(human.SessionId, channelId);
        registry.Subscribe(agent.SessionId, channelId);

        // channel 索引不区分类型（消息流对人、Agent 都可见）
        Assert.Equal(2, registry.GetChannelSubscribers(channelId).Count);

        // actor 索区分类型
        Assert.Equal([agent.SessionId], registry.GetAgentSubscribers(agentId));
        Assert.False(registry.HasAgentSession(humanId));
        Assert.Equal(1, registry.ActiveAgentSessionCount);
    }

    private static WsConnection MakeConnection(
        Guid actorId,
        WsActorType actorType = WsActorType.HUMAN)
    {
        WebSocket socket = WebSocket.CreateFromStream(
            stream: Stream.Null,
            options: new WebSocketCreationOptions { IsServer = true });

        return new WsConnection
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ActorType = actorType,
            ActorId = actorId,
            Socket = socket,
        };
    }
}
