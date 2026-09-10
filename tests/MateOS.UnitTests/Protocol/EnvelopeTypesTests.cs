using MateOS.Contracts.Protocol;

namespace MateOS.UnitTests.Protocol;

/// <summary>
/// 保证协议常量与 detailed/03 §2 的 message type 清单逐条对应。
/// 契约漂移是 S1 最容易出的事故——Runtime 与 stub 各按自己的理解实现，联调时才炸。
/// </summary>
public sealed class EnvelopeTypesTests
{
    [Fact]
    public void 类型集合应覆盖连接管理与两域协议()
    {
        // 03 §2：4 条连接管理 + 4 条 E4 + 8 条 E7 + 1 条 activity = 17
        Assert.Equal(17, EnvelopeTypes.All.Count);

        Assert.Contains(EnvelopeTypes.Hello, EnvelopeTypes.All);
        Assert.Contains(EnvelopeTypes.HelloAck, EnvelopeTypes.All);
        Assert.Contains(EnvelopeTypes.HelloNack, EnvelopeTypes.All);
        Assert.Contains(EnvelopeTypes.Heartbeat, EnvelopeTypes.All);
    }

    [Fact]
    public void 每个类型都必须声明方向()
    {
        foreach (string type in EnvelopeTypes.All)
        {
            Assert.True(EnvelopeTypes.Directions.ContainsKey(type), $"类型 {type} 缺方向声明");
        }

        Assert.Equal(EnvelopeTypes.All.Count, EnvelopeTypes.Directions.Count);
    }

    [Fact]
    public void 命名空间前缀应与域归属一致()
    {
        // SYSTEM_DESIGN §6.5：collaboration.* 属 E4，execution.* 属 E7，严格分离
        foreach (string type in EnvelopeTypes.All.Where(t => t.StartsWith("collaboration.", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("execution.", type, StringComparison.Ordinal);
        }

        foreach (string type in EnvelopeTypes.All.Where(t => t.StartsWith("execution.", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("collaboration.", type, StringComparison.Ordinal);
        }

        Assert.Equal(4, EnvelopeTypes.All.Count(t => t.StartsWith("collaboration.", StringComparison.Ordinal)));
        Assert.Equal(8, EnvelopeTypes.All.Count(t => t.StartsWith("execution.", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("execution.dispatch", EnvelopeDirection.RuntimeToAgent)]
    [InlineData("execution.dispatch_ack", EnvelopeDirection.AgentToRuntime)]
    [InlineData("execution.event", EnvelopeDirection.AgentToRuntime)]
    [InlineData("execution.result", EnvelopeDirection.AgentToRuntime)]
    [InlineData("execution.error", EnvelopeDirection.AgentToRuntime)]
    [InlineData("execution.cancel", EnvelopeDirection.RuntimeToAgent)]
    [InlineData("execution.resume_request", EnvelopeDirection.AgentToRuntime)]
    [InlineData("execution.resume_ack", EnvelopeDirection.RuntimeToAgent)]
    [InlineData("collaboration.request", EnvelopeDirection.RuntimeToAgent)]
    [InlineData("collaboration.decision", EnvelopeDirection.AgentToRuntime)]
    [InlineData("collaboration.cancelled", EnvelopeDirection.RuntimeToAgent)]
    [InlineData("collaboration.resolved", EnvelopeDirection.RuntimeToAgent)]
    [InlineData("hello", EnvelopeDirection.AgentToRuntime)]
    [InlineData("heartbeat", EnvelopeDirection.AgentToRuntime)]
    [InlineData("status", EnvelopeDirection.AgentToRuntime)]
    public void 关键消息方向应与文档一致(string type, EnvelopeDirection expected)
    {
        Assert.Equal(expected, EnvelopeTypes.Directions[type]);
    }

    [Fact]
    public void dispatch收据不应表达业务接受()
    {
        // 03 §2 v0.7：ACK 没有 accepted=false 分支，只有 received + protocol_error
        var ok = new ExecutionDispatchAckPayload(Guid.NewGuid(), 1, Received: true, ProtocolError: null);
        Assert.True(ok.Received);
        Assert.False(ok.IsProtocolError);

        var anomaly = new ExecutionDispatchAckPayload(
            Guid.NewGuid(), 1, Received: true, ProtocolError: ProtocolErrors.UnknownExecution);
        Assert.True(anomaly.Received);
        Assert.True(anomaly.IsProtocolError);
    }
}
