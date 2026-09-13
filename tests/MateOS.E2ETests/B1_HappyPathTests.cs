using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using MateOS.AgentStub;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Persistence;
using MateOS.Api.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MateOS.E2ETests;

/// <summary>
/// S1 DoD 收口：WorkItem 指派 → dispatch 实时推送 → agent-stub 行为 B1 → SUCCEEDED 闭环。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖 <c>detailed/10 §2</c> 行为矩阵的 <b>B1</b>（正常闭环）：
/// <list type="number">
///   <item>user 注册、建项目、注册 Agent、签 agent_token</item>
///   <item>agent-stub 用 agent_token 建 WS 会话（in-process 跑）</item>
///   <item>user 创 WorkItem、指派给 Agent</item>
///   <item>agent-stub 收到 <c>execution.dispatch</c> 推送帧</item>
///   <item>行为 B1：ack → 2 个 PROGRESS event → SUCCEEDED result</item>
///   <item>e2e 轮询 execution 终态，断言 SUCCEEDED + inbox 已 ack + events 已落库</item>
/// </list>
/// </para>
/// <para>
/// <b>同步策略</b>：in-process 跑整套，dispatch 推送 → stub 处理 → result 落库全程毫秒级；
/// 测试侧用 10s 窗口轮询 execution 状态而不是固定 sleep ——
/// 既避免脆弱时序，也跟生产代码会用的同步模式一致。
/// </para>
/// <para>
/// <b>运行</b>：需 <c>docker compose up -d</c> 让 <c>mateos-postgres</c> / <c>mateos-redis</c> 在跑。
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
public sealed class B1_HappyPathTests : IClassFixture<MateOsE2EApp>
{
    private readonly MateOsE2EApp _app;

    public B1_HappyPathTests(MateOsE2EApp app)
    {
        _app = app;
    }

    [Fact]
    public async Task B1_001_指派WorkItem后stub收到dispatch并完成SUCCEEDED闭环()
    {
        await _app.ResetAsync();

        // ── 1. 准备 user / project / agent ──
        HttpClient owner = _app.CreateClient();
        TokenResponse ownerToken = await E2ESetup.RegisterAsync(owner, "b1-owner@example.com");
        owner.Authorize(ownerToken.AccessToken);

        ProjectSummary project = await E2ESetup.CreateProjectForAsync(owner, "B1-Project");
        var credential = await E2ESetup.CreateCredentialAsync(owner);
        AgentSummary agent = await E2ESetup.CreateAgentAsync(owner, credential, "b1-stub-agent");

        // stub 用自己的 agent_token 建会话
        string agentToken = await E2ESetup.IssueAgentTokenJwtAsync(owner, agent.Id);

        // ── 2. 起 agent-stub（in-process）──
        // 2.1 走 WS：建反向 dispatch 接收通道
        WebSocket socket = await _app.Server
            .CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        // 2.2 走 HTTP：dispatch_ack / event / result 三个端点用 agent_token 调
        // WebApplicationFactory 的 server 是 in-memory 的，HttpClient.BaseAddress 指向它
        HttpClient agentHttp = _app.CreateClient();
        agentHttp.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", agentToken);

        StubResponder responder = new HttpStubResponder(
            agentHttp,
            agent.Id,
            baseUrl: agentHttp.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost");

        await using AgentStubClient stub = await AgentStubClient.AttachAsync(
            new InMemorySocketTransport(socket),
            agentToken,
            new B1HappyPathBehavior(responder));
        stub.Start();

        // hello_ack 已经在 AttachAsync 里收过，session 已就绪
        Assert.True(stub.IsReady);
        Assert.Equal(agent.Id, stub.AgentId);

        // ── 3. 创 WorkItem + 派给 Agent ──
        Guid itemId = await E2ESetup.CreateWorkItemAsync(
            owner, project.Id, "TASK", "B1 e2e 测试任务", "让 stub 跑个活");
        Guid executionId = await E2ESetup.AssignWorkItemAsync(owner, itemId, agent.Id, deadlineS: 300);

        // ── 4. 轮询 execution 终态（生产代码会用的同步模式） ──
        await WaitForTerminalAsync(executionId, TimeSpan.FromSeconds(10));

        // ── 5. 断言 DB 终态 ──
        await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
            .GetRequiredService<MateOSDbContext>();

        var execution = await db.AgentExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executionId);
        Assert.NotNull(execution);
        Assert.Equal("SUCCEEDED", execution!.Status);
        Assert.NotNull(execution.DispatchAckedAt);
        Assert.NotNull(execution.CompletedAt);

        var inbox = await db.AgentDispatchInbox
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ExecutionId == executionId);
        Assert.NotNull(inbox);
        Assert.NotNull(inbox!.AckedAt);

        ExecutionAttempt? attempt = await db.ExecutionAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.ExecutionId == executionId && a.AttemptNo == 1);
        Assert.NotNull(attempt);

        List<ExecutionEvent> attemptEvents = await db.ExecutionEvents
            .AsNoTracking()
            .Where(e => e.AttemptId == attempt!.Id)
            .OrderBy(e => e.Seq)
            .ToListAsync();

        // B1 行为上 2 条 PROGRESS：seq=1 + seq=2
        Assert.Equal(2, attemptEvents.Count);
        Assert.Equal(new long[] { 1, 2 }, attemptEvents.Select(e => e.Seq).ToArray());
        Assert.All(attemptEvents, e => Assert.Equal("PROGRESS", e.EventType));

        // cursor 推进到 2
        Assert.Equal(2, attempt!.LastPersistedSeq);

        // 不显式等 stub.Completion —— RunLoop 是 while-open 死循环，
        // 靠 `await using stub` 离开 scope 时 DisposeAsync 触发 close。
        // 业务断言已在上面完成，stub 退出与否不影响测试结论。
    }

    private async Task WaitForTerminalAsync(Guid executionId, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        string? lastSeenStatus = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
                .GetRequiredService<MateOSDbContext>();

            string? status = await db.AgentExecutions
                .AsNoTracking()
                .Where(e => e.Id == executionId)
                .Select(e => e.Status)
                .FirstOrDefaultAsync();

            if (status is "SUCCEEDED" or "FAILED" or "CANCELLED" or "TIMEOUT")
            {
                return;
            }

            lastSeenStatus = status;
            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"execution {executionId} 在 {timeout.TotalSeconds}s 内未进入终态，last status={lastSeenStatus ?? "(null)"}");
    }
}

/// <summary>把 <c>WebApplicationFactory.Server</c> 给的 in-memory socket 包装成 stub 的 transport 抽象。</summary>
internal sealed class InMemorySocketTransport : IStubTransport
{
    public WebSocket Socket { get; }
    public InMemorySocketTransport(WebSocket socket) => Socket = socket;
}
