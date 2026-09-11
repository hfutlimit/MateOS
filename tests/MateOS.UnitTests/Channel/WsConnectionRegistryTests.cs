using System.Net.WebSockets;
using MateOS.Api.Channels;

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

    private static WsConnection MakeConnection(Guid actorId)
    {
        WebSocket socket = WebSocket.CreateFromStream(
            stream: Stream.Null,
            options: new WebSocketCreationOptions { IsServer = true });

        return new WsConnection
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ActorId = actorId,
            Socket = socket,
        };
    }
}
