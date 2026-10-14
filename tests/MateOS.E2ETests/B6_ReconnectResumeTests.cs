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

/// <summary>
/// S1 DoD 收口：B6 执行中断线重连 + resume 续传（`detailed/10` §2 B6）。
/// </summary>
/// <remarks>
/// <para>
/// 行为矩阵 B6：Agent 收 dispatch → 发 seq 1..2 → <b>主动断 WS</b> → 用同一 agent_token
/// 重新 hello → 发 <c>execution.resume_request</c>（带 <c>last_persisted_seq</c>）→
/// server 回 <c>resume_ack</c> → 从 <c>last_persisted_seq + 1</c> 续发到 seq 5。
/// </para>
/// <para>
/// 本用例要证明的三件事（`detailed/03` §3 + `detailed/10` §2 B6 的断言点）：
/// <list type="number">
///   <item><b>attempt 不换</b> —— 重连是 transport 层的事，不新建 attempt（`attempt ≠ WS session`）</item>
///   <item><b>event 无 gap</b> —— 落库 seq 必须是 1,2,3,4,5 连续</item>
///   <item><b>event 无重复</b> —— 3..5 的 <c>provider_event_id</c> 与 1..2 不同
///     （若 Agent 错把 seq 1 重发，会被 <c>AttemptEventCursor</c> 判 Duplicate 丢掉而出现 gap）</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么不断言终态</b>：B6 行为只发 PROGRESS、不发 <c>execution.result</c>，
/// 所以执行停在 <c>RUNNING</c>。这是设计内 —— B6 验的是续传位点，不是收尾。
/// </para>
/// <para>
/// <b>为什么轮询 DB 而不等 stub 完成</b>：重连后的 <see cref="AgentStubClient"/> 主循环
/// 要等 socket 侧的 60s 读超时才会自然退出，await 其完成会把用例拖到分钟级。
/// 这里只等「事件全部落库」这个业务事实。
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
public sealed class B6_ReconnectResumeTests : IClassFixture<MateOsE2EApp>
{
    private readonly MateOsE2EApp _app;

    public B6_ReconnectResumeTests(MateOsE2EApp app)
    {
        _app = app;
    }

    [Fact]
    public async Task B6_001_断线重连从连续位点续发_event无gap无重复_attempt不换()
    {
        await _app.ResetAsync();

        // ── 1. seed ──
        HttpClient owner = _app.CreateClient();
        TokenResponse ownerToken = await E2ESetup.RegisterAsync(owner, "b6-owner@example.com");
        owner.Authorize(ownerToken.AccessToken);

        ProjectSummary project = await E2ESetup.CreateProjectForAsync(owner, "B6-Project");
        CredentialSummary credential = await E2ESetup.CreateCredentialAsync(owner);
        AgentSummary agent = await E2ESetup.CreateAgentAsync(owner, credential, "b6-stub-agent");
        string agentToken = await E2ESetup.IssueAgentTokenJwtAsync(owner, agent.Id);

        // ── 2. stub 通道 ──
        HttpClient agentHttp = _app.CreateClient();
        agentHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);

        StubResponder responder = new HttpStubResponder(
            agentHttp,
            agent.Id,
            baseUrl: agentHttp.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost");

        IStubTransport initialTransport = await ConnectTransportAsync();

        // 重连工厂：B6 行为断线后调它建第二条连接。
        // 签名是同步的 Func<IStubTransport>（stub 侧定义），这里同步等待 TestServer 的
        // in-memory connect —— 测试环境无 SynchronizationContext，不会死锁。
        Func<IStubTransport> transportFactory = () =>
            ConnectTransportAsync().GetAwaiter().GetResult();

        await using AgentStubClient stub = await AgentStubClient.AttachAsync(
            initialTransport,
            agentToken,
            new B6DropAtSeqBehavior(
                responder,
                initialTransport,
                transportFactory,
                agentToken,
                dropAfterSeq: 2,
                resumeFromSeq: 3));
        stub.Start();

        // ── 3. 派 WorkItem ──
        Guid itemId = await E2ESetup.CreateWorkItemAsync(
            owner, project.Id, "TASK", "B6 重连续传测试", "断线后从 last_persisted_seq + 1 续发");
        Guid executionId = await E2ESetup.AssignWorkItemAsync(owner, itemId, agent.Id, deadlineS: 600);

        // ── 4. 等 5 条 event 全部落库（drop 前 2 条 + 重连后 3 条）──
        await WaitForLastPersistedSeqAsync(executionId, expected: 5, TimeSpan.FromSeconds(30));

        // ── 5. 断言 ──
        await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
            .GetRequiredService<MateOSDbContext>();

        AgentExecution execution = await db.AgentExecutions
            .AsNoTracking()
            .FirstAsync(e => e.Id == executionId);

        // 连续位点推进到 5
        Assert.Equal(5, execution.LastPersistedSeq);

        // ① attempt 不换：重连 ≠ 新 attempt
        Assert.Equal(1, execution.AttemptCount);
        Assert.Equal(1, execution.ActiveAttemptNo);

        // dispatch 已 ack（B6 一收 dispatch 就 ack）
        Assert.NotNull(execution.DispatchAckedAt);

        // B6 不发 result → 仍 RUNNING
        Assert.Equal("RUNNING", execution.Status);

        // execution_events 表只有 AttemptId，没有 ExecutionId —— 经 attempt 关联
        Guid attemptId = await db.ExecutionAttempts
            .AsNoTracking()
            .Where(a => a.ExecutionId == executionId && a.AttemptNo == 1)
            .Select(a => a.Id)
            .FirstAsync();

        List<long> seqs = await db.ExecutionEvents
            .AsNoTracking()
            .Where(e => e.AttemptId == attemptId)
            .OrderBy(e => e.Seq)
            .Select(e => e.Seq)
            .ToListAsync();

        // ② 无 gap：1..5 连续
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, seqs.ToArray());

        // ③ 无重复：5 条不同的 provider_event_id
        int distinctProviderEventIds = await db.ExecutionEvents
            .AsNoTracking()
            .Where(e => e.AttemptId == attemptId)
            .Select(e => e.ProviderEventId)
            .Distinct()
            .CountAsync();

        Assert.Equal(5, distinctProviderEventIds);
    }

    private async Task<IStubTransport> ConnectTransportAsync()
    {
        WebSocket socket = await _app.Server
            .CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        return new InMemorySocketTransport(socket);
    }

    private async Task WaitForLastPersistedSeqAsync(Guid executionId, long expected, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        long lastSeen = -1;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using MateOSDbContext db = _app.Services.CreateScope().ServiceProvider
                .GetRequiredService<MateOSDbContext>();

            lastSeen = await db.AgentExecutions
                .AsNoTracking()
                .Where(e => e.Id == executionId)
                .Select(e => e.LastPersistedSeq)
                .FirstOrDefaultAsync();

            if (lastSeen >= expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"execution {executionId} 的 last_persisted_seq 在 {timeout.TotalSeconds}s 内未达到 {expected}，last={lastSeen}");
    }
}
