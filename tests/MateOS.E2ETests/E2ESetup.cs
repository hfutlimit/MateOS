using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Routing;
using MateOS.Api.Work;
using MateOS.Api.Workspace;
using MateOS.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MateOS.E2ETests;

/// <summary>
/// e2e 测试的 seed helpers：注册用户、建组织/团队/项目、注册 Agent、签 agent_token、创 WorkItem、指派。
/// </summary>
/// <remarks>
/// 跟 <c>tests/MateOS.IntegrationTests/AgentProtocolEndpointsTests.cs</c> 同源不同实例。
/// </remarks>
internal static class E2ESetup
{
    private const string Password = "P@ssw0rd-1234";

    public static async Task<TokenResponse> RegisterAsync(HttpClient client, string email)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            "/auth/register", new RegisterRequest(email, Password, email.Split('@')[0]));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadWireAsync<TokenResponse>())!;
    }

    public static async Task<OrganizationSummary> CreateOrganizationAsync(HttpClient client, string name)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            "/orgs", new CreateOrganizationRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadWireAsync<OrganizationSummary>())!;
    }

    public static async Task<TeamSummary> CreateTeamAsync(HttpClient client, Guid orgId, string name)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            "/teams", new CreateTeamRequest(orgId, name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadWireAsync<TeamSummary>())!;
    }

    public static async Task<ProjectSummary> CreateProjectForAsync(HttpClient client, string name)
    {
        OrganizationSummary org = await CreateOrganizationAsync(client, $"{name}-org");
        TeamSummary team = await CreateTeamAsync(client, org.Id, $"{name}-team");

        HttpResponseMessage response = await client.PostJsonAsync(
            "/projects", new CreateProjectRequest(team.Id, name, null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadWireAsync<ProjectSummary>())!;
    }

    public static async Task<CredentialSummary> CreateCredentialAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            "/credentials", new CreateCredentialRequest("openai", "e2e-stub", $"sk-stub-{Guid.NewGuid():N}", null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadWireAsync<CredentialSummary>())!;
    }

    public static async Task<AgentSummary> CreateAgentAsync(HttpClient client, CredentialSummary credential, string name)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            "/agents",
            new CreateAgentRequest(
                Name: name,
                Role: "test",
                Capabilities: new[] { "coding" },
                CredentialId: credential.Id,
                MaxConcurrency: null,
                DailyLimitUsd: null,
                MonthlyBudgetUsd: null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadWireAsync<AgentSummary>())!;
    }

    public static async Task<AgentTokenIssuanceResponse> IssueAgentTokenAsync(HttpClient owner, Guid agentId, string label = "e2e-stub")
    {
        HttpResponseMessage resp = await owner.PostJsonAsync(
            $"/agents/{agentId}/tokens", new IssueTokenRequest(label, 1));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadWireAsync<AgentTokenIssuanceResponse>())!;
    }

    public static async Task<string> IssueAgentTokenJwtAsync(HttpClient owner, Guid agentId, string label = "e2e-stub")
    {
        AgentTokenIssuanceResponse issued = await IssueAgentTokenAsync(owner, agentId, label);
        return issued.JwtToken;
    }

    public static async Task<Guid> CreateWorkItemAsync(HttpClient owner, Guid projectId, string type, string title, string? description)
    {
        HttpResponseMessage resp = await owner.PostJsonAsync(
            $"/projects/{projectId}/work-items",
            new CreateWorkItemRequest(
                Type: type,
                Title: title,
                Description: description,
                AssigneeType: null,
                AssigneeId: null,
                DueAt: null));
        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    public static async Task<Guid> AssignWorkItemAsync(HttpClient owner, Guid itemId, Guid agentId, int deadlineS = 300)
    {
        HttpResponseMessage resp = await owner.PostJsonAsync(
            $"/work-items/{itemId}/assign", new AssignWorkItemRequest(agentId, deadlineS));
        resp.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("execution_id").GetGuid();
    }

    /// <summary>
    /// 通过 <c>POST /internal/triggers</c> 触发一条 MENTION CR，绕过 channel
    /// message 的 @mention 解析（详细行为矩阵 B3/B4/B5 入口）。
    /// </summary>
    /// <returns>collaboration_request id。</returns>
    public static async Task<Guid> CreateMentionTriggerAsync(
        HttpClient owner,
        Guid agentId,
        int deadlineS = 300,
        string? idempotencyKey = null,
        IReadOnlyList<string>? capabilities = null)
    {
        string key = idempotencyKey ?? $"e2e-mention-{Guid.NewGuid():N}";

        HttpResponseMessage resp = await owner.PostJsonAsync(
            "/internal/triggers",
            new
            {
                trigger_type = "MENTION",
                trigger_ref = JsonDocument.Parse($$"""
                  {
                    "channel_id": "{{Guid.NewGuid()}}",
                    "message_seq": 1,
                    "summary": "e2e mention trigger"
                  }
                  """).RootElement,
                from_actor_type = "USER",
                from_actor_id = Guid.NewGuid(),
                target_agent_id = agentId,
                required_capabilities = capabilities ?? new[] { "coding" },
                context_refs = JsonDocument.Parse("{}").RootElement,
                deadline_s = deadlineS,
                idempotency_key = key,
            });

        resp.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("collaboration_request").GetProperty("id").GetGuid();
    }

    /// <summary>轮询 collaboration_request 直到 status 进入指定集合之一。</summary>
    public static async Task<string> WaitForCrStatusAsync(
        IServiceProvider services,
        Guid crId,
        IReadOnlySet<string> targets,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        string? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = services.CreateAsyncScope();
            MateOSDbContext db = scope.ServiceProvider.GetRequiredService<MateOSDbContext>();
            last = await db.CollaborationRequests
                .AsNoTracking()
                .Where(cr => cr.Id == crId)
                .Select(cr => cr.Status)
                .FirstOrDefaultAsync();

            if (last is not null && targets.Contains(last))
            {
                return last;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"CR {crId} 在 {timeout.TotalSeconds}s 内未进入 {string.Join("/", targets)}，last={last ?? "(null)"}");
    }

    public static HttpClient Authorize(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
