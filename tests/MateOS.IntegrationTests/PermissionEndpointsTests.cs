using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MateOS.Api.Auth;
using MateOS.Api.Channels;
using MateOS.Api.Permissions;
using MateOS.Api.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MateOS.IntegrationTests;

/// <summary>
/// E6 §4 端到端：Permission CRUD + /check 内部 + 三层合并。
/// </summary>
/// <remarks>
/// V1 简化：user token 调 /check；agent_token 留 M4a+。
/// </remarks>
public sealed class PermissionEndpointsTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── E6-001 ─────────────────────────

    [Fact]
    public async Task E6_001_默认矩阵_USER_member读消息应ALLOW()
    {
        await fixture.ResetAsync();
        HttpClient alice = fixture.CreateClient();
        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken.AccessToken);

        // 准备：alice 是 project owner
        Guid projectId = await CreateProjectAsync(alice, "Demo");

        // 准备 channel
        Guid channelId = await CreateChannelAsync(alice, projectId, "general");

        // /check 自身（alice 是 owner；read_message 应 ALLOW）
        var req = new CheckPermissionRequest(
            SubjectType: "USER", SubjectId: aliceToken.User.Id,
            PermKey: "read_message", ChannelId: channelId);
        HttpResponseMessage resp = await alice.PostAsJsonAsync("/permissions/check", req);
        await EnsureSuccessAsync(resp);
        var result = (await resp.Content.ReadFromJsonAsync<CheckPermissionResponse>())!;

        Assert.Equal("ALLOW", result.Effect);
        Assert.Equal("default", result.Source);
    }

    // ───────────────────────── E6-002 ─────────────────────────

    [Fact]
    public async Task E6_002_project_override应覆盖默认矩阵()
    {
        await fixture.ResetAsync();
        HttpClient alice = fixture.CreateClient();
        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken.AccessToken);

        Guid projectId = await CreateProjectAsync(alice, "Demo");
        Guid channelId = await CreateChannelAsync(alice, projectId, "general");

        // 设 project override: alice 写 memory → DENY（默认是 REQUIRE_APPROVAL）
        var permReq = new CreatePermissionRequest(
            SubjectType: "USER", SubjectId: aliceToken.User.Id,
            PermKey: "write_memory", Effect: "DENY");
        HttpResponseMessage permResp = await alice.PostAsJsonAsync(
            $"/projects/{projectId}/permissions", permReq);
        await EnsureSuccessAsync(permResp);

        // /check write_memory 应 DENY（project override 压默认）
        var checkReq = new CheckPermissionRequest(
            SubjectType: "USER", SubjectId: aliceToken.User.Id,
            PermKey: "write_memory", ChannelId: channelId);
        HttpResponseMessage checkResp = await alice.PostAsJsonAsync("/permissions/check", checkReq);
        await EnsureSuccessAsync(checkResp);
        var result = (await checkResp.Content.ReadFromJsonAsync<CheckPermissionResponse>())!;

        Assert.Equal("DENY", result.Effect);
        Assert.Equal("project_override", result.Source);
    }

    // ───────────────────────── E6-003 channel 优先 ─────────────────────────

    [Fact]
    public async Task E6_003_channel_override应优先于project_override()
    {
        await fixture.ResetAsync();
        HttpClient alice = fixture.CreateClient();
        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken.AccessToken);

        Guid projectId = await CreateProjectAsync(alice, "Demo");
        Guid channelId = await CreateChannelAsync(alice, projectId, "general");

        // project: ALLOW write_message
        var projPerm = new CreatePermissionRequest(
            SubjectType: "USER", SubjectId: aliceToken.User.Id,
            PermKey: "write_message", Effect: "ALLOW");
        await EnsureSuccessAsync(await alice.PostAsJsonAsync(
            $"/projects/{projectId}/permissions", projPerm));

        // channel: DENY write_message
        var chanPerm = new CreatePermissionRequest(
            SubjectType: "USER", SubjectId: aliceToken.User.Id,
            PermKey: "write_message", Effect: "DENY");
        await EnsureSuccessAsync(await alice.PostAsJsonAsync(
            $"/channels/{channelId}/permissions", chanPerm));

        // /check 应 DENY（channel 优先）
        var checkReq = new CheckPermissionRequest(
            SubjectType: "USER", SubjectId: aliceToken.User.Id,
            PermKey: "write_message", ChannelId: channelId);
        HttpResponseMessage checkResp = await alice.PostAsJsonAsync("/permissions/check", checkReq);
        await EnsureSuccessAsync(checkResp);
        var result = (await checkResp.Content.ReadFromJsonAsync<CheckPermissionResponse>())!;

        Assert.Equal("DENY", result.Effect);
        Assert.Equal("channel_override", result.Source);
    }

    // ───────────────────────── E6-004 默认 REQUIRE_APPROVAL ─────────────────────────

    [Fact]
    public async Task E6_004_write_memory默认应REQUIRE_APPROVAL_对owner()
    {
        await fixture.ResetAsync();
        HttpClient alice = fixture.CreateClient();
        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken.AccessToken);

        Guid projectId = await CreateProjectAsync(alice, "Demo");
        Guid channelId = await CreateChannelAsync(alice, projectId, "general");

        var checkReq = new CheckPermissionRequest(
            SubjectType: "USER", SubjectId: aliceToken.User.Id,
            PermKey: "write_memory", ChannelId: channelId);
        HttpResponseMessage checkResp = await alice.PostAsJsonAsync("/permissions/check", checkReq);
        await EnsureSuccessAsync(checkResp);
        var result = (await checkResp.Content.ReadFromJsonAsync<CheckPermissionResponse>())!;

        // E6 §3.1：write_memory 默认 REQUIRE_APPROVAL（所有 role 都是）
        Assert.Equal("REQUIRE_APPROVAL", result.Effect);
    }

    // ───────────────────────── E6-005 非 owner 拒绝 CRUD ─────────────────────────

    [Fact]
    public async Task E6_005_非project_owner的POST_permissions应被拒()
    {
        await fixture.ResetAsync();
        HttpClient alice = fixture.CreateClient();
        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken.AccessToken);

        Guid projectId = await CreateProjectAsync(alice, "Demo");

        // bob 注册 + 加入项目（member）
        HttpClient bob = fixture.CreateClient();
        TokenResponse bobToken = await RegisterAsync(bob, "bob@example.com");

        HttpResponseMessage addBob = await alice.PostAsJsonAsync(
            $"/projects/{projectId}/members",
            new AddMemberRequest(bobToken.User.Email, "member"));
        await EnsureSuccessAsync(addBob);

        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bobToken.AccessToken);

        var req = new CreatePermissionRequest(
            SubjectType: "USER", SubjectId: aliceToken.User.Id,
            PermKey: "write_message", Effect: "DENY");
        HttpResponseMessage resp = await bob.PostAsJsonAsync(
            $"/projects/{projectId}/permissions", req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ───────────────────────── E6-006 删除 override 回到 default ─────────────────────────

    [Fact]
    public async Task E6_006_delete_project_override后应回退到默认矩阵()
    {
        await fixture.ResetAsync();
        HttpClient alice = fixture.CreateClient();
        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken.AccessToken);

        Guid projectId = await CreateProjectAsync(alice, "Demo");
        Guid channelId = await CreateChannelAsync(alice, projectId, "general");

        // 加 override: DENY execute_code
        var createReq = new CreatePermissionRequest(
            SubjectType: "USER", SubjectId: aliceToken.User.Id,
            PermKey: "execute_code", Effect: "DENY");
        HttpResponseMessage createResp = await alice.PostAsJsonAsync(
            $"/projects/{projectId}/permissions", createReq);
        await EnsureSuccessAsync(createResp);
        var created = (await createResp.Content.ReadFromJsonAsync<PermissionSummary>())!;

        // 查：应 DENY
        var checkReq = new CheckPermissionRequest(
            SubjectType: "USER", SubjectId: aliceToken.User.Id,
            PermKey: "execute_code", ChannelId: channelId);
        var r1 = (await (await alice.PostAsJsonAsync("/permissions/check", checkReq)).Content
            .ReadFromJsonAsync<CheckPermissionResponse>())!;
        Assert.Equal("DENY", r1.Effect);

        // 删除 override
        HttpResponseMessage delResp = await alice.DeleteAsync(
            $"/projects/{projectId}/permissions/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // 再查：默认 execute_code = DENY（E6 §3.1）
        var r2 = (await (await alice.PostAsJsonAsync("/permissions/check", checkReq)).Content
            .ReadFromJsonAsync<CheckPermissionResponse>())!;
        Assert.Equal("DENY", r2.Effect);
        Assert.Equal("default", r2.Source);
    }

    // ───────────────────────── helpers ─────────────────────────

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"HTTP {(int)response.StatusCode} ({response.StatusCode}) {response.RequestMessage?.RequestUri}：{Truncate(body, 2000)}");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + " …";

    private static async Task<TokenResponse> RegisterAsync(HttpClient client, string email)
    {
        HttpResponseMessage resp = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest(email, Password, email.Split('@')[0]));
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, string name)
    {
        HttpResponseMessage orgResp = await client.PostAsJsonAsync(
            "/organizations", new CreateOrganizationRequest($"{name}-org"));
        await EnsureSuccessAsync(orgResp);
        OrganizationSummary org = (await orgResp.Content.ReadFromJsonAsync<OrganizationSummary>())!;

        HttpResponseMessage teamResp = await client.PostAsJsonAsync(
            $"/organizations/{org.Id}/teams", new CreateTeamRequest(org.Id, $"{name}-team"));
        await EnsureSuccessAsync(teamResp);
        TeamSummary team = (await teamResp.Content.ReadFromJsonAsync<TeamSummary>())!;

        HttpResponseMessage projResp = await client.PostAsJsonAsync(
            $"/teams/{team.Id}/projects", new CreateProjectRequest(team.Id, name, null, null));
        await EnsureSuccessAsync(projResp);
        ProjectSummary proj = (await projResp.Content.ReadFromJsonAsync<ProjectSummary>())!;

        return proj.Id;
    }

    private static async Task<Guid> CreateChannelAsync(HttpClient client, Guid projectId, string name)
    {
        HttpResponseMessage resp = await client.PostAsJsonAsync(
            $"/projects/{projectId}/channels", new CreateChannelRequest(name, null));
        await EnsureSuccessAsync(resp);
        ChannelSummary channel = (await resp.Content.ReadFromJsonAsync<ChannelSummary>())!;
        return channel.Id;
    }
}
