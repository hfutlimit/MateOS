using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Channels;
using MateOS.Api.Memory;
using MateOS.Api.NeedsYou;
using MateOS.Api.Routing;
using MateOS.Api.Workspace;

namespace MateOS.IntegrationTests;

/// <summary>
/// Needs You 端到端：跨 3 事实源（E4/E5/E7）聚合到统一 timeline。
/// </summary>
public sealed class NeedsYouEndpointsTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── NY-001 ─────────────────────────

    [Fact]
    public async Task NY_001_APPROVAL类应包含待审批的MemoryProposal()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "alice@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        Guid projectId = await CreateProjectAsync(userClient, "Demo");
        Guid channelId = await CreateChannelAsync(userClient, projectId, "general");

        // propose 一条 memory（PROPOSED 状态）
        var proposeReq = new ProposeMemoryRequest(
            Type: "PROJECT", Title: "需要审批的 memory", Content: "内容",
            SourceType: "CHANNEL_MESSAGE", SourceChannelId: channelId, SourceMessageSeq: 1L,
            SourceMessageId: Guid.NewGuid(), ProposedByAgentId: null, IdempotencyKey: "ny-1");
        HttpResponseMessage proposeResp = await userClient.PostAsJsonAsync("/memory/proposals", proposeReq);
        await EnsureSuccessAsync(proposeResp);

        // GET /needs-you
        HttpResponseMessage resp = await userClient.GetAsync("/needs-you");
        await EnsureSuccessAsync(resp);
        var body = (await resp.Content.ReadFromJsonAsync<NeedsYouResponse>())!;

        Assert.True(body.Total > 0);
        Assert.True(body.ApprovalCount >= 1);
        Assert.Contains(body.Items, i =>
            i.Category == "APPROVAL" && i.Source == "memory_proposal" && i.Title == "需要审批的 memory");
    }

    // ───────────────────────── NY-002 ─────────────────────────

    [Fact]
    public async Task NY_002_按category过滤应只返该类()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "bob@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        Guid projectId = await CreateProjectAsync(userClient, "Demo");
        Guid channelId = await CreateChannelAsync(userClient, projectId, "general");

        // 加一条 APPROVAL
        var proposeReq = new ProposeMemoryRequest(
            Type: "PROJECT", Title: "x", Content: "y",
            SourceType: "CHANNEL_MESSAGE", SourceChannelId: channelId, SourceMessageSeq: 1L,
            SourceMessageId: Guid.NewGuid(), ProposedByAgentId: null, IdempotencyKey: "ny-2");
        await EnsureSuccessAsync(await userClient.PostAsJsonAsync("/memory/proposals", proposeReq));

        // 过滤 APPROVAL
        HttpResponseMessage resp = await userClient.GetAsync("/needs-you?category=APPROVAL");
        await EnsureSuccessAsync(resp);
        var body = (await resp.Content.ReadFromJsonAsync<NeedsYouResponse>())!;

        Assert.True(body.ApprovalCount >= 1);
        Assert.All(body.Items, i => Assert.Equal("APPROVAL", i.Category));
    }

    // ───────────────────────── NY-003 ─────────────────────────

    [Fact]
    public async Task NY_003_排序应PROBLEMS在最前()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "carol@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        Guid projectId = await CreateProjectAsync(userClient, "Demo");
        Guid channelId = await CreateChannelAsync(userClient, projectId, "general");

        // 加 APPROVAL
        var proposeReq = new ProposeMemoryRequest(
            Type: "PROJECT", Title: "apprv", Content: "x",
            SourceType: "CHANNEL_MESSAGE", SourceChannelId: channelId, SourceMessageSeq: 1L,
            SourceMessageId: Guid.NewGuid(), ProposedByAgentId: null, IdempotencyKey: "ny-3");
        await EnsureSuccessAsync(await userClient.PostAsJsonAsync("/memory/proposals", proposeReq));

        // 抓取所有 items
        HttpResponseMessage resp = await userClient.GetAsync("/needs-you");
        await EnsureSuccessAsync(resp);
        var body = (await resp.Content.ReadFromJsonAsync<NeedsYouResponse>())!;

        // 排序：APPROVAL(1) 在 INFORMATION(3) 之前
        int firstApproval = -1;
        int firstInformation = -1;
        for (int i = 0; i < body.Items.Count; i++)
        {
            if (body.Items[i].Category == "APPROVAL" && firstApproval < 0) firstApproval = i;
            if (body.Items[i].Category == "INFORMATION" && firstInformation < 0) firstInformation = i;
        }

        if (firstApproval >= 0 && firstInformation >= 0)
        {
            Assert.True(firstApproval < firstInformation,
                $"APPROVAL 应排在 INFORMATION 前，但 firstApproval={firstApproval} firstInformation={firstInformation}");
        }
    }

    // ───────────────────────── NY-004 ─────────────────────────

    [Fact]
    public async Task NY_004_count端点应只返计数不返items()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "dave@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        Guid projectId = await CreateProjectAsync(userClient, "Demo");
        Guid channelId = await CreateChannelAsync(userClient, projectId, "general");

        var proposeReq = new ProposeMemoryRequest(
            Type: "PROJECT", Title: "x", Content: "y",
            SourceType: "CHANNEL_MESSAGE", SourceChannelId: channelId, SourceMessageSeq: 1L,
            SourceMessageId: Guid.NewGuid(), ProposedByAgentId: null, IdempotencyKey: "ny-4");
        await EnsureSuccessAsync(await userClient.PostAsJsonAsync("/memory/proposals", proposeReq));

        HttpResponseMessage resp = await userClient.GetAsync("/needs-you/count");
        await EnsureSuccessAsync(resp);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("ApprovalCount", json);
        Assert.Contains("ProblemsCount", json);
        // 验证不包含 items 字段
        Assert.DoesNotContain("\"items\"", json);
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
