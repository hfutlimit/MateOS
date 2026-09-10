using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MateOS.Api.Auth;
using MateOS.Api.Persistence;
using MateOS.Api.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MateOS.IntegrationTests;

/// <summary>
/// E1 §7.2 的端到端用例，走真实的 HTTP + PostgreSQL + Redis。
/// </summary>
/// <remarks>
/// 同一测试类内的用例由 xUnit 串行执行，因此可以在每个用例开头安全地 TRUNCATE。
/// </remarks>
public sealed class IdentityEndpointsTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── E1-001 ─────────────────────────

    [Fact]
    public async Task E1_001_注册后应拿到双令牌并能访问me()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();

        TokenResponse token = await RegisterAsync(client, "alice@example.com");

        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(token.RefreshToken));
        Assert.Equal("alice@example.com", token.User.Email);

        // E1 §2.1：access 15min / refresh 30d
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.InRange((token.AccessExpiresAtMs - nowMs) / 60_000.0, 13, 15);
        Assert.InRange((token.RefreshExpiresAtMs - nowMs) / 86_400_000.0, 29, 30);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        UserSummary? me = await client.GetFromJsonAsync<UserSummary>("/me");

        Assert.NotNull(me);
        Assert.Equal(token.User.Id, me.Id);
        Assert.Equal("alice@example.com", me.Email);
    }

    [Fact]
    public async Task E1_001b_重复邮箱注册应被拒绝()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();

        await RegisterAsync(client, "dup@example.com");

        HttpResponseMessage second = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest("DUP@example.com", Password, "dup"));

        // 邮箱是 citext，大小写不同也视为同一个账号（E1 F8）
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task E1_001c_refresh令牌不得当作access使用()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();

        TokenResponse token = await RegisterAsync(client, "typemix@example.com");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.RefreshToken);
        HttpResponseMessage response = await client.GetAsync("/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ───────────────────────── E1-003 ─────────────────────────

    [Fact]
    public async Task E1_003_refresh轮转后旧令牌应立即失效()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();

        TokenResponse first = await RegisterAsync(client, "rotate@example.com");

        TokenResponse rotated = await RotateAsync(client, first.RefreshToken);

        Assert.NotEqual(first.RefreshToken, rotated.RefreshToken);

        // 旧 refresh 已被消费（E1 F2）
        HttpResponseMessage reuse = await client.PostAsJsonAsync(
            "/auth/refresh", new RefreshRequest(first.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        // 新 refresh 仍然可用
        TokenResponse second = await RotateAsync(client, rotated.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(second.AccessToken));

        // access token 换了
        Assert.NotEqual(rotated.AccessToken, second.AccessToken);
    }

    // ───────────────────────── E1-004 ─────────────────────────

    [Fact]
    public async Task E1_004_三层owner应正交()
    {
        await fixture.ResetAsync();

        HttpClient alice = fixture.CreateClient();
        HttpClient bob = fixture.CreateClient();

        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        TokenResponse bobToken = await RegisterAsync(bob, "bob@example.com");

        Authorize(alice, aliceToken);
        Authorize(bob, bobToken);

        // alice 建 Org → 她是 Org owner
        OrganizationSummary org = await CreateOrganizationAsync(alice, "Acme");

        // bob 加入 Org 但只是 member
        HttpResponseMessage addBob = await alice.PostAsJsonAsync(
            $"/orgs/{org.Id}/members", new AddMemberRequest("bob@example.com", "member"));
        Assert.Equal(HttpStatusCode.NoContent, addBob.StatusCode);

        // bob 建 Team → 他是 Team owner；他不因为「Org member」而获得 Team 权限
        TeamSummary team = await CreateTeamAsync(bob, org.Id, "Backend");

        // alice 是 Org owner，但不是 Team 成员 —— Team 详情应拒绝她
        HttpResponseMessage aliceReadsTeam = await alice.GetAsync($"/teams/{team.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, aliceReadsTeam.StatusCode);

        // bob 也不是 Org owner —— 他不能改 Org
        HttpResponseMessage bobUpdatesOrg = await bob.PatchAsJsonAsync(
            $"/orgs/{org.Id}", new UpdateOrganizationRequest("Renamed"));
        Assert.Equal(HttpStatusCode.Forbidden, bobUpdatesOrg.StatusCode);
    }

    // ───────────────────────── E1-005 ─────────────────────────

    [Fact]
    public async Task E1_005_登录连续失败应在第六次被限流()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();

        await RegisterAsync(client, "limited@example.com");

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(
                "/auth/login", new LoginRequest("limited@example.com", "wrong-password"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        HttpResponseMessage sixth = await client.PostAsJsonAsync(
            "/auth/login", new LoginRequest("limited@example.com", "wrong-password"));

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.NotNull(sixth.Headers.RetryAfter);
    }

    // ───────────────────────── E1-006 ─────────────────────────

    [Fact]
    public async Task E1_006_组织owner应能管理成员且不得移除唯一owner()
    {
        await fixture.ResetAsync();

        HttpClient alice = fixture.CreateClient();
        HttpClient bob = fixture.CreateClient();

        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        TokenResponse bobToken = await RegisterAsync(bob, "bob@example.com");

        Authorize(alice, aliceToken);
        Authorize(bob, bobToken);

        OrganizationSummary org = await CreateOrganizationAsync(alice, "Acme");

        HttpResponseMessage added = await alice.PostAsJsonAsync(
            $"/orgs/{org.Id}/members", new AddMemberRequest("bob@example.com", "member"));
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);

        List<MemberSummary>? members = await alice.GetFromJsonAsync<List<MemberSummary>>($"/orgs/{org.Id}/members");
        Assert.NotNull(members);
        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m.Email == "alice@example.com" && m.Role == "owner");
        Assert.Contains(members, m => m.Email == "bob@example.com" && m.Role == "member");

        // 唯一的 owner 不能被移除（E1 §3.1）
        HttpResponseMessage orphan = await alice.DeleteAsync($"/orgs/{org.Id}/members/{aliceToken.User.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, orphan.StatusCode);

        // bob 不是 owner，不能管理成员
        HttpResponseMessage bobRemoves = await bob.DeleteAsync($"/orgs/{org.Id}/members/{aliceToken.User.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, bobRemoves.StatusCode);

        // alice 可以移除 bob
        HttpResponseMessage removed = await alice.DeleteAsync($"/orgs/{org.Id}/members/{bobToken.User.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    // ───────────────────── 组织 / 团队 / 项目链路 ─────────────────────

    [Fact]
    public async Task E1_002_组织团队项目应能串起来()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();

        TokenResponse token = await RegisterAsync(client, "po@example.com");
        Authorize(client, token);

        OrganizationSummary org = await CreateOrganizationAsync(client, "Acme");
        TeamSummary team = await CreateTeamAsync(client, org.Id, "Platform");
        ProjectSummary project = await CreateProjectAsync(client, team.Id, "MateOS");

        Assert.Equal("MateOS", project.Name);
        Assert.Null(project.RepoUrl);

        ProjectSummary? fetched = await client.GetFromJsonAsync<ProjectSummary>($"/projects/{project.Id}");
        Assert.NotNull(fetched);
        Assert.Equal(project.Id, fetched.Id);
        Assert.Equal("owner", fetched.Role);
    }

    [Fact]
    public async Task E1_002b_写操作应留下审计()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();

        TokenResponse token = await RegisterAsync(client, "audited@example.com");
        Authorize(client, token);

        OrganizationSummary org = await CreateOrganizationAsync(client, "Audited Org");

        using IServiceScope scope = fixture.Services.CreateScope();
        MateOSDbContext db = scope.ServiceProvider.GetRequiredService<MateOSDbContext>();

        List<AuditLog> logs = await db.AuditLogs.AsNoTracking().ToListAsync();

        Assert.Contains(logs, l => l.Action == "USER_REGISTERED");
        Assert.Contains(logs, l => l.Action == "ORGANIZATION_CREATED" && l.TargetId == org.Id);

        // I10：每条审计都要能追到本次请求
        Assert.All(logs, l => Assert.False(string.IsNullOrWhiteSpace(l.TraceId)));
    }

    // ──────────────────────────── 辅助 ────────────────────────────

    private static void Authorize(HttpClient client, TokenResponse token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

    /// <summary>
    /// 断言成功，失败时把响应体带进异常。
    /// 直接用 EnsureSuccessStatusCode 会把服务端 500 的真实原因丢掉，只留一个状态码，无法定位。
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync();

        throw new InvalidOperationException(
            $"HTTP {(int)response.StatusCode} ({response.StatusCode}) {response.RequestMessage?.RequestUri}：{Truncate(body, 3000)}");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + " …";

    private async Task<TokenResponse> RegisterAsync(HttpClient client, string email)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest(email, Password, email.Split('@')[0]));

        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task<TokenResponse> RotateAsync(HttpClient client, string refreshToken)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/refresh", new RefreshRequest(refreshToken));

        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task<OrganizationSummary> CreateOrganizationAsync(HttpClient client, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/orgs", new CreateOrganizationRequest(name));

        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<OrganizationSummary>())!;
    }

    private static async Task<TeamSummary> CreateTeamAsync(HttpClient client, Guid orgId, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/teams", new CreateTeamRequest(orgId, name));

        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<TeamSummary>())!;
    }

    private static async Task<ProjectSummary> CreateProjectAsync(HttpClient client, Guid teamId, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/projects", new CreateProjectRequest(teamId, name, Description: null, RepoUrl: null));

        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<ProjectSummary>())!;
    }
}
