using MateOS.Api.Work;
using MateOS.Domain.Work;

namespace MateOS.UnitTests.Work;

/// <summary>
/// E8 §4 Provider 抽象：ProviderRegistry 开放扩展 + Built-in 恒等实现。
/// </summary>
/// <remarks>
/// 这里刻意用一个「假 Provider」证明 F4/F5（E8 §7.1）：
/// 注册一个 WorkManagement Core 从未听说过的 provider_key，无需改动任何 Core 文件。
/// </remarks>
public sealed class WorkManagementProviderTests
{
    private static WorkItemBindingContext BuiltinContext(Guid bindingId) =>
        new(bindingId, BuiltInProviderKey.Value, ConnectionId: null, ExternalProjectRef: null, SettingsJson: null);

    // ───────────────────── Registry ─────────────────────

    [Fact]
    public void 注册后应能取到并出现在列表里()
    {
        var registry = new WorkManagementProviderRegistry();
        var provider = new BuiltInWorkManagementProvider();

        registry.Register(provider);

        Assert.True(registry.TryGet(BuiltInProviderKey.Value, out IWorkManagementProvider resolved));
        Assert.Same(provider, resolved);
        Assert.Equal(1, registry.Count);
        Assert.Equal([BuiltInProviderKey.Value], registry.Keys);
        Assert.Equal("MateOS Built-in", Assert.Single(registry.List()).DisplayName);
    }

    [Fact]
    public void 重复注册同一个key应抛错而不是静默覆盖()
    {
        var registry = new WorkManagementProviderRegistry();

        registry.Register(new BuiltInWorkManagementProvider());

        Assert.Throws<InvalidOperationException>(() => registry.Register(new BuiltInWorkManagementProvider()));
    }

    [Fact]
    public void 未注册的key取用应抛UnknownProviderException()
    {
        var registry = new WorkManagementProviderRegistry();

        UnknownProviderException ex = Assert.Throws<UnknownProviderException>(
            () => registry.Require("youtrack"));

        Assert.Equal("youtrack", ex.ProviderKey);
        Assert.False(registry.TryGet("youtrack", out _));
    }

    [Fact]
    public void 空key注册应被拒()
    {
        var registry = new WorkManagementProviderRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(new FakeProvider("   ")));
    }

    [Fact]
    public void 新增Provider不应改动Core文件且能与builtin共存()
    {
        var registry = new WorkManagementProviderRegistry();
        registry.Register(new BuiltInWorkManagementProvider());

        // 「加 YouTrack 时不改 Core」——只这一行
        registry.Register(new FakeProvider("youtrack"));

        Assert.Equal(2, registry.Count);
        Assert.Contains("builtin", registry.Keys);
        Assert.Contains("youtrack", registry.Keys);
    }

    // ───────────────────── Built-in 行为 ─────────────────────

    [Fact]
    public void Builtin的能力声明应为外接无关()
    {
        ProviderCapabilities capabilities = new BuiltInWorkManagementProvider().Metadata.Capabilities;

        Assert.False(capabilities.SupportsExternalRef);
        Assert.False(capabilities.SupportsStatusMapping);
        Assert.False(capabilities.SupportsWebhooks);
    }

    [Fact]
    public void Builtin应提供五态且category与状态派生一致()
    {
        IReadOnlyList<ProviderStatus> statuses = new BuiltInWorkManagementProvider()
            .ListStatuses(BuiltinContext(Guid.NewGuid()));

        Assert.Equal(5, statuses.Count);

        foreach (ProviderStatus status in statuses)
        {
            Assert.True(WorkItemStatusMap.TryParse(status.Status, out WorkItemStatus parsed));
            Assert.Equal(parsed.ToCanonicalCategoryDbValue(), status.CanonicalCategory);
        }
    }

    [Fact]
    public void Builtin绑定校验应只接受builtin这个key()
    {
        var provider = new BuiltInWorkManagementProvider();

        Assert.Null(provider.ValidateBinding(BuiltinContext(Guid.NewGuid())));

        string? error = provider.ValidateBinding(
            new WorkItemBindingContext(Guid.NewGuid(), "jira", null, "PROJ", null));

        Assert.NotNull(error);
        Assert.Contains("jira", error);
    }

    [Fact]
    public void Builtin创建应归一为OPEN且没有外部引用()
    {
        ProviderWriteOutcome outcome = new BuiltInWorkManagementProvider().OnCreate(
            new ProviderWorkItemInput(WorkItemType.TASK, "标题", null, null, null, null),
            BuiltinContext(Guid.NewGuid()));

        Assert.Equal(WorkItemStatus.OPEN, outcome.Status!.Value);
        Assert.Null(outcome.ExternalRef);
        Assert.Null(outcome.ExternalUrl);
        Assert.Null(outcome.ProviderStatus);
    }

    [Fact]
    public void Builtin更新应沿用调用方指定的状态()
    {
        var provider = new BuiltInWorkManagementProvider();

        ProviderWriteOutcome outcome = provider.OnUpdate(
            new ProviderWorkItemRef(BuiltInProviderKey.Value, Guid.NewGuid(), null),
            new ProviderWorkItemChanges(null, null, WorkItemStatus.DONE, null, null, false, null, false),
            BuiltinContext(Guid.NewGuid()));

        Assert.Equal(WorkItemStatus.DONE, outcome.Status!.Value);
        Assert.Null(outcome.ProviderStatus);
    }

    /// <summary>
    /// 只改标题（未请求状态变更）时，Provider 必须返回 <c>null</c> 而不是猜一个状态，
    /// 否则端点会把卡片状态悄悄重置。
    /// </summary>
    [Fact]
    public void Builtin未请求状态变更时应回显null()
    {
        var provider = new BuiltInWorkManagementProvider();

        ProviderWriteOutcome outcome = provider.OnUpdate(
            new ProviderWorkItemRef(BuiltInProviderKey.Value, Guid.NewGuid(), null),
            new ProviderWorkItemChanges("新标题", null, null, null, null, false, null, false),
            BuiltinContext(Guid.NewGuid()));

        Assert.Null(outcome.Status);
    }

    /// <summary>一个 Core 完全不认识的 Provider，用于验证开放扩展。</summary>
    private sealed class FakeProvider(string key) : IWorkManagementProvider
    {
        public string Key { get; } = key;

        public ProviderMetadata Metadata { get; } = new(
            key,
            "Fake",
            new ProviderCapabilities(true, true, true));

        public IReadOnlyList<ProviderStatus> ListStatuses(WorkItemBindingContext binding) =>
            [new("To Do", "TODO")];

        public string? ValidateBinding(WorkItemBindingContext binding) =>
            string.IsNullOrEmpty(binding.ExternalProjectRef) ? "需要 external_project_ref" : null;

        public ProviderWriteOutcome OnCreate(ProviderWorkItemInput input, WorkItemBindingContext binding) =>
            new(WorkItemStatus.OPEN, ExternalRef: "FAKE-1");

        public ProviderWriteOutcome OnUpdate(
            ProviderWorkItemRef reference,
            ProviderWorkItemChanges changes,
            WorkItemBindingContext binding) =>
            new(changes.Status, ProviderStatus: "To Do");
    }
}
