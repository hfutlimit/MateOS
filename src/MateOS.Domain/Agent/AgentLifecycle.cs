namespace MateOS.Domain.Agent;

/// <summary>
/// Agent 生命周期（E2 §3.1）：owner 显式控制。
/// </summary>
/// <remarks>
/// lifecycle 与 activity 正交：lifecycle 决定 Agent 是否参与 Resolver 候选 + dispatch；
/// activity 仅表达 UI 状态（detailed/03 §2 v0.7 重写后 Activity 不承载协议状态）。
/// </remarks>
public enum AgentLifecycle
{
    /// <summary>owner 启用，参与 Resolver 候选 + 接受 dispatch。</summary>
    Active,

    /// <summary>owner 主动暂停，从 Resolver 候选排除，不接 dispatch（E2 §5.3）。</summary>
    Paused,

    /// <summary>系统禁用（违规 / 滥用）；同 Paused 不接 dispatch，额外可触发审计告警。</summary>
    Disabled,
}

public static class AgentLifecycleMap
{
    public const string ActiveDbValue = "ACTIVE";
    public const string PausedDbValue = "PAUSED";
    public const string DisabledDbValue = "DISABLED";

    public static string ToDbValue(this AgentLifecycle lifecycle) => lifecycle switch
    {
        AgentLifecycle.Active => ActiveDbValue,
        AgentLifecycle.Paused => PausedDbValue,
        AgentLifecycle.Disabled => DisabledDbValue,
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, "未映射的 AgentLifecycle"),
    };

    public static bool TryParse(string? value, out AgentLifecycle lifecycle)
    {
        switch (value)
        {
            case ActiveDbValue:
                lifecycle = AgentLifecycle.Active;
                return true;
            case PausedDbValue:
                lifecycle = AgentLifecycle.Paused;
                return true;
            case DisabledDbValue:
                lifecycle = AgentLifecycle.Disabled;
                return true;
            default:
                lifecycle = (AgentLifecycle)(-1);
                return false;
        }
    }
}

/// <summary>
/// Agent activity 6 态（E2 §3.1）：仅做 UI derived，不参与调度。
/// </summary>
public enum AgentActivity
{
    /// <summary>心跳丢失 > 90s 或未启动；显示灰点。</summary>
    Offline,

    /// <summary>空闲且 lifecycle=Active；显示绿点。</summary>
    Available,

    /// <summary>已收到 CollaborationRequest，正在判断（E4 触发）。</summary>
    Thinking,

    /// <summary>已 ACCEPT，正在产生 Execution（E7 dispatch 触发）。</summary>
    Working,

    /// <summary>决策 NEED_CONTEXT，等待人类补充信息（E4 触发）。</summary>
    WaitingContext,

    /// <summary>Provider 失败 / 限额 / 凭据失效（带 fix_hint）。</summary>
    Error,
}

public static class AgentActivityMap
{
    public const string OfflineDbValue = "OFFLINE";
    public const string AvailableDbValue = "AVAILABLE";
    public const string ThinkingDbValue = "THINKING";
    public const string WorkingDbValue = "WORKING";
    public const string WaitingContextDbValue = "WAITING_CONTEXT";
    public const string ErrorDbValue = "ERROR";

    public static string ToDbValue(this AgentActivity activity) => activity switch
    {
        AgentActivity.Offline => OfflineDbValue,
        AgentActivity.Available => AvailableDbValue,
        AgentActivity.Thinking => ThinkingDbValue,
        AgentActivity.Working => WorkingDbValue,
        AgentActivity.WaitingContext => WaitingContextDbValue,
        AgentActivity.Error => ErrorDbValue,
        _ => throw new ArgumentOutOfRangeException(nameof(activity), activity, "未映射的 AgentActivity"),
    };

    public static bool TryParse(string? value, out AgentActivity activity)
    {
        switch (value)
        {
            case OfflineDbValue: activity = AgentActivity.Offline; return true;
            case AvailableDbValue: activity = AgentActivity.Available; return true;
            case ThinkingDbValue: activity = AgentActivity.Thinking; return true;
            case WorkingDbValue: activity = AgentActivity.Working; return true;
            case WaitingContextDbValue: activity = AgentActivity.WaitingContext; return true;
            case ErrorDbValue: activity = AgentActivity.Error; return true;
            default:
                activity = (AgentActivity)(-1);
                return false;
        }
    }
}

/// <summary>
/// Lifecycle × Activity 正交不变量（E2 §3.1 v0.4 拆开）。
/// </summary>
/// <remarks>
/// <para>
/// 规则：当 lifecycle ≠ Active 时，activity 强制显示为 <see cref="AgentActivity.Offline"/>。
/// 业务层校验：<see cref="ResolveActivity"/> 把任意输入归一为合法组合。
/// </para>
/// <para>
/// 反向不成立：lifecycle = Active 不蕴含 activity ≠ Offline（active + offline 是合法的
/// "刚注册还没启动 stub" 状态）。
/// </para>
/// </remarks>
public static class AgentLifecycleActivity
{
    /// <summary>归一化：lifecycle 处于非 Active 状态时强制 activity = Offline。</summary>
    public static AgentActivity ResolveActivity(AgentLifecycle lifecycle, AgentActivity activity)
    {
        return lifecycle is AgentLifecycle.Active
            ? activity
            : AgentActivity.Offline;
    }

    /// <summary>activity 上报校验：lifecycle ≠ Active 时拒绝变更（非业务允许的状态迁移）。</summary>
    public static string? CanTransitionTo(AgentLifecycle lifecycle, AgentActivity targetActivity)
    {
        if (lifecycle is not AgentLifecycle.Active)
        {
            return $"lifecycle={lifecycle.ToDbValue()} 时不允许 activity 变更";
        }

        return null;
    }
}
