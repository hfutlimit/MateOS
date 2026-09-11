using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Persistence;
using MateOS.Domain.Agent;
using MateOS.Domain.Channel;
using Microsoft.EntityFrameworkCore;
using DbAgent = MateOS.Api.Persistence.Agent;
using DbChannel = MateOS.Api.Persistence.Channel;

namespace MateOS.Api.Channels;

/// <summary>
/// WS 端点：<c>/ws</c>。
/// </summary>
/// <remarks>
/// <para>
/// 鉴权模式：客户端先建立 WebSocket，首个 <c>hello</c> 帧在 payload 里带
/// <c>access_token</c>（人）<b>或</b> <c>agent_token</c>（Agent）——两者必须且只能给一个。
/// 服务端校验后返 <c>hello_ack</c>（含 <c>session_id</c> / <c>actor_type</c> / <c>actor_id</c>）。
/// 这种模式适合浏览器（不能用 Authorization header）与 Agent SDK（统一走 hello 帧）。
/// </para>
/// <para>
/// 会话主体分 HUMAN / AGENT 两类，鉴权与订阅可见性都按类型分支：
/// 人查 <c>project_members</c>，Agent 查 <c>agent_project_membership</c>。
/// </para>
/// <para>
/// 已实现的消息类型：
/// hello / hello_ack / hello_nack / heartbeat / subscribe / unsubscribe / resume /
/// message.created / channel.archived / <c>execution.dispatch</c>（出站）/ error。
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
        TokenService tokenService,
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

            (WsActorType ActorType, Guid ActorId)? actor = await ResolveActorAsync(db, tokenService, hello, log);

            if (actor is null)
            {
                await CloseWithAsync(socket, WebSocketCloseStatus.PolicyViolation, "invalid token", log);
                return;
            }

            // 鉴权通过 → 注册连接
            connection = new WsConnection
            {
                SessionId = Guid.NewGuid().ToString("N"),
                ActorType = actor.Value.ActorType,
                ActorId = actor.Value.ActorId,
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
                    actor_type = connection.ActorType.ToDbValue(),
                    actor_id = connection.ActorId,
                    heartbeat_interval_sec = WsEnvelope.HeartbeatIntervalSec,
                    max_frames_per_sec = WsEnvelope.MaxFramesPerSec,
                    server_time_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            }, ct);

            log.LogInformation("WS 连接建立 session={SessionId} actorType={ActorType} actor={ActorId}",
                connection.SessionId, connection.ActorType, connection.ActorId);
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

    private sealed record HelloPayload(string? AccessToken, string? AgentToken);

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

        string? accessToken = null;
        string? agentToken = null;

        if (payload.TryGetProperty("access_token", out JsonElement accessElement))
        {
            if (accessElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            accessToken = accessElement.GetString();
        }

        if (payload.TryGetProperty("agent_token", out JsonElement agentElement))
        {
            if (agentElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            agentToken = agentElement.GetString();
        }

        bool hasAccess = !string.IsNullOrWhiteSpace(accessToken);
        bool hasAgent = !string.IsNullOrWhiteSpace(agentToken);

        // 必须且只能提供一种：两个都给会让"这个会话是谁"出现两种解释，
        // 而后续的鉴权判定（project member vs agent membership）完全依赖它。
        if (hasAccess == hasAgent)
        {
            return null;
        }

        return new HelloPayload(accessToken, agentToken);
    }

    /// <summary>
    /// 解析 hello 帧的令牌，返回会话主体（类型 + id）；失败返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 两条路径都走 <see cref="TokenService"/> 的完整校验（签名 / iss / aud / exp / token_type），
    /// 不自己解 JWT：M2 阶段手写解析只认 user token，且绕过了 token_type 校验。
    /// </remarks>
    private static async Task<(WsActorType ActorType, Guid ActorId)?> ResolveActorAsync(
        MateOSDbContext db,
        TokenService tokenService,
        HelloPayload hello,
        ILogger log)
    {
        if (!string.IsNullOrWhiteSpace(hello.AgentToken))
        {
            ClaimsPrincipal? principal = tokenService.ValidateAgentToken(hello.AgentToken!);

            if (principal?.GetAgentId() is not { } agentId)
            {
                log.LogDebug("WS hello agent_token 校验失败");
                return null;
            }

            DbAgent? agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId);

            if (agent is null)
            {
                log.LogDebug("WS hello 引用了不存在的 agent {AgentId}", agentId);
                return null;
            }

            // DISABLED = 已停用；再让它建立执行通道就等于绕过生命周期闸门。
            // PAUSED 允许连接：暂停只表达「不接新工作」，不代表必须断线。
            if (AgentLifecycleMap.TryParse(agent.Lifecycle, out AgentLifecycle lifecycle)
                && lifecycle is AgentLifecycle.Disabled)
            {
                log.LogInformation("WS hello 被拒：agent {AgentId} lifecycle=DISABLED", agentId);
                return null;
            }

            return (WsActorType.AGENT, agentId);
        }

        ClaimsPrincipal? userPrincipal = tokenService.ValidateAccessToken(hello.AccessToken!);

        if (userPrincipal?.GetUserId() is not { } userId)
        {
            log.LogDebug("WS hello access_token 校验失败");
            return null;
        }

        bool exists = await db.Users.AnyAsync(u => u.Id == userId);

        return exists ? (WsActorType.HUMAN, userId) : null;
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

        if (!await IsChannelReadableAsync(db, connection, channel.ProjectId, ct))
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

        // resume 也必须过同一道闸：否则任何已认证的主体都能拉到任意 channel 的消息
        // （M2 阶段漏了这一步，等于越权读取）。
        if (!await IsChannelReadableAsync(db, connection, channel.ProjectId, ct))
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

    /// <summary>
    /// 该会话能否读取指定 project 的 channel。
    /// </summary>
    /// <remarks>
    /// 人与 Agent 的可见性是<b>两套</b>关系表（project_members / agent_project_membership），
    /// 不能只看其中一张：agent_token 的 sub 是 agent_id，拿它去查 project_members
    /// 永远查不到，于是「Agent 读不到自己项目的频道」这种静默失效很难被发现。
    /// </remarks>
    private static Task<bool> IsChannelReadableAsync(
        MateOSDbContext db,
        WsConnection connection,
        Guid projectId,
        CancellationToken ct) =>
        connection.ActorType is WsActorType.AGENT
            ? db.AgentProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.AgentId == connection.ActorId, ct)
            : db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == connection.ActorId, ct);

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
