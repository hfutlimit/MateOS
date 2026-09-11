namespace MateOS.Domain.Permission;

/// <summary>
/// 8 个 canonical 权限键（E6 §2.1 v0.4.5 + detailed/07 v0.4.3）。
/// </summary>
/// <remarks>
/// <para>
/// canonical key 收敛：v0.4.5 起硬编码 8 个（V2 升 DB 表）。
/// </para>
/// <list type="bullet">
///   <item>read_message：读取 channel 消息（默认 ALLOW for member）</item>
///   <item>write_message：在 channel 发消息（默认 ALLOW for member）</item>
///   <item>propose_memory：发起 memory_proposal（默认 ALLOW for member；与 write_memory 拆开）</item>
///   <item>write_memory：写入 memory（默认 REQUIRE_APPROVAL；走 E5 审批路径）</item>
///   <item>execute_code：执行代码（默认 DENY；V1 关闭）</item>
///   <item>create_pr：建 PR（默认 REQUIRE_APPROVAL for owner；走 E4 CollaborationRequest）</item>
///   <item>approve_memory：审批 memory（默认 owner ALLOW；member DENY）</item>
///   <item>manage_channel：管理 channel（默认 owner ALLOW；member DENY）</item>
/// </list>
/// </remarks>
public static class PermKey
{
    public const string ReadMessage = "read_message";
    public const string WriteMessage = "write_message";
    public const string ProposeMemory = "propose_memory";
    public const string WriteMemory = "write_memory";
    public const string ExecuteCode = "execute_code";
    public const string CreatePr = "create_pr";
    public const string ApproveMemory = "approve_memory";
    public const string ManageChannel = "manage_channel";

    public static readonly IReadOnlySet<string> Canonical = new HashSet<string>(StringComparer.Ordinal)
    {
        ReadMessage, WriteMessage, ProposeMemory, WriteMemory,
        ExecuteCode, CreatePr, ApproveMemory, ManageChannel,
    };

    public static string? Validate(string? permKey)
    {
        if (string.IsNullOrWhiteSpace(permKey))
        {
            return "perm_key 不能为空";
        }

        if (!Canonical.Contains(permKey))
        {
            return $"未知 perm_key：{permKey}（canonical: {string.Join(", ", Canonical)}）";
        }

        return null;
    }
}

/// <summary>
/// Effect 三态（E6 §2.1 v0.4 改）。
/// </summary>
/// <remarks>
/// v0.4 把 v0.3 的 REQUEST 改为 REQUIRE_APPROVAL（避免与 CollaborationRequest / HTTP Request 概念冲突）。
/// v0.4.2 把 Guard 拆为 <c>checkPermission</c>（ALLOW/DENY）+ 业务层 <c>policy.evaluate()</c>（REQUIRE_APPROVAL 由业务层显式路由到 E5/E4）。
/// </remarks>
public enum PermEffect
{
    ALLOW,
    DENY,
    REQUIRE_APPROVAL,
}

public static class PermEffectMap
{
    public const string AllowValue = "ALLOW";
    public const string DenyValue = "DENY";
    public const string RequireApprovalValue = "REQUIRE_APPROVAL";

    public static string ToDbValue(this PermEffect effect) => effect switch
    {
        PermEffect.ALLOW => AllowValue,
        PermEffect.DENY => DenyValue,
        PermEffect.REQUIRE_APPROVAL => RequireApprovalValue,
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "未映射的 PermEffect"),
    };

    public static bool TryParse(string? value, out PermEffect effect)
    {
        switch (value)
        {
            case AllowValue: effect = PermEffect.ALLOW; return true;
            case DenyValue: effect = PermEffect.DENY; return true;
            case RequireApprovalValue: effect = PermEffect.REQUIRE_APPROVAL; return true;
            default:
                effect = (PermEffect)(-1);
                return false;
        }
    }
}

/// <summary>权限 scope 类型。</summary>
public enum PermScopeType
{
    PROJECT,
    CHANNEL,
}

public static class PermScopeTypeMap
{
    public const string ProjectValue = "PROJECT";
    public const string ChannelValue = "CHANNEL";

    public static string ToDbValue(this PermScopeType scope) => scope switch
    {
        PermScopeType.PROJECT => ProjectValue,
        PermScopeType.CHANNEL => ChannelValue,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "未映射的 PermScopeType"),
    };

    public static bool TryParse(string? value, out PermScopeType scope)
    {
        switch (value)
        {
            case ProjectValue: scope = PermScopeType.PROJECT; return true;
            case ChannelValue: scope = PermScopeType.CHANNEL; return true;
            default:
                scope = (PermScopeType)(-1);
                return false;
        }
    }
}

/// <summary>权限 subject 类型。</summary>
public enum PermSubjectType
{
    USER,
    AGENT,
}

public static class PermSubjectTypeMap
{
    public const string UserValue = "USER";
    public const string AgentValue = "AGENT";

    public static string ToDbValue(this PermSubjectType subject) => subject switch
    {
        PermSubjectType.USER => UserValue,
        PermSubjectType.AGENT => AgentValue,
        _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, "未映射的 PermSubjectType"),
    };

    public static bool TryParse(string? value, out PermSubjectType subject)
    {
        switch (value)
        {
            case UserValue: subject = PermSubjectType.USER; return true;
            case AgentValue: subject = PermSubjectType.AGENT; return true;
            default:
                subject = (PermSubjectType)(-1);
                return false;
        }
    }
}

/// <summary>Subject 在 workspace 中的角色（决定默认矩阵查询路径）。</summary>
public enum PermRole
{
    Owner,
    Member,
}

public static class PermRoleMap
{
    public const string OwnerValue = "owner";
    public const string MemberValue = "member";

    public static string ToDbValue(this PermRole role) => role switch
    {
        PermRole.Owner => OwnerValue,
        PermRole.Member => MemberValue,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未映射的 PermRole"),
    };

    public static bool TryParse(string? value, out PermRole role)
    {
        switch (value)
        {
            case OwnerValue: role = PermRole.Owner; return true;
            case MemberValue: role = PermRole.Member; return true;
            default:
                role = (PermRole)(-1);
                return false;
        }
    }
}
