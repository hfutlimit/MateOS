using System.Net.Http.Json;
using System.Text.Json;

namespace MateOS.E2ETests;

/// <summary>
/// e2e 测试的 wire 编码入口：统一按 snake_case 收发。
/// </summary>
/// <remarks>
/// <see cref="MateOS.IntegrationTests.WireHttpClientExtensions"/> 的 e2e 副本——
/// 不能直接跨 assembly 复用 internal 类，所以单独一份。两个文件必须保持严格同形，
/// 否则 e2e 与 IntegrationTests 的 wire 编码口径分叉（典型症状：响应反序列化为
/// <c>default</c>、断言看似通过）。
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
