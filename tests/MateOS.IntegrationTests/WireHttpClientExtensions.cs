using System.Net.Http.Json;
using System.Text.Json;

namespace MateOS.IntegrationTests;

/// <summary>
/// 测试侧的 wire 编码入口：统一按 snake_case 收发。
/// </summary>
/// <remarks>
/// <para>
/// API 侧全局策略是 <c>JsonNamingPolicy.SnakeCaseLower</c>（REST / WS / outbox / JSONB 四处同形），
/// 而 <c>PostAsJsonAsync</c> / <c>ReadFromJsonAsync</c> 默认用
/// <c>JsonSerializerDefaults.Web</c>（camelCase）。两者对不上时的表现是<b>静默</b>的：
/// 请求侧服务端把 <c>displayName</c> 当成字段缺失（返回 400 或落默认值），
/// 响应侧反序列化拿到 <c>default</c> —— 断言可能仍然「通过」，但其实什么都没验到。
/// </para>
/// <para>
/// 所以这里统一收口。<b>测试里不要再用裸的 <c>PostAsJsonAsync</c> / <c>ReadFromJsonAsync</c></b>：
/// 它们会带着 Web 默认命名策略，与服务端契约不一致。
/// </para>
/// </remarks>
internal static class WireHttpClientExtensions
{
    internal static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static Task<HttpResponseMessage> PostJsonAsync<T>(
        this HttpClient client,
        string url,
        T value) =>
        client.PostAsJsonAsync(url, value, Wire);

    internal static Task<T?> ReadWireAsync<T>(this HttpContent content) =>
        content.ReadFromJsonAsync<T>(Wire);
}
