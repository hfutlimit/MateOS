using MateOS.Domain.Permission;

namespace MateOS.UnitTests.Permission;

/// <summary>
/// 覆盖 E6 §2.1 + §3.1 8 权限键 + 默认权限矩阵 + §3 三层合并。
/// </summary>
public sealed class PermissionTests
{
    [Theory]
    [InlineData("read_message")]
    [InlineData("write_message")]
    [InlineData("propose_memory")]
    [InlineData("write_memory")]
    [InlineData("execute_code")]
    [InlineData("create_pr")]
    [InlineData("approve_memory")]
    [InlineData("manage_channel")]
    public void PermKeyValidate应接受8个canonical键(string key)
    {
        Assert.Null(PermKey.Validate(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("magic")]
    [InlineData(null)]
    [InlineData("read_message ")]
    public void PermKeyValidate应拒绝非法键(string? key)
    {
        Assert.NotNull(PermKey.Validate(key));
    }

    [Theory]
    [InlineData("ALLOW", PermEffect.ALLOW)]
    [InlineData("DENY", PermEffect.DENY)]
    [InlineData("REQUIRE_APPROVAL", PermEffect.REQUIRE_APPROVAL)]
    [InlineData("invalid", null)]
    public void PermEffectMap应正确解析(string raw, PermEffect? expected)
    {
        bool ok = PermEffectMap.TryParse(raw, out PermEffect effect);
        Assert.Equal(expected.HasValue, ok);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value, effect);
        }
    }

    [Theory]
    [InlineData("PROJECT", PermScopeType.PROJECT)]
    [InlineData("CHANNEL", PermScopeType.CHANNEL)]
    public void PermScopeTypeMap应正确解析(string raw, PermScopeType expected)
    {
        Assert.True(PermScopeTypeMap.TryParse(raw, out PermScopeType scope));
        Assert.Equal(expected, scope);
    }

    [Theory]
    [InlineData("USER", PermSubjectType.USER)]
    [InlineData("AGENT", PermSubjectType.AGENT)]
    public void PermSubjectTypeMap应正确解析(string raw, PermSubjectType expected)
    {
        Assert.True(PermSubjectTypeMap.TryParse(raw, out PermSubjectType subject));
        Assert.Equal(expected, subject);
    }
}

/// <summary>
/// 覆盖 E6 §3.1 默认权限矩阵（v0.4 关键约定）。
/// </summary>
public sealed class PermissionPolicyTests
{
    [Theory]
    // USER owner
    [InlineData(PermSubjectType.USER, PermRole.Owner, PermKey.ReadMessage, PermEffect.ALLOW)]
    [InlineData(PermSubjectType.USER, PermRole.Owner, PermKey.WriteMessage, PermEffect.ALLOW)]
    [InlineData(PermSubjectType.USER, PermRole.Owner, PermKey.ApproveMemory, PermEffect.ALLOW)]
    [InlineData(PermSubjectType.USER, PermRole.Owner, PermKey.ManageChannel, PermEffect.ALLOW)]
    [InlineData(PermSubjectType.USER, PermRole.Owner, PermKey.WriteMemory, PermEffect.REQUIRE_APPROVAL)]
    [InlineData(PermSubjectType.USER, PermRole.Owner, PermKey.CreatePr, PermEffect.REQUIRE_APPROVAL)]
    [InlineData(PermSubjectType.USER, PermRole.Owner, PermKey.ExecuteCode, PermEffect.DENY)]
    // USER member
    [InlineData(PermSubjectType.USER, PermRole.Member, PermKey.ReadMessage, PermEffect.ALLOW)]
    [InlineData(PermSubjectType.USER, PermRole.Member, PermKey.WriteMessage, PermEffect.ALLOW)]
    [InlineData(PermSubjectType.USER, PermRole.Member, PermKey.WriteMemory, PermEffect.REQUIRE_APPROVAL)]
    [InlineData(PermSubjectType.USER, PermRole.Member, PermKey.ApproveMemory, PermEffect.DENY)]
    [InlineData(PermSubjectType.USER, PermRole.Member, PermKey.ManageChannel, PermEffect.DENY)]
    [InlineData(PermSubjectType.USER, PermRole.Member, PermKey.ExecuteCode, PermEffect.DENY)]
    // AGENT member
    [InlineData(PermSubjectType.AGENT, PermRole.Member, PermKey.ReadMessage, PermEffect.ALLOW)]
    [InlineData(PermSubjectType.AGENT, PermRole.Member, PermKey.WriteMessage, PermEffect.ALLOW)]
    [InlineData(PermSubjectType.AGENT, PermRole.Member, PermKey.ProposeMemory, PermEffect.ALLOW)]
    [InlineData(PermSubjectType.AGENT, PermRole.Member, PermKey.WriteMemory, PermEffect.REQUIRE_APPROVAL)]
    [InlineData(PermSubjectType.AGENT, PermRole.Member, PermKey.ExecuteCode, PermEffect.DENY)]
    public void 默认矩阵应按E6_S3_1实现(PermSubjectType subject, PermRole role, string permKey, PermEffect expected)
    {
        Assert.Equal(expected, PermissionPolicy.GetDefault(subject, role, permKey));
    }
}

/// <summary>
/// 覆盖 E6 §2.1 + §3 三层合并（Channel > Project > Default）。
/// </summary>
public sealed class PermissionCheckTests
{
    [Fact]
    public void 无override时应返默认矩阵值()
    {
        Assert.Equal(PermEffect.ALLOW,
            PermissionCheck.Merge(null, null, PermSubjectType.USER, PermRole.Owner, PermKey.ReadMessage));
    }

    [Fact]
    public void channel_override应优先于project_override和default()
    {
        Assert.Equal(PermEffect.DENY,
            PermissionCheck.Merge(
                channelOverride: PermEffect.DENY,
                projectOverride: PermEffect.ALLOW,
                subject: PermSubjectType.USER,
                role: PermRole.Owner,
                permKey: PermKey.ReadMessage));
    }

    [Fact]
    public void 仅project_override时_应被采纳()
    {
        Assert.Equal(PermEffect.REQUIRE_APPROVAL,
            PermissionCheck.Merge(
                channelOverride: null,
                projectOverride: PermEffect.REQUIRE_APPROVAL,
                subject: PermSubjectType.USER,
                role: PermRole.Member,
                permKey: PermKey.WriteMemory));
    }

    [Fact]
    public void channel_override_ALLOW能压过project_default_DENY()
    {
        // V1 简化：仅 channel override 一票；不叠加 DENY-aliased
        Assert.Equal(PermEffect.ALLOW,
            PermissionCheck.Merge(
                channelOverride: PermEffect.ALLOW,
                projectOverride: null,
                subject: PermSubjectType.USER,
                role: PermRole.Member,
                permKey: PermKey.WriteMemory));
    }

    [Fact]
    public void Guard用IsAllowedForGuard_仅ALLOW返回true()
    {
        Assert.True(PermissionCheck.IsAllowedForGuard(PermEffect.ALLOW));
        Assert.False(PermissionCheck.IsAllowedForGuard(PermEffect.DENY));
        // v0.4.2 关键：REQUIRE_APPROVAL 也视为 denied（避免 Guard 静默放行）
        Assert.False(PermissionCheck.IsAllowedForGuard(PermEffect.REQUIRE_APPROVAL));
    }

    [Fact]
    public void Guard用IsDeniedForGuard_涵盖DENY和REQUIRE_APPROVAL()
    {
        Assert.False(PermissionCheck.IsDeniedForGuard(PermEffect.ALLOW));
        Assert.True(PermissionCheck.IsDeniedForGuard(PermEffect.DENY));
        Assert.True(PermissionCheck.IsDeniedForGuard(PermEffect.REQUIRE_APPROVAL));
    }

    [Fact]
    public void 默认fallback应DENY未列出的键()
    {
        // 即便有 8 个 canonical 键，Default 字典没列的 permKey 仍应 DENY
        // （防御性：未来添加新 key 不会意外放行）
        Assert.Equal(PermEffect.DENY,
            PermissionPolicy.GetDefault(PermSubjectType.USER, PermRole.Owner, "not_a_key"));
    }
}
