namespace MateOS.Domain.Work;

/// <summary>
/// ProviderKey 是 <b>string</b>（E8 §4 v0.4.1：去硬编码）。
/// </summary>
/// <remarks>
/// 刻意不定义 <c>enum ProviderKey</c>：加 YouTrack / Azure Boards / Linear 时
/// 不应改动 Work Management Core 的任何文件（F4/F5），只注册一个新实现。
/// </remarks>
public static class BuiltInProviderKey
{
    public const string Value = "builtin";
}

/// <summary>Provider 能力声明（UI 据此决定是否显示 external 字段 / 状态映射设置）。</summary>
public sealed record ProviderCapabilities(
    bool SupportsExternalRef,
    bool SupportsStatusMapping,
    bool SupportsWebhooks)
{
    /// <summary>Built-in：MateOS 自持事实源，没有外部系统可同步。</summary>
    public static ProviderCapabilities BuiltIn { get; } = new(
        SupportsExternalRef: false,
        SupportsStatusMapping: false,
        SupportsWebhooks: false);
}

/// <summary>Provider 暴露的一个状态选项（E8 §5.3：动态拉，不写死模板）。</summary>
public sealed record ProviderStatus(string Status, string CanonicalCategory);

/// <summary>Provider 自描述（<c>GET /work-management/providers</c> 返回）。</summary>
public sealed record ProviderMetadata(string ProviderKey, string DisplayName, ProviderCapabilities Capabilities);

/// <summary>
/// Provider 的路由上下文（= binding 的展开）。<b>路由只看 binding</b>，不看 active binding。
/// </summary>
public sealed record WorkItemBindingContext(
    Guid BindingId,
    string ProviderKey,
    Guid? ConnectionId,
    string? ExternalProjectRef,
    string? SettingsJson);

/// <summary>WorkItem 在 Provider 侧的引用（E8 §4 <c>WorkItemRef</c>）。</summary>
public sealed record ProviderWorkItemRef(string ProviderKey, Guid? LocalId, string? ExternalRef);

/// <summary>创建输入（provider 无关的 canonical 字段）。</summary>
public sealed record ProviderWorkItemInput(
    WorkItemType Type,
    string Title,
    string? Description,
    WorkAssigneeType? AssigneeType,
    Guid? AssigneeId,
    DateTimeOffset? DueAt);

/// <summary>修改输入。<c>Clear*</c> 用于区分「不改」与「清空」。</summary>
public sealed record ProviderWorkItemChanges(
    string? Title,
    string? Description,
    WorkItemStatus? Status,
    WorkAssigneeType? AssigneeType,
    Guid? AssigneeId,
    bool ClearAssignee,
    DateTimeOffset? DueAt,
    bool ClearDueAt);

/// <summary>
/// Provider 写入结果。Built-in 是恒等映射；未来 Jira 在此填入 external_ref / provider_status。
/// </summary>
/// <remarks>
/// <para>
/// <c>Status</c> 刻意做成可空：<c>null</c> = 「Provider 没有改变状态」。
/// 一次只改标题的 PATCH 不应该顺带把状态写成 OPEN；若这里用非空枚举，
/// Built-in 的 <c>OnUpdate</c> 就必须猜一个状态，猜错 =
/// <b>改描述会把卡片状态悄悄重置</b>。「设为 X / 不改 / 清空」三态在状态字段上是必需的。
/// </para>
/// </remarks>
public sealed record ProviderWriteOutcome(
    WorkItemStatus? Status,
    string? ExternalRef = null,
    string? ExternalUrl = null,
    string? ProviderStatus = null,
    string? ProviderMetaJson = null);

/// <summary>
/// Work Management Provider（E8 §4）。Built-in 是 MVP 默认实现。
/// </summary>
/// <remarks>
/// <para>
/// 本接口<b>不含任何 EF / HTTP 类型</b>：Provider 只做「canonical 字段 ↔ 外部表示」的翻译，
/// 持久化与事务由 Work Management Core（端点 / 应用服务）负责。这样 Jira 实现可以直接
/// 调外部 API 而不需要拿到 DbContext。
/// </para>
/// </remarks>
public interface IWorkManagementProvider
{
    /// <summary>注册键（ProviderRegistry 的 key）。</summary>
    string Key { get; }

    ProviderMetadata Metadata { get; }

    /// <summary>状态选项（E8 F8 / §5.3：动态拉，Provider 级静态模板是反模式）。</summary>
    IReadOnlyList<ProviderStatus> ListStatuses(WorkItemBindingContext binding);

    /// <summary>校验 binding 是否满足该 Provider 的最小要求；不满足返回原因。</summary>
    string? ValidateBinding(WorkItemBindingContext binding);

    /// <summary>创建时的字段归一（E8 §5.1 CREATE → active binding）。</summary>
    ProviderWriteOutcome OnCreate(ProviderWorkItemInput input, WorkItemBindingContext binding);

    /// <summary>
    /// 修改时的字段归一（E8 §5.1 UPDATE → <c>work_item.binding</c>，不是 active binding）。
    /// </summary>
    ProviderWriteOutcome OnUpdate(
        ProviderWorkItemRef reference,
        ProviderWorkItemChanges changes,
        WorkItemBindingContext binding);
}

/// <summary>
/// ProviderRegistry（E8 §4：bootstrap 注入，<c>register</c> / <c>get</c> / <c>list</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 注册是<b>启动期</b>行为：Built-in 在宿主启动时注册，E9 Jira 在绑连接时注册。
/// 运行期不允许 deregister —— 否则一个 provider 被移除后，引用它的历史
/// WorkItem 会在 UPDATE 路径上永久 500，而不是给出可读错误。
/// </para>
/// <para>线程安全：注册只发生在启动/绑定连接时，读取用无锁字典读。</para>
/// </remarks>
public sealed class WorkManagementProviderRegistry
{
    private readonly Dictionary<string, IWorkManagementProvider> _providers = new(StringComparer.Ordinal);

    public void Register(IWorkManagementProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrWhiteSpace(provider.Key))
        {
            throw new ArgumentException("provider.Key 不能为空", nameof(provider));
        }

        if (!_providers.TryAdd(provider.Key, provider))
        {
            throw new InvalidOperationException($"provider_key 已注册：{provider.Key}");
        }
    }

    public bool TryGet(string providerKey, out IWorkManagementProvider provider)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            provider = null!;
            return false;
        }

        return _providers.TryGetValue(providerKey, out provider!);
    }

    /// <summary>取不到时抛 <see cref="UnknownProviderException"/>，由端点映射成 422（而非 500）。</summary>
    public IWorkManagementProvider Require(string providerKey) =>
        TryGet(providerKey, out IWorkManagementProvider provider)
            ? provider
            : throw new UnknownProviderException(providerKey);

    public IReadOnlyList<ProviderMetadata> List() =>
        _providers.Values.Select(p => p.Metadata).OrderBy(m => m.ProviderKey, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<string> Keys => _providers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    public int Count => _providers.Count;
}

/// <summary>
/// 引用了未注册的 provider_key（E8 §5.1：CREAT 时必须 422 "no active Work Management Provider"）。
/// </summary>
public sealed class UnknownProviderException(string providerKey)
    : Exception($"work management provider 未注册：{providerKey}")
{
    public string ProviderKey { get; } = providerKey;
}
