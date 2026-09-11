using System.Text.Json;
using MateOS.Domain.Channel;

namespace MateOS.UnitTests.Channel;

/// <summary>
/// 覆盖 WS envelope 顶层 + 5 类 payload 校验。
/// </summary>
public sealed class WsEnvelopeTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("hello_ack")]
    [InlineData("hello_nack")]
    [InlineData("heartbeat")]
    [InlineData("subscribe")]
    [InlineData("unsubscribe")]
    [InlineData("resume")]
    [InlineData("message.created")]
    [InlineData("channel.archived")]
    [InlineData("execution.dispatch")]
    [InlineData("error")]
    public void TryParse应支持所有合法type(string wire)
    {
        Assert.True(WsMessageTypeMap.TryParse(wire, out WsMessageType type));
        Assert.Equal(wire, type.ToWireValue());
    }

    [Theory]
    [InlineData("HELLO")]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse应拒绝非法type(string? wire)
    {
        Assert.False(WsMessageTypeMap.TryParse(wire, out _));
    }

    [Fact]
    public void Validate应接受合法hello()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {"id":"6a8e1b8e-9d3a-4d3e-8a4d-3a8e1b8e9d3a","type":"hello","ts":1736380800000,"payload":{"access_token":"abc"}}
            """);
        Assert.Null(WsEnvelope.Validate(doc.RootElement));
    }

    [Fact]
    public void Validate应拒绝非对象()
    {
        using JsonDocument doc = JsonDocument.Parse("\"just a string\"");
        string? error = WsEnvelope.Validate(doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("必须是对象", error);
    }

    [Fact]
    public void Validate应拒绝缺失id()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"type":"hello"}""");
        string? error = WsEnvelope.Validate(doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("id", error);
    }

    [Fact]
    public void Validate应拒绝非法id()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"id":"not-uuid","type":"hello"}""");
        string? error = WsEnvelope.Validate(doc.RootElement);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate应拒绝非法type()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {"id":"6a8e1b8e-9d3a-4d3e-8a4d-3a8e1b8e9d3a","type":"unknown_type"}
            """);
        string? error = WsEnvelope.Validate(doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("type", error);
    }

    // ── subscribe / unsubscribe payload ──

    [Fact]
    public void Subscribe应要求channel_id()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"channel_id":"6a8e1b8e-9d3a-4d3e-8a4d-3a8e1b8e9d3a"}""");
        Assert.Null(WsEnvelope.ValidateClientRequest(WsMessageType.Subscribe, doc.RootElement));
    }

    [Fact]
    public void Subscribe应拒绝非UUID的channel_id()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"channel_id":"not-uuid"}""");
        string? error = WsEnvelope.ValidateClientRequest(WsMessageType.Subscribe, doc.RootElement);
        Assert.NotNull(error);
    }

    // ── resume payload ──

    [Fact]
    public void Resume应允许省略since_seq_默认从0开始()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"channel_id":"6a8e1b8e-9d3a-4d3e-8a4d-3a8e1b8e9d3a"}""");
        Assert.Null(WsEnvelope.ValidateClientRequest(WsMessageType.Resume, doc.RootElement));
    }

    [Fact]
    public void Resume应接受正整数since_seq()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {"channel_id":"6a8e1b8e-9d3a-4d3e-8a4d-3a8e1b8e9d3a","since_seq":100}
            """);
        Assert.Null(WsEnvelope.ValidateClientRequest(WsMessageType.Resume, doc.RootElement));
    }

    [Fact]
    public void Resume应拒绝负数since_seq()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {"channel_id":"6a8e1b8e-9d3a-4d3e-8a4d-3a8e1b8e9d3a","since_seq":-1}
            """);
        string? error = WsEnvelope.ValidateClientRequest(WsMessageType.Resume, doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("不能为负", error);
    }

    [Fact]
    public void Resume应拒绝非整数since_seq()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {"channel_id":"6a8e1b8e-9d3a-4d3e-8a4d-3a8e1b8e9d3a","since_seq":"100"}
            """);
        string? error = WsEnvelope.ValidateClientRequest(WsMessageType.Resume, doc.RootElement);
        Assert.NotNull(error);
    }

    [Fact]
    public void Heartbeat应允许空payload()
    {
        using JsonDocument doc = JsonDocument.Parse("""{}""");
        Assert.Null(WsEnvelope.ValidateClientRequest(WsMessageType.Heartbeat, doc.RootElement));
    }

    [Fact]
    public void 不应接受server类型作为client_request()
    {
        using JsonDocument doc = JsonDocument.Parse("""{}""");
        string? error = WsEnvelope.ValidateClientRequest(WsMessageType.MessageCreated, doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("不应作为 client request", error);
    }

    /// <summary>
    /// 出站指令不能由客户端伪造回灌：否则 Agent 可以把一条 dispatch 当成自己收到的指令。
    /// </summary>
    [Fact]
    public void 不应接受execution_dispatch作为client_request()
    {
        using JsonDocument doc = JsonDocument.Parse("""{}""");
        string? error = WsEnvelope.ValidateClientRequest(WsMessageType.ExecutionDispatch, doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("不应作为 client request", error);
    }
}
