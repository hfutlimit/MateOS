using System.Text.Json;

namespace MateOS.AgentStub;

/// <summary>
/// Stub 行为策略：对收到的 <c>execution.dispatch</c> 帧如何反应。
/// </summary>
/// <remarks>
/// <para>
/// <b>设计选择：WS 只做反向 dispatch 推送</b>。
/// <c>WsEnvelope.cs</c> §2 明确：<c>execution.dispatch_ack</c> / <c>execution.event</c> /
/// <c>execution.result</c> <b>目前</b>只能走既有 HTTP 端点（<c>POST /agents/{agentId}/executions/...</c>），
/// 下一阶段才会迁到 WS。所以策略 <see cref="IStubBehavior.HandleDispatchAsync"/>
/// 拿到的 <see cref="StubResponder"/> 是 HTTP 形态的，<b>不要</b>在策略里写
/// <c>responder.SendFrameAsync(...)</c> ——
/// 看到那种代码就要立刻怀疑是不是又把 dispatch_ack 塞回 WS 了。
/// </para>
/// <para>
/// <b>策略可被替换</b>：B1 happy path 是 <see cref="B1HappyPathBehavior"/>。
/// B2-B8（拒收 / 崩溃 / 重连 / 超时 / 重复 / 迟到）会各自实现一个 <see cref="IStubBehavior"/>，
/// 新增行为 = 新增一个实现，不再回到主循环加 if-else。
/// </para>
/// </remarks>
public interface IStubBehavior
{
    /// <summary>对一条新到达的 dispatch 做反应。</summary>
    /// <param name="dispatch">dispatch 帧 payload（已解析）。</param>
    /// <param name="ct">取消信号。crash / deadline 行为会自己监听。</param>
    /// <remarks>
    /// 行为策略应在构造时持 <see cref="StubResponder"/>（HTTP 形态），不要每次入参传
    /// — 同一 session 里 responder 是固定的（同一个 agent_token + baseUrl）。
    /// </remarks>
    Task HandleDispatchAsync(JsonElement dispatch, CancellationToken ct);

    /// <summary>
    /// 收到 hello_ack（session 就绪）后调一次的钩子 —— 让行为策略启动后台 task。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认实现 no-op。B3 / B4 / B5（CR 决策层）实现这个钩子启动 inbox polling；
    /// B1 / B2 / B6 / B7 / B8 不需要（它们只对 dispatch 帧做反应）。
    /// </para>
    /// <para>
    /// 钩子在 <see cref="AgentStubClient.AttachAsync"/> 期间同步启动，**不等完成**；
    /// 实现里要自己起 <c>Task.Run</c>，否则 hello_ack→挂第一条 dispatch 之间会卡住。
    /// </para>
    /// </remarks>
    Task OnSessionReadyAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// 协议级响应器（HTTP 形态）：把策略对该 dispatch 的「该说什么」转换成
/// 对 Runtime 的 HTTP 调用。
/// </summary>
/// <remarks>
/// <para>
/// 实现就是 <see cref="HttpStubResponder"/> —— 它内部持
/// <see cref="System.Net.Http.HttpClient"/> + agent_token，向
/// <c>POST /agents/{agentId}/executions/{executionId}/dispatch_ack</c> /
/// <c>.../events</c> / <c>.../result</c> 三个端点打。
/// </para>
/// <para>
/// <b>为什么不在 AgentStubClient 里直接实现</b>：<see cref="AgentStubClient"/> 是
/// 长连接管理方（WS hello / heartbeat / 收 dispatch / 派给策略），
/// <see cref="HttpStubResponder"/> 是「把策略意图翻译成 API 调用」。
/// 两件事的故障域不同 —— 走 HTTP 失败不该让 WS 主循环挂。
/// </para>
/// </remarks>
public abstract class StubResponder
{
    /// <summary>对一条 dispatch 回 dispatch_ack（detailed/03 §7.2，HTTP 端点：<c>POST .../dispatch_ack</c>）。</summary>
    public abstract Task AckAsync(JsonElement dispatchPayload, string? protocolError = null, CancellationToken ct = default);

    /// <summary>上报一条 execution event（HTTP 端点：<c>POST .../events</c>）。</summary>
    public abstract Task ReportEventAsync(JsonElement dispatchPayload, long seq, string eventType, string providerEventId, JsonElement payload, CancellationToken ct = default);

    /// <summary>上报 execution 终态（HTTP 端点：<c>POST .../result</c>）。</summary>
    public abstract Task ReportResultAsync(JsonElement dispatchPayload, string status, string envelopeId, JsonElement? output = null, JsonElement? usage = null, CancellationToken ct = default);

    /// <summary>
    /// 拉 Agent 的协作请求 inbox（HTTP 端点：<c>GET /agents/{agentId}/collaboration-requests/inbox</c>）。
    /// </summary>
    /// <remarks>
    /// V1 用轮询（detailed/05 §2 决策流）；B3 / B4 / B5 行为策略通过这个钩子
    /// 主动拉 inbox + 回决策，绕过 WS 推送（避免引入 <c>collaboration.request</c> WS 帧）。
    /// </remarks>
    public abstract Task<IReadOnlyList<JsonElement>> ListCollaborationInboxAsync(CancellationToken ct = default);

    /// <summary>
    /// 提交协作请求决策（HTTP 端点：<c>POST /agents/{agentId}/collaboration-requests/{crId}/decision</c>）。
    /// </summary>
    public abstract Task SubmitCollaborationDecisionAsync(
        Guid crId,
        string decision,
        string? reason = null,
        IReadOnlyList<string>? needs = null,
        CancellationToken ct = default);
}
