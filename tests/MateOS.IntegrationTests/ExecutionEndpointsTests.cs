using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Auth;

namespace MateOS.IntegrationTests;

/// <summary>
/// E7 §1 端到端用例：创建 execution → agent inbox 拉取 → dispatch_ack → event 上报 → result 上报。
/// </summary>
/// <remarks>
/// <para>
/// 路径：
/// <list type="number">
///   <item>user token 调 POST /internal/agent-executions 创建 execution + dispatch inbox</item>
///   <item>agent token 调 GET /agents/{id}/executions/inbox 拉取 dispatch</item>
///   <item>agent token POST /agents/{id}/executions/{eid}/dispatch_ack 回填 dispatch_acked_at</item>
///   <item>agent token POST /agents/{id}/executions/{eid}/events 上报 3 条 event（验证 contiguous cursor）</item>
///   <item>agent token POST /agents/{id}/executions/{eid}/result 上报 SUCCEEDED（CAS 终态）</item>
/// </list>
/// </para>
/// <para>
/// 鉴权关键：agent_token JWT（sub=agent_id, token_type=agent）vs user access token（sub=user_id, token_type=access）。
/// M3b 端到端验证这两类 token 走同中间件但被 <see cref="CurrentUserExtensions.RequireAgentId"/> 区分。
/// </para>
/// </remarks>
public sealed class ExecutionEndpointsTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── E7-001 ─────────────────────────

    [Fact]
    public async Task E7_001_完整dispatch_event_result流程应端到端通()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "alice@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        // 准备 agent
        CredentialSummary credential = await CreateCredentialAsync(userClient);
        AgentSummary agent = await CreateAgentAsync(userClient, credential.Id, "alice-coder");

        // 1. E4 → E7 创建 execution（user token 调 internal API）
        var createReq = new CreateAgentExecutionRequest(
            AgentId: agent.Id,
            CollaborationRequestId: null,
            WorkItemRef: null,
            Input: JsonDocument.Parse("""{"prompt":"say hi"}""").RootElement,
            ContextRefs: null,
            IdempotencyKey: "idemp-001",
            DeadlineS: 600);
        HttpResponseMessage createResp = await userClient.PostAsJsonAsync(
            "/internal/agent-executions", createReq);
        await EnsureSuccessAsync(createResp);
        var created = (await createResp.Content.ReadFromJsonAsync<JsonElement>())!;
        Guid executionId = created.GetProperty("execution").GetProperty("id").GetGuid();
        Assert.False(created.GetProperty("idempotent").GetBoolean());
        Assert.Equal("PENDING", created.GetProperty("execution").GetProperty("status").GetString());

        // 2. 同一 idempotency_key 重发应返 idempotent=true 且不创建新行
        HttpResponseMessage dupResp = await userClient.PostAsJsonAsync(
            "/internal/agent-executions", createReq);
        await EnsureSuccessAsync(dupResp);
        var dup = (await dupResp.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.True(dup.GetProperty("idempotent").GetBoolean());
        Assert.Equal(executionId, dup.GetProperty("execution").GetProperty("id").GetGuid());

        // 3. 用 agent_token JWT 调 inbox
        AgentTokenIssuanceResponse agentTokenIssued = await IssueAgentTokenAsync(userClient, agent.Id);
        string agentJwt = MintAgentJwt(agentTokenIssued);

        HttpClient agentClient = fixture.CreateClient();
        agentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentJwt);

        // 4. inbox 应有 1 条 dispatch envelope
        HttpResponseMessage inboxResp = await agentClient.GetAsync(
            $"/agents/{agent.Id}/executions/inbox");
        await EnsureSuccessAsync(inboxResp);
        var inbox = (await inboxResp.Content.ReadFromJsonAsync<List<DispatchEnvelope>>())!;
        Assert.Single(inbox);
        Assert.Equal(executionId, inbox[0].ExecutionId);
        Assert.Equal("idemp-001", inbox[0].IdempotencyKey);

        // 5. dispatch_ack
        HttpResponseMessage ackResp = await agentClient.PostAsync(
            $"/agents/{agent.Id}/executions/{executionId}/dispatch_ack", content: null);
        await EnsureSuccessAsync(ackResp);
        var afterAck = (await ackResp.Content.ReadFromJsonAsync<ExecutionSummary>())!;
        Assert.Equal("RUNNING", afterAck.Status);
        Assert.NotNull(afterAck.DispatchAckedAtMs);

        // 6. 上报 3 条 event（contiguous cursor 1, 2, 3）
        for (long seq = 1; seq <= 3; seq++)
        {
            var ev = new ReportExecutionEventRequest(
                EventType: "PROGRESS",
                ProviderEventId: $"pe-{seq}",
                Seq: seq,
                Payload: JsonDocument.Parse($$"""{"content":"step {{seq}}"}""").RootElement);
            HttpResponseMessage evResp = await agentClient.PostAsJsonAsync(
                $"/agents/{agent.Id}/executions/{executionId}/events", ev);
            await EnsureSuccessAsync(evResp);
        }

        // 7. 跳号 seq=5 应 gap 拒
        var gapEv = new ReportExecutionEventRequest(
            EventType: "PROGRESS", ProviderEventId: "pe-gap", Seq: 5,
            Payload: JsonDocument.Parse("""{"content":"gap"}""").RootElement);
        HttpResponseMessage gapResp = await agentClient.PostAsJsonAsync(
            $"/agents/{agent.Id}/executions/{executionId}/events", gapEv);
        Assert.Equal(HttpStatusCode.Conflict, gapResp.StatusCode);

        // 8. 重复 seq=1 应被静默忽略（v0.4.3 contiguous dedup）
        var dupEv = new ReportExecutionEventRequest(
            EventType: "PROGRESS", ProviderEventId: "pe-1-dup", Seq: 1,
            Payload: JsonDocument.Parse("""{"content":"dup"}""").RootElement);
        HttpResponseMessage dupEvResp = await agentClient.PostAsJsonAsync(
            $"/agents/{agent.Id}/executions/{executionId}/events", dupEv);
        await EnsureSuccessAsync(dupEvResp);

        // 9. 上报 result SUCCEEDED
        string envelopeId = Guid.NewGuid().ToString("N");
        var result = new ReportExecutionResultRequest(
            Status: "SUCCEEDED",
            EnvelopeId: envelopeId,
            Output: JsonDocument.Parse("""{"markdown":"done"}""").RootElement,
            Usage: JsonDocument.Parse("""{"tokens_in":10,"tokens_out":20}""").RootElement);
        HttpResponseMessage resultResp = await agentClient.PostAsJsonAsync(
            $"/agents/{agent.Id}/executions/{executionId}/result", result);
        await EnsureSuccessAsync(resultResp);
        var afterResult = (await resultResp.Content.ReadFromJsonAsync<ExecutionSummary>())!;
        Assert.Equal("SUCCEEDED", afterResult.Status);
        Assert.NotNull(afterResult.CompletedAtMs);

        // 10. 重复 result 同 envelopeId 应被静默忽略（v0.5 idempotency）
        HttpResponseMessage dupResultResp = await agentClient.PostAsJsonAsync(
            $"/agents/{agent.Id}/executions/{executionId}/result", result);
        await EnsureSuccessAsync(dupResultResp);
        var afterDup = (await dupResultResp.Content.ReadFromJsonAsync<ExecutionSummary>())!;
        Assert.Equal("SUCCEEDED", afterDup.Status);
    }

    // ───────────────────────── E7-002 inbox 落库 + CAS 终态 ─────────────────────────

    [Fact]
    public async Task E7_002_idempotencyKey唯一约束应防重复创建()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "bob@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        CredentialSummary credential = await CreateCredentialAsync(userClient);
        AgentSummary agent = await CreateAgentAsync(userClient, credential.Id, "bob-coder");

        // 同 idempotency_key 调两次
        var req = new CreateAgentExecutionRequest(
            AgentId: agent.Id,
            CollaborationRequestId: null,
            WorkItemRef: null,
            Input: JsonDocument.Parse("""{"prompt":"test"}""").RootElement,
            ContextRefs: null,
            IdempotencyKey: "dup-key",
            DeadlineS: null);

        HttpResponseMessage r1 = await userClient.PostAsJsonAsync("/internal/agent-executions", req);
        await EnsureSuccessAsync(r1);
        HttpResponseMessage r2 = await userClient.PostAsJsonAsync("/internal/agent-executions", req);
        await EnsureSuccessAsync(r2);

        // 应返同一 execution_id + idempotent=true
        using JsonDocument d1 = await JsonDocument.ParseAsync(await r1.Content.ReadAsStreamAsync());
        using JsonDocument d2 = await JsonDocument.ParseAsync(await r2.Content.ReadAsStreamAsync());

        Assert.Equal(
            d1.RootElement.GetProperty("execution").GetProperty("id").GetGuid(),
            d2.RootElement.GetProperty("execution").GetProperty("id").GetGuid());
        Assert.True(d2.RootElement.GetProperty("idempotent").GetBoolean());
    }

    // ───────────────────────── E7-003 非ACTIVE lifecycle 拒绝 ─────────────────────────

    [Fact]
    public async Task E7_003_paused_agent的dispatch应被拒()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "carol@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        CredentialSummary credential = await CreateCredentialAsync(userClient);
        AgentSummary agent = await CreateAgentAsync(userClient, credential.Id, "carol-coder");

        // pause
        HttpResponseMessage pauseResp = await userClient.PostAsync($"/agents/{agent.Id}/pause", content: null);
        await EnsureSuccessAsync(pauseResp);

        // 尝试创建 execution
        var req = new CreateAgentExecutionRequest(
            AgentId: agent.Id,
            CollaborationRequestId: null,
            WorkItemRef: null,
            Input: JsonDocument.Parse("""{"prompt":"x"}""").RootElement,
            ContextRefs: null,
            IdempotencyKey: "k",
            DeadlineS: null);

        HttpResponseMessage resp = await userClient.PostAsJsonAsync("/internal/agent-executions", req);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // ───────────────────────── E7-004 collaboration_request_id 唯一约束 ─────────────────────────

    [Fact]
    public async Task E7_004_同collaboration_request_id第二次dispatch应返同execution()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "dave@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        CredentialSummary credential = await CreateCredentialAsync(userClient);
        AgentSummary agent = await CreateAgentAsync(userClient, credential.Id, "dave-coder");
        Guid collabId = Guid.NewGuid();

        var req1 = new CreateAgentExecutionRequest(
            AgentId: agent.Id, CollaborationRequestId: collabId, WorkItemRef: null,
            Input: JsonDocument.Parse("""{"prompt":"v1"}""").RootElement, ContextRefs: null,
            IdempotencyKey: "k1", DeadlineS: null);
        var req2 = new CreateAgentExecutionRequest(
            AgentId: agent.Id, CollaborationRequestId: collabId, WorkItemRef: null,
            Input: JsonDocument.Parse("""{"prompt":"v2"}""").RootElement, ContextRefs: null,
            IdempotencyKey: "k2", DeadlineS: null);

        HttpResponseMessage r1 = await userClient.PostAsJsonAsync("/internal/agent-executions", req1);
        await EnsureSuccessAsync(r1);
        HttpResponseMessage r2 = await userClient.PostAsJsonAsync("/internal/agent-executions", req2);
        await EnsureSuccessAsync(r2);

        using JsonDocument d1 = await JsonDocument.ParseAsync(await r1.Content.ReadAsStreamAsync());
        using JsonDocument d2 = await JsonDocument.ParseAsync(await r2.Content.ReadAsStreamAsync());

        // v0.4.3：UNIQUE(collaboration_request_id) → 第二次返 idempotent=true 同 execution_id
        Assert.Equal(
            d1.RootElement.GetProperty("execution").GetProperty("id").GetGuid(),
            d2.RootElement.GetProperty("execution").GetProperty("id").GetGuid());
        Assert.True(d2.RootElement.GetProperty("idempotent").GetBoolean());
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
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/auth/register", new RegisterRequest(email, Password, email.Split('@')[0]));
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task<CredentialSummary> CreateCredentialAsync(HttpClient client)
    {
        var req = new CreateCredentialRequest("openai", "test", $"sk-{Guid.NewGuid():N}", null);
        HttpResponseMessage resp = await client.PostAsJsonAsync("/credentials", req);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<CredentialSummary>())!;
    }

    private static async Task<AgentSummary> CreateAgentAsync(
        HttpClient client, Guid credentialId, string name)
    {
        var req = new CreateAgentRequest(
            Name: name, Role: "coder",
            Capabilities: new[] { "coding" },
            CredentialId: credentialId,
            MaxConcurrency: 1, DailyLimitUsd: null, MonthlyBudgetUsd: null);
        HttpResponseMessage resp = await client.PostAsJsonAsync("/agents", req);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<AgentSummary>())!;
    }

    private static async Task<AgentTokenIssuanceResponse> IssueAgentTokenAsync(HttpClient client, Guid agentId)
    {
        var req = new IssueTokenRequest("dev-stub", 1);
        HttpResponseMessage resp = await client.PostAsJsonAsync($"/agents/{agentId}/tokens", req);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<AgentTokenIssuanceResponse>())!;
    }

    /// <summary>
    /// Mint agent_token JWT（V1 stub：直接用 agent_token endpoint 返的 JwtToken）。
    /// </summary>
    private static string MintAgentJwt(AgentTokenIssuanceResponse issued) => issued.JwtToken;
}
