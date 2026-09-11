namespace MateOS.Domain.Routing;

/// <summary>
/// Collaboration Request 6 态（E4 §2.1）。
/// </summary>
/// <remarks>
/// <para>
/// 状态机（v0.4.1 收敛）：
/// <code>
///   PENDING ─ACCEPT──▶ ACCEPTED（终态）
///      │──REJECT──▶ REJECTED（终态）
///      │──NEED_CONTEXT──▶ NEED_CONTEXT（终态）
///      │──CANCEL──▶ CANCELLED（终态）
///      └─timeout─┘▶ UNRESOLVED（终态）
/// </code>
/// </para>
/// <para>
/// 终态：ACCEPTED / REJECTED / NEED_CONTEXT / CANCELLED / UNRESOLVED。
/// </para>
/// </remarks>
public enum CrStatus
{
    PENDING,
    ACCEPTED,
    REJECTED,
    NEED_CONTEXT,
    UNRESOLVED,
    CANCELLED,
}

public static class CrStatusMap
{
    public const string PendingValue = "PENDING";
    public const string AcceptedValue = "ACCEPTED";
    public const string RejectedValue = "REJECTED";
    public const string NeedContextValue = "NEED_CONTEXT";
    public const string UnresolvedValue = "UNRESOLVED";
    public const string CancelledValue = "CANCELLED";

    public static string ToDbValue(this CrStatus status) => status switch
    {
        CrStatus.PENDING => PendingValue,
        CrStatus.ACCEPTED => AcceptedValue,
        CrStatus.REJECTED => RejectedValue,
        CrStatus.NEED_CONTEXT => NeedContextValue,
        CrStatus.UNRESOLVED => UnresolvedValue,
        CrStatus.CANCELLED => CancelledValue,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未映射的 CrStatus"),
    };

    public static bool TryParse(string? value, out CrStatus status)
    {
        switch (value)
        {
            case PendingValue: status = CrStatus.PENDING; return true;
            case AcceptedValue: status = CrStatus.ACCEPTED; return true;
            case RejectedValue: status = CrStatus.REJECTED; return true;
            case NeedContextValue: status = CrStatus.NEED_CONTEXT; return true;
            case UnresolvedValue: status = CrStatus.UNRESOLVED; return true;
            case CancelledValue: status = CrStatus.CANCELLED; return true;
            default:
                status = (CrStatus)(-1);
                return false;
        }
    }

    public static bool IsTerminal(this CrStatus status) =>
        status is CrStatus.ACCEPTED
              or CrStatus.REJECTED
              or CrStatus.NEED_CONTEXT
              or CrStatus.UNRESOLVED
              or CrStatus.CANCELLED;
}

/// <summary>
/// 决策 4 态（E4 §2.1 + detailed/01 §1 T+11）。
/// </summary>
public enum CrDecision
{
    ACCEPT,
    REJECT,
    NEED_CONTEXT,
    CANCEL,
}

public static class CrDecisionMap
{
    public const string AcceptValue = "ACCEPT";
    public const string RejectValue = "REJECT";
    public const string NeedContextValue = "NEED_CONTEXT";
    public const string CancelValue = "CANCEL";

    public static string ToDbValue(this CrDecision decision) => decision switch
    {
        CrDecision.ACCEPT => AcceptValue,
        CrDecision.REJECT => RejectValue,
        CrDecision.NEED_CONTEXT => NeedContextValue,
        CrDecision.CANCEL => CancelValue,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "未映射的 CrDecision"),
    };

    public static bool TryParse(string? value, out CrDecision decision)
    {
        switch (value)
        {
            case AcceptValue: decision = CrDecision.ACCEPT; return true;
            case RejectValue: decision = CrDecision.REJECT; return true;
            case NeedContextValue: decision = CrDecision.NEED_CONTEXT; return true;
            case CancelValue: decision = CrDecision.CANCEL; return true;
            default:
                decision = (CrDecision)(-1);
                return false;
        }
    }
}

/// <summary>
/// 触发类型（E4 §3）。
/// </summary>
public enum TriggerType
{
    MENTION,
    WORK_ITEM,
    API,
    AUTOMATION,
}

public static class TriggerTypeMap
{
    public const string MentionValue = "MENTION";
    public const string WorkItemValue = "WORK_ITEM";
    public const string ApiValue = "API";
    public const string AutomationValue = "AUTOMATION";

    public static string ToDbValue(this TriggerType type) => type switch
    {
        TriggerType.MENTION => MentionValue,
        TriggerType.WORK_ITEM => WorkItemValue,
        TriggerType.API => ApiValue,
        TriggerType.AUTOMATION => AutomationValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 TriggerType"),
    };

    public static bool TryParse(string? value, out TriggerType type)
    {
        switch (value)
        {
            case MentionValue: type = TriggerType.MENTION; return true;
            case WorkItemValue: type = TriggerType.WORK_ITEM; return true;
            case ApiValue: type = TriggerType.API; return true;
            case AutomationValue: type = TriggerType.AUTOMATION; return true;
            default:
                type = (TriggerType)(-1);
                return false;
        }
    }
}

/// <summary>
/// CR 请求类型（E4 §3）。
/// </summary>
public enum CrRequestKind
{
    MESSAGE_RESPONSE,
    WORK_ITEM_EXECUTION,
    API_CALL,
    AUTOMATION_RUN,
}

public static class CrRequestKindMap
{
    public const string MessageResponseValue = "MESSAGE_RESPONSE";
    public const string WorkItemExecutionValue = "WORK_ITEM_EXECUTION";
    public const string ApiCallValue = "API_CALL";
    public const string AutomationRunValue = "AUTOMATION_RUN";

    public static string ToDbValue(this CrRequestKind kind) => kind switch
    {
        CrRequestKind.MESSAGE_RESPONSE => MessageResponseValue,
        CrRequestKind.WORK_ITEM_EXECUTION => WorkItemExecutionValue,
        CrRequestKind.API_CALL => ApiCallValue,
        CrRequestKind.AUTOMATION_RUN => AutomationRunValue,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未映射的 CrRequestKind"),
    };

    public static bool TryParse(string? value, out CrRequestKind kind)
    {
        switch (value)
        {
            case MessageResponseValue: kind = CrRequestKind.MESSAGE_RESPONSE; return true;
            case WorkItemExecutionValue: kind = CrRequestKind.WORK_ITEM_EXECUTION; return true;
            case ApiCallValue: kind = CrRequestKind.API_CALL; return true;
            case AutomationRunValue: kind = CrRequestKind.AUTOMATION_RUN; return true;
            default:
                kind = (CrRequestKind)(-1);
                return false;
        }
    }
}

/// <summary>
/// Trigger Actor 类型（E4 §3）。
/// </summary>
public enum TriggerActorType
{
    USER,
    AGENT,
    SYSTEM,
}

public static class TriggerActorTypeMap
{
    public const string UserValue = "USER";
    public const string AgentValue = "AGENT";
    public const string SystemValue = "SYSTEM";

    public static string ToDbValue(this TriggerActorType type) => type switch
    {
        TriggerActorType.USER => UserValue,
        TriggerActorType.AGENT => AgentValue,
        TriggerActorType.SYSTEM => SystemValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 TriggerActorType"),
    };

    public static bool TryParse(string? value, out TriggerActorType type)
    {
        switch (value)
        {
            case UserValue: type = TriggerActorType.USER; return true;
            case AgentValue: type = TriggerActorType.AGENT; return true;
            case SystemValue: type = TriggerActorType.SYSTEM; return true;
            default:
                type = (TriggerActorType)(-1);
                return false;
        }
    }
}
