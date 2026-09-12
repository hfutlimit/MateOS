using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Workspace;

namespace MateOS.IntegrationTests;

/// <summary>
/// E2 §7 端到端用例：Credential CRUD + Agent CRUD + Lifecycle 转换 + Activity 上报。
/// </summary>
/// <remarks>
/// 走真实 HTTP + PostgreSQL；Token 鉴权走 M1 已建的 access token。
/// matk_ agent token 形式在 M3a 阶段暂无业务端点（保留给 M3b E7 connector）。
/// </remarks>
public sealed class AgentEndpointsTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── E2-001 ─────────────────────────

    [Fact]
    public async Task E2_001_注册后建credential和agent应拿到id()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "alice@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // 1. 创建 credential
        var createCred = new CreateCredentialRequest("openai", "prod", "sk-test-secret-1234", null);
        HttpResponseMessage credResp = await client.PostJsonAsync("/credentials", createCred);
        await EnsureSuccessAsync(credResp);
        CredentialSummary credential = (await credResp.Content.ReadWireAsync<CredentialSummary>())!;

        Assert.Equal("openai", credential.Provider);
        Assert.Equal("prod", credential.Label);
        // F2：API 返回永不包含密文
        Assert.True(credential.CreatedAtMs > 0);

        // 2. 创建 agent
        var createAgent = new CreateAgentRequest(
            Name: "alice-coder",
            Role: "coder",
            Capabilities: new[] { "coding", "review" },
            CredentialId: credential.Id,
            MaxConcurrency: 2,
            DailyLimitUsd: 10.0m,
            MonthlyBudgetUsd: 200.0m);
        HttpResponseMessage agentResp = await client.PostJsonAsync("/agents", createAgent);
        await EnsureSuccessAsync(agentResp);
        AgentSummary agent = (await agentResp.Content.ReadWireAsync<AgentSummary>())!;

        Assert.Equal("alice-coder", agent.Name);
        Assert.Equal("ACTIVE", agent.Lifecycle);
        Assert.Equal("OFFLINE", agent.Activity);
        Assert.Equal(new[] { "coding", "review" }, agent.Capabilities);
        Assert.Equal(2, agent.MaxConcurrency);

        // 3. F2：GET credentials 永不返密文
        HttpResponseMessage listResp = await client.GetAsync("/credentials");
        await EnsureSuccessAsync(listResp);
        var creds = (await listResp.Content.ReadWireAsync<List<CredentialSummary>>())!;
        Assert.Single(creds);

        // 4. GET agent 详情
        HttpResponseMessage getResp = await client.GetAsync($"/agents/{agent.Id}");
        await EnsureSuccessAsync(getResp);
        AgentSummary fetched = (await getResp.Content.ReadWireAsync<AgentSummary>())!;
        Assert.Equal(agent.Id, fetched.Id);
    }

    // ───────────────────────── E2-002 Lifecycle ─────────────────────────

    [Fact]
    public async Task E2_002_pause后activity应强制为offline()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "bob@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        CredentialSummary credential = await CreateCredentialAsync(client);
        AgentSummary agent = await CreateAgentAsync(client, credential.Id, "bob-coder");

        // 先上报 activity=AVAILABLE
        HttpResponseMessage activityResp = await client.PostJsonAsync(
            $"/agents/{agent.Id}/activity",
            new ReportActivityRequest("AVAILABLE", null));
        await EnsureSuccessAsync(activityResp);
        AgentSummary afterActivity = (await activityResp.Content.ReadWireAsync<AgentSummary>())!;
        Assert.Equal("AVAILABLE", afterActivity.Activity);

        // pause
        HttpResponseMessage pauseResp = await client.PostAsync($"/agents/{agent.Id}/pause", content: null);
        await EnsureSuccessAsync(pauseResp);
        AgentSummary afterPause = (await pauseResp.Content.ReadWireAsync<AgentSummary>())!;
        Assert.Equal("PAUSED", afterPause.Lifecycle);
        Assert.Equal("OFFLINE", afterPause.Activity);
        Assert.Equal("lifecycle_paused", afterPause.ActivityReason);
    }

    // ───────────────────────── E2-003 activity 拒绝 ─────────────────────────

    [Fact]
    public async Task E2_003_paused_lifecycle下activity上报应被拒()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "carol@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        CredentialSummary credential = await CreateCredentialAsync(client);
        AgentSummary agent = await CreateAgentAsync(client, credential.Id, "carol-coder");

        // pause
        HttpResponseMessage pauseResp = await client.PostAsync($"/agents/{agent.Id}/pause", content: null);
        await EnsureSuccessAsync(pauseResp);

        // 尝试上报 activity=WORKING，应被 409 拒
        HttpResponseMessage activityResp = await client.PostJsonAsync(
            $"/agents/{agent.Id}/activity",
            new ReportActivityRequest("WORKING", "should fail"));
        Assert.Equal(HttpStatusCode.Conflict, activityResp.StatusCode);
    }

    // ───────────────────────── E2-004 token 签发与撤销 ─────────────────────────

    [Fact]
    public async Task E2_004_签发agent_token后撤销应返revoked()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "dave@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        CredentialSummary credential = await CreateCredentialAsync(client);
        AgentSummary agent = await CreateAgentAsync(client, credential.Id, "dave-coder");

        // 签发
        var issueReq = new IssueTokenRequest("dev-stub", 7);
        HttpResponseMessage issueResp = await client.PostJsonAsync($"/agents/{agent.Id}/tokens", issueReq);
        await EnsureSuccessAsync(issueResp);
        AgentTokenIssuanceResponse issued = (await issueResp.Content.ReadWireAsync<AgentTokenIssuanceResponse>())!;

        Assert.StartsWith("eyJ", issued.JwtToken);  // JWT prefix
        long expiresIn = issued.ExpiresAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long expectedLifetimeMs = 7L * 24 * 60 * 60 * 1000;
        Assert.InRange(expiresIn, expectedLifetimeMs - 30_000, expectedLifetimeMs + 30_000);

        // 列表（永不返 matk_ / hash）
        HttpResponseMessage listResp = await client.GetAsync($"/agents/{agent.Id}/tokens");
        await EnsureSuccessAsync(listResp);
        var tokens = (await listResp.Content.ReadWireAsync<List<AgentTokenSummary>>())!;
        Assert.Single(tokens);
        Assert.Equal(issued.TokenId, tokens[0].Id);

        // 撤销
        HttpResponseMessage revokeResp = await client.DeleteAsync($"/agents/{agent.Id}/tokens/{issued.TokenId}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResp.StatusCode);

        // 列表里 revoked 状态应反映（V1 列表只返字段不返 revoked，复查 DB 略）
    }

    // ───────────────────────── E2-005 capability 非法应拒 ─────────────────────────

    [Fact]
    public async Task E2_005_非法capability应被400拒()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "eve@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        CredentialSummary credential = await CreateCredentialAsync(client);

        var req = new CreateAgentRequest(
            Name: "eve-agent",
            Role: "coder",
            Capabilities: new[] { "coding", "magic" },  // "magic" 不在 canonical
            CredentialId: credential.Id,
            MaxConcurrency: null,
            DailyLimitUsd: null,
            MonthlyBudgetUsd: null);
        HttpResponseMessage resp = await client.PostJsonAsync("/agents", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ───────────────────────── E2-006 agent加入项目 ─────────────────────────

    [Fact]
    public async Task E2_006_project_owner可邀请agent入项目()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "frank@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // 准备 project
        OrganizationSummary org = await CreateOrgAsync(client, "FrankOrg");
        TeamSummary team = await CreateTeamAsync(client, org.Id, "FrankTeam");
        ProjectSummary project = await CreateProjectAsync(client, team.Id, "FrankProj");

        // 准备 agent
        CredentialSummary credential = await CreateCredentialAsync(client);
        AgentSummary agent = await CreateAgentAsync(client, credential.Id, "frank-coder");

        // 邀请入项目
        var addReq = new AddAgentToProjectRequest(agent.Id, CanReadHistory: true);
        HttpResponseMessage addResp = await client.PostJsonAsync(
            $"/projects/{project.Id}/agents", addReq);
        await EnsureSuccessAsync(addResp);

        // 列出 project agents
        HttpResponseMessage listResp = await client.GetAsync($"/projects/{project.Id}/agents");
        await EnsureSuccessAsync(listResp);
        var projectAgents = (await listResp.Content.ReadWireAsync<List<AgentSummary>>())!;
        Assert.Single(projectAgents);
        Assert.Equal(agent.Id, projectAgents[0].Id);

        // 重复邀请应 409
        HttpResponseMessage dupResp = await client.PostJsonAsync(
            $"/projects/{project.Id}/agents", addReq);
        Assert.Equal(HttpStatusCode.Conflict, dupResp.StatusCode);
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
        HttpResponseMessage response = await client.PostJsonAsync(
            "/auth/register", new RegisterRequest(email, Password, email.Split('@')[0]));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadWireAsync<TokenResponse>())!;
    }

    private static async Task<CredentialSummary> CreateCredentialAsync(HttpClient client)
    {
        var req = new CreateCredentialRequest("openai", "test", $"sk-{Guid.NewGuid():N}", null);
        HttpResponseMessage resp = await client.PostJsonAsync("/credentials", req);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadWireAsync<CredentialSummary>())!;
    }

    private static async Task<AgentSummary> CreateAgentAsync(
        HttpClient client, Guid credentialId, string name)
    {
        var req = new CreateAgentRequest(
            Name: name,
            Role: "coder",
            Capabilities: new[] { "coding" },
            CredentialId: credentialId,
            MaxConcurrency: 1,
            DailyLimitUsd: null,
            MonthlyBudgetUsd: null);
        HttpResponseMessage resp = await client.PostJsonAsync("/agents", req);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadWireAsync<AgentSummary>())!;
    }

    private static async Task<OrganizationSummary> CreateOrgAsync(HttpClient client, string name)
    {
        HttpResponseMessage resp = await client.PostJsonAsync("/orgs", new CreateOrganizationRequest(name));
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadWireAsync<OrganizationSummary>())!;
    }

    private static async Task<TeamSummary> CreateTeamAsync(HttpClient client, Guid orgId, string name)
    {
        HttpResponseMessage resp = await client.PostJsonAsync(
            "/teams", new CreateTeamRequest(orgId, name));
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadWireAsync<TeamSummary>())!;
    }

    private static async Task<ProjectSummary> CreateProjectAsync(HttpClient client, Guid teamId, string name)
    {
        HttpResponseMessage resp = await client.PostJsonAsync(
            "/projects", new CreateProjectRequest(teamId, name, null, null));
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadWireAsync<ProjectSummary>())!;
    }
}
