using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Channels;
using MateOS.Api.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MateOS.IntegrationTests;

/// <summary>
/// E3 §4 的端到端用例，覆盖 channel CRUD + 消息流 + 客户端幂等 + 附件 presign。
/// </summary>
/// <remarks>
/// <para>
/// 走真实 HTTP + PostgreSQL（无 WS 阶段：M2-HTTP 单测不发 message.created 广播，
/// 集成测试只验证 HTTP 端到端 + 事务内 seq 分配）。
/// </para>
/// <para>
/// 用例之间通过 <c>fixture.ResetAsync()</c> 隔离（已含 E3 表 TRUNCATE）。
/// </para>
/// </remarks>
public sealed class ChannelEndpointsTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── E3-001 ─────────────────────────

    [Fact]
    public async Task E3_001_注册后建channel发消息应能拉回()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "alice@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // 准备一个 project（org → team → project）
        OrganizationSummary org = await CreateOrganizationAsync(client, "Acme");
        TeamSummary team = await CreateTeamAsync(client, org.Id, "Platform");
        ProjectSummary project = await CreateProjectAsync(client, team.Id, "Demo");

        // 创建 channel
        var createBody = new CreateChannelRequest("general", "默认频道");
        HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            $"/projects/{project.Id}/channels", createBody);
        await EnsureSuccessAsync(createResponse);
        ChannelSummary channel = (await createResponse.Content.ReadFromJsonAsync<ChannelSummary>())!;

        Assert.Equal("general", channel.Name);
        Assert.Equal(0, channel.LastSeq);
        Assert.Equal("owner", channel.Role);

        // 发 HUMAN 消息
        var postBody = new PostMessageRequest(
            ContentType: "HUMAN",
            Content: JsonDocument.Parse("""{"text":"hello world"}""").RootElement,
            ClientMsgId: null,
            ParentSeq: null);
        HttpResponseMessage postResponse = await client.PostAsJsonAsync(
            $"/channels/{channel.Id}/messages", postBody);
        await EnsureSuccessAsync(postResponse);
        MessageSummary first = (await postResponse.Content.ReadFromJsonAsync<MessageSummary>())!;

        Assert.Equal(1L, first.Seq);
        Assert.Equal("HUMAN", first.ContentType);
        Assert.Equal("HUMAN", first.SenderType);
        Assert.Equal(token.User.Id, first.SenderId);

        // 拉 since_seq=0 应拿回这条
        HttpResponseMessage listResponse = await client.GetAsync(
            $"/channels/{channel.Id}/messages?since_seq=0");
        await EnsureSuccessAsync(listResponse);
        MessagesPage page = (await listResponse.Content.ReadFromJsonAsync<MessagesPage>())!;

        Assert.Equal(1L, page.LastSeq);
        Assert.Single(page.Messages);
        Assert.Equal(first.Seq, page.Messages[0].Seq);
        Assert.Equal("hello world", page.Messages[0].Content.GetProperty("text").GetString());
    }

    // ───────────────────────── E3-002 客户端幂等 ─────────────────────────

    [Fact]
    public async Task E3_002_同clientMsgId重发应返已存在行不分配新seq()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "bob@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        ProjectSummary project = await CreateProjectForAsync(client, token, "Demo");
        ChannelSummary channel = await CreateChannelForAsync(client, project.Id, "general");

        Guid clientMsgId = Guid.NewGuid();
        var postBody = new PostMessageRequest(
            ContentType: "HUMAN",
            Content: JsonDocument.Parse("""{"text":"dup test"}""").RootElement,
            ClientMsgId: clientMsgId,
            ParentSeq: null);

        HttpResponseMessage first = await client.PostAsJsonAsync(
            $"/channels/{channel.Id}/messages", postBody);
        await EnsureSuccessAsync(first);
        MessageSummary msg1 = (await first.Content.ReadFromJsonAsync<MessageSummary>())!;

        // 同 client_msg_id 重发
        HttpResponseMessage second = await client.PostAsJsonAsync(
            $"/channels/{channel.Id}/messages", postBody);
        await EnsureSuccessAsync(second);
        MessageSummary msg2 = (await second.Content.ReadFromJsonAsync<MessageSummary>())!;

        // 应返同一条（seq + id 一致），不分配新 seq
        Assert.Equal(msg1.Seq, msg2.Seq);
        Assert.Equal(msg1.Id, msg2.Id);

        // 拉取只应有 1 条
        MessagesPage page = await GetMessagesAsync(client, channel.Id, sinceSeq: 0);
        Assert.Equal(1L, page.LastSeq);
        Assert.Single(page.Messages);
    }

    // ───────────────────────── E3-003 since_seq 续传 ─────────────────────────

    [Fact]
    public async Task E3_003_多条消息sinceSeq续传应只返后续()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "carol@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        ProjectSummary project = await CreateProjectForAsync(client, token, "Demo");
        ChannelSummary channel = await CreateChannelForAsync(client, project.Id, "general");

        for (int i = 0; i < 3; i++)
        {
            await PostHumanMessageAsync(client, channel.Id, $"msg {i}");
        }

        // since_seq=1 → 应返 seq=2, 3
        MessagesPage page = await GetMessagesAsync(client, channel.Id, sinceSeq: 1);
        Assert.Equal(3L, page.LastSeq);
        Assert.Equal(2, page.Messages.Count);
        Assert.Equal(2L, page.Messages[0].Seq);
        Assert.Equal(3L, page.Messages[1].Seq);

        // since_seq=3 → 返空
        MessagesPage empty = await GetMessagesAsync(client, channel.Id, sinceSeq: 3);
        Assert.Empty(empty.Messages);
    }

    // ───────────────────────── E3-004 非成员 403 ─────────────────────────

    [Fact]
    public async Task E3_004_非项目成员访问应被拒()
    {
        await fixture.ResetAsync();
        HttpClient alice = fixture.CreateClient();
        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken.AccessToken);

        ProjectSummary project = await CreateProjectForAsync(alice, aliceToken, "Alice");
        ChannelSummary channel = await CreateChannelForAsync(alice, project.Id, "private");

        // Bob 注册
        HttpClient bob = fixture.CreateClient();
        TokenResponse bobToken = await RegisterAsync(bob, "bob@example.com");
        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bobToken.AccessToken);

        // Bob 是 project 成员才能访问；非 project 成员直接 403
        // 这里 Bob 完全不是 project 成员
        HttpResponseMessage r = await bob.GetAsync($"/channels/{channel.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ───────────────────────── E3-005 message 删除（owner only）────────

    [Fact]
    public async Task E3_005_非owner删除消息应被拒()
    {
        await fixture.ResetAsync();
        HttpClient alice = fixture.CreateClient();
        TokenResponse aliceToken = await RegisterAsync(alice, "alice@example.com");
        alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aliceToken.AccessToken);

        ProjectSummary project = await CreateProjectForAsync(alice, aliceToken, "Demo");
        ChannelSummary channel = await CreateChannelForAsync(alice, project.Id, "general");

        MessageSummary posted = await PostHumanMessageAsync(alice, channel.Id, "to delete");

        // Bob 注册 + 加入 project + 尝试删
        HttpClient bob = fixture.CreateClient();
        TokenResponse bobToken = await RegisterAsync(bob, "bob@example.com");
        bob.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bobToken.AccessToken);

        // Bob 加入 project（不是 owner）
        HttpResponseMessage addBob = await alice.PostAsJsonAsync(
            $"/projects/{project.Id}/members",
            new AddMemberRequest(bobToken.User.Email, "member"));
        await EnsureSuccessAsync(addBob);

        HttpResponseMessage r = await bob.DeleteAsync($"/channels/{channel.Id}/messages/{posted.Seq}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ───────────────────────── E3-006 attachment presign ─────────────────────────

    [Fact]
    public async Task E3_006_attachment_presign后confirm应拿到uploaded状态()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "dan@example.com");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var presignBody = new PresignAttachmentRequest("plan.md", "text/markdown", 1024);
        HttpResponseMessage presign = await client.PostAsJsonAsync("/attachments/presign", presignBody);
        await EnsureSuccessAsync(presign);
        PresignAttachmentResponse presignResp = (await presign.Content.ReadFromJsonAsync<PresignAttachmentResponse>())!;

        Assert.NotEqual(Guid.Empty, presignResp.AttachmentId);
        Assert.StartsWith("attachments/", presignResp.S3Key);
        Assert.NotEmpty(presignResp.UploadUrl);

        HttpResponseMessage confirm = await client.PostAsync(
            $"/attachments/{presignResp.AttachmentId}/confirm", content: null);
        await EnsureSuccessAsync(confirm);
        AttachmentSummary confirmed = (await confirm.Content.ReadFromJsonAsync<AttachmentSummary>())!;

        Assert.Equal("UPLOADED", confirmed.Status);
    }

    // ───────────────────────── Helpers ─────────────────────────

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

    private static async Task<TokenResponse> RegisterAsync(HttpClient client, string email)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest(email, Password, email.Split('@')[0]));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task<OrganizationSummary> CreateOrganizationAsync(HttpClient client, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/organizations", new CreateOrganizationRequest(name));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<OrganizationSummary>())!;
    }

    private static async Task<TeamSummary> CreateTeamAsync(HttpClient client, Guid orgId, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/organizations/{orgId}/teams", new CreateTeamRequest(orgId, name));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<TeamSummary>())!;
    }

    private static async Task<ProjectSummary> CreateProjectAsync(HttpClient client, Guid teamId, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/teams/{teamId}/projects", new CreateProjectRequest(teamId, name, null, null));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ProjectSummary>())!;
    }

    private static async Task<ProjectSummary> CreateProjectForAsync(HttpClient client, TokenResponse token, string name)
    {
        OrganizationSummary org = await CreateOrganizationAsync(client, name + "-org");
        TeamSummary team = await CreateTeamAsync(client, org.Id, name + "-team");
        return await CreateProjectAsync(client, team.Id, name);
    }

    private static async Task<ChannelSummary> CreateChannelForAsync(HttpClient client, Guid projectId, string name)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/projects/{projectId}/channels", new CreateChannelRequest(name, null));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<ChannelSummary>())!;
    }

    private static async Task<MessageSummary> PostHumanMessageAsync(HttpClient client, Guid channelId, string text)
    {
        var body = new PostMessageRequest(
            ContentType: "HUMAN",
            Content: JsonDocument.Parse($$"""{"text":"{{text}}"}""").RootElement,
            ClientMsgId: null,
            ParentSeq: null);
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/channels/{channelId}/messages", body);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<MessageSummary>())!;
    }

    private static async Task<MessagesPage> GetMessagesAsync(HttpClient client, Guid channelId, long sinceSeq)
    {
        HttpResponseMessage response = await client.GetAsync(
            $"/channels/{channelId}/messages?since_seq={sinceSeq}");
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<MessagesPage>())!;
    }
}
