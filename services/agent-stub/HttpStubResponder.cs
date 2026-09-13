using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MateOS.AgentStub;

/// <summary>
/// 协议级响应器的 HTTP 实现：把策略「该说什么」翻译成对 Runtime 的 HTTP 调用。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独成类</b>：与 <see cref="AgentStubClient"/> 的 WS 长连接管理职责解耦 —
/// 走 HTTP 失败（401 / 409 / 5xx）绝不能让 WS 主循环挂，反之亦然。
/// </para>
/// <para>
/// <b>鉴权</b>：用 <c>Bearer {agentToken}</c>，与 WS hello 同一份 token。
/// Runtime 端 <c>ExecutionEndpoints</c> 的 <c>RequireAuthorization()</c> +
/// <c>http.RequireAgentId()</c> 会校验「token 的 agent_id == path 的 agent_id」，
/// 不一致就 403，调用方应该按协议层错误处理（不算业务 ack 失败）。
/// </para>
/// </remarks>
public sealed class HttpStubResponder : StubResponder
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly Guid _agentId;
    private readonly string _baseUrl;

    public HttpStubResponder(HttpClient http, Guid agentId, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _agentId = agentId;
        _baseUrl = baseUrl?.TrimEnd('/') ?? (_http.BaseAddress?.ToString().TrimEnd('/') ?? string.Empty);
    }

    public override async Task AckAsync(JsonElement dispatchPayload, string? protocolError = null, CancellationToken ct = default)
    {
        Guid executionId = dispatchPayload.GetProperty("execution_id").GetGuid();

        // dispatch_ack 端点（ExecutionEndpoints.cs:119）没有 body，token.agentId 必须等于 path agentId
        HttpRequestMessage req = new(HttpMethod.Post, $"{_baseUrl}/agents/{_agentId}/executions/{executionId}/dispatch_ack");

        // protocolError 暂时只能记到 header（detailed/03 §7.2 的 received=true + protocol_error 是
        // wire 层概念，HTTP 端点没接收这个字段；B2 行为才走 null≠null 分支）
        if (protocolError is not null)
        {
            req.Headers.Add("X-Protocol-Error", protocolError);
        }

        using HttpResponseMessage resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"dispatch_ack HTTP {(int)resp.StatusCode} {resp.StatusCode}: {body}",
                inner: null,
                statusCode: resp.StatusCode);
        }
    }

    public override async Task ReportEventAsync(JsonElement dispatchPayload, long seq, string eventType, string providerEventId, JsonElement payload, CancellationToken ct = default)
    {
        Guid executionId = dispatchPayload.GetProperty("execution_id").GetGuid();

        // wire 字段名与服务端 ReportExecutionEventRequest(EventType/ProviderEventId/Seq/Payload)
        // 一致：snake_case（s_json 策略）
        var body = new
        {
            event_type = eventType,
            provider_event_id = providerEventId,
            seq = seq,
            payload = payload,
        };

        HttpResponseMessage resp = await _http.PostAsJsonAsync(
            $"{_baseUrl}/agents/{_agentId}/executions/{executionId}/events",
            body,
            s_json,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            string responseBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"report_event seq={seq} HTTP {(int)resp.StatusCode} {resp.StatusCode}: {responseBody}",
                inner: null,
                statusCode: resp.StatusCode);
        }
    }

    public override async Task ReportResultAsync(JsonElement dispatchPayload, string status, string envelopeId, JsonElement? output = null, JsonElement? usage = null, CancellationToken ct = default)
    {
        Guid executionId = dispatchPayload.GetProperty("execution_id").GetGuid();

        // ReportExecutionResultRequest(Status, EnvelopeId, Output, Usage) — 字段 wire 名按 snake_case
        var body = new
        {
            status = status,
            envelope_id = envelopeId,
            output = output,
            usage = usage,
        };

        HttpResponseMessage resp = await _http.PostAsJsonAsync(
            $"{_baseUrl}/agents/{_agentId}/executions/{executionId}/result",
            body,
            s_json,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            string responseBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"report_result HTTP {(int)resp.StatusCode} {resp.StatusCode}: {responseBody}",
                inner: null,
                statusCode: resp.StatusCode);
        }
    }
}
