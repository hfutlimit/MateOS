namespace MateOS.Api.Observability;

/// <summary>
/// trace_id 的生成、透传与日志作用域（detailed/09 §2：E10 基础从 M1 起就存在）。
/// </summary>
/// <remarks>
/// <para>
/// 规则：客户端带了 <c>X-Trace-Id</c> 就沿用（便于跨系统串联），否则新生成；
/// 响应头回写同一个值，日志与 <c>audit_logs.trace_id</c> 都取它。
/// </para>
/// <para>
/// S1 不引入 OpenTelemetry（09 §3.1：OTel/Prom/Grafana/Loki 推后），
/// 只保证 trace_id 从入口贯穿到审计表。
/// </para>
/// </remarks>
public sealed class TraceMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
{
    public const string HeaderName = "X-Trace-Id";

    private const string ItemKey = "mateos.trace_id";

    private readonly ILogger _logger = loggerFactory.CreateLogger("MateOS.Trace");

    public async Task InvokeAsync(HttpContext context)
    {
        string traceId = ResolveTraceId(context);

        context.Items[ItemKey] = traceId;
        context.Response.Headers[HeaderName] = traceId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["trace_id"] = traceId }))
        {
            await next(context);
        }
    }

    private static string ResolveTraceId(HttpContext context)
    {
        string? incoming = context.Request.Headers[HeaderName].FirstOrDefault();

        // 只接受合理长度的十六进制/短标识，避免把任意客户端串直接写进审计
        if (!string.IsNullOrWhiteSpace(incoming)
            && incoming.Length <= 64
            && incoming.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            return incoming;
        }

        return Guid.NewGuid().ToString("N");
    }
}

/// <summary>读取当前请求 trace_id 的便捷入口。</summary>
public static class TraceContext
{
    public static string? GetTraceId(this HttpContext context) =>
        context.Items.TryGetValue("mateos.trace_id", out object? value) ? value as string : null;
}
