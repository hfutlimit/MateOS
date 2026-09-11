using MateOS.Domain.Work;

namespace MateOS.Api.Work;

/// <summary>
/// Built-in Work Management Provider（E8 §3：MVP 默认实现，source of truth = <c>work_items</c> 表）。
/// </summary>
/// <remarks>
/// <para>
/// 它是 Provider 抽象的<b>恒等实现</b>：没有外部系统，因此所有 provider_* 字段保持 null，
/// 状态就是 canonical 状态。
/// </para>
/// <para>
/// 它存在的意义不是「多一层间接」，而是把 <b>路由纪律</b> 固化下来：
/// CREATE 用 active binding、UPDATE 用 work_item.binding。E9 Jira 落地时
/// 只需要新增一个实现并 register，Work Management Core 文件零改动（F4/F5）。
/// </para>
/// </remarks>
public sealed class BuiltInWorkManagementProvider : IWorkManagementProvider
{
    public const string KeyValue = BuiltInProviderKey.Value;

    public string Key => KeyValue;

    public ProviderMetadata Metadata { get; } = new(
        ProviderKey: KeyValue,
        DisplayName: "MateOS Built-in",
        Capabilities: ProviderCapabilities.BuiltIn);

    /// <summary>
    /// Built-in 的 5 态即 canonical 状态，canonical category 由状态派生（F8）。
    /// </summary>
    public IReadOnlyList<ProviderStatus> ListStatuses(WorkItemBindingContext binding) =>
    [
        new(WorkItemStatusMap.OpenValue, WorkItemStatus.OPEN.ToCanonicalCategoryDbValue()),
        new(WorkItemStatusMap.InProgressValue, WorkItemStatus.IN_PROGRESS.ToCanonicalCategoryDbValue()),
        new(WorkItemStatusMap.InReviewValue, WorkItemStatus.IN_REVIEW.ToCanonicalCategoryDbValue()),
        new(WorkItemStatusMap.DoneValue, WorkItemStatus.DONE.ToCanonicalCategoryDbValue()),
        new(WorkItemStatusMap.ClosedValue, WorkItemStatus.CLOSED.ToCanonicalCategoryDbValue()),
    ];

    /// <summary>Built-in 不依赖 connection / external_project_ref（E8 §3：connection_id 可空）。</summary>
    public string? ValidateBinding(WorkItemBindingContext binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (!string.Equals(binding.ProviderKey, KeyValue, StringComparison.Ordinal))
        {
            return $"BuiltInProvider 只接受 provider_key=builtin，收到 {binding.ProviderKey}";
        }

        return null;
    }

    public ProviderWriteOutcome OnCreate(ProviderWorkItemInput input, WorkItemBindingContext binding)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Built-in：新建即 OPEN，无外部引用
        return new ProviderWriteOutcome(WorkItemStatus.OPEN);
    }

    public ProviderWriteOutcome OnUpdate(
        ProviderWorkItemRef reference,
        ProviderWorkItemChanges changes,
        WorkItemBindingContext binding)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(changes);

        // Built-in：状态由调用方（状态机）决定，Provider 不再改；provider_status 无意义。
        // 原样回显（含 null）：null 表示「本次没有状态变更」，端点据此保持原状态。
        return new ProviderWriteOutcome(changes.Status);
    }
}
