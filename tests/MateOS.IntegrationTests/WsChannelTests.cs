using System.Net.WebSockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MateOS.Api.Auth;
using MateOS.Api.Channels;
using MateOS.Api.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace MateOS.IntegrationTests;

/// <summary>
/// E3 §4.4 的 WS 端到端用例：POST /channels/{id}/messages → 订阅该 channel 的 WS 客户端
/// 收到 <c>message.created</c> 广播。
/// </summary>
/// <remarks>
/// <para>
/// 测试用真实的 WebSocket（<see cref="ClientWebSocket"/>）直连 <c>ws://</c>，
/// 通过 HTTP 注册 + 拿到 access_token → 在 <c>hello</c> 帧带 token 鉴权 → 订阅 channel
/// → 另起一个 HTTP 客户端 POST message → WS 客户端收到推送。
/// </para>
/// <para>
/// fixture.ResetAsync 顺带 TRUNCATE E3 表（fixture 已加）。
/// </para>
/// </remarks>
public sealed class WsChannelTests(MateOsApiFixture fixture) : IClassFixture<MateOsApiFixture>
{
    private const string Password = "P@ssw0rd-1234";

    // ───────────────────────── M2-WS-001 ─────────────────────────

    [Fact]
    public async Task M2WS_001_订阅channel后POST消息应收到message_created广播()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "alice@example.com");

        // 准备一个 channel
        ProjectSummary project = await CreateProjectForAsync(client, token, "WS Demo");
        ChannelSummary channel = await CreateChannelForAsync(client, project.Id, "general");

        // 用 alice 的 token 连 WS，订阅 channel
        await using WsTestClient ws = await WsTestClient.ConnectAsync(fixture, token.AccessToken);

        await ws.HelloAsync();

        // hello 之后服务端会先回一帧 hello_ack，必须消费掉：
        // 否则下面的 ReceiveNextAsync 读到的是它，而不是期待的广播帧。
        JsonElement? helloAck = await ws.ReceiveNextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello_ack", helloAck!.Value.GetProperty("type").GetString());

        await ws.SubscribeAsync(channel.Id);

        // subscribe 同样会回一帧 subscribe.ack，消费掉它之后才是期望的广播帧。
        JsonElement? subscribeAck = await ws.ReceiveNextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("subscribe.ack", subscribeAck!.Value.GetProperty("type").GetString());

        // 略等 100ms 让 subscribe 索引生效
        await Task.Delay(100);

        // 另起一个 HTTP 客户端发消息
        HttpClient poster = fixture.CreateClient();
        poster.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var postBody = new PostMessageRequest(
            ContentType: "HUMAN",
            Content: JsonDocument.Parse("""{"text":"ws push test"}""").RootElement,
            ClientMsgId: null,
            ParentSeq: null);
        HttpResponseMessage post = await poster.PostJsonAsync(
            $"/channels/{channel.Id}/messages", postBody);
        Assert.Equal(System.Net.HttpStatusCode.Created, post.StatusCode);

        // WS 客户端应在合理时间内收到 message.created
        JsonElement? frame = await ws.ReceiveNextAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(frame);
        Assert.Equal("message.created", frame.Value.GetProperty("type").GetString());
        Assert.Equal(channel.Id, frame.Value.GetProperty("payload").GetProperty("channel_id").GetGuid());
        Assert.Equal(1L, frame.Value.GetProperty("payload").GetProperty("message").GetProperty("seq").GetInt64());
    }

    // ───────────────────────── M2-WS-002 ─────────────────────────

    [Fact]
    public async Task M2WS_002_resume应返since_seq之后的消息()
    {
        await fixture.ResetAsync();
        HttpClient client = fixture.CreateClient();
        TokenResponse token = await RegisterAsync(client, "bob@example.com");
        ProjectSummary project = await CreateProjectForAsync(client, token, "Resume Demo");
        ChannelSummary channel = await CreateChannelForAsync(client, project.Id, "general");

        // 先发 3 条消息
        for (int i = 0; i < 3; i++)
        {
            await PostHumanMessageAsync(client, channel.Id, $"msg {i}");
        }

        // 客户端连 WS，用 resume 拉 since_seq=1（应得 seq 2, 3）
        await using WsTestClient ws = await WsTestClient.ConnectAsync(fixture, token.AccessToken);
        await ws.HelloAsync();

        // 先消费 hello_ack（同上），再发 resume。
        JsonElement? helloAck = await ws.ReceiveNextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello_ack", helloAck!.Value.GetProperty("type").GetString());

        await ws.ResumeAsync(channel.Id, sinceSeq: 1L);

        JsonElement? ack = await ws.ReceiveNextAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(ack);
        Assert.Equal("resume.ack", ack.Value.GetProperty("type").GetString());
        Assert.Equal(2, ack.Value.GetProperty("payload").GetProperty("returned").GetInt32());
        Assert.Equal(3L, ack.Value.GetProperty("payload").GetProperty("last_seq").GetInt64());
    }

    // ───────────────────────── helpers ─────────────────────────

    private static async Task<TokenResponse> RegisterAsync(HttpClient client, string email)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            "/auth/register", new RegisterRequest(email, Password, email.Split('@')[0]));
        response.EnsureSuccessStatusCode();

        TokenResponse token = (await response.Content.ReadWireAsync<TokenResponse>())!;

        // 注册后立刻把 token 挂上：本文件的 helper 复用同一个 client，
        // 漏了这一步，后面所有请求（建模版、建 channel…）都会 401。
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        return token;
    }

    private static async Task<OrganizationSummary> CreateOrganizationAsync(HttpClient client, string name)
    {
        HttpResponseMessage response = await client.PostJsonAsync("/orgs", new CreateOrganizationRequest(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadWireAsync<OrganizationSummary>())!;
    }

    private static async Task<TeamSummary> CreateTeamAsync(HttpClient client, Guid orgId, string name)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            "/teams", new CreateTeamRequest(orgId, name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadWireAsync<TeamSummary>())!;
    }

    private static async Task<ProjectSummary> CreateProjectAsync(HttpClient client, Guid teamId, string name)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            "/projects", new CreateProjectRequest(teamId, name, null, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadWireAsync<ProjectSummary>())!;
    }

    private static async Task<ProjectSummary> CreateProjectForAsync(HttpClient client, TokenResponse token, string name)
    {
        OrganizationSummary org = await CreateOrganizationAsync(client, name + "-org");
        TeamSummary team = await CreateTeamAsync(client, org.Id, name + "-team");
        return await CreateProjectAsync(client, team.Id, name);
    }

    private static async Task<ChannelSummary> CreateChannelForAsync(HttpClient client, Guid projectId, string name)
    {
        HttpResponseMessage response = await client.PostJsonAsync(
            $"/projects/{projectId}/channels", new CreateChannelRequest(name, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadWireAsync<ChannelSummary>())!;
    }

    private static async Task PostHumanMessageAsync(HttpClient client, Guid channelId, string text)
    {
        var body = new PostMessageRequest(
            ContentType: "HUMAN",
            Content: JsonDocument.Parse($$"""{"text":"{{text}}"}""").RootElement,
            ClientMsgId: null,
            ParentSeq: null);
        HttpResponseMessage response = await client.PostJsonAsync(
            $"/channels/{channelId}/messages", body);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// 测试用 WS 客户端封装（<c>ClientWebSocket</c> + 帧读写 + envelope 序列化）。
/// </summary>
internal sealed class WsTestClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly string _accessToken;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private WsTestClient(WebSocket socket, string accessToken)
    {
        _socket = socket;
        _accessToken = accessToken;
    }

    /// <summary>
    /// 连上被测宿主的 <c>/ws</c>。
    /// </summary>
    /// <remarks>
    /// 必须用 TestServer 的 <c>WebSocketClient</c>，不能用 <c>ClientWebSocket</c>：
    /// <c>WebApplicationFactory</c> 是 in-memory 宿主、没有真实监听端口，
    /// <c>ClientWebSocket</c> 会去连 <c>localhost:80</c> 并报 Connection refused。
    /// </remarks>
    public static async Task<WsTestClient> ConnectAsync(MateOsApiFixture fixture, string accessToken)
    {
        WebSocket socket = await fixture.Server
            .CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);

        return new WsTestClient(socket, accessToken);
    }

    public async Task HelloAsync()
    {
        var hello = new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "hello",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new { access_token = _accessToken },
        };

        await SendJsonAsync(hello);
    }

    public async Task SubscribeAsync(Guid channelId)
    {
        await SendJsonAsync(new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "subscribe",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new { channel_id = channelId },
        });
    }

    /// <summary>
    /// 用 <c>agent_token</c> 建立 Agent 会话（M3b Phase 2）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="HelloAsync"/> 互斥：服务端要求 hello 帧里 access_token / agent_token
    /// 只能给一个，两个都给会被当作「会话主体有两种解释」直接拒掉。
    /// </remarks>
    public async Task HelloAsAgentAsync(string agentToken)
    {
        await SendJsonAsync(new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "hello",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new { agent_token = agentToken },
        });
    }

    public async Task ResumeAsync(Guid channelId, long sinceSeq)
    {
        await SendJsonAsync(new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "resume",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new { channel_id = channelId, since_seq = sinceSeq },
        });
    }

    public async Task<JsonElement?> ReceiveNextAsync(TimeSpan timeout)
    {
        var buffer = new byte[1024 * 64];
        using var ms = new MemoryStream();

        using CancellationTokenSource cts = new(timeout);
        WebSocketReceiveResult result;

        do
        {
            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Position = 0;
        using JsonDocument doc = await JsonDocument.ParseAsync(ms);
        return doc.RootElement.Clone();
    }

    private async Task SendJsonAsync(object envelope)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, s_jsonOptions);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch
            {
                // 已被服务端关掉
            }
        }

        _socket.Dispose();
    }
}
