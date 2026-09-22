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
// B7：重复 provider_event_id 去重
// =============================================================================

/// <summary>
/// S1 DoD 收口：B7 重复 provider_event_id 去重（detailed/10 §2 B7）。
/// </summary>
/// <remarks>
/// <para>
/// 行为矩阵 B7：客户端 SDK 异常（重发 / 双发）发同 <c>provider_event_id</c> 的
/// 两条 event + seq 不同 → Runtime <see cref="MateOS.Domain.Execution.AttemptEventCursor"/>
/// 必须把第二条判为 <c>DuplicateCause.ProviderEventIdReplay</c> 静默吞掉。
/// </para>
/// <para>
/// 测试断言：
/// <list type="bullet">
///   <item><c>execution_events</c> 表只有 1 行（seq=1 落库，seq=2 被去重）</item>
///   <item><c>execution_attempts.last_persisted_seq = 1</c>（不前进）</item>
///   <item><c>agent_dispatch_inbox.acked_at</c> 仍 IS NOT NULL（dispatch 已 ack）</item>
///   <item>execution 持续 RUNNING（stub 不上报 result，execution 不进 terminal）</item>
/// </list>
/// </para>
/// <para>
/// 本测试不直接验 Runtime 端的"同 dispatch 重收"路径（<c>AgentDispatchNotifier</c>
/// 自身幂等，<c>dispatch_ack</c> 是幂等 UPDATE）；e2e 焦点是 attempt_event 表的
/// provider_event_id UNIQUE 约束 + cursor 推进行为。
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
public sealed class B7_DuplicateProviderEventIdTests : IClassFixture<MateOsE2EApp>
{
    private readonly MateOsE2EApp _app;

    public B7_DuplicateProviderEventIdTests(MateOsE2EApp app) => _app = app;

    [Fact]
    public async Task B7_001_stub重发同provider_event_id_seq2被去重_attempt_events只1行()
    {
        await _app.ResetAsync();

        HttpClient owner = _app.CreateClient();
        TokenResponse ownerToken = await E2ESetup.RegisterAsync(owner, "b7-owner@example.com");
        owner.Authorize(ownerToken.AccessToken);

        ProjectSummary project = await E2ESetup.CreateProjectForAsync(owner, "B7-Project");
        var credential = await E2ESetup.CreateCredentialAsync(owner);
        AgentSummary agent = await E2ESetup.CreateAgentAsync(owner, credential, "b7-stub-agent");
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
            new B7DuplicateProviderEventIdBehavior(responder));
        stub.Start();

        Guid itemId = await E2ESetup.CreateWorkItemAsync(
            owner, project.Id, "TASK", "B7 dedup 测试", "stub 重发 provider_event_id");
        Guid executionId = await E2ESetup.AssignWorkItemAsync(owner, itemId, agent.Id, deadlineS: 600);

        // B7 stub 不上报 result；execution 永远 RUNNING —— 等 2s 让 stub 把 2 条
        // event 都发出去 + Runtime 落库 + 去重判定完。
        await Task.Delay(2000);

        await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
            .GetRequiredService<MateOSDbContext>();

        var execution = await db.AgentExecutions
            .AsNoTracking()
            .FirstAsync(e => e.Id == executionId);
        Assert.Equal("RUNNING", execution.Status);  // 没 terminal
        Assert.NotNull(execution.DispatchAckedAt);

        var attempt = await db.ExecutionAttempts
            .AsNoTracking()
            .FirstAsync(a => a.ExecutionId == executionId && a.AttemptNo == 1);

        // B7 关键断言：execution_events 只有 1 行（seq=1 落库，seq=2 被去重吞掉）
        List<ExecutionEvent> events = await db.ExecutionEvents
            .AsNoTracking()
            .Where(e => e.AttemptId == attempt.Id)
            .OrderBy(e => e.Seq)
            .ToListAsync();
        Assert.Single(events);
        Assert.Equal(1, events[0].Seq);
        Assert.Equal("PROGRESS", events[0].EventType);
        Assert.Equal("B7-fixed-provider-event-id", events[0].ProviderEventId);

        // cursor 推进到 1（seq=2 被 ProviderEventIdReplay 判 Duplicate，不前进）
        Assert.Equal(1, attempt.LastPersistedSeq);
    }
}

// =============================================================================
// B8：迟到 result → active_attempt_no 不匹配 → 丢弃
// =============================================================================

/// <summary>
/// S1 DoD 收口：B8 迟到结果丢弃（detailed/10 §2 B8）。
/// </summary>
/// <remarks>
/// <para>
/// 行为矩阵 B8：Runtime watchdog 重派 attempt 2 后，attempt 1 的迟到 result
/// 到达 → Runtime 端必须核对 <c>result.attempt_no == execution.active_attempt_no</c>
/// 否则丢弃（避免「过时的 SUCCEEDED 把当前 attempt 2 盖掉」）。
/// </para>
/// <para>
/// 时序：
/// <list type="number">
///   <item>stub 收 attempt 1 dispatch → ack → 发 seq=1 event（<b>不</b>发 result）</item>
///   <item>Runtime watchdog（ack_wait=2s, max_redispatch=2）探测 → 重派 attempt 2</item>
///   <item>stub 收 attempt 2 dispatch → ack → 发 seq=1 event（attempt 2 自己的）→ 发
///     <b>迟到</b> result（attempt_no=1, SUCCEEDED）→ Runtime 端丢弃 →
///     发正常 result（attempt_no=2, SUCCEEDED）</item>
///   <item>最终 Execution=SUCCEEDED（被正常 result 落库）</item>
/// </list>
/// </para>
/// <para>
/// 测试断言：
/// <list type="bullet">
///   <item><c>execution.status = SUCCEEDED</c>（由 attempt 2 的 result 决定）</item>
///   <item><c>dispatch_redispatch_count = 2</c>（说明 watchdog 重派触发过）</item>
///   <item><c>execution_events</c> 表 attempt 2 那次 dispatch 之后 seq=1 落库（不重复 attempt 1 的 seq=1）</item>
/// </list>
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
public sealed class B8_LateResultDiscardedTests : IClassFixture<MateOsE2EApp>
{
    private readonly MateOsE2EApp _app;

    public B8_LateResultDiscardedTests(MateOsE2EApp app) => _app = app;

    [Fact]
    public async Task B8_001_attempt1迟到result被丢弃_attempt2正常result落库_execution_SUCCEEDED()
    {
        await _app.ResetAsync();

        HttpClient owner = _app.CreateClient();
        TokenResponse ownerToken = await E2ESetup.RegisterAsync(owner, "b8-owner@example.com");
        owner.Authorize(ownerToken.AccessToken);

        ProjectSummary project = await E2ESetup.CreateProjectForAsync(owner, "B8-Project");
        var credential = await E2ESetup.CreateCredentialAsync(owner);
        AgentSummary agent = await E2ESetup.CreateAgentAsync(owner, credential, "b8-stub-agent");
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
            new B8LateResultBehavior(responder));
        stub.Start();

        Guid itemId = await E2ESetup.CreateWorkItemAsync(
            owner, project.Id, "TASK", "B8 late result 测试", "stub 不发 result 让 watchdog 重派");
        Guid executionId = await E2ESetup.AssignWorkItemAsync(owner, itemId, agent.Id, deadlineS: 600);

        // 等 execution 进 terminal —— B8 stub 流程：attempt 1 ack+event（不 result）→
        // watchdog 重派 attempt 2 → ack+event+迟到 result（被丢弃）+正常 result（落库）→
        // execution SUCCEEDED。最坏 ~10s（2s ack_wait × 2 + 重派 + ack）。
        await WaitForExecutionStatusAsync(executionId, "SUCCEEDED", TimeSpan.FromSeconds(20));

        await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
            .GetRequiredService<MateOSDbContext>();

        var execution = await db.AgentExecutions
            .AsNoTracking()
            .FirstAsync(e => e.Id == executionId);

        Assert.Equal("SUCCEEDED", execution.Status);
        Assert.Equal(2, execution.DispatchRedispatchCount);

        // attempt 2 应有 1 个 seq=1 event（attempt 1 的 seq=1 留在 attempt 1，
        // active_attempt=2 时上报的 event 进 attempt 2）
        var attempt2 = await db.ExecutionAttempts
            .AsNoTracking()
            .Where(a => a.ExecutionId == executionId && a.AttemptNo == 2)
            .FirstOrDefaultAsync();

        Assert.NotNull(attempt2);  // attempt 2 实际创建了（watchdog 重派时建新 attempt）

        // attempt 2 的 events：seq=1 一条
        List<ExecutionEvent> attempt2Events = await db.ExecutionEvents
            .AsNoTracking()
            .Where(e => e.AttemptId == attempt2!.Id)
            .OrderBy(e => e.Seq)
            .ToListAsync();
        Assert.Single(attempt2Events);
        Assert.Equal(1, attempt2Events[0].Seq);
    }

    private async Task WaitForExecutionStatusAsync(Guid executionId, string expected, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        string? lastSeen = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
                .GetRequiredService<MateOSDbContext>();

            lastSeen = await db.AgentExecutions
                .AsNoTracking()
                .Where(e => e.Id == executionId)
                .Select(e => e.Status)
                .FirstOrDefaultAsync();

            if (lastSeen == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"execution {executionId} 在 {timeout.TotalSeconds}s 内未进入 {expected}，last={lastSeen ?? "(null)"}");
    }
}
