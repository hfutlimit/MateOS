using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Routing;
using MateOS.Api.Workspace;

namespace MateOS.IntegrationTests;

/// <summary>
/// E4 §3 + §5 端到端：写 trigger → 创建 CR → decision=ACCEPT → E4 同步调 E7 创建 Execution。
/// </summary>
public sealed class RoutingEndpointsTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── E4-001 ─────────────────────────

    [Fact]
    public async Task E4_001_创建trigger应生成CR且idempotency()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "alice@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        var req = new CreateTriggerRequest(
            TriggerType: "MENTION",
            TriggerRef: JsonDocument.Parse("""{"channel_id":"00000000-0000-0000-0000-000000000000","message_seq":1}""").RootElement,
            FromActorType: "USER",
            FromActorId: userToken.User.Id,
            RequestKind: null,
            TargetAgentId: null,
            RequiredCapabilities: null,
            ContextRefs: null,
            DeadlineS: 60,
            IdempotencyKey: "idem-1");

        HttpResponseMessage r1 = await userClient.PostJsonAsync("/internal/triggers", req);
        await EnsureSuccessAsync(r1);
        using JsonDocument d1 = await JsonDocument.ParseAsync(await r1.Content.ReadAsStreamAsync());
        Assert.False(d1.RootElement.GetProperty("idempotent").GetBoolean());
        Guid crId = d1.RootElement.GetProperty("collaboration_request").GetProperty("id").GetGuid();
        Assert.Equal("PENDING", d1.RootElement.GetProperty("collaboration_request").GetProperty("status").GetString());

        // 同 idempotency_key 重发
        HttpResponseMessage r2 = await userClient.PostJsonAsync("/internal/triggers", req);
        await EnsureSuccessAsync(r2);
        using JsonDocument d2 = await JsonDocument.ParseAsync(await r2.Content.ReadAsStreamAsync());
        Assert.True(d2.RootElement.GetProperty("idempotent").GetBoolean());
        Assert.Equal(crId, d2.RootElement.GetProperty("collaboration_request").GetProperty("id").GetGuid());
    }

    // ───────────────────────── E4-002 ACCEPT 端到端 ─────────────────────────

    [Fact]
    public async Task E4_002_ACCEPT决策应调E7创建Execution()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "bob@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        // 准备：agent + dispatch
        CredentialSummary credential = await CreateCredentialAsync(userClient);
        AgentSummary agent = await CreateAgentAsync(userClient, credential.Id, "bob-coder");

        // 创建 CR（target_agent_id 指定）
        var createReq = new CreateTriggerRequest(
            TriggerType: "MENTION",
            TriggerRef: JsonDocument.Parse("""{"channel_id":"00000000-0000-0000-0000-000000000000","message_seq":1}""").RootElement,
            FromActorType: "USER",
            FromActorId: userToken.User.Id,
            RequestKind: "MESSAGE_RESPONSE",
            TargetAgentId: agent.Id,
            RequiredCapabilities: new[] { "coding" },
            ContextRefs: JsonDocument.Parse("""{"project_id":"00000000-0000-0000-0000-000000000000"}""").RootElement,
            DeadlineS: 60,
            IdempotencyKey: "accept-1");
        HttpResponseMessage createResp = await userClient.PostJsonAsync("/internal/triggers", createReq);
        await EnsureSuccessAsync(createResp);
        using JsonDocument createDoc = await JsonDocument.ParseAsync(await createResp.Content.ReadAsStreamAsync());
        Guid crId = createDoc.RootElement.GetProperty("collaboration_request").GetProperty("id").GetGuid();

        // 决策 ACCEPT
        var decisionReq = new CreateDecisionRequest(
            Decision: "ACCEPT",
            Reason: "capability match",
            Needs: null,
            AnalysisCapability: true,
            AnalysisContextScore: 88,
            AnalysisPermission: true);
        HttpResponseMessage decisionResp = await userClient.PostJsonAsync(
            $"/internal/collaboration-requests/{crId}/decision", decisionReq);
        await EnsureSuccessAsync(decisionResp);
        using JsonDocument decisionDoc = await JsonDocument.ParseAsync(await decisionResp.Content.ReadAsStreamAsync());

        Assert.Equal("ACCEPTED",
            decisionDoc.RootElement.GetProperty("collaboration_request").GetProperty("status").GetString());
        Guid executionId = decisionDoc.RootElement.GetProperty("collaboration_request")
            .GetProperty("target_execution_id").GetGuid();
        Assert.NotEqual(Guid.Empty, executionId);

        // 应同时有 decision_record
        Assert.Equal("ACCEPT",
            decisionDoc.RootElement.GetProperty("decision").GetProperty("decision").GetString());
    }

    // ───────────────────────── E4-003 REJECT ─────────────────────────

    [Fact]
    public async Task E4_003_REJECT决策应不创建Execution()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "carol@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        var createReq = new CreateTriggerRequest(
            TriggerType: "MENTION",
            TriggerRef: JsonDocument.Parse("""{"k":1}""").RootElement,
            FromActorType: "USER",
            FromActorId: userToken.User.Id,
            RequestKind: null, TargetAgentId: null, RequiredCapabilities: null, ContextRefs: null,
            DeadlineS: null, IdempotencyKey: "reject-1");
        HttpResponseMessage createResp = await userClient.PostJsonAsync("/internal/triggers", createReq);
        await EnsureSuccessAsync(createResp);
        using JsonDocument d = await JsonDocument.ParseAsync(await createResp.Content.ReadAsStreamAsync());
        Guid crId = d.RootElement.GetProperty("collaboration_request").GetProperty("id").GetGuid();

        var rejectReq = new CreateDecisionRequest(
            Decision: "REJECT", Reason: "out of scope", Needs: null,
            AnalysisCapability: null, AnalysisContextScore: 30, AnalysisPermission: false);
        HttpResponseMessage rejectResp = await userClient.PostJsonAsync(
            $"/internal/collaboration-requests/{crId}/decision", rejectReq);
        await EnsureSuccessAsync(rejectResp);
        using JsonDocument rd = await JsonDocument.ParseAsync(await rejectResp.Content.ReadAsStreamAsync());

        Assert.Equal("REJECTED", rd.RootElement.GetProperty("collaboration_request").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, rd.RootElement.GetProperty("collaboration_request").GetProperty("target_execution_id").ValueKind);
    }

    // ───────────────────────── E4-004 终态再决策应被拒 ─────────────────────────

    [Fact]
    public async Task E4_004_ACCEPTED后再次决策应被409拒()
    {
        await fixture.ResetAsync();
        HttpClient userClient = fixture.CreateClient();
        TokenResponse userToken = await RegisterAsync(userClient, "dave@example.com");
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

        CredentialSummary credential = await CreateCredentialAsync(userClient);
        AgentSummary agent = await CreateAgentAsync(userClient, credential.Id, "dave-coder");

        var createReq = new CreateTriggerRequest(
            TriggerType: "API",
            TriggerRef: JsonDocument.Parse("""{"k":1}""").RootElement,
            FromActorType: "USER", FromActorId: userToken.User.Id,
            RequestKind: "API_CALL", TargetAgentId: agent.Id,
            RequiredCapabilities: null, ContextRefs: null, DeadlineS: null,
            IdempotencyKey: "terminal-1");
        HttpResponseMessage createResp = await userClient.PostJsonAsync("/internal/triggers", createReq);
        await EnsureSuccessAsync(createResp);
        using JsonDocument d = await JsonDocument.ParseAsync(await createResp.Content.ReadAsStreamAsync());
        Guid crId = d.RootElement.GetProperty("collaboration_request").GetProperty("id").GetGuid();

        var acceptReq = new CreateDecisionRequest(
            Decision: "ACCEPT", Reason: null, Needs: null,
            AnalysisCapability: true, AnalysisContextScore: 90, AnalysisPermission: true);
        await EnsureSuccessAsync(await userClient.PostJsonAsync(
            $"/internal/collaboration-requests/{crId}/decision", acceptReq));

        // 重复决策
        var rejectReq = new CreateDecisionRequest(
            Decision: "REJECT", Reason: "should fail", Needs: null,
            AnalysisCapability: null, AnalysisContextScore: null, AnalysisPermission: null);
        HttpResponseMessage second = await userClient.PostJsonAsync(
            $"/internal/collaboration-requests/{crId}/decision", rejectReq);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
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
            Name: name, Role: "coder",
            Capabilities: new[] { "coding" },
            CredentialId: credentialId,
            MaxConcurrency: 1, DailyLimitUsd: null, MonthlyBudgetUsd: null);
        HttpResponseMessage resp = await client.PostJsonAsync("/agents", req);
        await EnsureSuccessAsync(resp);
        return (await resp.Content.ReadWireAsync<AgentSummary>())!;
    }
}
