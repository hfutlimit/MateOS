using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Persistence;
using MateOS.Domain.Channel;
using Microsoft.EntityFrameworkCore;
using DbChannel = MateOS.Api.Persistence.Channel;

namespace MateOS.Api.Channels;

/// <summary>
/// WS 端点：<c>/ws</c>。
/// </summary>
/// <remarks>
/// <para>
/// 鉴权模式（M2 简化）：
/// <list type="number">
///   <item>客户端先建立 WebSocket</item>
///   <item>首个 <c>hello</c> 帧 payload 带 <c>access_token</c>（JWT）</item>
///   <item>服务端校验 → 返 <c>hello_ack</c> + session_id</item>
/// </list>
/// 这种模式适合浏览器（不能用 Authorization header）与 stub SDK（统一走 query 或 hello 帧）。
/// </para>
/// <para>
/// 完整消息类型（M2 范围）：hello / hello_ack / hello_nack / heartbeat /
/// subscribe / unsubscribe / resume / message.created / channel.archived / error。
/// </para>
/// </remarks>
public static class WsEndpoint
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapWs(this IEndpointRouteBuilder app)
    {
        app.Map("/ws", HandleAsync);
    }

    private static async Task HandleAsync(
        HttpContext http,
        WsConnectionRegistry registry,
        WsSender sender,
        MateOSDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        ILogger log = loggerFactory.CreateLogger("WsEndpoint");

        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync();

        WsConnection? connection = null;
        WsRateLimiter rateLimiter = new();

        // ── Step 1：等 hello 帧（带 access_token），最多等 5 秒 ──
        CancellationTokenSource helloCts = new(TimeSpan.FromSeconds(5));
        try
        {
            JsonElement? firstFrame = await ReceiveFrameAsync(socket, rateLimiter, helloCts.Token);

            if (firstFrame is null || helloCts.IsCancellationRequested)
            {
                await CloseWithAsync(socket, WebSocketCloseStatus.PolicyViolation, "hello timeout", log);
                return;
            }

            HelloPayload hello = ParseHello(firstFrame.Value);

            if (hello is null)
            {
                await CloseWithAsync(socket, WebSocketCloseStatus.PolicyViolation, "first frame must be hello", log);
                return;
            }

            Guid? actorId = ResolveToken(db, hello.AccessToken, log);

            if (actorId is null)
            {
                await CloseWithAsync(socket, WebSocketCloseStatus.PolicyViolation, "invalid access_token", log);
                return;
            }

            // 鉴权通过 → 注册连接
            connection = new WsConnection
            {
                SessionId = Guid.NewGuid().ToString("N"),
                ActorId = actorId.Value,
                Socket = socket,
            };
            registry.Register(connection);

            // hello_ack
            await sender.SendAsync(connection.SessionId, new
            {
                id = Guid.NewGuid().ToString("N"),
                type = WsMessageTypeMap.HelloAckValue,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payload = new
                {
                    session_id = connection.SessionId,
                    heartbeat_interval_sec = WsEnvelope.HeartbeatIntervalSec,
                    max_frames_per_sec = WsEnvelope.MaxFramesPerSec,
                    server_time_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            }, ct);

            log.LogInformation("WS 连接建立 session={SessionId} actor={ActorId}", connection.SessionId, connection.ActorId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "WS hello 阶段失败");
            await CloseWithAsync(socket, WebSocketCloseStatus.InternalServerError, "hello error", log);
            return;
        }

        if (connection is null)
        {
            return;
        }

        // ── Step 2：消息循环 ──
        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                JsonElement? frame = await ReceiveFrameAsync(socket, rateLimiter, ct);

                if (frame is null)
                {
                    break; // 客户端关闭或超时
                }

                string? envelopeError = WsEnvelope.Validate(frame.Value);

                if (envelopeError is not null)
                {
                    await sender.SendAsync(connection.SessionId, new
                    {
                        id = Guid.NewGuid().ToString("N"),
                        type = WsMessageTypeMap.ErrorValue,
                        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        payload = new { code = "BAD_ENVELOPE", message = envelopeError },
                    }, ct);
                    continue;
                }

                JsonElement typeElement = frame.Value.GetProperty("type");
                string? typeStr = typeElement.GetString();
                if (!WsMessageTypeMap.TryParse(typeStr, out WsMessageType type))
                {
                    // envelope.Validate 已做过 type 检查，理论上走不到这里
                    continue;
                }

                JsonElement payload = frame.Value.TryGetProperty("payload", out JsonElement p)
                    ? p
                    : default;

                string? requestError = WsEnvelope.ValidateClientRequest(type, payload);

                if (requestError is not null)
                {
                    await sender.SendAsync(connection.SessionId, new
                    {
                        id = Guid.NewGuid().ToString("N"),
                        type = WsMessageTypeMap.ErrorValue,
                        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        payload = new { code = "BAD_REQUEST", message = requestError },
                    }, ct);
                    continue;
                }

                connection.LastHeartbeatUtc = DateTime.UtcNow;

                switch (type)
                {
                    case WsMessageType.Heartbeat:
                        // 单向 echo（不返帧；detailed/03 §1 heartbeat 30s 双向）
                        break;

                    case WsMessageType.Subscribe:
                        await HandleSubscribeAsync(connection, payload, db, sender, registry, log, ct);
                        break;

                    case WsMessageType.Unsubscribe:
                        await HandleUnsubscribeAsync(connection, payload, sender, registry, log, ct);
                        break;

                    case WsMessageType.Resume:
                        await HandleResumeAsync(connection, payload, db, sender, log, ct);
                        break;

                    case WsMessageType.Hello:
                    case WsMessageType.HelloAck:
                    case WsMessageType.HelloNack:
                    case WsMessageType.MessageCreated:
                    case WsMessageType.ChannelArchived:
                    case WsMessageType.Error:
                        // 这些类型不应由 client 发
                        await sender.SendAsync(connection.SessionId, new
                        {
                            id = Guid.NewGuid().ToString("N"),
                            type = WsMessageTypeMap.ErrorValue,
                            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            payload = new { code = "UNEXPECTED_TYPE", message = type.ToWireValue() },
                        }, ct);
                        break;
                }
            }
        }
        catch (WebSocketException wse)
        {
            log.LogInformation("WS 异常关闭 session={SessionId} status={Status}", connection.SessionId, wse.WebSocketErrorCode);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "WS 消息循环异常 session={SessionId}", connection.SessionId);
        }
        finally
        {
            registry.Unregister(connection.SessionId);
            rateLimiter.Reset();

            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch
                {
                    // 已被对端关掉
                }
            }

            log.LogInformation("WS 连接断开 session={SessionId}", connection.SessionId);
        }
    }

    // ── helpers ──

    private sealed record HelloPayload(string? AccessToken);

    private static HelloPayload? ParseHello(JsonElement frame)
    {
        if (!frame.TryGetProperty("type", out JsonElement typeElement) ||
            typeElement.GetString() != WsMessageTypeMap.HelloValue)
        {
            return null;
        }

        if (!frame.TryGetProperty("payload", out JsonElement payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!payload.TryGetProperty("access_token", out JsonElement tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return new HelloPayload(tokenElement.GetString());
    }

    private static Guid? ResolveToken(MateOSDbContext db, string? accessToken, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        // 复用 JWT 解析：仅当 token 是有效 access token 且能找到 user 时返 user_id
        // 注：M2 阶段不实现 agent_token 解析（E2 上线后）
        try
        {
            var handler = new JwtSecurityTokenHandler();
            handler.InboundClaimTypeMap.Clear();
            var jwt = handler.ReadJwtToken(accessToken);

            string? subject = jwt.Subject;

            if (!Guid.TryParse(subject, out Guid userId))
            {
                return null;
            }

            bool exists = db.Users.Any(u => u.Id == userId);

            return exists ? userId : null;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "WS hello token 解析失败");
            return null;
        }
    }

    private static async Task<JsonElement?> ReceiveFrameAsync(
        WebSocket socket,
        WsRateLimiter rateLimiter,
        CancellationToken ct)
    {
        var buffer = new byte[WsEnvelope.MaxEnvelopeBytes];
        var segment = new ArraySegment<byte>(buffer);
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;

        do
        {
            try
            {
                result = await socket.ReceiveAsync(segment, ct);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (!rateLimiter.TryAccept())
            {
                // 限速：直接丢弃剩余帧（不断开连接，由 client 心跳恢复）
                while (!result.EndOfMessage)
                {
                    try
                    {
                        result = await socket.ReceiveAsync(segment, ct);
                    }
                    catch (WebSocketException)
                    {
                        return null;
                    }
                }
                return null;
            }

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Position = 0;

        try
        {
            using var doc = await JsonDocument.ParseAsync(ms, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static async Task HandleSubscribeAsync(
        WsConnection connection,
        JsonElement payload,
        MateOSDbContext db,
        WsSender sender,
        WsConnectionRegistry registry,
        ILogger log,
        CancellationToken ct)
    {
        Guid channelId = Guid.Parse(payload.GetProperty("channel_id").GetString()!);

        // 鉴权：channel 是否对 actor 可访问（V1 简化：project member 即 ok）
        DbChannel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.DeletedAt == null, ct);

        if (channel is null)
        {
            await sender.SendAsync(connection.SessionId, new
            {
                id = Guid.NewGuid().ToString("N"),
                type = WsMessageTypeMap.ErrorValue,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payload = new { code = "CHANNEL_NOT_FOUND", channel_id = channelId },
            }, ct);
            return;
        }

        bool isProjectMember = await db.ProjectMembers
            .AnyAsync(pm => pm.ProjectId == channel.ProjectId && pm.UserId == connection.ActorId, ct);

        if (!isProjectMember)
        {
            await sender.SendAsync(connection.SessionId, new
            {
                id = Guid.NewGuid().ToString("N"),
                type = WsMessageTypeMap.ErrorValue,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payload = new { code = "NOT_AUTHORIZED", channel_id = channelId },
            }, ct);
            return;
        }

        // 走 sender 所在 singleton 的 registry（先存注册表）
        // 已在 DI 注入，registry 在 HandleAsync 签名里
        registry.Subscribe(connection.SessionId, channelId);

        // 回 ack（不重发历史消息；客户端用 resume 拿）
        await sender.SendAsync(connection.SessionId, new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "subscribe.ack",   // 协议子类型，简写
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new { channel_id = channelId, last_seq = channel.LastSeq },
        }, ct);
    }

    private static async Task HandleUnsubscribeAsync(
        WsConnection connection,
        JsonElement payload,
        WsSender sender,
        WsConnectionRegistry registry,
        ILogger log,
        CancellationToken ct)
    {
        Guid channelId = Guid.Parse(payload.GetProperty("channel_id").GetString()!);

        registry.Unsubscribe(connection.SessionId, channelId);

        await sender.SendAsync(connection.SessionId, new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "unsubscribe.ack",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new { channel_id = channelId },
        }, ct);
    }

    private static async Task HandleResumeAsync(
        WsConnection connection,
        JsonElement payload,
        MateOSDbContext db,
        WsSender sender,
        ILogger log,
        CancellationToken ct)
    {
        Guid channelId = Guid.Parse(payload.GetProperty("channel_id").GetString()!);
        long sinceSeq = 0L;

        if (payload.TryGetProperty("since_seq", out JsonElement seqElement))
        {
            sinceSeq = seqElement.GetInt64();
        }

        DbChannel? channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId && c.DeletedAt == null, ct);

        if (channel is null)
        {
            await sender.SendAsync(connection.SessionId, new
            {
                id = Guid.NewGuid().ToString("N"),
                type = WsMessageTypeMap.ErrorValue,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payload = new { code = "CHANNEL_NOT_FOUND", channel_id = channelId },
            }, ct);
            return;
        }

        long startSeq = sinceSeq == 0 ? ChannelSeq.FirstSeq : sinceSeq + 1;

        var rows = await db.Messages
            .Where(m => m.ChannelId == channelId && m.DeletedAt == null && m.Seq >= startSeq)
            .OrderBy(m => m.Seq)
            .Take(200)
            .Select(m => new
            {
                m.Id,
                m.Seq,
                m.SenderType,
                m.SenderId,
                m.ContentType,
                m.Content,
                m.ParentSeq,
                m.ClientMsgId,
                m.CreatedAt,
            })
            .ToListAsync(ct);

        // 单帧发：resume_ack 携带 messages 列表（≤ 200 条 / 帧）
        // V1 简化：超 200 条就让 client 续 resume（不切片）
        await sender.SendAsync(connection.SessionId, new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "resume.ack",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new
            {
                channel_id = channelId,
                last_seq = channel.LastSeq,
                returned = rows.Count,
                messages = rows.Select(m => new
                {
                    m.Id,
                    m.Seq,
                    m.SenderType,
                    m.SenderId,
                    m.ContentType,
                    content = JsonDocument.Parse(m.Content).RootElement,
                    m.ParentSeq,
                    m.ClientMsgId,
                    m.CreatedAt,
                }),
            },
        }, ct);
    }

    private static async Task CloseWithAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string reason,
        ILogger log)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(status, reason, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "WS 关闭时异常");
        }
    }
}
