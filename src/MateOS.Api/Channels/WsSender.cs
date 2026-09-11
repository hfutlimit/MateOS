using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MateOS.Domain.Channel;

namespace MateOS.Api.Channels;

/// <summary>
/// 单连接发送器：把 envelope 序列化为 JSON 文本帧写入 socket。
/// </summary>
/// <remarks>
/// 写帧用 <c>SendAsync(..., WebSocketMessageType.Text, endOfMessage: true)</c>，
/// 每帧独立发送（不攒批 / 不压缩）—— V1 简化，等 M3b E7 落地后再切到二进制帧。
/// </remarks>
public sealed class WsSender
{
    private readonly WsConnectionRegistry _registry;
    private readonly ILogger<WsSender> _log;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        // 保持 wire 形态一致：snake_case（message.created / channel.archived）
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public WsSender(WsConnectionRegistry registry, ILogger<WsSender> log)
    {
        _registry = registry;
        _log = log;
    }

    /// <summary>向单个 session 推一帧 envelope。失败时记录 + 反注册连接（让 caller 走 close 路径）。</summary>
    public async Task<bool> SendAsync(string sessionId, object envelope, CancellationToken ct)
    {
        WsConnection? connection = _registry.GetBySession(sessionId);

        if (connection is null)
        {
            return false;
        }

        if (connection.Socket.State != WebSocketState.Open)
        {
            _log.LogDebug("WS session {SessionId} 已不在 Open 状态，跳过推帧", sessionId);
            return false;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, s_jsonOptions);

        if (bytes.Length > WsEnvelope.MaxEnvelopeBytes)
        {
            _log.LogWarning("WS envelope 超过 {Limit} 字节上限，跳过推帧", WsEnvelope.MaxEnvelopeBytes);
            return false;
        }

        try
        {
            await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "WS 推帧失败 session={SessionId}", sessionId);
            return false;
        }
    }

    /// <summary>向 channel 的全部订阅者广播 envelope。</summary>
    public async Task BroadcastToChannelAsync(
        Guid channelId,
        object envelope,
        CancellationToken ct)
    {
        IReadOnlyCollection<string> subscribers = _registry.GetChannelSubscribers(channelId);

        if (subscribers.Count == 0)
        {
            return;
        }

        // 并发推，但每条都单独 await（不抢锁 / 不串行）
        Task[] tasks = subscribers
            .Select(sessionId => SendAsync(sessionId, envelope, ct))
            .ToArray();

        await Task.WhenAll(tasks);
    }
}
