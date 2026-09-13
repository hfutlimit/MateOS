using System.Net.WebSockets;
using System.Text.Json;

namespace MateOS.E2ETests;

/// <summary>
/// e2e 测试的 WS 客户端封装：连 host 的 <c>/ws</c>、发 hello 帧、按 envelope 收帧。
/// </summary>
/// <remarks>
/// <para>
/// <b>用 <c>TestServer.WebSocketClient</c> 而不是 <c>ClientWebSocket</c></b>：宿主是
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// in-memory 起的，没真实端口；<c>ClientWebSocket</c> 会去连 <c>localhost:80</c> 然后报
/// <i>Connection refused</i>。这是 xUnit + WAF 写 WS 测试的固定坑。
/// </para>
/// <para>
/// 而 e2e 测试真正的 stub Agent（<c>MateOS.AgentStub.AgentStubClient</c>）走的是另一条路：
/// 它<b>也是</b> in-process 实例，<b>不需要</b>走真实 socket 出去，而是直接拿
/// <c>fixture.Server.CreateWebSocketClient().ConnectAsync(...)</c> 拿到的 socket 跑。
/// 这样 e2e 不起任何子进程、跑得快 10 倍，又跟生产 transport 是同一份协议层。
/// </para>
/// </remarks>
internal sealed class WsTestClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private WsTestClient(WebSocket socket) => _socket = socket;

    public static async Task<WsTestClient> ConnectAsync(MateOsE2EApp app, string accessToken)
    {
        WebSocket socket = await app.Server
            .CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        var client = new WsTestClient(socket);
        await client.HelloAsync(accessToken);
        return client;
    }

    /// <summary>用 user / access_token 建立 HUMAN 会话。</summary>
    public async Task HelloAsync(string accessToken)
    {
        var hello = new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "hello",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new { access_token = accessToken },
        };

        await SendJsonAsync(hello);
    }

    /// <summary>用 agent_token 建立 AGENT 会话。</summary>
    public async Task HelloAsAgentAsync(string agentToken)
    {
        var hello = new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "hello",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new { agent_token = agentToken },
        };

        await SendJsonAsync(hello);
    }

    public async Task SubscribeAsync(Guid channelId)
    {
        await SendJsonAsync(new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "subscribe",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new { channel_id = channelId },
        });
    }

    public async Task<JsonElement?> ReceiveNextAsync(TimeSpan timeout)
    {
        var buffer = new byte[1024 * 64];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(timeout);

        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ms, cancellationToken: cts.Token);
        return doc.RootElement.Clone();
    }

    private async Task SendJsonAsync(object envelope)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, s_json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch
            {
                // 已被对端关掉
            }
        }

        _socket.Dispose();
    }
}
