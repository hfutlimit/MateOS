using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Channels;
using MateOS.Api.Memory;
using MateOS.Api.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MateOS.IntegrationTests;

/// <summary>
/// E5 §3 端到端：propose → approve → search 完整流程。
/// </summary>
public sealed class MemoryEndpointsTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── E5-001 ─────────────────────────

    [Fact]
    public async Task E5_001_PROJECT_memory_完整_propose_approve_search_流程()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "alice@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        // 准备：project + channel
        Guid projectId = await CreateProjectAsync(userClient, "Demo");
        Guid channelId = await CreateChannelAsync(userClient, projectId, "general");
        long sourceSeq = 1;
        Guid sourceMessageId = Guid.NewGuid();

        // propose PROJECT memory，源 = CHANNEL_MESSAGE
        var proposeReq = new ProposeMemoryRequest(
            Type: "PROJECT",
            Title: "项目密码学规范",
            Content: "本项目所有加密用 AES-256-GCM，nonce 12 字节，tag 16 字节。",
            SourceType: "CHANNEL_MESSAGE",
            SourceChannelId: channelId,
            SourceMessageSeq: sourceSeq,
            SourceMessageId: sourceMessageId,
            ProposedByAgentId: null,
            IdempotencyKey: "prop-1");
        HttpResponseMessage proposeResp = await userClient.PostJsonAsync("/memory/proposals", proposeReq);
        await EnsureSuccessAsync(proposeResp);
        var proposed = (await proposeResp.Content.ReadWireAsync<MemoryProposalSummary>())!;

        Assert.Equal("PROJECT", proposed.Type);
        Assert.Equal("PROPOSED", proposed.Status);
        Assert.Equal("CHANNEL_MESSAGE", proposed.SourceType);
        Assert.Equal(sourceSeq, proposed.SourceMessageSeq);

        // approve
        HttpResponseMessage approveResp = await userClient.PostJsonAsync(
            $"/memory/proposals/{proposed.Id}/approve", new ApproveMemoryRequest(null));
        await EnsureSuccessAsync(approveResp);
        var approved = (await approveResp.Content.ReadWireAsync<MemoryProposalSummary>())!;
        Assert.Equal("APPROVED", approved.Status);
        Assert.NotNull(approved.ApprovedAtMs);

        // search 应能命中
        HttpResponseMessage searchResp = await userClient.GetAsync(
            $"/memory/search?q={Uri.EscapeDataString("AES-256-GCM")}&project_id={projectId}");
        await EnsureSuccessAsync(searchResp);
        var hits = (await searchResp.Content.ReadWireAsync<List<MemoryItemSummary>>())!;
        Assert.Single(hits);
        Assert.Contains("AES-256-GCM", hits[0].Content);

        // list items 应包含这条
        HttpResponseMessage listResp = await userClient.GetAsync($"/memory/items?project_id={projectId}");
        await EnsureSuccessAsync(listResp);
        var items = (await listResp.Content.ReadWireAsync<List<MemoryItemSummary>>())!;
        Assert.Single(items);
    }

    // ───────────────────────── E5-002 ─────────────────────────

    [Fact]
    public async Task E5_002_PERSONAL_memory不需要project()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "bob@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        // PERSONAL memory（无 project_id，无 source_channel_id）
        var proposeReq = new ProposeMemoryRequest(
            Type: "PERSONAL",
            Title: "个人偏好",
            Content: "我习惯下午 3 点开始 work。",
            SourceType: "MANUAL",
            SourceChannelId: null,
            SourceMessageSeq: null,
            SourceMessageId: null,
            ProposedByAgentId: null,
            IdempotencyKey: "personal-1");
        HttpResponseMessage resp = await userClient.PostJsonAsync("/memory/proposals", proposeReq);
        await EnsureSuccessAsync(resp);
        var proposed = (await resp.Content.ReadWireAsync<MemoryProposalSummary>())!;

        Assert.Equal("PERSONAL", proposed.Type);
        Assert.Null(proposed.ProjectId);

        // approve（PERSONAL 申请人本人批准）
        HttpResponseMessage approveResp = await userClient.PostJsonAsync(
            $"/memory/proposals/{proposed.Id}/approve", new ApproveMemoryRequest(null));
        await EnsureSuccessAsync(approveResp);

        // search 应能命中
        HttpResponseMessage searchResp = await userClient.GetAsync(
            $"/memory/search?q={Uri.EscapeDataString("下午")}");
        await EnsureSuccessAsync(searchResp);
        var hits = (await searchResp.Content.ReadWireAsync<List<MemoryItemSummary>>())!;
        Assert.Single(hits);
    }

    // ───────────────────────── E5-003 ─────────────────────────

    [Fact]
    public async Task E5_003_reject路径应不写memory_items()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "carol@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        Guid projectId = await CreateProjectAsync(userClient, "Demo");
        Guid channelId = await CreateChannelAsync(userClient, projectId, "general");

        var proposeReq = new ProposeMemoryRequest(
            Type: "PROJECT",
            Title: "应被 reject 的 memory",
            Content: "这是个被拒绝的内容。",
            SourceType: "CHANNEL_MESSAGE",
            SourceChannelId: channelId,
            SourceMessageSeq: 1L,
            SourceMessageId: Guid.NewGuid(),
            ProposedByAgentId: null,
            IdempotencyKey: "reject-1");
        HttpResponseMessage proposeResp = await userClient.PostJsonAsync("/memory/proposals", proposeReq);
        await EnsureSuccessAsync(proposeResp);
        var proposed = (await proposeResp.Content.ReadWireAsync<MemoryProposalSummary>())!;

        // reject
        HttpResponseMessage rejectResp = await userClient.PostJsonAsync(
            $"/memory/proposals/{proposed.Id}/reject",
            new RejectMemoryRequest("内容不准"));
        await EnsureSuccessAsync(rejectResp);
        var rejected = (await rejectResp.Content.ReadWireAsync<MemoryProposalSummary>())!;
        Assert.Equal("REJECTED", rejected.Status);
        Assert.Equal("内容不准", rejected.RejectReason);

        // search 应无命中
        HttpResponseMessage searchResp = await userClient.GetAsync(
            $"/memory/search?q={Uri.EscapeDataString("被拒绝")}&project_id={projectId}");
        await EnsureSuccessAsync(searchResp);
        var hits = (await searchResp.Content.ReadWireAsync<List<MemoryItemSummary>>())!;
        Assert.Empty(hits);
    }

    // ───────────────────────── E5-004 ─────────────────────────

    [Fact]
    public async Task E5_004_非项目owner_reject应被403拒()
    {
        await fixture.ResetAsync();
        HttpClient alice = fixture.CreateClient();
        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken.AccessToken);

        Guid projectId = await CreateProjectAsync(alice, "Demo");
        Guid channelId = await CreateChannelAsync(alice, projectId, "general");

        // bob 注册 + 加入项目（member）
        HttpClient bob = fixture.CreateClient();
        TokenResponse bobToken = await RegisterAsync(bob, "bob@example.com");

        HttpResponseMessage addBob = await alice.PostJsonAsync(
            $"/projects/{projectId}/members",
            new AddMemberRequest(bobToken.User.Email, "member"));
        await EnsureSuccessAsync(addBob);

        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bobToken.AccessToken);

        // alice 申请
        var req = new ProposeMemoryRequest(
            Type: "PROJECT", Title: "test", Content: "x",
            SourceType: "CHANNEL_MESSAGE", SourceChannelId: channelId, SourceMessageSeq: 1L,
            SourceMessageId: Guid.NewGuid(), ProposedByAgentId: null, IdempotencyKey: "k1");
        HttpResponseMessage propResp = await alice.PostJsonAsync("/memory/proposals", req);
        await EnsureSuccessAsync(propResp);
        var prop = (await propResp.Content.ReadWireAsync<MemoryProposalSummary>())!;

        // bob 试图 approve → 403
        HttpResponseMessage approveResp = await bob.PostJsonAsync(
            $"/memory/proposals/{prop.Id}/approve", new ApproveMemoryRequest(null));
        Assert.Equal(HttpStatusCode.Forbidden, approveResp.StatusCode);
    }

    // ───────────────────────── E5-005 ─────────────────────────

    [Fact]
    public async Task E5_005_idempotency_key_重发应返同proposal()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "dave@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        Guid projectId = await CreateProjectAsync(userClient, "Demo");
        Guid channelId = await CreateChannelAsync(userClient, projectId, "general");

        var req = new ProposeMemoryRequest(
            Type: "PROJECT", Title: "t", Content: "c",
            SourceType: "CHANNEL_MESSAGE", SourceChannelId: channelId, SourceMessageSeq: 1L,
            SourceMessageId: Guid.NewGuid(), ProposedByAgentId: null, IdempotencyKey: "idem-2");

        HttpResponseMessage r1 = await userClient.PostJsonAsync("/memory/proposals", req);
        await EnsureSuccessAsync(r1);
        var p1 = (await r1.Content.ReadWireAsync<MemoryProposalSummary>())!;

        HttpResponseMessage r2 = await userClient.PostJsonAsync("/memory/proposals", req);
        await EnsureSuccessAsync(r2);
        var p2 = (await r2.Content.ReadWireAsync<MemoryProposalSummary>())!;

        Assert.Equal(p1.Id, p2.Id);
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
        HttpResponseMessage resp = await client.PostJsonAsync(
            "/auth/register", new RegisterRequest(email, Password, email.Split('@')[0]));
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadWireAsync<TokenResponse>())!;
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, string name)
    {
        HttpResponseMessage orgResp = await client.PostJsonAsync(
            "/orgs", new CreateOrganizationRequest($"{name}-org"));
        await EnsureSuccessAsync(orgResp);
        OrganizationSummary org = (await orgResp.Content.ReadWireAsync<OrganizationSummary>())!;

        HttpResponseMessage teamResp = await client.PostJsonAsync(
            "/teams", new CreateTeamRequest(org.Id, $"{name}-team"));
        await EnsureSuccessAsync(teamResp);
        TeamSummary team = (await teamResp.Content.ReadWireAsync<TeamSummary>())!;

        HttpResponseMessage projResp = await client.PostJsonAsync(
            "/projects", new CreateProjectRequest(team.Id, name, null, null));
        await EnsureSuccessAsync(projResp);
        ProjectSummary proj = (await projResp.Content.ReadWireAsync<ProjectSummary>())!;

        return proj.Id;
    }

    private static async Task<Guid> CreateChannelAsync(HttpClient client, Guid projectId, string name)
    {
        HttpResponseMessage resp = await client.PostJsonAsync(
            $"/projects/{projectId}/channels", new CreateChannelRequest(name, null));
        await EnsureSuccessAsync(resp);
        ChannelSummary channel = (await resp.Content.ReadWireAsync<ChannelSummary>())!;
        return channel.Id;
    }
}
