using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Routing;

namespace MateOS.IntegrationTests;

/// <summary>
/// M3b Phase 3 端到端用例：Agent 侧协议闭环。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖 detailed/10 §1 协议面里此前<b>完全缺失</b>的三段：
/// <list type="number">
///   <item><c>collaboration.request</c> 投递 —— Agent 轮询 CR inbox（V1 走轮询，WS 推送属下一阶段）</item>
///   <item><c>collaboration.decision</c> 上报 —— Agent 自己用 agent_token 表态</item>
///   <item><c>execution.resume_request</c> / <c>resume_ack</c> —— 断线重连后取连续位点续发（B2 / B6）</item>
/// </list>
/// </para>
/// <para>
/// <b>序列化口径</b>：wire 层统一 snake_case，而 <c>PostAsJsonAsync</c> /
/// <c>ReadFromJsonAsync</c> 默认用 <c>JsonSerializerDefaults.Web</c>（camelCase），
/// 两边对不上时请求体会被服务端当成「字段缺失」（400），响应会静默反序列化成
/// <c>default</c> 值（断言看似通过、其实什么都没验）。统一走
/// <see cref="WireHttpClientExtensions"/>。
/// </para>
/// </remarks>
public sealed class AgentProtocolEndpointsTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── M3b-101 Agent 收协作请求 ─────────────────────────

    [Fact]
    public async Task M3b_101_agent应能通过inbox拉到派给自己的待决策CR()
    {
        await fixture.ResetAsync();

        (HttpClient userClient, TokenResponse userToken) = await SetupUserAsync("alice@example.com");
        AgentSummary agent = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "alice-coder");
        HttpClient agentClient = await SetupAgentClientAsync(userClient, agent.Id);

        Guid channelId = Guid.NewGuid();
        Guid crId = await CreatePendingCrAsync(userClient, userToken, agent.Id, channelId);

        HttpResponseMessage resp = await agentClient.GetAsync(
            $"/agents/{agent.Id}/collaboration-requests/inbox");
        await EnsureSuccessAsync(resp);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement item = Assert.Single(doc.RootElement.EnumerateArray());

        Assert.Equal(crId, item.GetProperty("collaboration_request_id").GetGuid());

        // from_actor 是嵌套对象（USER + 发起人），不是裸 id
        JsonElement fromActor = item.GetProperty("from_actor");
        Assert.Equal("USER", fromActor.GetProperty("type").GetString());
        Assert.Equal(userToken.User.Id, fromActor.GetProperty("id").GetGuid());

        // context_refs 把 channel / message 位置透出来，Agent 据此知道「在哪个上下文里被叫到」
        JsonElement contextRefs = item.GetProperty("context_refs");
        Assert.Equal(channelId, contextRefs.GetProperty("channel_id").GetGuid());
        Assert.Equal(7, contextRefs.GetProperty("message_seq").GetInt64());

        // required_capabilities 是数组，不是 JSON 字符串
        JsonElement capabilities = item.GetProperty("required_capabilities");
        Assert.Equal(JsonValueKind.Array, capabilities.ValueKind);
        Assert.Contains("coding", capabilities.EnumerateArray().Select(c => c.GetString()));

        // deadline_s 是「相对秒」：创建时给 600，这里必须是 0 < 剩余 ≤ 600
        int deadlineS = item.GetProperty("deadline_s").GetInt32();
        Assert.InRange(deadlineS, 1, 600);
    }

    [Fact]
    public async Task M3b_102_inbox只应返回派给自己的CR且不含已决策的()
    {
        await fixture.ResetAsync();

        (HttpClient userClient, TokenResponse userToken) = await SetupUserAsync("bob@example.com");
        AgentSummary alice = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "bob-alice");
        AgentSummary bob = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "bob-bob");
        HttpClient aliceClient = await SetupAgentClientAsync(userClient, alice.Id);

        // 三条 CR：派给 alice（PENDING）、派给 bob（PENDING）、派给 alice 但已被决策
        Guid aliceCr = await CreatePendingCrAsync(userClient, userToken, alice.Id, Guid.NewGuid());
        await CreatePendingCrAsync(userClient, userToken, bob.Id, Guid.NewGuid());
        Guid decidedCr = await CreatePendingCrAsync(userClient, userToken, alice.Id, Guid.NewGuid());

        await EnsureSuccessAsync(await PostDecisionAsync(
            aliceClient, alice.Id, decidedCr, new AgentDecisionRequest("REJECT", "not my scope", null, null)));

        HttpResponseMessage resp = await aliceClient.GetAsync(
            $"/agents/{alice.Id}/collaboration-requests/inbox");
        await EnsureSuccessAsync(resp);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        List<Guid> ids = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("collaboration_request_id").GetGuid())
            .ToList();

        // 派给 bob 的不出现（按 agent_id 过滤）；已决策的不出现（只有 PENDING 才是待办）
        Assert.Equal([aliceCr], ids);
    }

    // ───────────────────────── M3b-103 Agent 自己决策 ─────────────────────────

    [Fact]
    public async Task M3b_103_agent自己ACCEPT应创建execution且审计主体为AGENT()
    {
        await fixture.ResetAsync();

        (HttpClient userClient, TokenResponse userToken) = await SetupUserAsync("carol@example.com");
        AgentSummary agent = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "carol-coder");
        HttpClient agentClient = await SetupAgentClientAsync(userClient, agent.Id);

        Guid crId = await CreatePendingCrAsync(userClient, userToken, agent.Id, Guid.NewGuid());

        var decision = new AgentDecisionRequest(
            Decision: "ACCEPT",
            Reason: "capability match",
            Analysis: new AgentDecisionAnalysis(Capability: true, ContextScore: 91.5, Permission: true),
            Needs: null);

        HttpResponseMessage resp = await PostDecisionAsync(agentClient, agent.Id, crId, decision);
        await EnsureSuccessAsync(resp);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        Assert.Equal("ACCEPTED", doc.RootElement.GetProperty("status").GetString());

        Guid executionId = doc.RootElement.GetProperty("target_execution_id").GetGuid();
        Assert.NotEqual(Guid.Empty, executionId);

        JsonElement record = doc.RootElement.GetProperty("decision");
        Assert.Equal("ACCEPT", record.GetProperty("decision").GetString());

        // 决策主体必须是 Agent 自己（而不是「某个登录用户」）—— 这是 agent_token 路径的全部意义
        Assert.Equal("AGENT", record.GetProperty("actor_type").GetString());
        Assert.Equal(agent.Id, record.GetProperty("actor_id").GetGuid());
        Assert.Equal(executionId, record.GetProperty("accepted_execution_id").GetGuid());

        // ACCEPT 必须真的把活派下去：execution 出现在该 agent 的 dispatch inbox 里
        HttpResponseMessage inboxResp = await agentClient.GetAsync($"/agents/{agent.Id}/executions/inbox");
        await EnsureSuccessAsync(inboxResp);

        using JsonDocument inboxDoc = JsonDocument.Parse(await inboxResp.Content.ReadAsStringAsync());
        JsonElement dispatch = Assert.Single(inboxDoc.RootElement.EnumerateArray());
        Assert.Equal(executionId, dispatch.GetProperty("execution_id").GetGuid());
        Assert.Equal(crId, dispatch.GetProperty("collaboration_request_id").GetGuid());
    }

    [Fact]
    public async Task M3b_104_别的agent不能替它决策()
    {
        await fixture.ResetAsync();

        (HttpClient userClient, TokenResponse userToken) = await SetupUserAsync("dave@example.com");
        AgentSummary alice = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "dave-alice");
        AgentSummary bob = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "dave-bob");

        HttpClient bobClient = await SetupAgentClientAsync(userClient, bob.Id);
        HttpClient aliceClient = await SetupAgentClientAsync(userClient, alice.Id);

        Guid aliceCr = await CreatePendingCrAsync(userClient, userToken, alice.Id, Guid.NewGuid());

        // bob 拿着合法 agent_token，去改派给 alice 的 CR
        HttpResponseMessage resp = await PostDecisionAsync(
            bobClient, bob.Id, aliceCr, new AgentDecisionRequest("ACCEPT", null, null, null));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // 且 CR 必须仍是 PENDING（越权尝试不得留下任何痕迹）
        HttpResponseMessage inboxResp = await aliceClient.GetAsync(
            $"/agents/{alice.Id}/collaboration-requests/inbox");
        await EnsureSuccessAsync(inboxResp);

        using JsonDocument doc = JsonDocument.Parse(await inboxResp.Content.ReadAsStringAsync());
        Assert.Equal(aliceCr, Assert.Single(doc.RootElement.EnumerateArray())
            .GetProperty("collaboration_request_id").GetGuid());
    }

    [Fact]
    public async Task M3b_105_agent_token不能用于别的agentId路径()
    {
        await fixture.ResetAsync();

        (HttpClient userClient, _) = await SetupUserAsync("erin@example.com");
        AgentSummary alice = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "erin-alice");
        AgentSummary bob = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "erin-bob");

        HttpClient bobClient = await SetupAgentClientAsync(userClient, bob.Id);

        // 用 bob 的 token 打 alice 的路径：即使只是读，也必须 403
        HttpResponseMessage resp = await bobClient.GetAsync(
            $"/agents/{alice.Id}/collaboration-requests/inbox");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ───────────────────────── M3b-106 断线重连续传 ─────────────────────────

    [Fact]
    public async Task M3b_106_resume_request应返回连续位点并支持从下一位续发()
    {
        await fixture.ResetAsync();

        (HttpClient userClient, _) = await SetupUserAsync("frank@example.com");
        AgentSummary agent = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "frank-coder");
        HttpClient agentClient = await SetupAgentClientAsync(userClient, agent.Id);

        Guid executionId = await CreateExecutionAsync(userClient, agent.Id, "resume-1");

        await EnsureSuccessAsync(await AckAsync(agentClient, agent.Id, executionId));

        // 发 1..3，然后模拟「断线前最后成功落库到 3」
        for (long seq = 1; seq <= 3; seq++)
        {
            await EnsureSuccessAsync(await PostEventAsync(agentClient, agent.Id, executionId, seq));
        }

        HttpResponseMessage resumeResp = await PostResumeRequestAsync(
            agentClient, agent.Id, executionId, new ResumeRequestPayload(AttemptNo: null, ExpectedSeq: null));
        await EnsureSuccessAsync(resumeResp);

        using JsonDocument resumeDoc = JsonDocument.Parse(await resumeResp.Content.ReadAsStringAsync());
        Assert.Equal(executionId, resumeDoc.RootElement.GetProperty("execution_id").GetGuid());
        Assert.Equal(1, resumeDoc.RootElement.GetProperty("attempt_no").GetInt32());
        Assert.Equal(3, resumeDoc.RootElement.GetProperty("last_persisted_seq").GetInt64());

        // 快照必须是 dispatch 的冻结原样（Agent 断线后靠它恢复「我要干什么」）
        JsonElement snapshot = resumeDoc.RootElement.GetProperty("snapshot");
        Assert.Equal(JsonValueKind.Object, snapshot.ValueKind);
        Assert.Equal("resume prompt", snapshot.GetProperty("input").GetProperty("prompt").GetString());

        // Agent 从 last_persisted_seq + 1 续发：seq=4 必须被接受（而不是被判 gap）
        await EnsureSuccessAsync(await PostEventAsync(agentClient, agent.Id, executionId, 4));

        // 再次询问应前进到 4
        HttpResponseMessage again = await PostResumeRequestAsync(
            agentClient, agent.Id, executionId, new ResumeRequestPayload(AttemptNo: 1, ExpectedSeq: null));
        await EnsureSuccessAsync(again);

        using JsonDocument againDoc = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.Equal(4, againDoc.RootElement.GetProperty("last_persisted_seq").GetInt64());
    }

    [Fact]
    public async Task M3b_107_resume_request对过期attempt与终态execution应拒绝()
    {
        await fixture.ResetAsync();

        (HttpClient userClient, _) = await SetupUserAsync("grace@example.com");
        AgentSummary agent = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "grace-coder");
        HttpClient agentClient = await SetupAgentClientAsync(userClient, agent.Id);

        Guid executionId = await CreateExecutionAsync(userClient, agent.Id, "resume-2");

        // 过期 attempt：Agent 手里是上一轮，位点对它无意义（与 B8 迟到结果同根因）
        HttpResponseMessage staleAttempt = await PostResumeRequestAsync(
            agentClient, agent.Id, executionId, new ResumeRequestPayload(AttemptNo: 99, ExpectedSeq: null));
        Assert.Equal(HttpStatusCode.Conflict, staleAttempt.StatusCode);

        // 收尾之后再问：活已经结束了，不该再给一个「接着发」的位点
        await EnsureSuccessAsync(await AckAsync(agentClient, agent.Id, executionId));
        await EnsureSuccessAsync(await PostResultAsync(agentClient, agent.Id, executionId));

        HttpResponseMessage afterTerminal = await PostResumeRequestAsync(
            agentClient, agent.Id, executionId, new ResumeRequestPayload(AttemptNo: null, ExpectedSeq: null));
        Assert.Equal(HttpStatusCode.Conflict, afterTerminal.StatusCode);
    }

    // ───────────────────────── M3b-108 Agent 自报 activity ─────────────────────────

    [Fact]
    public async Task M3b_108_agent应用自己的token上报activity()
    {
        await fixture.ResetAsync();

        (HttpClient userClient, _) = await SetupUserAsync("heidi@example.com");
        AgentSummary alice = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "heidi-alice");
        AgentSummary bob = await CreateAgentAsync(userClient, await CreateCredentialAsync(userClient), "heidi-bob");

        HttpClient aliceClient = await SetupAgentClientAsync(userClient, alice.Id);

        // M3a 阶段这条会 403（端点用 RequireUserId() 且只认 owner）。
        // stub 一接上就必然踩到，所以这里是回归闸门。
        HttpResponseMessage resp = await aliceClient.PostJsonAsync(
            $"/agents/{alice.Id}/activity",
            new ReportActivityRequest("WORKING", "执行中"));

        await EnsureSuccessAsync(resp);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("WORKING", doc.RootElement.GetProperty("activity").GetString());

        // 别人的 token 不能替 alice 上报
        HttpClient bobClient = await SetupAgentClientAsync(userClient, bob.Id);
        HttpResponseMessage cross = await bobClient.PostJsonAsync(
            $"/agents/{alice.Id}/activity",
            new ReportActivityRequest("AVAILABLE", null));

        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
    }

    // ───────────────────────── helpers ─────────────────────────

    private async Task<(HttpClient Client, TokenResponse Token)> SetupUserAsync(string email)
    {
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        return (client, token);
    }

    /// <summary>给 agent 签 token 并返回一个已带该 agent_token 的 HttpClient。</summary>
    private async Task<HttpClient> SetupAgentClientAsync(HttpClient ownerClient, Guid agentId)
    {
        HttpResponseMessage resp = await ownerClient.PostJsonAsync(
            $"/agents/{agentId}/tokens", new IssueTokenRequest("dev-stub", 1));
        await EnsureSuccessAsync(resp);

        AgentTokenIssuanceResponse issued =
            (await resp.Content.ReadWireAsync<AgentTokenIssuanceResponse>())!;

        HttpClient agentClient = fixture.CreateClient();
        agentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.JwtToken);

        return agentClient;
    }

    /// <summary>造一条派给指定 agent 的 PENDING CR（走 E4 内部入口）。</summary>
    private static async Task<Guid> CreatePendingCrAsync(
        HttpClient userClient, TokenResponse userToken, Guid targetAgentId, Guid channelId)
    {
        var req = new CreateTriggerRequest(
            TriggerType: "MENTION",
            TriggerRef: JsonDocument.Parse("""{"text":"@backend 看下这段代码"}""").RootElement,
            FromActorType: "USER",
            FromActorId: userToken.User.Id,
            RequestKind: "MESSAGE_RESPONSE",
            TargetAgentId: targetAgentId,
            RequiredCapabilities: new[] { "coding" },
            ContextRefs: JsonDocument.Parse(
                $$"""{"channel_id":"{{channelId}}","message_seq":7,"project_id":"{{Guid.NewGuid()}}"}""").RootElement,
            DeadlineS: 600,
            IdempotencyKey: $"cr-{Guid.NewGuid():N}");

        HttpResponseMessage resp = await userClient.PostJsonAsync("/internal/triggers", req);
        await EnsureSuccessAsync(resp);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("collaboration_request").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateExecutionAsync(HttpClient userClient, Guid agentId, string idempotencyKey)
    {
        var req = new CreateAgentExecutionRequest(
            AgentId: agentId,
            CollaborationRequestId: null,
            WorkItemRef: null,
            Input: JsonDocument.Parse("""{"prompt":"resume prompt"}""").RootElement,
            ContextRefs: null,
            IdempotencyKey: idempotencyKey,
            DeadlineS: 600);

        HttpResponseMessage resp = await userClient.PostJsonAsync("/internal/agent-executions", req);
        await EnsureSuccessAsync(resp);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("execution").GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> PostDecisionAsync(
        HttpClient agentClient, Guid agentId, Guid crId, AgentDecisionRequest decision) =>
        agentClient.PostJsonAsync(
            $"/agents/{agentId}/collaboration-requests/{crId}/decision", decision);

    private static Task<HttpResponseMessage> PostResumeRequestAsync(
        HttpClient agentClient, Guid agentId, Guid executionId, ResumeRequestPayload payload) =>
        agentClient.PostJsonAsync(
            $"/agents/{agentId}/executions/{executionId}/resume_request", payload);

    private static Task<HttpResponseMessage> AckAsync(HttpClient agentClient, Guid agentId, Guid executionId) =>
        agentClient.PostAsync($"/agents/{agentId}/executions/{executionId}/dispatch_ack", content: null);

    private static Task<HttpResponseMessage> PostEventAsync(
        HttpClient agentClient, Guid agentId, Guid executionId, long seq) =>
        agentClient.PostJsonAsync(
            $"/agents/{agentId}/executions/{executionId}/events",
            new ReportExecutionEventRequest(
                EventType: "PROGRESS",
                ProviderEventId: $"pe-{seq}",
                Seq: seq,
                Payload: JsonDocument.Parse($$"""{"step":{{seq}}}""").RootElement));

    private static Task<HttpResponseMessage> PostResultAsync(
        HttpClient agentClient, Guid agentId, Guid executionId) =>
        agentClient.PostJsonAsync(
            $"/agents/{agentId}/executions/{executionId}/result",
            new ReportExecutionResultRequest(
                Status: "SUCCEEDED",
                EnvelopeId: Guid.NewGuid().ToString("N"),
                Output: JsonDocument.Parse("""{"markdown":"done"}""").RootElement,
                Usage: null));

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
        HttpClient client, CredentialSummary credential, string name)
    {
        var req = new CreateAgentRequest(
            Name: name, Role: "coder",
            Capabilities: new[] { "coding" },
            CredentialId: credential.Id,
            MaxConcurrency: 1, DailyLimitUsd: null, MonthlyBudgetUsd: null);

        HttpResponseMessage resp = await client.PostJsonAsync("/agents", req);
        await EnsureSuccessAsync(resp);

        return (await resp.Content.ReadWireAsync<AgentSummary>())!;
    }

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
}
