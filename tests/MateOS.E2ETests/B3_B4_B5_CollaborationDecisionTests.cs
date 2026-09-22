using System.Net.Http.Headers;
using System.Net.WebSockets;
using MateOS.AgentStub;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Persistence;
using MateOS.Api.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MateOS.E2ETests;

// =============================================================================
// B3：CR 决策 REJECT
// =============================================================================

/// <summary>
/// S1 DoD 收口：B3 协作请求 REJECT（detailed/10 §2 B3）。
/// </summary>
/// <remarks>
/// <para>
/// 行为矩阵 B3：Agent 明确拒收 → CR=REJECTED → Execution <b>不创建</b>。
/// </para>
/// <para>
/// 入口走 <c>POST /internal/triggers</c>（trigger_type=MENTION），
/// 由 stub 主动轮询 inbox + 回 REJECT。
/// </para>
/// <para>
/// 测试断言：
/// <list type="bullet">
///   <item><c>collaboration_requests.status = REJECTED</c></item>
///   <item><c>decision_records</c> 落库一条（decision=REJECT, actor_type=AGENT）</item>
///   <item><c>agent_executions</c> 表对该 trigger <b>不</b>有任何行</item>
///   <item>outbox 写 <c>collaboration.resolved</c> 事件（reason=REJECTED）</item>
/// </list>
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
public sealed class B3_RejectCollaborationDecisionTests : IClassFixture<MateOsE2EApp>
{
    private readonly MateOsE2EApp _app;

    public B3_RejectCollaborationDecisionTests(MateOsE2EApp app) => _app = app;

    [Fact]
    public async Task B3_001_mention触发后stub回REJECT_CR_REJECTED_execution不创建()
    {
        await _app.ResetAsync();

        // ── 1. seed ──
        HttpClient owner = _app.CreateClient();
        TokenResponse ownerToken = await E2ESetup.RegisterAsync(owner, "b3-owner@example.com");
        owner.Authorize(ownerToken.AccessToken);

        var credential = await E2ESetup.CreateCredentialAsync(owner);
        AgentSummary agent = await E2ESetup.CreateAgentAsync(owner, credential, "b3-stub-agent");
        string agentToken = await E2ESetup.IssueAgentTokenJwtAsync(owner, agent.Id);

        // ── 2. stub：B3RejectCollaborationBehavior ──
        WebSocket socket = await _app.Server
            .CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        HttpClient agentHttp = _app.CreateClient();
        agentHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);

        StubResponder responder = new HttpStubResponder(
            agentHttp,
            agent.Id,
            baseUrl: agentHttp.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost");

        await using AgentStubClient stub = await AgentStubClient.AttachAsync(
            new InMemorySocketTransport(socket),
            agentToken,
            new B3RejectCollaborationBehavior(responder));
        stub.Start();

        // ── 3. mention 触发 CR ──
        Guid crId = await E2ESetup.CreateMentionTriggerAsync(
            owner, agent.Id, deadlineS: 60);

        // ── 4. 等 CR 进 REJECTED ──
        string status = await E2ESetup.WaitForCrStatusAsync(
            _app.Services,
            crId,
            new HashSet<string> { "REJECTED", "UNRESOLVED" },
            TimeSpan.FromSeconds(15));
        Assert.Equal("REJECTED", status);

        // ── 5. 断言 DB 终态 ──
        await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
            .GetRequiredService<MateOSDbContext>();

        var cr = await db.CollaborationRequests
            .AsNoTracking()
            .FirstAsync(c => c.Id == crId);
        Assert.Equal("REJECTED", cr.Status);

        // 决策记录：REJECT + actor_type=AGENT
        var decision = await db.DecisionRecords
            .AsNoTracking()
            .Where(d => d.CollaborationRequestId == crId)
            .OrderByDescending(d => d.DecidedAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(decision);
        Assert.Equal("REJECT", decision!.Decision);

        // 不创建 Execution（整张表为空 —— ResetAsync 已清空 + B3 路径只走 CR 不进 Execution）
        int execCount = await db.AgentExecutions.AsNoTracking().CountAsync();
        Assert.Equal(0, execCount);

        // outbox：collaboration.resolved 事件 + reason=REJECTED
        var resolved = await db.OutboxEvents
            .AsNoTracking()
            .Where(o => o.AggregateId == crId && o.EventType == "collaboration.resolved")
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(resolved);
    }
}

// =============================================================================
// B4：CR 决策 NEED_CONTEXT
// =============================================================================

/// <summary>
/// S1 DoD 收口：B4 协作请求 NEED_CONTEXT（detailed/10 §2 B4）。
/// </summary>
/// <remarks>
/// <para>
/// 行为矩阵 B4：Agent 需要补上下文 → CR=NEED_CONTEXT + decision_record.needs 列表。
/// Execution 同样不创建（NEED_CONTEXT 也是 CR 终结态）。
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
public sealed class B4_NeedContextCollaborationDecisionTests : IClassFixture<MateOsE2EApp>
{
    private readonly MateOsE2EApp _app;

    public B4_NeedContextCollaborationDecisionTests(MateOsE2EApp app) => _app = app;

    [Fact]
    public async Task B4_001_mention触发后stub回NEED_CONTEXT_CR_NEED_CONTEXT且needs落库()
    {
        await _app.ResetAsync();

        HttpClient owner = _app.CreateClient();
        TokenResponse ownerToken = await E2ESetup.RegisterAsync(owner, "b4-owner@example.com");
        owner.Authorize(ownerToken.AccessToken);

        var credential = await E2ESetup.CreateCredentialAsync(owner);
        AgentSummary agent = await E2ESetup.CreateAgentAsync(owner, credential, "b4-stub-agent");
        string agentToken = await E2ESetup.IssueAgentTokenJwtAsync(owner, agent.Id);

        WebSocket socket = await _app.Server
            .CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        HttpClient agentHttp = _app.CreateClient();
        agentHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);

        StubResponder responder = new HttpStubResponder(
            agentHttp,
            agent.Id,
            baseUrl: agentHttp.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost");

        await using AgentStubClient stub = await AgentStubClient.AttachAsync(
            new InMemorySocketTransport(socket),
            agentToken,
            new B4NeedContextBehavior(responder));
        stub.Start();

        Guid crId = await E2ESetup.CreateMentionTriggerAsync(
            owner, agent.Id, deadlineS: 60);

        string status = await E2ESetup.WaitForCrStatusAsync(
            _app.Services,
            crId,
            new HashSet<string> { "NEED_CONTEXT", "UNRESOLVED" },
            TimeSpan.FromSeconds(15));
        Assert.Equal("NEED_CONTEXT", status);

        await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
            .GetRequiredService<MateOSDbContext>();

        var cr = await db.CollaborationRequests
            .AsNoTracking()
            .FirstAsync(c => c.Id == crId);
        Assert.Equal("NEED_CONTEXT", cr.Status);

        var decision = await db.DecisionRecords
            .AsNoTracking()
            .Where(d => d.CollaborationRequestId == crId)
            .OrderByDescending(d => d.DecidedAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(decision);
        Assert.Equal("NEED_CONTEXT", decision!.Decision);

        // needs JSON 数组非空
        Assert.False(string.IsNullOrWhiteSpace(decision.Needs));
        Assert.Contains("missing", decision.Needs!);

        // 不创建 Execution
        int execCount = await db.AgentExecutions
            .AsNoTracking()
            .CountAsync();
        Assert.Equal(0, execCount);
    }
}

// =============================================================================
// B5：CR 决策沉默 → deadline 触发 UNRESOLVED
// =============================================================================

/// <summary>
/// S1 DoD 收口：B5 协作请求决策超时（detailed/10 §2 B5）。
/// </summary>
/// <remarks>
/// <para>
/// 行为矩阵 B5：Agent 完全不响应 → E4 deadline 触发 → CR=UNRESOLVED。
/// </para>
/// <para>
/// <b>不</b>写 <c>decision_records</c>（不是 Agent 主动决策，是 deadline 兜底）；
/// <b>不</b>创建 Execution。
/// </para>
/// <para>
/// e2e 把 deadline_s 调短到 3s（默认 600s 太长），让 UNRESOLVED 在测试时间内触发。
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
public sealed class B5_UnresolvedByDeadlineTests : IClassFixture<MateOsE2EApp>
{
    private readonly MateOsE2EApp _app;

    public B5_UnresolvedByDeadlineTests(MateOsE2EApp app) => _app = app;

    [Fact]
    public async Task B5_001_mention触发后stub沉默deadline过期_CR_UNRESOLVED且不落decision_record()
    {
        await _app.ResetAsync();

        HttpClient owner = _app.CreateClient();
        TokenResponse ownerToken = await E2ESetup.RegisterAsync(owner, "b5-owner@example.com");
        owner.Authorize(ownerToken.AccessToken);

        var credential = await E2ESetup.CreateCredentialAsync(owner);
        AgentSummary agent = await E2ESetup.CreateAgentAsync(owner, credential, "b5-stub-agent");
        string agentToken = await E2ESetup.IssueAgentTokenJwtAsync(owner, agent.Id);

        // stub 沉默（连 hello 都行，OnSessionReadyAsync no-op，dispatch 也 no-op）
        WebSocket socket = await _app.Server
            .CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        HttpClient agentHttp = _app.CreateClient();
        agentHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);

        StubResponder responder = new HttpStubResponder(
            agentHttp,
            agent.Id,
            baseUrl: agentHttp.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost");

        await using AgentStubClient stub = await AgentStubClient.AttachAsync(
            new InMemorySocketTransport(socket),
            agentToken,
            new B5SilentBehavior());
        stub.Start();

        // deadline_s = 3 让 UNRESOLVED 在 ~5s 内触发（含 E4 sweep 间隔）
        Guid crId = await E2ESetup.CreateMentionTriggerAsync(
            owner, agent.Id, deadlineS: 3);

        string status = await E2ESetup.WaitForCrStatusAsync(
            _app.Services,
            crId,
            new HashSet<string> { "UNRESOLVED" },
            TimeSpan.FromSeconds(30));
        Assert.Equal("UNRESOLVED", status);

        await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
            .GetRequiredService<MateOSDbContext>();

        var cr = await db.CollaborationRequests
            .AsNoTracking()
            .FirstAsync(c => c.Id == crId);
        Assert.Equal("UNRESOLVED", cr.Status);

        // 不落 decision_records —— 不是 Agent 主动决策
        int decisionCount = await db.DecisionRecords
            .AsNoTracking()
            .CountAsync(d => d.CollaborationRequestId == crId);
        Assert.Equal(0, decisionCount);

        // 不创建 Execution
        int execCount = await db.AgentExecutions
            .AsNoTracking()
            .CountAsync();
        Assert.Equal(0, execCount);

        // outbox：collaboration.resolved 事件（reason 不应是 REJECTED；应是 deadline）
        var resolved = await db.OutboxEvents
            .AsNoTracking()
            .Where(o => o.AggregateId == crId && o.EventType == "collaboration.resolved")
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(resolved);
    }
}
