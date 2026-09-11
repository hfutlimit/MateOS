namespace MateOS.Domain.NeedsYou;

/// <summary>
/// Needs You 4 分类（SYSTEM_DESIGN v0.9 §5.2 + PRD §1.2 / §1.3）。
/// </summary>
/// <remarks>
/// <para>
/// 聚合多个事实源（E4 / E5 / E7）到统一 UI 入口：
/// <list type="bullet">
///   <item>INFORMATION：Agent 缺上下文（E4 CR.NEED_CONTEXT）—— 需要补充</item>
///   <item>APPROVAL：人类审批请求（E5 MemoryProposal.PROPOSED）</item>
///   <item>DECISION：CR 决策待人类拍板（V1 stub：暂用 E4 PENDING CR 投影）</item>
///   <item>PROBLEMS：执行失败 / Agent 异常（E7 Execution FAILED）</item>
/// </list>
/// </para>
/// <para>
/// 排序：先按 category 优先级（PROBLEMS > APPROVAL > DECISION > INFORMATION），
/// 再按 created_at 倒序。
/// </para>
/// </remarks>
public enum NeedsYouCategory
{
    PROBLEMS,
    APPROVAL,
    DECISION,
    INFORMATION,
}

public static class NeedsYouCategoryMap
{
    public const string ProblemsValue = "PROBLEMS";
    public const string ApprovalValue = "APPROVAL";
    public const string DecisionValue = "DECISION";
    public const string InformationValue = "INFORMATION";

    public static string ToWireValue(this NeedsYouCategory category) => category switch
    {
        NeedsYouCategory.PROBLEMS => ProblemsValue,
        NeedsYouCategory.APPROVAL => ApprovalValue,
        NeedsYouCategory.DECISION => DecisionValue,
        NeedsYouCategory.INFORMATION => InformationValue,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "未映射的 NeedsYouCategory"),
    };

    public static bool TryParse(string? value, out NeedsYouCategory category)
    {
        switch (value)
        {
            case ProblemsValue: category = NeedsYouCategory.PROBLEMS; return true;
            case ApprovalValue: category = NeedsYouCategory.APPROVAL; return true;
            case DecisionValue: category = NeedsYouCategory.DECISION; return true;
            case InformationValue: category = NeedsYouCategory.INFORMATION; return true;
            default:
                category = (NeedsYouCategory)(-1);
                return false;
        }
    }

    /// <summary>排序优先级（数字越小越靠前）。</summary>
    public static int Priority(this NeedsYouCategory category) => category switch
    {
        NeedsYouCategory.PROBLEMS => 0,
        NeedsYouCategory.APPROVAL => 1,
        NeedsYouCategory.DECISION => 2,
        NeedsYouCategory.INFORMATION => 3,
        _ => 99,
    };
}

/// <summary>
/// Needs You 6 源类型（每个源对应一个事实源事实）。
/// </summary>
public enum NeedsYouSource
{
    CollaborationRequest,
    MemoryProposal,
    Execution,
    Agent,
}

public static class NeedsYouSourceMap
{
    public const string CollaborationRequestValue = "collaboration_request";
    public const string MemoryProposalValue = "memory_proposal";
    public const string ExecutionValue = "execution";
    public const string AgentValue = "agent";

    public static string ToWireValue(this NeedsYouSource source) => source switch
    {
        NeedsYouSource.CollaborationRequest => CollaborationRequestValue,
        NeedsYouSource.MemoryProposal => MemoryProposalValue,
        NeedsYouSource.Execution => ExecutionValue,
        NeedsYouSource.Agent => AgentValue,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "未映射的 NeedsYouSource"),
    };
}
