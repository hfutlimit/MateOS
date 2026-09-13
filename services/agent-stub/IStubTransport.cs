using System.Net.WebSockets;

namespace MateOS.AgentStub;

/// <summary>
/// Stub → Runtime 传输抽象。
/// </summary>
/// <remarks>
/// <para>
/// 设计意图：让 <see cref="AgentStubClient"/> 不依赖具体传输（<c>ClientWebSocket</c>
/// vs <c>TestServer.WebSocketClient</c>），e2e 测试可传 in-memory socket，
/// 独立进程跑时用真实 <c>ClientWebSocket</c>。
/// </para>
/// <para>
/// 实现只暴露 <see cref="WebSocket"/>（来自 <c>System.Net.WebSockets</c>），
/// 已有 <c>Open</c> / <c>SendAsync</c> / <c>ReceiveAsync</c> / <c>CloseAsync</c> 的标准契约，
/// 两条路径不再分叉。
/// </para>
/// </remarks>
public interface IStubTransport
{
    /// <summary>已建立的 WebSocket（必须处于 <c>Open</c> 状态）。</summary>
    WebSocket Socket { get; }
}

/// <summary>
/// 真实部署用的传输（<c>ClientWebSocket</c> 包装）。独立进程跑时用这个。
/// </summary>
/// <remarks>
/// 连上后 host 必须已就绪且能接受 WebSocket Upgrade；本类不重试 —
/// e2e 跑出来的失败信号比"自动重连 3 次"更可读。
/// </remarks>
public sealed class ClientWebSocketTransport : IStubTransport
{
    public WebSocket Socket { get; }

    private ClientWebSocketTransport(WebSocket socket) => Socket = socket;

    public static async Task<ClientWebSocketTransport> ConnectAsync(Uri wsUri, CancellationToken ct = default)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(wsUri, ct);
        return new ClientWebSocketTransport(socket);
    }
}
