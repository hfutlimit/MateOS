using System.Net.WebSockets;
using System.Text.Json;

namespace MateOS.AgentStub;

/// <summary>
/// 出站 WS 客户端：与 Runtime 建立 <c>/ws</c> 会话，hello 用 <c>agent_token</c>，
/// 收 <c>execution.dispatch</c> 推送后转交给 <see cref="IStubBehavior"/> 处理。
/// </summary>
/// <remarks>
/// <para>
/// <b>职责边界</b>：本类只负责 WS 长连接（hello / heartbeat / 收 dispatch），
/// 不发 <c>dispatch_ack</c> / <c>event</c> / <c>result</c>。
/// 那些走 HTTP，由 <see cref="HttpStubResponder"/> 实现，行为策略在
/// <see cref="IStubBehavior.HandleDispatchAsync"/> 里用它。
/// </para>
/// <para>
/// <b>JSON 序列化</b>：snake_case，与 server 端 <c>AgentDispatchNotifier</c> 同口径。
/// 接收路径则直接 <see cref="JsonElement"/> 透传给策略，让策略决定
/// <c>execution_id</c> / <c>attempt_no</c> / <c>work_item_ref</c> 等具体字段怎么用。
/// </para>
/// <para>
/// <b>行为错误隔离</b>：策略抛异常不会拖死主循环（行为矩阵 B1-B8 本就要 stub
/// 自己模拟崩溃 / 拒收，主循环必须能继续走下一帧）。但走 socket 出错就停 —
/// 半挂状态最危险。
/// </para>
/// </remarks>
public sealed class AgentStubClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly WebSocket _socket;
    private readonly string _agentToken;
    private readonly IStubBehavior _behavior;
    private readonly CancellationTokenSource _internalCts = new();
    private Task? _runTask;

    private AgentStubClient(WebSocket socket, string agentToken, IStubBehavior behavior)
    {
        _socket = socket;
        _agentToken = agentToken;
        _behavior = behavior;
    }

    /// <summary>从 hello_ack 拿到的会话 id（用于排查日志）。</summary>
    public string? SessionId { get; private set; }

    /// <summary>鉴权后绑定的 agent id。</summary>
    public Guid AgentId { get; private set; }

    /// <summary>hello 是否成功（hello_ack 已收）。</summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// 把一个已连好的 <see cref="WebSocket"/> 接入 stub 行为：先发 hello 并等 hello_ack。
    /// </summary>
    /// <param name="transport">e2e 传 in-memory socket；独立进程跑时传 <see cref="ClientWebSocketTransport"/>。</param>
    /// <param name="agentToken">Agent 持有的 agent_token（JWT）。</param>
    /// <param name="behavior">策略（决定怎么响应 dispatch）。</param>
    /// <param name="helloTimeout">等 hello_ack 的超时（默认 5s）。</param>
    public static async Task<AgentStubClient> AttachAsync(
        IStubTransport transport,
        string agentToken,
        IStubBehavior behavior,
        TimeSpan? helloTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(behavior);

        var client = new AgentStubClient(transport.Socket, agentToken, behavior);
        await client.HelloAsync(helloTimeout ?? TimeSpan.FromSeconds(5), ct);
        return client;
    }

    /// <summary>起一个后台 task 处理入站帧（dispatch / heartbeat / error）。</summary>
    public void Start()
    {
        if (_runTask is not null)
        {
            throw new InvalidOperationException("stub client already started");
        }

        _runTask = Task.Run(() => RunLoopAsync(_internalCts.Token));
    }

    /// <summary>阻塞直到 stub 处理完当前所有帧（用于 e2e 显式同步点）。</summary>
    public Task Completion => _runTask ?? Task.CompletedTask;

    private async Task HelloAsync(TimeSpan timeout, CancellationToken ct)
    {
        var hello = new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "hello",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new { agent_token = _agentToken },
        };

        await SendJsonAsync(hello, ct);

        JsonElement? ack = await ReceiveJsonAsync(timeout, ct);

        if (ack is null)
        {
            throw new TimeoutException("agent-stub hello 超时未收到 hello_ack");
        }

        string? type = ack.Value.GetProperty("type").GetString();
        if (type != "hello_ack")
        {
            string? code = type == "error"
                ? ack.Value.GetProperty("payload").GetProperty("code").GetString()
                : null;
            throw new InvalidOperationException(
                $"agent-stub hello 收到意外帧：type={type} code={code ?? "(n/a)"}");
        }

        JsonElement payload = ack.Value.GetProperty("payload");
        SessionId = payload.GetProperty("session_id").GetString();
        AgentId = payload.GetProperty("actor_id").GetGuid();
        IsReady = true;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                JsonElement? frame = await ReceiveJsonAsync(TimeSpan.FromSeconds(60), ct);
                if (frame is null)
                {
                    break;
                }

                string? type = frame.Value.TryGetProperty("type", out JsonElement t)
                    ? t.GetString()
                    : null;

                switch (type)
                {
                    case "execution.dispatch":
                        await HandleDispatchAsync(frame.Value, ct);
                        break;

                    case "heartbeat":
                        // 协议层单向 echo：无需回帧
                        break;

                    case "error":
                        // 收到 server 主动 error（如 BAD_ENVELOPE / NOT_AUTHORIZED）：记日志，不影响后续帧
                        Console.Error.WriteLine($"[stub] server sent error frame: {frame.Value}");
                        break;

                    default:
                        // 未知类型：忽略（M3b 协议演进期间这是设计内的 forward-compat）
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 显式 cancel：Dispose 触发
        }
        catch (WebSocketException ex)
        {
            // server 主动关 / 网络断：主循环退出
            Console.Error.WriteLine($"[stub] WS closed: {ex.WebSocketErrorCode} {ex.Message}");
        }
    }

    private async Task HandleDispatchAsync(JsonElement frame, CancellationToken ct)
    {
        if (!frame.TryGetProperty("payload", out JsonElement payload))
        {
            Console.Error.WriteLine("[stub] dispatch 帧无 payload 字段");
            return;
        }

        try
        {
            await _behavior.HandleDispatchAsync(payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 行为矩阵 B1-B8 故意让策略抛 — 主循环绝不能被一次坏行为拖死
            Console.Error.WriteLine($"[stub] behavior exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ──────────────────────── 帧底层 ────────────────────────

    private async Task SendJsonAsync(object envelope, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, s_json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private async Task<JsonElement?> ReceiveJsonAsync(TimeSpan timeout, CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];
        using var ms = new MemoryStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _internalCts.Cancel();

        if (_runTask is { } task)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // 等不等得到都行：清资源为先
            }
        }

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
        _internalCts.Dispose();
    }
}
