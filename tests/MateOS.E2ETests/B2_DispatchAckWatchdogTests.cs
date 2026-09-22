using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using MateOS.AgentStub;
using MateOS.Api.Agents;
using MateOS.Api.Auth;
using MateOS.Api.Persistence;
using MateOS.Api.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MateOS.E2ETests;

/// <summary>
/// S1 DoD 收口：B2 dispatch_ack watchdog（detailed/10 §2 B2）。
/// </summary>
/// <remarks>
/// <para>
/// 行为矩阵 B2：Agent 收 dispatch 但<b>不</b>回 ACK → Runtime watchdog 探测后重派
/// 同一 (execution_id, attempt_no) → 超阈值 → Execution FAILED，reason=
/// "dispatch_ack_watchdog_giveup"。
/// </para>
/// <para>
/// 覆盖场景：
/// <list type="bullet">
///   <item><b>B2-1：送达失败 → 重派 ×N → 超阈值 FAILED</b> —— stub 全程不 ack，
///     watchdog 连续重派到 <c>max_redispatch</c>（e2e = 2）后放弃</item>
///   <item><b>B2-2：ACK 迟到但 worker 已介入</b> —— stub 在 watchdog 第一次重派
///     之前 ACK 一次（验证 inbox 表上 <c>acked_at</c> 能落，execution 不一定
///     进 FAILED）</item>
/// </list>
/// </para>
/// <para>
/// 时序节奏由 <see cref="MateOsE2EApp"/> 的 <c>DispatchAckWatchdog</c> 配置覆盖：
/// <c>AckWaitSeconds=2 / MaxRedispatch=2 / PollIntervalSeconds=1</c>，让 B2 在
/// ~6-8s 内跑通；默认值 15s × 3 = 45s 在测试环境不现实。
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
public sealed class B2_DispatchAckWatchdogTests : IClassFixture<MateOsE2EApp>
{
    private readonly MateOsE2EApp _app;

    public B2_DispatchAckWatchdogTests(MateOsE2EApp app)
    {
        _app = app;
    }

    [Fact]
    public async Task B2_001_stub全不ACK_watchdog重派到阈值_executionFAILED()
    {
        await _app.ResetAsync();

        // ── 1. seed ──
        HttpClient owner = _app.CreateClient();
        TokenResponse ownerToken = await E2ESetup.RegisterAsync(owner, "b2-owner@example.com");
        owner.Authorize(ownerToken.AccessToken);

        ProjectSummary project = await E2ESetup.CreateProjectForAsync(owner, "B2-Project");
        var credential = await E2ESetup.CreateCredentialAsync(owner);
        AgentSummary agent = await E2ESetup.CreateAgentAsync(owner, credential, "b2-stub-agent");
        string agentToken = await E2ESetup.IssueAgentTokenJwtAsync(owner, agent.Id);

        // ── 2. stub：B2RejectAckBehavior（收 dispatch 但不 ack）──
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
            new B2RejectAckBehavior(responder));
        stub.Start();

        // ── 3. 派 WorkItem ──
        Guid itemId = await E2ESetup.CreateWorkItemAsync(
            owner, project.Id, "TASK", "B2 watchdog 测试", "stub 全程不 ACK");
        Guid executionId = await E2ESetup.AssignWorkItemAsync(owner, itemId, agent.Id, deadlineS: 600);

        // ── 4. 等 Execution 进 FAILED ──
        // ack_wait=2s + poll_interval=1s + max_redispatch=2 → 最坏 ~6-8s；
        // 测试侧给 20s 留余量（CI 慢机器 + GC 停顿）。
        await WaitForExecutionStatusAsync(executionId, "FAILED", TimeSpan.FromSeconds(20));

        // ── 5. 断言 DB 终态 ──
        await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
            .GetRequiredService<MateOSDbContext>();

        var execution = await db.AgentExecutions
            .AsNoTracking()
            .FirstAsync(e => e.Id == executionId);

        Assert.Equal("FAILED", execution.Status);
        Assert.Null(execution.DispatchAckedAt);  // 全程没 ack
        Assert.NotNull(execution.CompletedAt);
        Assert.Equal(2, execution.DispatchRedispatchCount);
        Assert.NotNull(execution.LastRedispatchedAt);

        // attempt 也应转 FAILED
        var attempt = await db.ExecutionAttempts
            .AsNoTracking()
            .FirstAsync(a => a.ExecutionId == executionId && a.AttemptNo == 1);
        Assert.Equal("FAILED", attempt.Status);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Null(attempt.DispatchAckedAt);

        // outbox：执行了 execution.completed + reason=watchdog_giveup
        var outboxEvent = await db.OutboxEvents
            .AsNoTracking()
            .Where(o => o.AggregateId == executionId && o.EventType == "execution.completed")
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync();
        Assert.NotNull(outboxEvent);
        Assert.Contains("dispatch_ack_watchdog_giveup", outboxEvent.Payload);
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
