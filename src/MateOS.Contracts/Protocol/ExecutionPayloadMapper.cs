using System.Text.Json;
using System.Text.Json.Nodes;

namespace MateOS.Contracts.Protocol;

/// <summary>
/// DB 里存的 <c>jsonb</c> 快照 → Connector 契约形状 的映射。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：<c>agent_executions.input</c> / <c>context_refs</c> 是自由形态的
/// 「请求快照」（由 E4 / E7 各触发路径写入），而 Connector 契约要求固定形状
/// （<c>input { prompt, params }</c> / <c>context { memory_refs, recent_messages,
/// permissions, refs }</c>）。
/// </para>
/// <para>
/// <b>映射放在这里而不是散在推送/轮询两处</b>：两条投递通道必须给出逐字段一致的
/// payload（SDK 只应有一份解析代码），映射一旦分叉就会变成「推送能收、轮询不能收」
/// 这类极难排查的问题。
/// </para>
/// <para>
/// 不丢信息是硬要求：已符合契约形状的对象按原样取用；否则<b>原始对象整体放进
/// <c>params</c> / <c>refs</c></b>，绝不因为形状不匹配就丢弃内容。
/// </para>
/// <para>
/// 后续方向：让 E4 / E7 在<b>写入时</b>就存契约形状，届时本映射退化为恒等函数。
/// 在此之前它是兼容层。
/// </para>
/// </remarks>
public static class ExecutionPayloadMapper
{
    /// <summary>
    /// 把存下来的 <c>input</c> 快照映射成契约形状。
    /// </summary>
    /// <remarks>
    /// 规则：已含 <c>prompt</c> 或 <c>params</c> 键 → 视为契约形状，原样取用；
    /// 否则 <c>prompt</c> 依次尝试 <c>prompt</c> / <c>text</c> / <c>title</c>
    /// （mention 触发是消息正文，WorkItem 指派是标题），
    /// <c>params</c> 放整个原始对象。
    /// </remarks>
    public static ExecutionInput ToInput(string? storedJson)
    {
        JsonNode? node = Parse(storedJson);

        if (node is JsonObject obj && (obj.ContainsKey("prompt") || obj.ContainsKey("params")))
        {
            return new ExecutionInput(Prompt: AsString(obj["prompt"]), Params: obj["params"]);
        }

        string? prompt = FirstString(node, "prompt", "text", "title");

        return new ExecutionInput(Prompt: prompt, Params: node);
    }

    /// <summary>
    /// 把存下来的 <c>context_refs</c> 快照映射成契约形状。
    /// </summary>
    /// <remarks>
    /// 三个注入位（<c>memory_refs</c> / <c>recent_messages</c> / <c>permissions</c>）
    /// 只有当存储对象里确实带着对应键时才填充；<c>refs</c> 恒为原始对象。
    /// </remarks>
    public static ExecutionContext ToContext(string? storedJson)
    {
        JsonNode? node = Parse(storedJson);

        if (node is not JsonObject obj)
        {
            return new ExecutionContext(null, null, null, node);
        }

        return new ExecutionContext(
            MemoryRefs: ReadGuidList(obj["memory_refs"]),
            RecentMessages: obj["recent_messages"],
            Permissions: obj["permissions"],
            Refs: node);
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // 非合法 JSON 时不猜测、不丢弃：把原文当字符串参数带走，
            // 让下游能看出「这里本应是一段 JSON」而不是静默变 null。
            return JsonValue.Create(json);
        }
    }

    private static string? FirstString(JsonNode? node, params string[] keys)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (string key in keys)
        {
            if (obj.TryGetPropertyValue(key, out JsonNode? value) && AsString(value) is { } text)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>只接受真正的字符串值：数字 / 布尔 / 对象一律不隐式转字符串。</summary>
    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static IReadOnlyList<Guid>? ReadGuidList(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count == 0)
        {
            return null;
        }

        var ids = new List<Guid>(array.Count);

        foreach (JsonNode? item in array)
        {
            if (Guid.TryParse(AsString(item), out Guid id))
            {
                ids.Add(id);
            }
        }

        return ids.Count == 0 ? null : ids;
    }
}
