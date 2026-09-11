namespace MateOS.Domain.Channel;

/// <summary>
/// Channel 成员类型（E3 §3 DDL：<c>member_type IN ('HUMAN','AGENT')</c>）。
/// </summary>
/// <remarks>
/// 与 <see cref="MemberActorType"/> 区分：这里是 <b>谁是 channel 的成员</b>，
/// 那个是 <b>消息流的发送方</b>（额外含 <c>SYSTEM</c>）。
/// </remarks>
public enum ChannelMemberType
{
    Human,
    Agent,
}

public static class ChannelMemberTypeMap
{
    public const string HumanDbValue = "HUMAN";
    public const string AgentDbValue = "AGENT";

    public static string ToDbValue(this ChannelMemberType type) => type switch
    {
        ChannelMemberType.Human => HumanDbValue,
        ChannelMemberType.Agent => AgentDbValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未映射的 ChannelMemberType"),
    };

    public static bool TryParse(string? value, out ChannelMemberType type)
    {
        switch (value)
        {
            case HumanDbValue:
                type = ChannelMemberType.Human;
                return true;
            case AgentDbValue:
                type = ChannelMemberType.Agent;
                return true;
            default:
                type = (ChannelMemberType)(-1);
                return false;
        }
    }
}

/// <summary>
/// Channel 内成员角色（E3 §3 DDL：<c>role IN ('owner','member')</c>）。
/// </summary>
/// <remarks>
/// 与 E1 <see cref="Identity.MembershipRole"/> 共享枚举（'owner'/'member' 小写），
/// 但这是 <b>channel 维度</b>的角色，不是 workspace 维度的。映射不复用，
/// 因为后续 V2 可能加 channel-only 角色（moderator / pinned），届时扩展点收敛在这里。
/// </remarks>
public enum ChannelMemberRole
{
    Owner,
    Member,
}

public static class ChannelMemberRoleMap
{
    public const string OwnerDbValue = "owner";
    public const string MemberDbValue = "member";

    public static string ToDbValue(this ChannelMemberRole role) => role switch
    {
        ChannelMemberRole.Owner => OwnerDbValue,
        ChannelMemberRole.Member => MemberDbValue,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未映射的 ChannelMemberRole"),
    };

    public static bool TryParse(string? value, out ChannelMemberRole role)
    {
        switch (value)
        {
            case OwnerDbValue:
                role = ChannelMemberRole.Owner;
                return true;
            case MemberDbValue:
                role = ChannelMemberRole.Member;
                return true;
            default:
                role = (ChannelMemberRole)(-1);
                return false;
        }
    }
}

/// <summary>
/// Channel 成员关系与角色判定（E3 §2.1 / §7 F6）。
/// </summary>
/// <remarks>
/// 「channel member」= 在 <c>channel_members</c> 表里有一行（HUMAN 或 AGENT）。
/// 「channel owner」= 同上但 role='owner'，可管理 channel（踢人 / 归档 / 改 info）。
/// 「非成员」= 没有任何行——访问消息流应返 403。
/// </remarks>
public static class ChannelMembership
{
    /// <summary>是否属于该 channel（HUMAN 或 AGENT 任一类型皆可）。</summary>
    public static bool IsMember(
        IReadOnlyCollection<ChannelMemberType> memberTypes,
        ChannelMemberType actorType)
    {
        ArgumentNullException.ThrowIfNull(memberTypes);
        return memberTypes.Contains(actorType);
    }

    /// <summary>是否能在 channel 内发言（owner 与 member 皆可发消息）。</summary>
    public static bool CanPost(ChannelMemberRole? role) => role is not null;

    /// <summary>是否可管理 channel（踢人 / 归档 / 改名 / 改描述）。</summary>
    public static bool CanManage(ChannelMemberRole? role) => role is ChannelMemberRole.Owner;
}
