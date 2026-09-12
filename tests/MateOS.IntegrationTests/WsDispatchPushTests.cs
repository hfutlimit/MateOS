using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Channels;
using MateOS.Api.Work;
using MateOS.Api.Workspace;

namespace MateOS.IntegrationTests;

/// <summary>
/// M3b Phase 2 · E7 dispatch 实时推送端到端。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖价值最高的两条：
/// <list type="number">
///   <item>Agent 用 <c>agent_token</c> 建立 WS 会话后，指派工作 → 实时收到 <c>execution.dispatch</c>，
///         且 payload 与轮询 inbox 的形状一致（同一份事实、两条通道）；</item>
///   <item>Agent <b>不在线</b>时指派仍然成功，dispatch 留在 inbox 可轮询领取
///         —— 推送是优化，不是依赖。</item>
/// </list>
/// </para>
/// <para>
/// 用真实 <see cref="System.Net.WebSockets.ClientWebSocket"/>（<see cref="WsTestClient"/>）。
/// </para>
/// </remarks>
public sealed class WsDispatchPushTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── E7-PUSH-001 ─────────────────────────

    [Fact]
    public async Task E7PUSH_001_agent_token连WS后指派应实时收到execution_dispatch()
    {
        await fixture.ResetAsync();
        HttpClient owner = fixture.CreateClient();
        TokenResponse ownerToken = await RegisterAsync(owner, "po@example.com");
        Authorize(owner, ownerToken.AccessToken);

        ProjectSummary project = await CreateProjectForAsync(owner, "Push");
        Guid agentId = await CreateAgentAsync(owner, "backend-agent");
        string agentToken = await IssueAgentTokenAsync(owner, agentId);

        // ① Agent 用 agent_token 建会话
        await using WsTestClient ws = await WsTestClient.ConnectAsync(fixture, agentToken);
        await ws.HelloAsAgentAsync(agentToken);

        JsonElement? helloAck = await ws.ReceiveNextAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(helloAck);
        Assert.Equal("hello_ack", helloAck.Value.GetProperty("type").GetString());
        Assert.Equal("AGENT", helloAck.Value.GetProperty("payload").GetProperty("actor_type").GetString());
        Assert.Equal(agentId, helloAck.Value.GetProperty("payload").GetProperty("actor_id").GetGuid());

        // ② 建卡 + 指派（hello_ack 收到即意味着连接已注册，无需额外等待）
        Guid itemId = await CreateWorkItemAsync(owner, project.Id, "TASK", "推送给 agent 的活", "实现接口");

        HttpResponseMessage assign = await owner.PostJsonAsync(
            $"/work-items/{itemId}/assign", new AssignWorkItemRequest(agentId, 300));
        await EnsureSuccessAsync(assign);

        Guid executionId;
        using (JsonDocument doc = await JsonDocument.ParseAsync(await assign.Content.ReadAsStreamAsync()))
        {
            executionId = doc.RootElement.GetProperty("execution_id").GetGuid();
        }

        // ③ Agent 应实时收到 dispatch（推送路径，不是轮询）
        JsonElement? frame = await ws.ReceiveNextAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(frame);
        Assert.Equal("execution.dispatch", frame.Value.GetProperty("type").GetString());

        JsonElement payload = frame.Value.GetProperty("payload");
        Assert.Equal(executionId, payload.GetProperty("execution_id").GetGuid());
        Assert.Equal(1, payload.GetProperty("attempt_no").GetInt32());
        Assert.NotEqual(Guid.Empty, payload.GetProperty("collaboration_request_id").GetGuid());
        Assert.False(string.IsNullOrEmpty(payload.GetProperty("idempotency_key").GetString()));

        // work_item_ref 是对象而非裸 id：Agent 必须能知道走哪个 Provider
        JsonElement workItemRef = payload.GetProperty("work_item_ref");
        Assert.Equal("builtin", workItemRef.GetProperty("provider_key").GetString());
        Assert.Equal(itemId.ToString(), workItemRef.GetProperty("work_item_id").GetString());
        Assert.Equal(JsonValueKind.Null, workItemRef.GetProperty("external_ref").ValueKind);

        // input 是契约形状 { prompt, params }；WorkItem 指派时 prompt 取标题
        Assert.Equal("推送给 agent 的活", payload.GetProperty("input").GetProperty("prompt").GetString());

        // context 保留原始引用透传位
        Assert.Equal(itemId.ToString(),
            payload.GetProperty("context").GetProperty("refs").GetProperty("work_item_id").GetString());

        // 相对秒数：时钟不同步也不该让 Agent 提前放弃
        Assert.True(payload.GetProperty("deadline_s").GetInt32() is > 0 and <= 300);

        // ④ 同一份事实的另一条通道（轮询 inbox）必须给出同样的形状
        HttpClient asAgent = fixture.CreateClient();
        Authorize(asAgent, agentToken);

        HttpResponseMessage inboxResponse = await asAgent.GetAsync($"/agents/{agentId}/executions/inbox");
        await EnsureSuccessAsync(inboxResponse);

        using JsonDocument inboxDoc = await JsonDocument.ParseAsync(await inboxResponse.Content.ReadAsStreamAsync());
        JsonElement inboxItem = Assert.Single(inboxDoc.RootElement.EnumerateArray().Select(x => x.Clone()));

        Assert.Equal(executionId, inboxItem.GetProperty("execution_id").GetGuid());
        Assert.Equal(payload.GetProperty("attempt_no").GetInt32(), inboxItem.GetProperty("attempt_no").GetInt32());
        Assert.Equal(payload.GetProperty("idempotency_key").GetString(),
            inboxItem.GetProperty("idempotency_key").GetString());

        // 两条通道给出的形状必须一致（SDK 只应有一份解析代码）
        Assert.Equal(
            payload.GetProperty("work_item_ref").GetRawText(),
            inboxItem.GetProperty("work_item_ref").GetRawText());
        Assert.Equal(
            payload.GetProperty("input").GetRawText(),
            inboxItem.GetProperty("input").GetRawText());
    }

    // ───────────────────────── E7-PUSH-002 ─────────────────────────

    [Fact]
    public async Task E7PUSH_002_agent不在线时指派仍应成功且dispatch可轮询领取()
    {
        await fixture.ResetAsync();
        HttpClient owner = fixture.CreateClient();
        TokenResponse ownerToken = await RegisterAsync(owner, "po2@example.com");
        Authorize(owner, ownerToken.AccessToken);

        ProjectSummary project = await CreateProjectForAsync(owner, "Offline");
        Guid agentId = await CreateAgentAsync(owner, "offline-agent");
        string agentToken = await IssueAgentTokenAsync(owner, agentId);

        // 刻意不建 WS 会话
        Guid itemId = await CreateWorkItemAsync(owner, project.Id, "TASK", "离线 agent 的活", null);

        HttpResponseMessage assign = await owner.PostJsonAsync(
            $"/work-items/{itemId}/assign", new AssignWorkItemRequest(agentId, 300));

        // 推送送不出去 ≠ 指派失败
        await EnsureSuccessAsync(assign);

        using JsonDocument doc = await JsonDocument.ParseAsync(await assign.Content.ReadAsStreamAsync());
        Guid executionId = doc.RootElement.GetProperty("execution_id").GetGuid();

        // 轮询仍能领到这条 dispatch
        HttpClient asAgent = fixture.CreateClient();
        Authorize(asAgent, agentToken);

        HttpResponseMessage inboxResponse = await asAgent.GetAsync($"/agents/{agentId}/executions/inbox");
        await EnsureSuccessAsync(inboxResponse);

        using JsonDocument inboxDoc = await JsonDocument.ParseAsync(await inboxResponse.Content.ReadAsStreamAsync());
        JsonElement dispatch = Assert.Single(inboxDoc.RootElement.EnumerateArray().Select(x => x.Clone()));

        Assert.Equal(executionId, dispatch.GetProperty("execution_id").GetGuid());
    }

    // ───────────────────────── E7-PUSH-003 ─────────────────────────

    [Fact]
    public async Task E7PUSH_003_agent会话不因user身份获得channel可见性()
    {
        await fixture.ResetAsync();
        HttpClient owner = fixture.CreateClient();
        TokenResponse ownerToken = await RegisterAsync(owner, "po3@example.com");
        Authorize(owner, ownerToken.AccessToken);

        ProjectSummary project = await CreateProjectForAsync(owner, "Readable");
        ChannelSummary channel = await CreateChannelAsync(owner, project.Id, "general");
        Guid agentId = await CreateAgentAsync(owner, "not-a-member");
        string agentToken = await IssueAgentTokenAsync(owner, agentId);

        await using WsTestClient ws = await WsTestClient.ConnectAsync(fixture, agentToken);
        await ws.HelloAsAgentAsync(agentToken);
        Assert.NotNull(await ws.ReceiveNextAsync(TimeSpan.FromSeconds(5)));

        // Agent 尚未加入 project → 订阅必须被拒。
        // 若实现只看 project_members，agent_id 恰好为某个 user 的 id 时会错误放行。
        await ws.SubscribeAsync(channel.Id);

        JsonElement? frame = await ws.ReceiveNextAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(frame);
        Assert.Equal("error", frame.Value.GetProperty("type").GetString());
        Assert.Equal("NOT_AUTHORIZED", frame.Value.GetProperty("payload").GetProperty("code").GetString());

        // 加入 project（agent_project_membership）之后才可见
        HttpResponseMessage added = await owner.PostJsonAsync(
            $"/projects/{project.Id}/agents", new AddAgentToProjectRequest(agentId, true));
        await EnsureSuccessAsync(added);

        await ws.SubscribeAsync(channel.Id);

        JsonElement? ack = await ws.ReceiveNextAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(ack);
        Assert.Equal("subscribe.ack", ack.Value.GetProperty("type").GetString());
        Assert.Equal(channel.Id, ack.Value.GetProperty("payload").GetProperty("channel_id").GetGuid());
    }

    // ───────────────────────── helpers ─────────────────────────

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"HTTP {(int)response.StatusCode} ({response.StatusCode}) {response.RequestMessage?.RequestUri}：{body}");
    }

    private static async Task<TokenResponse> RegisterAsync(HttpClient client, string email)
    {
        HttpResponseMessage resp = await client.PostJsonAsync(
            "/auth/register", new RegisterRequest(email, Password, email.Split('@')[0]));

        await EnsureSuccessAsync(resp);

        return (await resp.Content.ReadWireAsync<TokenResponse>())!;
    }

    private static async Task<ProjectSummary> CreateProjectForAsync(HttpClient client, string name)
    {
        OrganizationSummary org = await PostAsync<OrganizationSummary>(
            client, "/orgs", new CreateOrganizationRequest($"{name}-org"));

        TeamSummary team = await PostAsync<TeamSummary>(
            client, "/teams", new CreateTeamRequest(org.Id, $"{name}-team"));

        return await PostAsync<ProjectSummary>(
            client, "/projects", new CreateProjectRequest(team.Id, name, null, null));
    }

    private static async Task<ChannelSummary> CreateChannelAsync(HttpClient client, Guid projectId, string name) =>
        await PostAsync<ChannelSummary>(client, $"/projects/{projectId}/channels", new CreateChannelRequest(name, null));

    private static async Task<Guid> CreateAgentAsync(HttpClient client, string name)
    {
        CredentialSummary credential = await PostAsync<CredentialSummary>(
            client, "/credentials",
            new CreateCredentialRequest("openai", "test", $"sk-{Guid.NewGuid():N}", null));

        AgentSummary agent = await PostAsync<AgentSummary>(
            client, "/agents",
            new CreateAgentRequest(
                Name: name,
                Role: "coder",
                Capabilities: new[] { "coding" },
                CredentialId: credential.Id,
                MaxConcurrency: 1,
                DailyLimitUsd: null,
                MonthlyBudgetUsd: null));

        return agent.Id;
    }

    private static async Task<string> IssueAgentTokenAsync(HttpClient client, Guid agentId)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            $"/agents/{agentId}/tokens", new IssueTokenRequest(Label: "test", LifetimeDays: 1));

        await EnsureSuccessAsync(response);

        using JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        return doc.RootElement.GetProperty("jwt_token").GetString()!;
    }

    private static async Task<Guid> CreateWorkItemAsync(
        HttpClient client, Guid projectId, string type, string title, string? description)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            $"/projects/{projectId}/work-items",
            new CreateWorkItemRequest(type, title, description, null, null, null));

        await EnsureSuccessAsync(response);

        using JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        HttpResponseMessage response = await client.PostJsonAsync(url, body);

        await EnsureSuccessAsync(response);

        return (await response.Content.ReadWireAsync<T>())!;
    }
}
