using System.Text.Json;

namespace MateOS.Domain.Channel;

/// <summary>
/// WS message type 清单（M2-WS 范围：E3 channel 域）。
/// </summary>
/// <remarks>
/// 完整 execution 协议（E4 / E7 域）见 detailed/03，本枚举**只**覆盖 M2 阶段：
/// channel 消息流广播 + 续传。M3b（E7 connector transport）会扩展此枚举。
/// </remarks>
public enum WsMessageType
{
    // ─── 连接管理 ───
    Hello,
    HelloAck,
    HelloNack,
    Heartbeat,

    // ─── 订阅管理（client → server）───
    Subscribe,
    Unsubscribe,
    Resume,

    // ─── 服务端推送（server → client）───
    MessageCreated,
    ChannelArchived,

    // ─── 错误 ───
    Error,
}

public static class WsMessageTypeMap
{
    public const string HelloValue = "hello";
    public const string HelloAckValue = "hello_ack";
    public const string HelloNackValue = "hello_nack";
    public const string HeartbeatValue = "heartbeat";
    public const string SubscribeValue = "subscribe";
    public const string UnsubscribeValue = "unsubscribe";
    public const string ResumeValue = "resume";
    public const string MessageCreatedValue = "message.created";
    public const string ChannelArchivedValue = "channel.archived";
    public const string ErrorValue = "error";

    public static string ToWireValue(this WsMessageType type) => type switch
    {
        WsMessageType.Hello => HelloValue,
        WsMessageType.HelloAck => HelloAckValue,
        WsMessageType.HelloNack => HelloNackValue,
        WsMessageType.Heartbeat => HeartbeatValue,
        WsMessageType.Subscribe => SubscribeValue,
        WsMessageType.Unsubscribe => UnsubscribeValue,
        WsMessageType.Resume => ResumeValue,
        WsMessageType.MessageCreated => MessageCreatedValue,
        WsMessageType.ChannelArchived => ChannelArchivedValue,
        WsMessageType.Error => ErrorValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 WsMessageType"),
    };

    public static bool TryParse(string? value, out WsMessageType type)
    {
        switch (value)
        {
            case HelloValue: type = WsMessageType.Hello; return true;
            case HelloAckValue: type = WsMessageType.HelloAck; return true;
            case HelloNackValue: type = WsMessageType.HelloNack; return true;
            case HeartbeatValue: type = WsMessageType.Heartbeat; return true;
            case SubscribeValue: type = WsMessageType.Subscribe; return true;
            case UnsubscribeValue: type = WsMessageType.Unsubscribe; return true;
            case ResumeValue: type = WsMessageType.Resume; return true;
            case MessageCreatedValue: type = WsMessageType.MessageCreated; return true;
            case ChannelArchivedValue: type = WsMessageType.ChannelArchived; return true;
            case ErrorValue: type = WsMessageType.Error; return true;
            default:
                type = (WsMessageType)(-1);
                return false;
        }
    }
}

/// <summary>
/// WS envelope 校验（detailed/03 §2 通用 envelope 形态）。
/// </summary>
/// <remarks>
/// <para>
/// 形态：<c>{ id, type, ts, payload }</c>。
/// </para>
/// <para>
/// 校验只覆盖 envelope 形态与 type 合法性；payload 内部字段（subscribe.channel_id 等）
/// 走单类型校验函数。
/// </para>
/// </remarks>
public static class WsEnvelope
{
    /// <summary>envelope 顶层最大 size 限制（detailed/03 §6：max_payload_kb = 1024）。</summary>
    public const int MaxEnvelopeBytes = 1024 * 1024;

    /// <summary>心跳间隔（detailed/03 §1：30s）。</summary>
    public const int HeartbeatIntervalSec = 30;

    /// <summary>心跳超时（3 倍间隔 = 90s 后视作离线）。</summary>
    public const int HeartbeatTimeoutSec = HeartbeatIntervalSec * 3;

    /// <summary>限速：每秒最多 10 帧（detailed/03 §6）。</summary>
    public const int MaxFramesPerSec = 10;

    /// <summary>校验 envelope 顶层：必含 id + type；ts 可选；payload 必为对象或缺失。</summary>
    public static string? Validate(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            return "envelope 必须是对象";
        }

        if (!envelope.TryGetProperty("id", out JsonElement idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            return "envelope 必须包含 id 字符串";
        }

        string id = idElement.GetString() ?? string.Empty;

        if (id.Length == 0 || !Guid.TryParse(id, out _))
        {
            return "envelope.id 必须是 UUID 字符串";
        }

        if (!envelope.TryGetProperty("type", out JsonElement typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return "envelope 必须包含 type 字符串";
        }

        string typeStr = typeElement.GetString() ?? string.Empty;

        if (!WsMessageTypeMap.TryParse(typeStr, out _))
        {
            return $"envelope.type 非法：{typeStr}";
        }

        if (envelope.TryGetProperty("payload", out JsonElement payload)
            && payload.ValueKind != JsonValueKind.Object
            && payload.ValueKind != JsonValueKind.Null)
        {
            return "envelope.payload 必须是对象或缺失";
        }

        return null;
    }

    /// <summary>校验 subscribe / unsubscribe / resume 三类「client → server」请求的 payload。</summary>
    public static string? ValidateClientRequest(WsMessageType type, JsonElement payload)
    {
        return type switch
        {
            WsMessageType.Subscribe => ValidateChannelRef(payload),
            WsMessageType.Unsubscribe => ValidateChannelRef(payload),
            WsMessageType.Resume => ValidateResumePayload(payload),
            WsMessageType.Heartbeat => null,
            _ => $"该 type 不应作为 client request 收到：{type.ToWireValue()}",
        };
    }

    private static string? ValidateChannelRef(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return "payload 必须是对象";
        }

        if (!payload.TryGetProperty("channel_id", out JsonElement idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            return "payload 必须包含 channel_id 字符串";
        }

        string idStr = idElement.GetString() ?? string.Empty;

        if (idStr.Length == 0 || !Guid.TryParse(idStr, out _))
        {
            return "payload.channel_id 必须是 UUID 字符串";
        }

        return null;
    }

    private static string? ValidateResumePayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return "resume payload 必须是对象";
        }

        if (!payload.TryGetProperty("channel_id", out JsonElement idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            return "resume payload 必须包含 channel_id 字符串";
        }

        string idStr = idElement.GetString() ?? string.Empty;

        if (idStr.Length == 0 || !Guid.TryParse(idStr, out _))
        {
            return "resume.channel_id 必须是 UUID 字符串";
        }

        long sinceSeq = 0L;

        if (payload.TryGetProperty("since_seq", out JsonElement seqElement))
        {
            if (seqElement.ValueKind != JsonValueKind.Number)
            {
                return "resume.since_seq 必须是整数";
            }

            if (!seqElement.TryGetInt64(out sinceSeq))
            {
                return "resume.since_seq 必须是 long 范围内的整数";
            }
        }

        if (sinceSeq < 0)
        {
            return "resume.since_seq 不能为负数";
        }

        return null;
    }
}
