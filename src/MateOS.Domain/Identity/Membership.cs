namespace MateOS.Domain.Identity;

/// <summary>
/// Organization / Team / Project 三层的成员角色。
/// </summary>
/// <remarks>
/// 注意 DB 取值是<b>小写</b>（E1 §3 DDL：<c>CHECK (role IN ('owner','member'))</c>），
/// 与三条 lifecycle 的 SCREAMING_SNAKE 不同，映射集中在 <see cref="MembershipRoleMap"/>。
/// </remarks>
public enum MembershipRole
{
    Owner,
    Member,
}

/// <summary>DB 值 ↔ 强类型枚举的双向映射。</summary>
public static class MembershipRoleMap
{
    public const string OwnerDbValue = "owner";
    public const string MemberDbValue = "member";

    public static string ToDbValue(this MembershipRole role) => role switch
    {
        MembershipRole.Owner => OwnerDbValue,
        MembershipRole.Member => MemberDbValue,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未映射的 MembershipRole"),
    };

    public static bool TryParse(string? value, out MembershipRole role)
    {
        switch (value)
        {
            case OwnerDbValue:
                role = MembershipRole.Owner;
                return true;
            case MemberDbValue:
                role = MembershipRole.Member;
                return true;
            default:
                role = (MembershipRole)(-1);
                return false;
        }
    }
}

/// <summary>
/// 一个用户在单个 Workspace 内的三层角色快照（<c>null</c> = 不是该层成员）。
/// </summary>
/// <remarks>
/// <para>依据 E1 §3.1 与 §5.2：Org owner / Team owner / Project owner <b>互相独立、互不蕴含</b>。</para>
/// <para>
/// 最常见的错误实现是「Org owner 自然是所有 Team / Project 的 owner」——本类型刻意不给这种便捷属性，
/// 每次判定都必须写明是哪一层的角色，以免把正交关系写丢。
/// </para>
/// </remarks>
public sealed record WorkspaceRoles(
    MembershipRole? Organization,
    MembershipRole? Team,
    MembershipRole? Project)
{
    /// <summary>不属于任何一层。</summary>
    public static WorkspaceRoles None => new(null, null, null);

    public bool IsOrganizationOwner => Organization is MembershipRole.Owner;
    public bool IsTeamOwner => Team is MembershipRole.Owner;
    public bool IsProjectOwner => Project is MembershipRole.Owner;

    /// <summary>是否是该 Org 的成员（含 owner 与 member）。</summary>
    public bool IsOrganizationMember => Organization is not null;

    /// <summary>是否是该 Team 的成员。</summary>
    public bool IsTeamMember => Team is not null;

    /// <summary>是否是该 Project 的成员。</summary>
    public bool IsProjectMember => Project is not null;

    /// <summary>改 Org 名 / 删 Org / 转让所有权（E1 §5.2）。</summary>
    public bool CanManageOrganization => IsOrganizationOwner;

    /// <summary>改 Team 名 / 邀请成员（E1 §5.2）。</summary>
    public bool CanManageTeam => IsTeamOwner;

    /// <summary>改 Project 设置 / 邀请成员 / 绑定 Provider（E1 §5.2）。</summary>
    public bool CanManageProject => IsProjectOwner;
}

/// <summary>
/// Org 成员关系的不变量（E1 §3.1）。
/// </summary>
/// <remarks>
/// 完整约束「组织至少一个 owner」在 V1 由应用层校验（DDL 注释：DB trigger / deferrable constraint 可延后到 V2），
/// 此处提供纯函数供变更前预检，避免出现无人可管理的组织。
/// </remarks>
public static class OrganizationOwnership
{
    /// <summary>给定变更后的完整成员角色集合，判断是否仍满足「至少一个 owner」。</summary>
    public static bool HasAtLeastOneOwner(IEnumerable<MembershipRole> rolesAfterChange)
    {
        ArgumentNullException.ThrowIfNull(rolesAfterChange);

        return rolesAfterChange.Any(role => role is MembershipRole.Owner);
    }

    /// <summary>
    /// 判断本次成员变更是否会让组织失去最后一个 owner。
    /// </summary>
    /// <param name="currentOwners">变更前 owner 的用户集合。</param>
    /// <param name="targetUserId">本次变更的目标用户。</param>
    /// <param name="newRole">目标用户变更后的角色；<c>null</c> 表示移除该成员。</param>
    public static bool WouldOrphanOrganization(
        IReadOnlyCollection<Guid> currentOwners,
        Guid targetUserId,
        MembershipRole? newRole)
    {
        ArgumentNullException.ThrowIfNull(currentOwners);

        // 目标用户本就不是 owner → 无论怎么改都不会减少 owner 数
        if (!currentOwners.Contains(targetUserId))
        {
            return false;
        }

        // 目标用户仍是 owner → 不受影响
        if (newRole is MembershipRole.Owner)
        {
            return false;
        }

        // 移除 / 降级最后一个 owner
        return currentOwners.Count <= 1;
    }
}
