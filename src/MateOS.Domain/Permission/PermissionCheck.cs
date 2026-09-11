namespace MateOS.Domain.Permission;

/// <summary>
/// 三层合并：Channel > Project > Default（E6 §2.1 + §3）。
/// </summary>
/// <remarks>
/// <para>
/// 流程（v0.4 + v0.4.2）：
/// <list type="number">
///   <item>查 <c>channel scope</c> 的 override；命中则按 override effect 返回</item>
///   <item>查 <c>project scope</c> 的 override；命中则返回</item>
///   <item>查 <c>PermissionPolicy.GetDefault(subject, role, perm)</c> 返回默认矩阵</item>
/// </list>
/// </para>
/// <para>
/// v0.4.2 安全规则：Channel override 为 <see cref="PermEffect.DENY"/> 时立即返回 DENY，
/// 不被 project override / default 覆盖（deny-by-default 在最近 scope 优先）。
/// </para>
/// <para>
/// 本类只放纯函数判定（不连 DB）；DB 查 override 在 Api 层 <c>PermissionStore</c>。
/// </para>
/// </remarks>
public static class PermissionCheck
{
    /// <summary>三层合并纯函数（override 集合由 caller 从 DB 拉来）。</summary>
    /// <param name="channelOverride">channel scope 的 override（null 表示无）</param>
    /// <param name="projectOverride">project scope 的 override（null 表示无）</param>
    /// <param name="subject">subject 类型（USER / AGENT）</param>
    /// <param name="role">subject 在 workspace 内的角色（owner / member）</param>
    /// <param name="permKey">权限键</param>
    public static PermEffect Merge(
        PermEffect? channelOverride,
        PermEffect? projectOverride,
        PermSubjectType subject,
        PermRole role,
        string permKey)
    {
        // 1. Channel 优先
        if (channelOverride.HasValue)
        {
            return channelOverride.Value;
        }

        // 2. Project 次之
        if (projectOverride.HasValue)
        {
            return projectOverride.Value;
        }

        // 3. 默认矩阵兜底
        return PermissionPolicy.GetDefault(subject, role, permKey);
    }

    /// <summary>
    /// v0.4.2 Guard 用的二态判定（REQUIRE_APPROVAL 视作 DENY，避免 Guard 静默放行）。
    /// </summary>
    /// <remarks>
    /// ASP.NET filter / 端点级 Guard 应走这个函数；业务层需 <c>REQUIRE_APPROVAL</c> 路由时
    /// 走 <see cref="Merge"/> 拿三态，自己决定是否发起审批（不调用本函数）。
    /// </remarks>
    public static bool IsAllowedForGuard(PermEffect effect) =>
        effect is PermEffect.ALLOW;

    /// <summary>Guard 反向判定。</summary>
    public static bool IsDeniedForGuard(PermEffect effect) =>
        effect is PermEffect.DENY or PermEffect.REQUIRE_APPROVAL;
}
