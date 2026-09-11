using System.Text.Json;

namespace MateOS.Domain.Channel;

/// <summary>
/// 消息流的 5 形态（E3 §3.1 / SD §5.2 messages 表 content_type CHECK）。
/// </summary>
/// <remarks>
/// 投影模式（v0.4 关键变化）：3 种「非人类直发」形态只引 <c>entity_ref</c>，
/// 事实源在各自表（decision_records / agent_executions / memory_proposals）。
/// UI 拿到 message 后按 content_type 决定是否要 GET 事实源 endpoint 补全。
/// </remarks>
public enum MessageContentType
{
    /// <summary>人类直发的纯文本消息（可含 @mentions 与 attachment 引用）。</summary>
    Human,

    /// <summary>Decision 投影（事实源 <c>decision_records</c>，仅引 decision_ref + summary）。</summary>
    Decision,

    /// <summary>Agent 执行产出投影（事实源 <c>agent_executions</c> + 可选 artifact）。</summary>
    AgentOutput,

    /// <summary>系统提示（E10 / 流程卡，含可点击 actions）。</summary>
    System,

    /// <summary>记忆申请投影（事实源 <c>memory_proposals</c>，仅引 memory_proposal_ref + summary）。</summary>
    MemoryRequest,
}

public static class MessageContentTypeMap
{
    public const string HumanDbValue = "HUMAN";
    public const string DecisionDbValue = "DECISION";
    public const string AgentOutputDbValue = "AGENT_OUTPUT";
    public const string SystemDbValue = "SYSTEM";
    public const string MemoryRequestDbValue = "MEMORY_REQUEST";

    public static string ToDbValue(this MessageContentType type) => type switch
    {
        MessageContentType.Human => HumanDbValue,
        MessageContentType.Decision => DecisionDbValue,
        MessageContentType.AgentOutput => AgentOutputDbValue,
        MessageContentType.System => SystemDbValue,
        MessageContentType.MemoryRequest => MemoryRequestDbValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 MessageContentType"),
    };

    public static bool TryParse(string? value, out MessageContentType type)
    {
        switch (value)
        {
            case HumanDbValue:
                type = MessageContentType.Human;
                return true;
            case DecisionDbValue:
                type = MessageContentType.Decision;
                return true;
            case AgentOutputDbValue:
                type = MessageContentType.AgentOutput;
                return true;
            case SystemDbValue:
                type = MessageContentType.System;
                return true;
            case MemoryRequestDbValue:
                type = MessageContentType.MemoryRequest;
                return true;
            default:
                type = (MessageContentType)(-1);
                return false;
        }
    }
}

/// <summary>
/// 消息发送方类型（E3 §3 DDL：<c>sender_type IN ('HUMAN','AGENT','SYSTEM')</c>）。
/// </summary>
public enum MessageSenderType
{
    Human,
    Agent,
    System,
}

public static class MessageSenderTypeMap
{
    public const string HumanDbValue = "HUMAN";
    public const string AgentDbValue = "AGENT";
    public const string SystemDbValue = "SYSTEM";

    public static string ToDbValue(this MessageSenderType type) => type switch
    {
        MessageSenderType.Human => HumanDbValue,
        MessageSenderType.Agent => AgentDbValue,
        MessageSenderType.System => SystemDbValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 MessageSenderType"),
    };

    public static bool TryParse(string? value, out MessageSenderType type)
    {
        switch (value)
        {
            case HumanDbValue:
                type = MessageSenderType.Human;
                return true;
            case AgentDbValue:
                type = MessageSenderType.Agent;
                return true;
            case SystemDbValue:
                type = MessageSenderType.System;
                return true;
            default:
                type = (MessageSenderType)(-1);
                return false;
        }
    }
}

/// <summary>
/// 消息内容投影校验（E3 §3.1 / §7 F8）。
/// </summary>
/// <remarks>
/// <para>
/// v0.4 强约束：DECISION / AGENT_OUTPUT / MEMORY_REQUEST 形态的 content <b>不得</b>
/// 复制事实源字段（如 decision.analysis、execution.context_refs）。
/// 校验失败 → 业务层返 400，不写入 DB。
/// </para>
/// <para>
/// 校验与 schema 解耦：这里只验「内容是否符合该形态的最小契约」，
/// JSON Schema 校验可后置到 contracts 体系（v0.7 §4）。
/// </para>
/// </remarks>
public static class MessageProjection
{
    /// <summary>投影形态的最小契约校验。</summary>
    /// <param name="contentType">5 形态之一。</param>
    /// <param name="content">提交时附带的 content JSON（已反序列化为 <see cref="JsonElement"/>）。</param>
    /// <returns>校验通过则 <c>null</c>；失败则返人类可读错误信息。</returns>
    public static string? Validate(MessageContentType contentType, JsonElement content)
    {
        return contentType switch
        {
            MessageContentType.Human => ValidateHuman(content),
            MessageContentType.Decision => ValidateDecision(content),
            MessageContentType.AgentOutput => ValidateAgentOutput(content),
            MessageContentType.System => ValidateSystem(content),
            MessageContentType.MemoryRequest => ValidateMemoryRequest(content),
            _ => "未知的 content_type",
        };
    }

    /// <summary>
    /// HUMAN 形态：<c>text</c> 必填（≤ 32KB 文本），可选 <c>attachment_ids</c>。
    /// </summary>
    private static string? ValidateHuman(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            return "HUMAN 形态的 content 必须是对象";
        }

        if (!content.TryGetProperty("text", out JsonElement textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            return "HUMAN 形态的 content 必须包含 text 字符串";
        }

        string text = textElement.GetString() ?? string.Empty;

        if (text.Length == 0)
        {
            return "HUMAN 形态的 text 不能为空";
        }

        // 32KB 上限：超过即返错误，避免被滥用为大附件 / 长日志
        if (text.Length > 32 * 1024)
        {
            return "HUMAN 形态的 text 超过 32KB 上限";
        }

        return null;
    }

    /// <summary>
    /// DECISION 形态：<c>decision_ref</c> 必填，<c>summary</c> 可选。
    /// F8：<b>不得</b>含 analysis / missing / reason（事实源字段）。
    /// </summary>
    private static string? ValidateDecision(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            return "DECISION 形态的 content 必须是对象";
        }

        if (!content.TryGetProperty("decision_ref", out JsonElement refElement) ||
            refElement.ValueKind != JsonValueKind.String)
        {
            return "DECISION 形态的 content 必须包含 decision_ref 字符串";
        }

        string decisionRef = refElement.GetString() ?? string.Empty;

        if (decisionRef.Length == 0 || !Guid.TryParse(decisionRef, out _))
        {
            return "DECISION 形态的 decision_ref 必须是 UUID 字符串";
        }

        // F8 投影纪律：禁带的事实源字段
        string[] forbidden = { "analysis", "missing", "reason" };
        foreach (string field in forbidden)
        {
            if (content.TryGetProperty(field, out _))
            {
                return $"DECISION 形态的 content 不得包含事实源字段 {field}（v0.4 projection 纪律）";
            }
        }

        return null;
    }

    /// <summary>
    /// AGENT_OUTPUT 形态：<c>execution_ref</c> 必填，<c>artifact_id</c> 可选。
    /// </summary>
    private static string? ValidateAgentOutput(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            return "AGENT_OUTPUT 形态的 content 必须是对象";
        }

        if (!content.TryGetProperty("execution_ref", out JsonElement refElement) ||
            refElement.ValueKind != JsonValueKind.String)
        {
            return "AGENT_OUTPUT 形态的 content 必须包含 execution_ref 字符串";
        }

        string executionRef = refElement.GetString() ?? string.Empty;

        if (executionRef.Length == 0 || !Guid.TryParse(executionRef, out _))
        {
            return "AGENT_OUTPUT 形态的 execution_ref 必须是 UUID 字符串";
        }

        return null;
    }

    /// <summary>
    /// SYSTEM 形态：<c>text</c> 必填，<c>actions</c> 可选（流程卡的按钮）。
    /// </summary>
    private static string? ValidateSystem(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            return "SYSTEM 形态的 content 必须是对象";
        }

        if (!content.TryGetProperty("text", out JsonElement textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            return "SYSTEM 形态的 content 必须包含 text 字符串";
        }

        string text = textElement.GetString() ?? string.Empty;

        if (text.Length == 0)
        {
            return "SYSTEM 形态的 text 不能为空";
        }

        return null;
    }

    /// <summary>
    /// MEMORY_REQUEST 形态：<c>memory_proposal_ref</c> 必填，<c>summary</c> 可选。
    /// </summary>
    private static string? ValidateMemoryRequest(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            return "MEMORY_REQUEST 形态的 content 必须是对象";
        }

        if (!content.TryGetProperty("memory_proposal_ref", out JsonElement refElement) ||
            refElement.ValueKind != JsonValueKind.String)
        {
            return "MEMORY_REQUEST 形态的 content 必须包含 memory_proposal_ref 字符串";
        }

        string proposalRef = refElement.GetString() ?? string.Empty;

        if (proposalRef.Length == 0 || !Guid.TryParse(proposalRef, out _))
        {
            return "MEMORY_REQUEST 形态的 memory_proposal_ref 必须是 UUID 字符串";
        }

        return null;
    }
}
