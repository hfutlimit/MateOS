namespace MateOS.Domain.Work;

/// <summary>
/// WorkItem 4 类型（E8 §3：<c>type IN ('TASK','STORY','BUG','EPIC')</c>）。
/// </summary>
public enum WorkItemType
{
    TASK,
    STORY,
    BUG,
    EPIC,
}

public static class WorkItemTypeMap
{
    public const string TaskValue = "TASK";
    public const string StoryValue = "STORY";
    public const string BugValue = "BUG";
    public const string EpicValue = "EPIC";

    public static string ToDbValue(this WorkItemType type) => type switch
    {
        WorkItemType.TASK => TaskValue,
        WorkItemType.STORY => StoryValue,
        WorkItemType.BUG => BugValue,
        WorkItemType.EPIC => EpicValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 WorkItemType"),
    };

    public static bool TryParse(string? value, out WorkItemType type)
    {
        switch (value)
        {
            case TaskValue: type = WorkItemType.TASK; return true;
            case StoryValue: type = WorkItemType.STORY; return true;
            case BugValue: type = WorkItemType.BUG; return true;
            case EpicValue: type = WorkItemType.EPIC; return true;
            default:
                type = (WorkItemType)(-1);
                return false;
        }
    }
}

/// <summary>
/// WorkItem 5 状态（E8 §3：<c>status IN ('OPEN','IN_PROGRESS','IN_REVIEW','DONE','CLOSED')</c>）。
/// </summary>
public enum WorkItemStatus
{
    OPEN,
    IN_PROGRESS,
    IN_REVIEW,
    DONE,
    CLOSED,
}

/// <summary>
/// Canonical status category（3 态）。Provider 自定义状态由 E9 动态映射到此三态，
/// Work 列表 / Dashboard 只认这三个值，因此切 Provider 不会让看板语义漂移。
/// </summary>
public enum CanonicalStatusCategory
{
    TODO,
    IN_PROGRESS,
    DONE,
}

public static class CanonicalStatusCategoryMap
{
    public const string TodoValue = "TODO";
    public const string InProgressValue = "IN_PROGRESS";
    public const string DoneValue = "DONE";

    public static string ToDbValue(this CanonicalStatusCategory category) => category switch
    {
        CanonicalStatusCategory.TODO => TodoValue,
        CanonicalStatusCategory.IN_PROGRESS => InProgressValue,
        CanonicalStatusCategory.DONE => DoneValue,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "未映射的 CanonicalStatusCategory"),
    };
}

public static class WorkItemStatusMap
{
    public const string OpenValue = "OPEN";
    public const string InProgressValue = "IN_PROGRESS";
    public const string InReviewValue = "IN_REVIEW";
    public const string DoneValue = "DONE";
    public const string ClosedValue = "CLOSED";

    public static string ToDbValue(this WorkItemStatus status) => status switch
    {
        WorkItemStatus.OPEN => OpenValue,
        WorkItemStatus.IN_PROGRESS => InProgressValue,
        WorkItemStatus.IN_REVIEW => InReviewValue,
        WorkItemStatus.DONE => DoneValue,
        WorkItemStatus.CLOSED => ClosedValue,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未映射的 WorkItemStatus"),
    };

    public static bool TryParse(string? value, out WorkItemStatus status)
    {
        switch (value)
        {
            case OpenValue: status = WorkItemStatus.OPEN; return true;
            case InProgressValue: status = WorkItemStatus.IN_PROGRESS; return true;
            case InReviewValue: status = WorkItemStatus.IN_REVIEW; return true;
            case DoneValue: status = WorkItemStatus.DONE; return true;
            case ClosedValue: status = WorkItemStatus.CLOSED; return true;
            default:
                status = (WorkItemStatus)(-1);
                return false;
        }
    }

    /// <summary>
    /// F8：canonical_status_category 由 status 派生，绝不手填（两列漂移 = 看板说谎）。
    /// </summary>
    public static CanonicalStatusCategory ToCanonicalCategory(this WorkItemStatus status) => status switch
    {
        WorkItemStatus.OPEN => CanonicalStatusCategory.TODO,
        WorkItemStatus.IN_PROGRESS => CanonicalStatusCategory.IN_PROGRESS,
        WorkItemStatus.IN_REVIEW => CanonicalStatusCategory.IN_PROGRESS,
        WorkItemStatus.DONE => CanonicalStatusCategory.DONE,
        WorkItemStatus.CLOSED => CanonicalStatusCategory.DONE,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未映射的 WorkItemStatus"),
    };

    public static string ToCanonicalCategoryDbValue(this WorkItemStatus status) =>
        status.ToCanonicalCategory().ToDbValue();

    /// <summary>终态：DONE / CLOSED 之后仍可被显式 reopen（见状态机），但不参与「进行中」统计。</summary>
    public static bool IsTerminal(this WorkItemStatus status) =>
        status is WorkItemStatus.DONE or WorkItemStatus.CLOSED;
}

/// <summary>
/// WorkItem 状态机（E8 §8）。合法迁移集中在此，端点不得自己判断。
/// </summary>
/// <remarks>
/// <para>
/// 设计取向：WorkItem 是<b>人来定义推进目标</b>的载体，因此允许 reopen / 回退
/// （不像 Execution 那样一旦终态就不可逆）。真正需要闸门的是「谁有权改」，
/// 不是「能不能改回来」。
/// </para>
/// </remarks>
public static class WorkItemStateMachine
{
    private static readonly Dictionary<WorkItemStatus, WorkItemStatus[]> s_allowed = new()
    {
        [WorkItemStatus.OPEN] = [WorkItemStatus.IN_PROGRESS, WorkItemStatus.CLOSED],
        [WorkItemStatus.IN_PROGRESS] =
            [WorkItemStatus.OPEN, WorkItemStatus.IN_REVIEW, WorkItemStatus.DONE, WorkItemStatus.CLOSED],
        [WorkItemStatus.IN_REVIEW] =
            [WorkItemStatus.OPEN, WorkItemStatus.IN_PROGRESS, WorkItemStatus.DONE, WorkItemStatus.CLOSED],
        [WorkItemStatus.DONE] =
            [WorkItemStatus.IN_PROGRESS, WorkItemStatus.IN_REVIEW, WorkItemStatus.CLOSED],
        [WorkItemStatus.CLOSED] = [WorkItemStatus.OPEN],
    };

    /// <summary>该迁移是否合法。</summary>
    public static bool CanTransition(WorkItemStatus from, WorkItemStatus to)
    {
        if (from == to)
        {
            return false; // 同状态「迁移」是无操作，调用方应省略而不是当成成功
        }

        return s_allowed.TryGetValue(from, out WorkItemStatus[]? targets) && targets.Contains(to);
    }

    /// <summary>不合法时返回人类可读原因；合法时返回 <c>null</c>。</summary>
    public static string? WhyCannotTransition(WorkItemStatus from, WorkItemStatus to)
    {
        if (from == to)
        {
            return $"work item 已经是 {from.ToDbValue()}，无需迁移";
        }

        if (CanTransition(from, to))
        {
            return null;
        }

        string allowed = string.Join(", ",
            s_allowed.TryGetValue(from, out WorkItemStatus[]? targets)
                ? targets.Select(t => t.ToDbValue())
                : Array.Empty<string>());

        return $"work item 不允许从 {from.ToDbValue()} 迁移到 {to.ToDbValue()}（合法目标：{allowed}）";
    }

    /// <summary>该状态可迁移到的目标集合（UI 渲染按钮用，避免前端硬编码状态机）。</summary>
    public static IReadOnlyList<WorkItemStatus> AllowedTargets(WorkItemStatus from) =>
        s_allowed.TryGetValue(from, out WorkItemStatus[]? targets) ? targets : Array.Empty<WorkItemStatus>();
}

/// <summary>work_comments.author_type（E8 §3）。</summary>
public enum WorkCommentAuthorType
{
    HUMAN,
    AGENT,
    SYSTEM,
}

public static class WorkCommentAuthorTypeMap
{
    public const string HumanValue = "HUMAN";
    public const string AgentValue = "AGENT";
    public const string SystemValue = "SYSTEM";

    public static string ToDbValue(this WorkCommentAuthorType type) => type switch
    {
        WorkCommentAuthorType.HUMAN => HumanValue,
        WorkCommentAuthorType.AGENT => AgentValue,
        WorkCommentAuthorType.SYSTEM => SystemValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 WorkCommentAuthorType"),
    };

    public static bool TryParse(string? value, out WorkCommentAuthorType type)
    {
        switch (value)
        {
            case HumanValue: type = WorkCommentAuthorType.HUMAN; return true;
            case AgentValue: type = WorkCommentAuthorType.AGENT; return true;
            case SystemValue: type = WorkCommentAuthorType.SYSTEM; return true;
            default:
                type = (WorkCommentAuthorType)(-1);
                return false;
        }
    }
}

/// <summary>WorkItem assignee 类型（E8 §3：<c>assignee_type IN ('HUMAN','AGENT')</c>）。</summary>
public enum WorkAssigneeType
{
    HUMAN,
    AGENT,
}

public static class WorkAssigneeTypeMap
{
    public const string HumanValue = "HUMAN";
    public const string AgentValue = "AGENT";

    public static string ToDbValue(this WorkAssigneeType type) => type switch
    {
        WorkAssigneeType.HUMAN => HumanValue,
        WorkAssigneeType.AGENT => AgentValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 WorkAssigneeType"),
    };

    public static bool TryParse(string? value, out WorkAssigneeType type)
    {
        switch (value)
        {
            case HumanValue: type = WorkAssigneeType.HUMAN; return true;
            case AgentValue: type = WorkAssigneeType.AGENT; return true;
            default:
                type = (WorkAssigneeType)(-1);
                return false;
        }
    }
}

/// <summary>work_relations.relation_type（E8 §3）。</summary>
public enum WorkRelationType
{
    BLOCKS,
    BLOCKED_BY,
    RELATES_TO,
    PARENT_OF,
    CHILD_OF,
}

public static class WorkRelationTypeMap
{
    public const string BlocksValue = "BLOCKS";
    public const string BlockedByValue = "BLOCKED_BY";
    public const string RelatesToValue = "RELATES_TO";
    public const string ParentOfValue = "PARENT_OF";
    public const string ChildOfValue = "CHILD_OF";

    public static string ToDbValue(this WorkRelationType type) => type switch
    {
        WorkRelationType.BLOCKS => BlocksValue,
        WorkRelationType.BLOCKED_BY => BlockedByValue,
        WorkRelationType.RELATES_TO => RelatesToValue,
        WorkRelationType.PARENT_OF => ParentOfValue,
        WorkRelationType.CHILD_OF => ChildOfValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 WorkRelationType"),
    };

    public static bool TryParse(string? value, out WorkRelationType type)
    {
        switch (value)
        {
            case BlocksValue: type = WorkRelationType.BLOCKS; return true;
            case BlockedByValue: type = WorkRelationType.BLOCKED_BY; return true;
            case RelatesToValue: type = WorkRelationType.RELATES_TO; return true;
            case ParentOfValue: type = WorkRelationType.PARENT_OF; return true;
            case ChildOfValue: type = WorkRelationType.CHILD_OF; return true;
            default:
                type = (WorkRelationType)(-1);
                return false;
        }
    }

    /// <summary>
    /// 反向关系：BLOCKS ↔ BLOCKED_BY，PARENT_OF ↔ CHILD_OF，RELATES_TO 自反。
    /// 「A BLOCKS B」写入时同步写「B BLOCKED_BY A」，否则两端视图会互相矛盾。
    /// </summary>
    public static WorkRelationType Inverse(this WorkRelationType type) => type switch
    {
        WorkRelationType.BLOCKS => WorkRelationType.BLOCKED_BY,
        WorkRelationType.BLOCKED_BY => WorkRelationType.BLOCKS,
        WorkRelationType.PARENT_OF => WorkRelationType.CHILD_OF,
        WorkRelationType.CHILD_OF => WorkRelationType.PARENT_OF,
        WorkRelationType.RELATES_TO => WorkRelationType.RELATES_TO,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 WorkRelationType"),
    };
}

/// <summary>
/// Search 文本构造（F10：检索在 <c>work_items.search_text</c> 上）。
/// </summary>
/// <remarks>
/// 刻意<b>不</b>把手写的 <c>search_text</c> 作为可写字段暴露给 API：
/// 它必须与 title / description 同步，否则搜索会静默漏结果。
/// </remarks>
public static class WorkItemSearchText
{
    public static string Build(string title, string? description) =>
        string.Join(' ', new[] { title, description }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim()));
}
