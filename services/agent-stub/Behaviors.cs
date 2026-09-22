using System.Net.Http.Json;
using System.Text.Json;

namespace MateOS.AgentStub;

// =============================================================================
// B1 happy path
// =============================================================================

/// <summary>
/// B1 行为：正常闭环（detailed/10 §2 表 B1）。
/// </summary>
/// <remarks>
/// <para>
/// 时序：
/// <list type="number">
///   <item>收到 <c>execution.dispatch</c> → 调 <see cref="StubResponder.AckAsync"/>（HTTP dispatch_ack）</item>
///   <item>上报 1 个 <c>PROGRESS</c> 事件（<c>seq=1</c>）</item>
///   <item>上报 1 个 <c>PROGRESS</c> 事件（<c>seq=2</c>）</item>
///   <item>上报 <c>execution.result</c>（<c>status=SUCCEEDED</c>），带 fixture 假产出</item>
/// </list>
/// </para>
/// <para>
/// fixture 默认产出：<c>{ "markdown": "stub ok" }</c>。
/// B2-B8 行为用同一个基线（<see cref="HttpStubResponder"/>），但策略在 ack 之后
/// / event 期间 / result 之前选择「不响应 / 抛 / 晚回」等分支。
/// </para>
/// </remarks>
public sealed class B1HappyPathBehavior : IStubBehavior
{
    private readonly StubResponder _responder;
    private readonly string _fixture;

    public B1HappyPathBehavior(StubResponder responder, string? fixture = null)
    {
        _responder = responder;
        _fixture = fixture ?? """{"markdown":"stub ok"}""";
    }

    public async Task HandleDispatchAsync(JsonElement dispatch, CancellationToken ct)
    {
        // ① dispatch_ack（HTTP 端点：POST .../dispatch_ack）
        await _responder.AckAsync(dispatch, protocolError: null, ct);

        // ② 上报 1 个 progress event
        await _responder.ReportEventAsync(
            dispatch,
            seq: 1,
            eventType: "PROGRESS",
            providerEventId: $"stub-{Guid.NewGuid():N}",
            payload: JsonDocument.Parse("""{"step":1,"note":"starting"}""").RootElement,
            ct);

        // ③ 上报第 2 个 progress event
        await _responder.ReportEventAsync(
            dispatch,
            seq: 2,
            eventType: "PROGRESS",
            providerEventId: $"stub-{Guid.NewGuid():N}",
            payload: JsonDocument.Parse("""{"step":2,"note":"working"}""").RootElement,
            ct);

        // ④ 上报 SUCCEEDED
        await _responder.ReportResultAsync(
            dispatch,
            status: "SUCCEEDED",
            envelopeId: Guid.NewGuid().ToString("N"),
            output: JsonDocument.Parse(_fixture).RootElement,
            usage: null,
            ct);
    }
}

// =============================================================================
// B2-B8 行为矩阵（detailed/10 §2）
// =============================================================================

/// <summary>
/// B2 行为：收 dispatch 但不回 dispatch_ack —— 模拟「送达失败 / Agent 死锁在
/// 收到 dispatch 后崩溃前」，让 Runtime dispatch_ack watchdog 探测后重派。
/// </summary>
/// <remarks>
/// <para>
/// 依据 detailed/03 §7.1 + detailed/10 §2 B2：
/// <list type="bullet">
///   <item>收 dispatch → 什么也不做（不 ack / 不 event / 不 result）</item>
///   <item>Runtime ack_wait 阈值过 → 重派同一 (execution_id, attempt_no)</item>
///   <item>再次送达 → 仍不回 ack（让 watchdog 继续）</item>
///   <item>redispatch 次数耗尽（默认 3） → Execution FAILED</item>
/// </list>
/// </para>
/// <para>
/// 测试断言：
/// <list type="bullet">
///   <item><c>dispatch_acked_at IS NULL</c> 持续</item>
///   <item><c>dispatch_redispatch_count &gt; 1</c></item>
///   <item>最终 <c>agent_executions.status = FAILED</c>，reason="dispatch_ack_watchdog_giveup"</item>
/// </list>
/// </para>
/// </remarks>
public sealed class B2RejectAckBehavior : IStubBehavior
{
    private readonly StubResponder _responder;

    public B2RejectAckBehavior(StubResponder responder)
    {
        _responder = responder;
    }

    public Task HandleDispatchAsync(JsonElement dispatch, CancellationToken ct)
    {
        // 故意什么都不做 —— 让 dispatch_acked_at 永远 IS NULL
        // 也不要 await：避免任何 RPC 副作用（让 worker 看到的 ack_wait 来自真实时间）
        return Task.CompletedTask;
    }
}

/// <summary>
/// B3 行为：协作请求 → 决策 REJECTED —— 模拟「Agent 明确拒收」。
/// </summary>
/// <remarks>
/// <para>
/// 入口走 <c>@mention</c> 触发的 CR（不走 work-item assign），所以 stub 不收
/// dispatch（dispatch 本就不会来），而是 inbox polling 后回 REJECT。
/// </para>
/// <para>
/// 测试断言：
/// <list type="bullet">
///   <item><c>collaboration_requests.status = REJECTED</c></item>
///   <item>对应 decision_record 落库</item>
///   <item><c>agent_executions</c> <b>不创建</b>（决策拒绝不进入 Execution）</item>
///   <item><c>execution.completed</c> outbox 不写</item>
/// </list>
/// </para>
/// </remarks>
public sealed class B3RejectCollaborationBehavior : IStubBehavior
{
    private readonly StubResponder _responder;
    private readonly TimeSpan _pollInterval;
    private readonly string _reason;

    public B3RejectCollaborationBehavior(
        StubResponder responder,
        string reason = "B3 stub: capability gate rejected by stub",
        TimeSpan? pollInterval = null)
    {
        _responder = responder;
        _reason = reason;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
    }

    public Task HandleDispatchAsync(JsonElement dispatch, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnSessionReadyAsync(CancellationToken ct)
    {
        // 不 await —— 后台跑，主循环立刻让 hello 完成
        return Task.Run(() => PollAndRejectAsync(ct), ct);
    }

    private async Task PollAndRejectAsync(CancellationToken ct)
    {
        HashSet<Guid> handled = [];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<JsonElement> inbox = await _responder.ListCollaborationInboxAsync(ct);
                foreach (JsonElement item in inbox)
                {
                    Guid crId = item.GetProperty("collaboration_request_id").GetGuid();
                    if (handled.Add(crId))
                    {
                        await _responder.SubmitCollaborationDecisionAsync(
                            crId,
                            decision: "REJECT",
                            reason: _reason,
                            ct: ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[B3] inbox poll exception: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

/// <summary>
/// B4 行为：协作请求 → 决策 NEED_CONTEXT —— 模拟「Agent 需要补上下文」。
/// </summary>
/// <remarks>
/// <para>
/// 依据 detailed/05 §3 NEED_CONTEXT 路径：
/// <list type="bullet">
///   <item>CR=NEED_CONTEXT，Execution 不创建</item>
///   <item>需要由人代补上下文 → 重新 trigger（不在 stub 范围；e2e 只测到
///     <c>NEED_CONTEXT</c> 落库 + 决策记录含 needs 列表）</item>
/// </list>
/// </para>
/// </remarks>
public sealed class B4NeedContextBehavior : IStubBehavior
{
    private readonly StubResponder _responder;
    private readonly IReadOnlyList<string> _needs;
    private readonly TimeSpan _pollInterval;

    public B4NeedContextBehavior(
        StubResponder responder,
        IReadOnlyList<string>? needs = null,
        TimeSpan? pollInterval = null)
    {
        _responder = responder;
        _needs = needs ?? new[] { "missing: work_item_ref.detail", "missing: latest_commit_hash" };
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
    }

    public Task HandleDispatchAsync(JsonElement dispatch, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnSessionReadyAsync(CancellationToken ct) =>
        Task.Run(() => PollAndNeedContextAsync(ct), ct);

    private async Task PollAndNeedContextAsync(CancellationToken ct)
    {
        HashSet<Guid> handled = [];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<JsonElement> inbox = await _responder.ListCollaborationInboxAsync(ct);
                foreach (JsonElement item in inbox)
                {
                    Guid crId = item.GetProperty("collaboration_request_id").GetGuid();
                    if (handled.Add(crId))
                    {
                        await _responder.SubmitCollaborationDecisionAsync(
                            crId,
                            decision: "NEED_CONTEXT",
                            reason: "B4 stub: stub requires more context",
                            needs: _needs,
                            ct: ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[B4] inbox poll exception: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

/// <summary>
/// B5 行为：协作请求 → 沉默 —— 模拟「Agent 完全不响应」让 E4 deadline 触发 UNRESOLVED。
/// </summary>
/// <remarks>
/// <para>
/// 依据 detailed/05 §3 UNRESOLVED 路径：CR 决策 deadline_s 到 → E4 释放 slot → CR=UNRESOLVED。
/// </para>
/// <para>
/// 测试断言：
/// <list type="bullet">
///   <item><c>collaboration_requests.status = UNRESOLVED</c></item>
///   <item><b>不</b>写 decision_record（不是 Agent 主动决策的，是 deadline 触发的）</item>
///   <item><c>collaboration.resolved</c> outbox 事件 reason="deadline"</item>
/// </list>
/// </para>
/// <para>
/// stub 端什么都不做：<c>HandleDispatchAsync</c> no-op，<c>OnSessionReadyAsync</c> no-op。
/// 让 e2e 把 CR 的 deadline_s 调短（比如 3s）就能在测试时间内触发。
/// </para>
/// </remarks>
public sealed class B5SilentBehavior : IStubBehavior
{
    public Task HandleDispatchAsync(JsonElement dispatch, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnSessionReadyAsync(CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>
/// B6 行为：收 dispatch → 发几个 event → 断 WS → 重新 hello → 发 resume_request → 续发。
/// </summary>
/// <remarks>
/// <para>
/// 依据 detailed/03 §7.1 + detailed/10 §2 B6：
/// <list type="number">
///   <item>收 dispatch → ack</item>
///   <item>发 seq=1, seq=2 两个 PROGRESS event</item>
///   <item>主动 close WS</item>
///   <item>用同一个 agent_token 重新 hello（不创建新 session，server 侧按
///     agent_token 复用同一 connection registry 槽）</item>
///   <item>发 <c>execution.resume_request</c>（HTTP <c>POST .../resume_request</c>，
///     body 含 <c>last_persisted_seq</c>）→ server 回 <c>resume_ack</c> + 从
///     last_persisted_seq+1 续发</item>
///   <item>从 seq=3 起续发（不能 seq=1 重发，否则会被 AttemptEventCursor 判 Duplicate 丢）</item>
/// </list>
/// </para>
/// <para>
/// 测试断言：
/// <list type="bullet">
///   <item><c>last_persisted_seq = 5</c>（共 5 个 PROGRESS event 落库）</item>
///   <item><c>execution_events</c> 表 seq 无 gap（1,2,3,4,5 连续）</item>
///   <item><c>provider_event_id</c> 无重复（3/4/5 的 provider_event_id 与 1/2 不同）</item>
///   <item><c>execution.status = RUNNING</c>（未终止）</item>
/// </list>
/// </para>
/// </remarks>
public sealed class B6DropAtSeqBehavior : IStubBehavior
{
    private readonly StubResponder _responder;
    private readonly IStubTransport _transport;
    private readonly Func<IStubTransport> _transportFactory;
    private readonly string _agentToken;
    private readonly int _dropAfterSeq;
    private readonly int _resumeFromSeq;

    public B6DropAtSeqBehavior(
        StubResponder responder,
        IStubTransport transport,
        Func<IStubTransport> transportFactory,
        string agentToken,
        int dropAfterSeq = 2,
        int resumeFromSeq = 3)
    {
        _responder = responder;
        _transport = transport;
        _transportFactory = transportFactory;
        _agentToken = agentToken;
        _dropAfterSeq = dropAfterSeq;
        _resumeFromSeq = resumeFromSeq;
    }

    public async Task HandleDispatchAsync(JsonElement dispatch, CancellationToken ct)
    {
        Guid executionId = dispatch.GetProperty("execution_id").GetGuid();
        int attemptNo = dispatch.GetProperty("attempt_no").GetInt32();

        await _responder.AckAsync(dispatch, protocolError: null, ct);

        // 发 seq=1 .. dropAfterSeq 的 event
        for (int seq = 1; seq <= _dropAfterSeq; seq++)
        {
            await _responder.ReportEventAsync(
                dispatch,
                seq: seq,
                eventType: "PROGRESS",
                providerEventId: $"b6-{seq}-{Guid.NewGuid():N}",
                payload: JsonDocument.Parse($$"""{"step":{{seq}},"note":"B6 before drop"}""").RootElement,
                ct);
        }

        // 主动 close WS —— RunLoopAsync 会因 WebSocketException 退出
        try
        {
            if (_transport.Socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                await _transport.Socket.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                    "B6 drop",
                    CancellationToken.None);
            }
        }
        catch
        {
            // 已经关了
        }

        // 用新 transport 重新 hello + 发 resume_request
        await ReconnectAndResumeAsync(executionId, attemptNo, ct);
    }

    private async Task ReconnectAndResumeAsync(Guid executionId, int attemptNo, CancellationToken ct)
    {
        IStubTransport newTransport = _transportFactory();
        AgentStubClient newClient = await AgentStubClient.AttachAsync(
            newTransport,
            _agentToken,
            new B6AfterDropBehavior(_responder, _resumeFromSeq),
            helloTimeout: TimeSpan.FromSeconds(5),
            ct);

        // 发 resume_request：HTTP POST .../resume_request { last_persisted_seq, attempt_no }
        // 当前 StubResponder 没暴露 resume_request —— 走通用 _http（HttpStubResponder._http
        // 是 private）。这里用 reflection 取到 HttpClient 让 B6 走通；
        // 后续可以把 resume_request 提到 StubResponder 接口上。
        HttpClient http = GetHttpFromResponder(_responder);
        HttpResponseMessage resp = await http.PostAsJsonAsync(
            $"/agents/{newClient.AgentId}/executions/{executionId}/resume_request",
            new { last_persisted_seq = _dropAfterSeq, attempt_no = attemptNo },
            ct);
        resp.EnsureSuccessStatusCode();

        // 把续发任务挂在新 client 上 —— e2e 拿这个 task 等完成
        newClient.Start();
        await newClient.Completion;
    }

    private static HttpClient GetHttpFromResponder(StubResponder responder)
    {
        System.Reflection.FieldInfo f = typeof(HttpStubResponder)
            .GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("HttpStubResponder._http field not found");

        return (HttpClient)f.GetValue(responder)!;
    }
}

/// <summary>
/// B6 重连后的续发行为：从 <see cref="_resumeFromSeq"/> 起发到固定总条数。
/// </summary>
internal sealed class B6AfterDropBehavior : IStubBehavior
{
    private readonly StubResponder _responder;
    private readonly int _resumeFromSeq;
    private const int TotalEvents = 5;

    public B6AfterDropBehavior(StubResponder responder, int resumeFromSeq)
    {
        _responder = responder;
        _resumeFromSeq = resumeFromSeq;
    }

    public async Task HandleDispatchAsync(JsonElement dispatch, CancellationToken ct)
    {
        await _responder.AckAsync(dispatch, protocolError: null, ct);

        // 续发：从 resumeFromSeq 到 TotalEvents（含两端）
        for (int seq = _resumeFromSeq; seq <= TotalEvents; seq++)
        {
            await _responder.ReportEventAsync(
                dispatch,
                seq: seq,
                eventType: "PROGRESS",
                providerEventId: $"b6-after-{seq}-{Guid.NewGuid():N}",
                payload: JsonDocument.Parse($$"""{"step":{{seq}},"note":"B6 after drop"}""").RootElement,
                ct);
        }
    }
}

/// <summary>
/// B7 行为：故意重复上报同 <c>provider_event_id</c> 的 event + 同 dispatch 收多次。
/// </summary>
/// <remarks>
/// <para>
/// 依据 detailed/03 §3.2 + detailed/10 §2 B7：Runtime AttemptEventCursor 必须把
/// 重复 <c>provider_event_id</c> 判为 DuplicateCause.ProviderEventIdReplay 并静默吞掉。
/// </para>
/// <para>
/// 时序：
/// <list type="number">
///   <item>收 dispatch → ack</item>
///   <item>发 seq=1 event（<c>provider_event_id = "B7-fixed"</c>）</item>
///   <item>故意再发同 <c>provider_event_id = "B7-fixed"</c> + seq=2（异常客户端行为）</item>
///   <item>结果应该是：seq=1 落库，seq=2 被 ProviderEventIdReplay 判 Duplicate 吞掉</item>
/// </list>
/// </para>
/// <para>
/// 测试断言：
/// <list type="bullet">
///   <item><c>execution_events</c> 表只有 1 行（seq=1）</item>
///   <item><c>last_persisted_seq = 1</c>（不前进）</item>
///   <item>不上报 SUCCEEDED —— 由 B7 stub 继续卡住</item>
/// </list>
/// </para>
/// <para>
/// stub 只测到 Runtime 端的去重；同 dispatch 收多次的路径由 Runtime 端
/// <see cref="AgentDispatchNotifier"/> 自己幂等（dispatch_ack 端点写
/// <c>dispatch_acked_at</c> 是幂等 UPDATE）。
/// </para>
/// </remarks>
public sealed class B7DuplicateProviderEventIdBehavior : IStubBehavior
{
    private readonly StubResponder _responder;
    private readonly string _fixedProviderEventId;

    public B7DuplicateProviderEventIdBehavior(StubResponder responder)
    {
        _responder = responder;
        _fixedProviderEventId = "B7-fixed-provider-event-id";
    }

    public async Task HandleDispatchAsync(JsonElement dispatch, CancellationToken ct)
    {
        await _responder.AckAsync(dispatch, protocolError: null, ct);

        // 第一次：seq=1
        await _responder.ReportEventAsync(
            dispatch,
            seq: 1,
            eventType: "PROGRESS",
            providerEventId: _fixedProviderEventId,
            payload: JsonDocument.Parse("""{"step":1,"note":"B7 first send"}""").RootElement,
            ct);

        // 故意重复：seq=2 + 同 provider_event_id
        await _responder.ReportEventAsync(
            dispatch,
            seq: 2,
            eventType: "PROGRESS",
            providerEventId: _fixedProviderEventId,
            payload: JsonDocument.Parse("""{"step":2,"note":"B7 duplicate replay"}""").RootElement,
            ct);

        // 不上报 SUCCEEDED —— 让 e2e 拿 last_persisted_seq 断言等于 1
    }
}

/// <summary>
/// B8 行为：先收 dispatch + ack + 发 seq=1 event；故意不发 result；让 Runtime watchdog
/// 重派 attempt 2；等到 attempt 2 dispatch 到达后再发迟到的 result（attempt_no=1，
/// 但 execution.active_attempt_no 已经 =2）。
/// </summary>
/// <remarks>
/// <para>
/// 依据 detailed/03 §4 + detailed/10 §2 B8：Runtime 端收到 result 时必须核对
/// <c>result.attempt_no == execution.active_attempt_no</c>，否则丢弃。
/// </para>
/// <para>
/// 时序：
/// <list type="number">
///   <item>收 attempt 1 dispatch → ack → 发 seq=1 event</item>
///   <item>不发 result → Runtime watchdog 重派 attempt 2</item>
///   <item>收 attempt 2 dispatch → ack → 发 seq=2 event</item>
///   <item>发迟到 result：<c>attempt_no=1</c> + <c>status=SUCCEEDED</c></item>
///   <item>Runtime 应丢弃（active_attempt_no=2）</item>
///   <item>然后 stub 上报正常 attempt 2 result → Execution SUCCEEDED</item>
/// </list>
/// </para>
/// <para>
/// 测试断言：
/// <list type="bullet">
///   <item>迟到 result 被丢弃（execution_events 不多一行；execution.status 不先 SUCCEEDED 再变）</item>
///   <item>正常 result 落库 → 最终 SUCCEEDED</item>
///   <item><c>dispatch_redispatch_count</c> &gt;= 1（说明 watchdog 重派触发过）</item>
/// </list>
/// </para>
/// <para>
/// 简化：v0.7 stub 用"先发 seq=1 → 故意停 30s → 让 watchdog 重派"会拖慢测试。
/// e2e 可以注入更短的 ack_wait（比如 1s）让重派快速发生。本 stub 不关心 watchdog
/// 节奏 —— 它只是按"收 dispatch 后回什么"组织好，让 e2e 在不同时刻验。
/// </para>
/// </remarks>
public sealed class B8LateResultBehavior : IStubBehavior
{
    private readonly StubResponder _responder;
    private int _attemptSeen = 0;

    public B8LateResultBehavior(StubResponder responder)
    {
        _responder = responder;
    }

    public async Task HandleDispatchAsync(JsonElement dispatch, CancellationToken ct)
    {
        int attemptNo = dispatch.GetProperty("attempt_no").GetInt32();
        _attemptSeen++;

        await _responder.AckAsync(dispatch, protocolError: null, ct);

        if (attemptNo == 1)
        {
            // 第一次 dispatch：发 seq=1 event；故意不发 result
            await _responder.ReportEventAsync(
                dispatch,
                seq: 1,
                eventType: "PROGRESS",
                providerEventId: $"b8-attempt1-seq1-{Guid.NewGuid():N}",
                payload: JsonDocument.Parse("""{"step":1,"note":"B8 attempt 1"}""").RootElement,
                ct);
            // 不发 result —— 让 watchdog 重派
            return;
        }

        if (attemptNo == 2)
        {
            // 第二次 dispatch：先发 seq=1 event（attempt 2 自己的 seq=1）
            await _responder.ReportEventAsync(
                dispatch,
                seq: 1,
                eventType: "PROGRESS",
                providerEventId: $"b8-attempt2-seq1-{Guid.NewGuid():N}",
                payload: JsonDocument.Parse("""{"step":1,"note":"B8 attempt 2"}""").RootElement,
                ct);

            // 然后故意发迟到 result：attempt_no=1 + status=SUCCEEDED
            // Runtime 应判 attempt_no 不匹配 active_attempt_no=2 → 丢弃
            await _responder.ReportResultAsync(
                dispatch,
                status: "SUCCEEDED",
                envelopeId: Guid.NewGuid().ToString("N"),
                output: JsonDocument.Parse("""{"markdown":"B8 late result (should be discarded)"}""").RootElement,
                usage: null,
                ct);

            // 再发正常 result：attempt_no=2 → 落库
            await _responder.ReportResultAsync(
                dispatch,
                status: "SUCCEEDED",
                envelopeId: Guid.NewGuid().ToString("N"),
                output: JsonDocument.Parse("""{"markdown":"B8 actual result"}""").RootElement,
                usage: null,
                ct);
        }
    }
}
