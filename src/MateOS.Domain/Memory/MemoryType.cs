namespace MateOS.Domain.Memory;

/// <summary>
/// Memory 4 类型（E5 §1 v0.4）。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>PERSONAL：仅 user owner，跨 project 不参与过滤（v0.5 DM-I4）</item>
///   <item>PROJECT：挂在 project，所有 project member 可读（受 E6 权限约束）</item>
///   <item>DECISION：项目决策归档（终态后事实源）</item>
///   <item>KNOWLEDGE：通用知识（架构 / 规范 / 经验）</item>
/// </list>
/// </remarks>
public enum MemoryType
{
    PERSONAL,
    PROJECT,
    DECISION,
    KNOWLEDGE,
}

public static class MemoryTypeMap
{
    public const string PersonalValue = "PERSONAL";
    public const string ProjectValue = "PROJECT";
    public const string DecisionValue = "DECISION";
    public const string KnowledgeValue = "KNOWLEDGE";

    public static string ToDbValue(this MemoryType type) => type switch
    {
        MemoryType.PERSONAL => PersonalValue,
        MemoryType.PROJECT => ProjectValue,
        MemoryType.DECISION => DecisionValue,
        MemoryType.KNOWLEDGE => KnowledgeValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 MemoryType"),
    };

    public static bool TryParse(string? value, out MemoryType type)
    {
        switch (value)
        {
            case PersonalValue: type = MemoryType.PERSONAL; return true;
            case ProjectValue: type = MemoryType.PROJECT; return true;
            case DecisionValue: type = MemoryType.DECISION; return true;
            case KnowledgeValue: type = MemoryType.KNOWLEDGE; return true;
            default:
                type = (MemoryType)(-1);
                return false;
        }
    }
}

/// <summary>
/// Memory Proposal 4 状态（E5 §3）。
/// </summary>
public enum MemoryStatus
{
    PROPOSED,
    APPROVED,
    REJECTED,
    WITHDRAWN,
}

public static class MemoryStatusMap
{
    public const string ProposedValue = "PROPOSED";
    public const string ApprovedValue = "APPROVED";
    public const string RejectedValue = "REJECTED";
    public const string WithdrawnValue = "WITHDRAWN";

    public static string ToDbValue(this MemoryStatus status) => status switch
    {
        MemoryStatus.PROPOSED => ProposedValue,
        MemoryStatus.APPROVED => ApprovedValue,
        MemoryStatus.REJECTED => RejectedValue,
        MemoryStatus.WITHDRAWN => WithdrawnValue,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未映射的 MemoryStatus"),
    };

    public static bool TryParse(string? value, out MemoryStatus status)
    {
        switch (value)
        {
            case ProposedValue: status = MemoryStatus.PROPOSED; return true;
            case ApprovedValue: status = MemoryStatus.APPROVED; return true;
            case RejectedValue: status = MemoryStatus.REJECTED; return true;
            case WithdrawnValue: status = MemoryStatus.WITHDRAWN; return true;
            default:
                status = (MemoryStatus)(-1);
                return false;
        }
    }

    public static bool IsTerminal(this MemoryStatus status) =>
        status is MemoryStatus.APPROVED or MemoryStatus.REJECTED or MemoryStatus.WITHDRAWN;
}

/// <summary>
/// Source 类型 6 态（E5 §3 source_type）。
/// </summary>
/// <remarks>
/// <para>
/// CHANNEL_MESSAGE 必填三件套（channel_id + seq + message_id），CHECK 约束强制。
/// </para>
/// </remarks>
public enum MemorySourceType
{
    CHANNEL_MESSAGE,
    EXECUTION_RESULT,
    DECISION,
    WORK_ITEM,
    API,
    MANUAL,
}

public static class MemorySourceTypeMap
{
    public const string ChannelMessageValue = "CHANNEL_MESSAGE";
    public const string ExecutionResultValue = "EXECUTION_RESULT";
    public const string DecisionValue = "DECISION";
    public const string WorkItemValue = "WORK_ITEM";
    public const string ApiValue = "API";
    public const string ManualValue = "MANUAL";

    public static string ToDbValue(this MemorySourceType type) => type switch
    {
        MemorySourceType.CHANNEL_MESSAGE => ChannelMessageValue,
        MemorySourceType.EXECUTION_RESULT => ExecutionResultValue,
        MemorySourceType.DECISION => DecisionValue,
        MemorySourceType.WORK_ITEM => WorkItemValue,
        MemorySourceType.API => ApiValue,
        MemorySourceType.MANUAL => ManualValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 MemorySourceType"),
    };

    public static bool TryParse(string? value, out MemorySourceType type)
    {
        switch (value)
        {
            case ChannelMessageValue: type = MemorySourceType.CHANNEL_MESSAGE; return true;
            case ExecutionResultValue: type = MemorySourceType.EXECUTION_RESULT; return true;
            case DecisionValue: type = MemorySourceType.DECISION; return true;
            case WorkItemValue: type = MemorySourceType.WORK_ITEM; return true;
            case ApiValue: type = MemorySourceType.API; return true;
            case ManualValue: type = MemorySourceType.MANUAL; return true;
            default:
                type = (MemorySourceType)(-1);
                return false;
        }
    }
}

/// <summary>
/// Memory Proposal 不变量（E5 §3 + scope_target CHECK）。
/// </summary>
public static class MemoryProposalInvariant
{
    /// <summary>校验 source 三件套（CHANNEL_MESSAGE 必填 channel_id + seq）。</summary>
    public static string? ValidateSource(
        MemorySourceType sourceType,
        Guid? sourceChannelId,
        long? sourceMessageSeq)
    {
        if (sourceType is MemorySourceType.CHANNEL_MESSAGE)
        {
            if (sourceChannelId is null)
            {
                return "source_type=CHANNEL_MESSAGE 必须填 source_channel_id";
            }

            if (sourceMessageSeq is null || sourceMessageSeq <= 0)
            {
                return "source_type=CHANNEL_MESSAGE 必须填 source_message_seq > 0";
            }
        }

        return null;
    }

    /// <summary>校验 scope-target 关系（PERSONAL 不挂 project；其他必须挂 project）。</summary>
    public static string? ValidateScopeTarget(
        MemoryType type,
        Guid? projectId,
        Guid? proposerUserId,
        Guid? proposerAgentId)
    {
        if (type is MemoryType.PERSONAL)
        {
            if (projectId is not null)
            {
                return "type=PERSONAL 不应挂 project_id（DM-I4 scope_target）";
            }

            if (proposerUserId is null)
            {
                return "type=PERSONAL 必须由 user 申请";
            }

            if (proposerAgentId is not null)
            {
                return "type=PERSONAL 不允许 agent 申请";
            }
        }
        else
        {
            if (projectId is null)
            {
                return $"type={type.ToDbValue()} 必须挂 project_id";
            }

            if (proposerUserId is null && proposerAgentId is null)
            {
                return $"type={type.ToDbValue()} 必须有申请人（user 或 agent）";
            }
        }

        return null;
    }
}
