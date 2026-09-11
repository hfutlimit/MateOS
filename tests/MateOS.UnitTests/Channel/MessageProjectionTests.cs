using System.Text.Json;
using MateOS.Domain.Channel;

namespace MateOS.UnitTests.Channel;

/// <summary>
/// 覆盖 E3 §3.1 5 形态 content schema 校验 + §7 F8 投影纪律（不得带事实源字段）。
/// </summary>
public sealed class MessageProjectionTests
{
    // ── HUMAN 形态 ──

    [Fact]
    public void Human应要求text字段()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"text":"hello"}""");
        Assert.Null(MessageProjection.Validate(MessageContentType.Human, doc.RootElement));
    }

    [Fact]
    public void Human应拒绝空text()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"text":""}""");
        string? error = MessageProjection.Validate(MessageContentType.Human, doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("text 不能为空", error);
    }

    [Fact]
    public void Human应拒绝超过32KB的text()
    {
        string longText = new string('a', 32 * 1024 + 1);
        string json = $$"""{"text":"{{longText}}"}""";
        using JsonDocument doc = JsonDocument.Parse(json);
        string? error = MessageProjection.Validate(MessageContentType.Human, doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("32KB", error);
    }

    [Fact]
    public void Human应接受32KB上界()
    {
        string longText = new string('a', 32 * 1024);
        string json = $$"""{"text":"{{longText}}"}""";
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Null(MessageProjection.Validate(MessageContentType.Human, doc.RootElement));
    }

    [Fact]
    public void Human应拒绝非对象()
    {
        using JsonDocument doc = JsonDocument.Parse("\"just a string\"");
        string? error = MessageProjection.Validate(MessageContentType.Human, doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("必须是对象", error);
    }

    // ── DECISION 形态（F8 投影纪律）──

    [Fact]
    public void Decision应要求decisionRef为UUID()
    {
        using JsonDocument doc = JsonDocument.Parse($$"""{"decision_ref":"{{Guid.NewGuid()}}","summary":"ACCEPT"}""");
        Assert.Null(MessageProjection.Validate(MessageContentType.Decision, doc.RootElement));
    }

    [Fact]
    public void Decision应拒绝非UUID的decisionRef()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"decision_ref":"not-a-uuid"}""");
        string? error = MessageProjection.Validate(MessageContentType.Decision, doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("UUID", error);
    }

    [Fact]
    public void Decision应拒绝analysis字段_v04投影纪律()
    {
        // F8：DECISION 形态不得复制事实源字段（analysis / missing / reason）
        string decisionRef = Guid.NewGuid().ToString();
        string json = "{\"decision_ref\":\"" + decisionRef + "\",\"summary\":\"ACCEPT\",\"analysis\":{\"score\":0.9}}";
        using JsonDocument doc = JsonDocument.Parse(json);
        string? error = MessageProjection.Validate(MessageContentType.Decision, doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("analysis", error);
    }

    [Fact]
    public void Decision应拒绝missing字段_v04投影纪律()
    {
        string decisionRef = Guid.NewGuid().ToString();
        using JsonDocument doc = JsonDocument.Parse($$"""
            {"decision_ref":"{{decisionRef}}","missing":["need context"]}
            """);
        string? error = MessageProjection.Validate(MessageContentType.Decision, doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("missing", error);
    }

    [Fact]
    public void Decision应拒绝reason字段_v04投影纪律()
    {
        string decisionRef = Guid.NewGuid().ToString();
        using JsonDocument doc = JsonDocument.Parse($$"""
            {"decision_ref":"{{decisionRef}}","reason":"out of scope"}
            """);
        string? error = MessageProjection.Validate(MessageContentType.Decision, doc.RootElement);
        Assert.NotNull(error);
        Assert.Contains("reason", error);
    }

    // ── AGENT_OUTPUT 形态 ──

    [Fact]
    public void AgentOutput应要求executionRef()
    {
        using JsonDocument doc = JsonDocument.Parse($$"""{"execution_ref":"{{Guid.NewGuid()}}"}""");
        Assert.Null(MessageProjection.Validate(MessageContentType.AgentOutput, doc.RootElement));
    }

    [Fact]
    public void AgentOutput应接受artifactId可选字段()
    {
        using JsonDocument doc = JsonDocument.Parse($$"""
            {"execution_ref":"{{Guid.NewGuid()}}","artifact_id":"{{Guid.NewGuid()}}"}
            """);
        Assert.Null(MessageProjection.Validate(MessageContentType.AgentOutput, doc.RootElement));
    }

    [Fact]
    public void AgentOutput应拒绝非UUID的executionRef()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"execution_ref":"oops"}""");
        string? error = MessageProjection.Validate(MessageContentType.AgentOutput, doc.RootElement);
        Assert.NotNull(error);
    }

    // ── SYSTEM 形态 ──

    [Fact]
    public void System应要求text字段()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"text":"Agent 接入失败","actions":[]}""");
        Assert.Null(MessageProjection.Validate(MessageContentType.System, doc.RootElement));
    }

    [Fact]
    public void System应拒绝空text()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"text":""}""");
        string? error = MessageProjection.Validate(MessageContentType.System, doc.RootElement);
        Assert.NotNull(error);
    }

    // ── MEMORY_REQUEST 形态 ──

    [Fact]
    public void MemoryRequest应要求memoryProposalRef()
    {
        using JsonDocument doc = JsonDocument.Parse($$"""
            {"memory_proposal_ref":"{{Guid.NewGuid()}}","summary":"申请写入项目记忆"}
            """);
        Assert.Null(MessageProjection.Validate(MessageContentType.MemoryRequest, doc.RootElement));
    }

    [Fact]
    public void MemoryRequest应拒绝非UUID的memoryProposalRef()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"memory_proposal_ref":"nope"}""");
        string? error = MessageProjection.Validate(MessageContentType.MemoryRequest, doc.RootElement);
        Assert.NotNull(error);
    }

    // ── 通用：未知 content_type ──

    [Fact]
    public void 未知ContentType应返错误()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"text":"x"}""");
        // 强制传入非法枚举
        string? error = MessageProjection.Validate((MessageContentType)999, doc.RootElement);
        Assert.NotNull(error);
    }
}
