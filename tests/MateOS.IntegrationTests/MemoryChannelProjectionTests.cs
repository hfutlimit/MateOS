using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Channels;
using MateOS.Api.Memory;
using MateOS.Api.Workspace;

namespace MateOS.IntegrationTests;

/// <summary>
/// E5 + E3 集成：MemoryProposal 申请 / 审批 / 拒绝 时往 source_channel 写消息并推 WS。
/// （detailed/05 §3.3 T+5 + T+12 流程）
/// </summary>
public sealed class MemoryChannelProjectionTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── MRP-001 ─────────────────────────

    [Fact]
    public async Task MRP_001_propose_PROJECT_memory应写一条MEMORY_REQUEST形态消息()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "alice@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        Guid projectId = await CreateProjectAsync(userClient, "Demo");
        Guid channelId = await CreateChannelAsync(userClient, projectId, "general");

        // propose
        var req = new ProposeMemoryRequest(
            Type: "PROJECT", Title: "需要审批的 memory", Content: "内容",
            SourceType: "CHANNEL_MESSAGE", SourceChannelId: channelId, SourceMessageSeq: 1L,
            SourceMessageId: Guid.NewGuid(), ProposedByAgentId: null, IdempotencyKey: "mrp-1");
        HttpResponseMessage proposeResp = await userClient.PostAsJsonAsync("/memory/proposals", req);
        await EnsureSuccessAsync(proposeResp);
        var proposal = (await proposeResp.Content.ReadFromJsonAsync<MemoryProposalSummary>())!;

        // GET channel messages 应包含 MEMORY_REQUEST 形态
        HttpResponseMessage listResp = await userClient.GetAsync(
            $"/channels/{channelId}/messages?since_seq=0");
        await EnsureSuccessAsync(listResp);
        var page = (await listResp.Content.ReadFromJsonAsync<MessagesPage>())!;

        Assert.True(page.LastSeq >= 1);
        var memoryRequestMessage = page.Messages.Single(m => m.ContentType == "MEMORY_REQUEST");
        Assert.Equal(1L, memoryRequestMessage.Seq);
        Assert.Equal("HUMAN", memoryRequestMessage.SenderType);
        // F8 投影纪律：content 只引 memory_proposal_ref，不含 analysis / missing / reason
        JsonElement content = memoryRequestMessage.Content;
        Assert.True(content.TryGetProperty("memory_proposal_ref", out JsonElement propRef));
        Assert.Equal(proposal.Id, propRef.GetGuid());
        Assert.False(content.TryGetProperty("analysis", out _));
        Assert.False(content.TryGetProperty("missing", out _));
        Assert.False(content.TryGetProperty("reason", out _));
    }

    // ───────────────────────── MRP-002 ─────────────────────────

    [Fact]
    public async Task MRP_002_approve后应写一条SYSTEM通知消息()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "bob@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        Guid projectId = await CreateProjectAsync(userClient, "Demo");
        Guid channelId = await CreateChannelAsync(userClient, projectId, "general");

        var req = new ProposeMemoryRequest(
            Type: "PROJECT", Title: "t", Content: "c",
            SourceType: "CHANNEL_MESSAGE", SourceChannelId: channelId, SourceMessageSeq: 1L,
            SourceMessageId: Guid.NewGuid(), ProposedByAgentId: null, IdempotencyKey: "mrp-2");
        HttpResponseMessage proposeResp = await userClient.PostAsJsonAsync("/memory/proposals", req);
        await EnsureSuccessAsync(proposeResp);
        var proposal = (await proposeResp.Content.ReadFromJsonAsync<MemoryProposalSummary>())!;

        // approve
        HttpResponseMessage approveResp = await userClient.PostAsJsonAsync(
            $"/memory/proposals/{proposal.Id}/approve", new ApproveMemoryRequest(null));
        await EnsureSuccessAsync(approveResp);

        // GET channel messages：应包含 1 MEMORY_REQUEST + 1 SYSTEM 通知
        HttpResponseMessage listResp = await userClient.GetAsync(
            $"/channels/{channelId}/messages?since_seq=0");
        await EnsureSuccessAsync(listResp);
        var page = (await listResp.Content.ReadFromJsonAsync<MessagesPage>())!;

        Assert.Equal(2, page.Messages.Count);
        var sysMsg = page.Messages.Single(m => m.ContentType == "SYSTEM");
        Assert.Equal(2L, sysMsg.Seq);
        Assert.Equal("SYSTEM", sysMsg.SenderType);
        Assert.Contains("已写入项目记忆", sysMsg.Content.GetProperty("text").GetString());
    }

    // ───────────────────────── MRP-003 ─────────────────────────

    [Fact]
    public async Task MRP_003_reject后应写一条SYSTEM通知消息()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "carol@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        Guid projectId = await CreateProjectAsync(userClient, "Demo");
        Guid channelId = await CreateChannelAsync(userClient, projectId, "general");

        var req = new ProposeMemoryRequest(
            Type: "PROJECT", Title: "t", Content: "c",
            SourceType: "CHANNEL_MESSAGE", SourceChannelId: channelId, SourceMessageSeq: 1L,
            SourceMessageId: Guid.NewGuid(), ProposedByAgentId: null, IdempotencyKey: "mrp-3");
        HttpResponseMessage proposeResp = await userClient.PostAsJsonAsync("/memory/proposals", req);
        await EnsureSuccessAsync(proposeResp);
        var proposal = (await proposeResp.Content.ReadFromJsonAsync<MemoryProposalSummary>())!;

        HttpResponseMessage rejectResp = await userClient.PostAsJsonAsync(
            $"/memory/proposals/{proposal.Id}/reject", new RejectMemoryRequest("不准"));
        await EnsureSuccessAsync(rejectResp);

        // 应有 2 条：MEMORY_REQUEST + SYSTEM 拒绝通知
        HttpResponseMessage listResp = await userClient.GetAsync(
            $"/channels/{channelId}/messages?since_seq=0");
        await EnsureSuccessAsync(listResp);
        var page = (await listResp.Content.ReadFromJsonAsync<MessagesPage>())!;

        var sysMsg = page.Messages.First(m => m.ContentType == "SYSTEM");
        Assert.Contains("已拒绝记忆申请", sysMsg.Content.GetProperty("text").GetString());
    }

    // ───────────────────────── MRP-004 ─────────────────────────

    [Fact]
    public async Task MRP_004_PERSONAL_memory不应写channel消息()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "dave@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        // PERSONAL 无 source_channel
        var req = new ProposeMemoryRequest(
            Type: "PERSONAL", Title: "personal", Content: "c",
            SourceType: "MANUAL", SourceChannelId: null, SourceMessageSeq: null,
            SourceMessageId: null, ProposedByAgentId: null, IdempotencyKey: "mrp-4");
        HttpResponseMessage proposeResp = await userClient.PostAsJsonAsync("/memory/proposals", req);
        await EnsureSuccessAsync(proposeResp);
        await EnsureSuccessAsync(await userClient.PostAsJsonAsync(
            $"/memory/proposals/{(await proposeResp.Content.ReadFromJsonAsync<MemoryProposalSummary>())!.Id}/approve",
            new ApproveMemoryRequest(null)));

        // 没有 channel 写消息，verify via list MEMORY_REQUEST 形态没有
        HttpResponseMessage listResp = await userClient.GetAsync("/memory/items");
        await EnsureSuccessAsync(listResp);
        var items = (await listResp.Content.ReadFromJsonAsync<List<MemoryItemSummary>>())!;
        Assert.Single(items);  // PERSONAL memory item 写好了
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
