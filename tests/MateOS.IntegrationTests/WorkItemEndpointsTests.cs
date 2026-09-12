using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Work;
using MateOS.Api.Workspace;
using MateOS.Domain.Work;
using Microsoft.Extensions.DependencyInjection;

namespace MateOS.IntegrationTests;

/// <summary>
/// S3 · E8 Work Management Core 端到端：WorkItem CRUD → Binding 路由 → 指派 → Execution 关联。
/// </summary>
/// <remarks>
/// <para>
/// 关键断言对应 E8 §7.1 的验收项：
/// F1 CRUD / F2 active binding 唯一 / F3 默认 builtin / F6 CREATE vs UPDATE 路由分离 /
/// F7 切 Provider 不丢历史 / F8 canonical category 派生 / F9 WorkItem ↔ Execution。
/// </para>
/// <para>
/// 契约形态：body 与 response 都是 snake_case（全局 JsonNamingPolicy），
/// query 用 snake_case 明确命名（<c>project_id</c> / <c>assignee_type</c>）。
/// </para>
/// </remarks>
public sealed class WorkItemEndpointsTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";
    private const string FakeProviderKey = "fake-provider";

    // ───────────────────────── E8-001 默认 builtin binding ─────────────────────────

    [Fact]
    public async Task E8_001_创建work_item应自动建builtin_binding且状态为OPEN()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "po@example.com");
        Authorize(client, token);

        ProjectSummary project = await CreateProjectForAsync(client, "Work");

        HttpResponseMessage create = await client.PostJsonAsync(
            $"/projects/{project.Id}/work-items",
            new CreateWorkItemRequest("TASK", "实现登录接口", "需要 JWT + refresh", null, null, null));

        await EnsureSuccessAsync(create);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using JsonDocument doc = await JsonDocument.ParseAsync(await create.Content.ReadAsStreamAsync());
        JsonElement item = doc.RootElement;

        Assert.Equal("OPEN", item.GetProperty("status").GetString());
        Assert.Equal("TODO", item.GetProperty("canonical_status_category").GetString());
        Assert.Equal("builtin", item.GetProperty("provider_key").GetString());
        Assert.NotEqual(Guid.Empty, item.GetProperty("binding_id").GetGuid());

        // F3：project 默认 binding = builtin，且它是 active 的
        List<JsonElement> bindings = await GetJsonArrayAsync(
            client, $"/projects/{project.Id}/work-management/bindings");

        JsonElement binding = Assert.Single(bindings);

        Assert.Equal("builtin", binding.GetProperty("provider_key").GetString());
        Assert.True(binding.GetProperty("is_active").GetBoolean());
        Assert.Equal(item.GetProperty("binding_id").GetGuid(), binding.GetProperty("id").GetGuid());

        // 状态机对 UI 可见（前端不该硬编码迁移表）
        Assert.Contains("IN_PROGRESS", item.GetProperty("allowed_transitions")
            .EnumerateArray().Select(x => x.GetString()));
    }

    // ───────────────────────── E8-002 列表 / 中文子串搜索 ─────────────────────────

    [Fact]
    public async Task E8_002_列表过滤与中文子串搜索应命中()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "po2@example.com");
        Authorize(client, token);

        ProjectSummary project = await CreateProjectForAsync(client, "Search");

        await CreateWorkItemAsync(client, project.Id, "TASK", "修复登录问题", null);
        await CreateWorkItemAsync(client, project.Id, "BUG", "新增报表导出", null);

        List<JsonElement> all = await GetJsonArrayAsync(client, $"/projects/{project.Id}/work-items");
        Assert.Equal(2, all.Count);

        // type 过滤
        List<JsonElement> bugs = await GetJsonArrayAsync(
            client, $"/projects/{project.Id}/work-items?type=BUG");
        Assert.Equal("新增报表导出", Assert.Single(bugs).GetProperty("title").GetString());

        // 中文子串：'simple' 分词不切 CJK，靠 ILIKE 兜底才搜得到
        List<JsonElement> hits = await GetJsonArrayAsync(
            client, $"/work-items/search?project_id={project.Id}&q=%E7%99%BB%E5%BD%95");

        Assert.Equal("修复登录问题", Assert.Single(hits).GetProperty("title").GetString());

        // 搜不到时返回空数组而不是报错
        List<JsonElement> miss = await GetJsonArrayAsync(
            client, $"/work-items/search?project_id={project.Id}&q=zzz-not-exist");
        Assert.Empty(miss);
    }

    // ───────────────────────── E8-003 只改标题不得重置状态 ─────────────────────────

    [Fact]
    public async Task E8_003_仅修改标题不应把状态重置()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "po3@example.com");
        Authorize(client, token);

        ProjectSummary project = await CreateProjectForAsync(client, "Patch");
        Guid itemId = await CreateWorkItemAsync(client, project.Id, "TASK", "原始标题", null);

        await EnsureSuccessAsync(await client.PostJsonAsync(
            $"/work-items/{itemId}/transition", new TransitionWorkItemRequest("IN_PROGRESS")));

        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/work-items/{itemId}", new UpdateWorkItemRequest("改过的标题", null, null, null, null, null, null, null));

        await EnsureSuccessAsync(patch);

        using JsonDocument doc = await JsonDocument.ParseAsync(await patch.Content.ReadAsStreamAsync());

        Assert.Equal("改过的标题", doc.RootElement.GetProperty("title").GetString());
        // 回归点：Provider 未裁决状态时必须保持 IN_PROGRESS，而不是掉回 OPEN
        Assert.Equal("IN_PROGRESS", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("IN_PROGRESS", doc.RootElement.GetProperty("canonical_status_category").GetString());
    }

    // ───────────────────────── E8-004 状态机 ─────────────────────────

    [Fact]
    public async Task E8_004_非法状态迁移应被409拒()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "po4@example.com");
        Authorize(client, token);

        ProjectSummary project = await CreateProjectForAsync(client, "State");
        Guid itemId = await CreateWorkItemAsync(client, project.Id, "TASK", "状态机用例", null);

        // OPEN → DONE 跳过 IN_PROGRESS
        HttpResponseMessage skip = await client.PostJsonAsync(
            $"/work-items/{itemId}/transition", new TransitionWorkItemRequest("DONE"));
        Assert.Equal(HttpStatusCode.Conflict, skip.StatusCode);

        // OPEN → IN_PROGRESS → IN_REVIEW
        await EnsureSuccessAsync(await client.PostJsonAsync(
            $"/work-items/{itemId}/transition", new TransitionWorkItemRequest("IN_PROGRESS")));

        HttpResponseMessage review = await client.PostJsonAsync(
            $"/work-items/{itemId}/transition", new TransitionWorkItemRequest("IN_REVIEW"));
        await EnsureSuccessAsync(review);

        using JsonDocument doc = await JsonDocument.ParseAsync(await review.Content.ReadAsStreamAsync());

        Assert.Equal("IN_REVIEW", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("IN_PROGRESS", doc.RootElement.GetProperty("canonical_status_category").GetString());

        // DONE 之后仍可回到 IN_REVIEW（WorkItem 允许回退，与 Execution 的不可逆不同）
        await EnsureSuccessAsync(await client.PostJsonAsync(
            $"/work-items/{itemId}/transition", new TransitionWorkItemRequest("DONE")));
        await EnsureSuccessAsync(await client.PostJsonAsync(
            $"/work-items/{itemId}/transition", new TransitionWorkItemRequest("IN_REVIEW")));
    }

    // ───────────────────────── E8-005 切 Provider 不丢历史 ─────────────────────────

    [Fact]
    public async Task E8_005_切换provider后历史work_item仍走原provider()
    {
        await fixture.ResetAsync();
        EnsureFakeProviderRegistered();

        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "po5@example.com");
        Authorize(client, token);

        ProjectSummary project = await CreateProjectForAsync(client, "Routing");

        // ① CREATE 走 active binding = builtin
        Guid legacyItemId = await CreateWorkItemAsync(client, project.Id, "TASK", "历史卡片", null);

        // ② 切换 active binding 到 fake provider
        HttpResponseMessage change = await client.PostJsonAsync(
            $"/projects/{project.Id}/work-management/bindings",
            new ChangeWorkProviderBindingRequest(FakeProviderKey, null, "PROJ", null));
        await EnsureSuccessAsync(change);

        using (JsonDocument changed = await JsonDocument.ParseAsync(await change.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(FakeProviderKey, changed.RootElement.GetProperty("provider_key").GetString());
            Assert.True(changed.RootElement.GetProperty("is_active").GetBoolean());
        }

        // ③ 新建的卡片走新 binding
        HttpResponseMessage created = await client.PostJsonAsync(
            $"/projects/{project.Id}/work-items",
            new CreateWorkItemRequest("STORY", "新卡片", null, null, null, null));
        await EnsureSuccessAsync(created);

        using (JsonDocument doc = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(FakeProviderKey, doc.RootElement.GetProperty("provider_key").GetString());
            // fake provider 的 OnCreate 会填 external_ref
            Assert.Equal("FAKE-1", doc.RootElement.GetProperty("external_ref").GetString());
        }

        // ④ 历史卡片仍引用旧 binding（F7）
        HttpResponseMessage legacy = await client.GetAsync($"/work-items/{legacyItemId}");
        await EnsureSuccessAsync(legacy);

        using (JsonDocument doc = await JsonDocument.ParseAsync(await legacy.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("builtin", doc.RootElement.GetProperty("provider_key").GetString());
        }

        // ⑤ UPDATE 历史卡片走 work_item.binding（builtin），不是 active binding（fake）
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            $"/work-items/{legacyItemId}",
            new UpdateWorkItemRequest("历史卡片（改名）", null, null, null, null, null, null, null));
        await EnsureSuccessAsync(patch);

        using (JsonDocument doc = await JsonDocument.ParseAsync(await patch.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("builtin", doc.RootElement.GetProperty("provider_key").GetString());
            // fake provider 的 OnUpdate 会写入 provider_status；builtin 不会
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("provider_status").ValueKind);
        }

        // ⑥ F2：project 下只有一条 active binding
        List<JsonElement> bindings = await GetJsonArrayAsync(
            client, $"/projects/{project.Id}/work-management/bindings");

        Assert.Equal(2, bindings.Count);
        Assert.Single(bindings.Where(b => b.GetProperty("is_active").GetBoolean()));
        Assert.Equal(FakeProviderKey,
            Assert.Single(bindings.Where(b => b.GetProperty("is_active").GetBoolean()))
                .GetProperty("provider_key").GetString());

        // ⑦ Provider 目录对前端可见
        List<JsonElement> providers = await GetJsonArrayAsync(client, "/work-management/providers");
        Assert.Contains(providers, p => p.GetProperty("provider_key").GetString() == FakeProviderKey);
        Assert.Contains(providers, p => p.GetProperty("provider_key").GetString() == "builtin");
    }

    // ───────────────────────── E8-006 未注册 provider ─────────────────────────

    [Fact]
    public async Task E8_006_未注册的provider_key应被拒()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "po6@example.com");
        Authorize(client, token);

        ProjectSummary project = await CreateProjectForAsync(client, "Unknown");

        HttpResponseMessage response = await client.PostJsonAsync(
            $"/projects/{project.Id}/work-management/bindings",
            new ChangeWorkProviderBindingRequest("no-such-provider", null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("UNKNOWN_WORK_PROVIDER", doc.RootElement.GetProperty("code").GetString());
    }

    // ───────────────────────── E8-007 指派 → Execution ─────────────────────────

    [Fact]
    public async Task E8_007_指派agent应创建execution并前推work_item()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "po7@example.com");
        Authorize(client, token);

        ProjectSummary project = await CreateProjectForAsync(client, "Delivery");
        AgentSummary agent = await CreateAgentAsync(client, "backend-agent");
        Guid itemId = await CreateWorkItemAsync(client, project.Id, "TASK", "给 agent 的活", "把接口实现出来");

        HttpResponseMessage assign = await client.PostJsonAsync(
            $"/work-items/{itemId}/assign", new AssignWorkItemRequest(agent.Id, 300));
        await EnsureSuccessAsync(assign);

        Guid executionId;

        using (JsonDocument doc = await JsonDocument.ParseAsync(await assign.Content.ReadAsStreamAsync()))
        {
            Assert.False(doc.RootElement.GetProperty("idempotent").GetBoolean());

            JsonElement workItem = doc.RootElement.GetProperty("work_item");
            Assert.Equal("IN_PROGRESS", workItem.GetProperty("status").GetString());
            Assert.Equal("IN_PROGRESS", workItem.GetProperty("canonical_status_category").GetString());
            Assert.Equal("AGENT", workItem.GetProperty("assignee_type").GetString());
            Assert.Equal(agent.Id, workItem.GetProperty("assignee_id").GetGuid());

            Assert.NotEqual(Guid.Empty, doc.RootElement.GetProperty("collaboration_request_id").GetGuid());
            executionId = doc.RootElement.GetProperty("execution_id").GetGuid();
            Assert.NotEqual(Guid.Empty, executionId);
        }

        // F9：WorkItem → Execution 正向可见
        List<JsonElement> executions = await GetJsonArrayAsync(client, $"/work-items/{itemId}/executions");
        JsonElement only = Assert.Single(executions);

        Assert.Equal(executionId, only.GetProperty("id").GetGuid());
        Assert.Equal(agent.Id, only.GetProperty("agent_id").GetGuid());
        Assert.Equal("PENDING", only.GetProperty("status").GetString());
        Assert.Equal("backend-agent", only.GetProperty("agent_name").GetString());

        // 幂等：重复指派不产生第二个 Execution
        HttpResponseMessage again = await client.PostJsonAsync(
            $"/work-items/{itemId}/assign", new AssignWorkItemRequest(agent.Id, 300));
        await EnsureSuccessAsync(again);

        using (JsonDocument doc = await JsonDocument.ParseAsync(await again.Content.ReadAsStreamAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("idempotent").GetBoolean());
            Assert.Equal(executionId, doc.RootElement.GetProperty("execution_id").GetGuid());
        }

        List<JsonElement> after = await GetJsonArrayAsync(client, $"/work-items/{itemId}/executions");
        Assert.Single(after);
    }

    // ───────────────────────── E8-008 评论 ─────────────────────────

    [Fact]
    public async Task E8_008_评论应可写入并读回()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "po8@example.com");
        Authorize(client, token);

        ProjectSummary project = await CreateProjectForAsync(client, "Comments");
        Guid itemId = await CreateWorkItemAsync(client, project.Id, "TASK", "带评论的卡片", null);

        HttpResponseMessage post = await client.PostJsonAsync(
            $"/work-items/{itemId}/comments", new AddWorkCommentRequest("这个优先级需要提高"));

        await EnsureSuccessAsync(post);

        List<JsonElement> comments = await GetJsonArrayAsync(client, $"/work-items/{itemId}/comments");
        JsonElement comment = Assert.Single(comments);

        Assert.Equal("HUMAN", comment.GetProperty("author_type").GetString());
        Assert.Equal(token.User.Id, comment.GetProperty("author_id").GetGuid());
        Assert.Equal("这个优先级需要提高", comment.GetProperty("body").GetString());

        // 空 body 应被拒
        HttpResponseMessage empty = await client.PostJsonAsync(
            $"/work-items/{itemId}/comments", new AddWorkCommentRequest("   "));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    // ───────────────────────── E8-009 关联 ─────────────────────────

    [Fact]
    public async Task E8_009_关联应双向写入且跨project被拒()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "po9@example.com");
        Authorize(client, token);

        ProjectSummary project = await CreateProjectForAsync(client, "Relations");
        Guid blocking = await CreateWorkItemAsync(client, project.Id, "TASK", "被阻塞的卡", null);
        Guid blocked = await CreateWorkItemAsync(client, project.Id, "TASK", "阻塞者", null);

        HttpResponseMessage relation = await client.PostJsonAsync(
            $"/work-items/{blocking}/relations",
            new CreateWorkRelationRequest(blocked, "BLOCKED_BY"));
        await EnsureSuccessAsync(relation);

        // 响应里应同时有正向与反向两行，两端视图才不矛盾
        using (JsonDocument doc = await JsonDocument.ParseAsync(await relation.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(2, doc.RootElement.GetArrayLength());
        }

        // 幂等：重复建立同一条关联不新增行
        HttpResponseMessage repeat = await client.PostJsonAsync(
            $"/work-items/{blocking}/relations",
            new CreateWorkRelationRequest(blocked, "BLOCKED_BY"));
        await EnsureSuccessAsync(repeat);

        using (JsonDocument doc = await JsonDocument.ParseAsync(await repeat.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("created").GetInt32());
        }

        // 自关联应被拒
        HttpResponseMessage self = await client.PostJsonAsync(
            $"/work-items/{blocking}/relations",
            new CreateWorkRelationRequest(blocking, "RELATES_TO"));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        // 跨 project 关联应被拒（否则按 project 过滤的列表会出现幽灵依赖）
        ProjectSummary other = await CreateProjectForAsync(client, "Other");
        Guid outsider = await CreateWorkItemAsync(client, other.Id, "TASK", "别家的卡", null);

        HttpResponseMessage cross = await client.PostJsonAsync(
            $"/work-items/{blocking}/relations",
            new CreateWorkRelationRequest(outsider, "RELATES_TO"));
        Assert.Equal(HttpStatusCode.BadRequest, cross.StatusCode);
    }

    // ───────────────────────── E8-010 非成员 403 ─────────────────────────

    [Fact]
    public async Task E8_010_非项目成员访问work_item应403()
    {
        await fixture.ResetAsync();
        HttpClient owner = fixture.CreateClient();
        TokenResponse ownerToken = await RegisterAsync(owner, "owner@example.com");
        Authorize(owner, ownerToken);

        ProjectSummary project = await CreateProjectForAsync(owner, "Private");
        Guid itemId = await CreateWorkItemAsync(owner, project.Id, "TASK", "内部卡片", null);

        HttpClient stranger = fixture.CreateClient();
        TokenResponse strangerToken = await RegisterAsync(stranger, "stranger@example.com");
        Authorize(stranger, strangerToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"/work-items/{itemId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await stranger.GetAsync($"/projects/{project.Id}/work-items")).StatusCode);

        // 非 owner 不能切 Provider
        HttpResponseMessage bind = await stranger.PostJsonAsync(
            $"/projects/{project.Id}/work-management/bindings",
            new ChangeWorkProviderBindingRequest("builtin", null, null, null));
        Assert.True(bind.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
    }

    // ───────────────────────── helpers ─────────────────────────

    /// <summary>
    /// 注册一个 Core 完全未知的 Provider（E8 §7.1 F4/F5：开放扩展不需要改 Core 文件）。
    /// </summary>
    /// <remarks>
    /// Registry 是宿主 singleton，同一 <see cref="MateOsApiFixture"/> 内的测试共享；
    /// 重复注册会抛错，因此先探测再注册。
    /// </remarks>
    private void EnsureFakeProviderRegistered()
    {
        WorkManagementProviderRegistry registry =
            fixture.Services.GetRequiredService<WorkManagementProviderRegistry>();

        if (!registry.TryGet(FakeProviderKey, out _))
        {
            registry.Register(new FakeWorkManagementProvider());
        }
    }

    private sealed class FakeWorkManagementProvider : IWorkManagementProvider
    {
        public string Key => FakeProviderKey;

        public ProviderMetadata Metadata { get; } = new(
            FakeProviderKey,
            "Fake Work Provider",
            new ProviderCapabilities(SupportsExternalRef: true, SupportsStatusMapping: true, SupportsWebhooks: true));

        public IReadOnlyList<ProviderStatus> ListStatuses(WorkItemBindingContext binding) =>
        [
            new(WorkItemStatusMap.OpenValue, "TODO"),
            new(WorkItemStatusMap.InProgressValue, "IN_PROGRESS"),
        ];

        public string? ValidateBinding(WorkItemBindingContext binding) =>
            string.IsNullOrEmpty(binding.ExternalProjectRef)
                ? "fake provider 需要 external_project_ref"
                : null;

        public ProviderWriteOutcome OnCreate(ProviderWorkItemInput input, WorkItemBindingContext binding) =>
            new(WorkItemStatus.OPEN, ExternalRef: "FAKE-1", ExternalUrl: "https://fake.local/FAKE-1");

        public ProviderWriteOutcome OnUpdate(
            ProviderWorkItemRef reference,
            ProviderWorkItemChanges changes,
            WorkItemBindingContext binding) =>
            new(changes.Status, ProviderStatus: "Fake In Progress");
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

    private static async Task<List<JsonElement>> GetJsonArrayAsync(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url);
        await EnsureSuccessAsync(response);

        using JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        return doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
    }

    private static void Authorize(HttpClient client, TokenResponse token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

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

    private static async Task<ProjectSummary> CreateProjectForAsync(HttpClient client, string name)
    {
        OrganizationSummary org = await PostAsync<OrganizationSummary>(
            client, "/orgs", new CreateOrganizationRequest($"{name}-org"));

        TeamSummary team = await PostAsync<TeamSummary>(
            client, "/teams", new CreateTeamRequest(org.Id, $"{name}-team"));

        return await PostAsync<ProjectSummary>(
            client, "/projects", new CreateProjectRequest(team.Id, name, null, null));
    }

    private static async Task<AgentSummary> CreateAgentAsync(HttpClient client, string name)
    {
        CredentialSummary credential = await PostAsync<CredentialSummary>(
            client, "/credentials",
            new CreateCredentialRequest("openai", "test", $"sk-{Guid.NewGuid():N}", null));

        return await PostAsync<AgentSummary>(
            client, "/agents",
            new CreateAgentRequest(
                Name: name,
                Role: "coder",
                Capabilities: new[] { "coding" },
                CredentialId: credential.Id,
                MaxConcurrency: 1,
                DailyLimitUsd: null,
                MonthlyBudgetUsd: null));
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        HttpResponseMessage response = await client.PostJsonAsync(url, body);

        await EnsureSuccessAsync(response);

        return (await response.Content.ReadWireAsync<T>())!;
    }
}
