using System.Text.Json;

namespace MateOS.AgentStub;

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
