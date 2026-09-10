using MateOS.Domain.Identity;

namespace MateOS.UnitTests.Identity;

/// <summary>
/// 覆盖 E1 §3.1 / §5.2 的三层 owner 正交与 §7.1 F3，以及「组织至少一个 owner」约束。
/// </summary>
public sealed class MembershipTests
{
    [Fact]
    public void 三层owner应互相正交()
    {
        // E1 §7.1 F3：同一 User 可同时是 Org owner + Team member + Project owner
        var roles = new WorkspaceRoles(
            Organization: MembershipRole.Owner,
            Team: MembershipRole.Member,
            Project: MembershipRole.Owner);

        Assert.True(roles.CanManageOrganization);
        Assert.False(roles.CanManageTeam);      // Org owner 不蕴含 Team owner
        Assert.True(roles.CanManageProject);
    }

    [Fact]
    public void OrgOwner不应自动成为Team或ProjectOwner()
    {
        var orgOwnerOnly = new WorkspaceRoles(MembershipRole.Owner, null, null);

        Assert.True(orgOwnerOnly.CanManageOrganization);
        Assert.False(orgOwnerOnly.CanManageTeam);
        Assert.False(orgOwnerOnly.CanManageProject);

        // 且不应被视为 Team / Project 的成员
        Assert.True(orgOwnerOnly.IsOrganizationMember);
        Assert.False(orgOwnerOnly.IsTeamMember);
        Assert.False(orgOwnerOnly.IsProjectMember);
    }

    [Fact]
    public void ProjectOwner不应获得Org或Team权限()
    {
        var projectOwnerOnly = new WorkspaceRoles(null, null, MembershipRole.Owner);

        Assert.False(projectOwnerOnly.CanManageOrganization);
        Assert.False(projectOwnerOnly.CanManageTeam);
        Assert.True(projectOwnerOnly.CanManageProject);
    }

    [Fact]
    public void 普通成员不应有管理权限()
    {
        var member = new WorkspaceRoles(MembershipRole.Member, MembershipRole.Member, MembershipRole.Member);

        Assert.True(member.IsOrganizationMember);
        Assert.True(member.IsTeamMember);
        Assert.True(member.IsProjectMember);

        Assert.False(member.CanManageOrganization);
        Assert.False(member.CanManageTeam);
        Assert.False(member.CanManageProject);
    }

    [Fact]
    public void 非成员不应通过任何层级校验()
    {
        WorkspaceRoles none = WorkspaceRoles.None;

        Assert.False(none.IsOrganizationMember);
        Assert.False(none.IsTeamMember);
        Assert.False(none.IsProjectMember);
        Assert.False(none.CanManageOrganization);
        Assert.False(none.CanManageTeam);
        Assert.False(none.CanManageProject);
    }

    [Theory]
    [InlineData(MembershipRole.Owner, "owner")]
    [InlineData(MembershipRole.Member, "member")]
    public void 角色应映射为DDL中的小写取值(MembershipRole role, string expected)
    {
        // E1 §3：CHECK (role IN ('owner','member')) —— 注意是小写，与 lifecycle 的 SCREAMING_SNAKE 不同
        Assert.Equal(expected, role.ToDbValue());
        Assert.True(MembershipRoleMap.TryParse(expected, out MembershipRole parsed));
        Assert.Equal(role, parsed);
    }

    [Theory]
    [InlineData("OWNER")]     // 大小写敏感
    [InlineData("Owner")]
    [InlineData("admin")]
    [InlineData("")]
    [InlineData(null)]
    public void 未知角色取值不应被解析(string? value)
    {
        Assert.False(MembershipRoleMap.TryParse(value, out _));
    }

    [Fact]
    public void 组织应至少保留一个owner()
    {
        Assert.True(OrganizationOwnership.HasAtLeastOneOwner([MembershipRole.Owner, MembershipRole.Member]));
        Assert.True(OrganizationOwnership.HasAtLeastOneOwner([MembershipRole.Owner]));
        Assert.False(OrganizationOwnership.HasAtLeastOneOwner([MembershipRole.Member]));
        Assert.False(OrganizationOwnership.HasAtLeastOneOwner([]));
    }

    [Fact]
    public void 移除最后一个owner应被识别为孤儿组织()
    {
        Guid solo = Guid.NewGuid();

        // 唯一 owner 被移除
        Assert.True(OrganizationOwnership.WouldOrphanOrganization([solo], solo, newRole: null));

        // 唯一 owner 被降级为 member
        Assert.True(OrganizationOwnership.WouldOrphanOrganization([solo], solo, MembershipRole.Member));
    }

    [Fact]
    public void 还有其他owner时不应被判为孤儿()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();

        Assert.False(OrganizationOwnership.WouldOrphanOrganization([a, b], a, newRole: null));
        Assert.False(OrganizationOwnership.WouldOrphanOrganization([a, b], a, MembershipRole.Member));
    }

    [Fact]
    public void 变更非owner成员不应影响owner计数()
    {
        Guid owner = Guid.NewGuid();
        Guid plainMember = Guid.NewGuid();

        Assert.False(OrganizationOwnership.WouldOrphanOrganization([owner], plainMember, newRole: null));
        Assert.False(OrganizationOwnership.WouldOrphanOrganization([owner], plainMember, MembershipRole.Member));
    }

    [Fact]
    public void 保持owner角色不应被判为孤儿()
    {
        Guid solo = Guid.NewGuid();

        Assert.False(OrganizationOwnership.WouldOrphanOrganization([solo], solo, MembershipRole.Owner));
    }
}
