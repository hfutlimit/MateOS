namespace MateOS.Domain.Permission;

/// <summary>
/// 默认权限矩阵（E6 §3.1）。
/// </summary>
/// <remarks>
/// 行 = (subject_type, role_in_workspace)，列 = perm_key。
/// V1 简化：只区分 subject 类型（USER / AGENT）+ workspace 角色（owner / member）。
/// Agent 一律按 lifecycle=ACTIVE 视为 member（lifecycle 检查由 E2 单独做）。
/// </remarks>
public static class PermissionPolicy
{
    /// <summary>默认权限矩阵（不可变）。</summary>
    public static readonly IReadOnlyDictionary<(PermSubjectType Subject, PermRole Role, string PermKey), PermEffect>
        Default = new Dictionary<(PermSubjectType, PermRole, string), PermEffect>()
        {
            // ── USER owner ──
            [(PermSubjectType.USER, PermRole.Owner, PermKey.ReadMessage)] = PermEffect.ALLOW,
            [(PermSubjectType.USER, PermRole.Owner, PermKey.WriteMessage)] = PermEffect.ALLOW,
            [(PermSubjectType.USER, PermRole.Owner, PermKey.ProposeMemory)] = PermEffect.ALLOW,
            [(PermSubjectType.USER, PermRole.Owner, PermKey.WriteMemory)] = PermEffect.REQUIRE_APPROVAL,
            [(PermSubjectType.USER, PermRole.Owner, PermKey.ExecuteCode)] = PermEffect.DENY,
            [(PermSubjectType.USER, PermRole.Owner, PermKey.CreatePr)] = PermEffect.REQUIRE_APPROVAL,
            [(PermSubjectType.USER, PermRole.Owner, PermKey.ApproveMemory)] = PermEffect.ALLOW,
            [(PermSubjectType.USER, PermRole.Owner, PermKey.ManageChannel)] = PermEffect.ALLOW,

            // ── USER member ──
            [(PermSubjectType.USER, PermRole.Member, PermKey.ReadMessage)] = PermEffect.ALLOW,
            [(PermSubjectType.USER, PermRole.Member, PermKey.WriteMessage)] = PermEffect.ALLOW,
            [(PermSubjectType.USER, PermRole.Member, PermKey.ProposeMemory)] = PermEffect.ALLOW,
            [(PermSubjectType.USER, PermRole.Member, PermKey.WriteMemory)] = PermEffect.REQUIRE_APPROVAL,
            [(PermSubjectType.USER, PermRole.Member, PermKey.ExecuteCode)] = PermEffect.DENY,
            [(PermSubjectType.USER, PermRole.Member, PermKey.CreatePr)] = PermEffect.DENY,
            [(PermSubjectType.USER, PermRole.Member, PermKey.ApproveMemory)] = PermEffect.DENY,
            [(PermSubjectType.USER, PermRole.Member, PermKey.ManageChannel)] = PermEffect.DENY,

            // ── AGENT owner 不存在（agent 在 workspace 中只有 member 角色；按 E6 §3.1 行定义）──
            [(PermSubjectType.AGENT, PermRole.Owner, PermKey.ReadMessage)] = PermEffect.DENY,
            [(PermSubjectType.AGENT, PermRole.Owner, PermKey.WriteMessage)] = PermEffect.DENY,
            [(PermSubjectType.AGENT, PermRole.Owner, PermKey.ProposeMemory)] = PermEffect.DENY,
            [(PermSubjectType.AGENT, PermRole.Owner, PermKey.WriteMemory)] = PermEffect.DENY,
            [(PermSubjectType.AGENT, PermRole.Owner, PermKey.ExecuteCode)] = PermEffect.DENY,
            [(PermSubjectType.AGENT, PermRole.Owner, PermKey.CreatePr)] = PermEffect.DENY,
            [(PermSubjectType.AGENT, PermRole.Owner, PermKey.ApproveMemory)] = PermEffect.DENY,
            [(PermSubjectType.AGENT, PermRole.Owner, PermKey.ManageChannel)] = PermEffect.DENY,

            // ── AGENT member（V1 agent 默认 read/write message ALLOW，其他 DENY）──
            [(PermSubjectType.AGENT, PermRole.Member, PermKey.ReadMessage)] = PermEffect.ALLOW,
            [(PermSubjectType.AGENT, PermRole.Member, PermKey.WriteMessage)] = PermEffect.ALLOW,
            [(PermSubjectType.AGENT, PermRole.Member, PermKey.ProposeMemory)] = PermEffect.ALLOW,
            [(PermSubjectType.AGENT, PermRole.Member, PermKey.WriteMemory)] = PermEffect.REQUIRE_APPROVAL,
            [(PermSubjectType.AGENT, PermRole.Member, PermKey.ExecuteCode)] = PermEffect.DENY,
            [(PermSubjectType.AGENT, PermRole.Member, PermKey.CreatePr)] = PermEffect.DENY,
            [(PermSubjectType.AGENT, PermRole.Member, PermKey.ApproveMemory)] = PermEffect.DENY,
            [(PermSubjectType.AGENT, PermRole.Member, PermKey.ManageChannel)] = PermEffect.DENY,
        };

    /// <summary>查默认矩阵（V1 简化：所有 default 都从 Default 表取，无业务侧硬编码）。</summary>
    public static PermEffect GetDefault(PermSubjectType subject, PermRole role, string permKey)
    {
        if (Default.TryGetValue((subject, role, permKey), out PermEffect effect))
        {
            return effect;
        }

        // 防御性：未列入矩阵的 key 全部 DENY（不引入新权限时即拒绝；E6 §5 收口）
        return PermEffect.DENY;
    }
}
