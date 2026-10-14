using System.Text.Json.Nodes;
using MateOS.Contracts.Protocol;
// 与 System.Threading.ExecutionContext 同名，必须别名（同 ConnectorContractTests 的做法）
using ExecutionContext = MateOS.Contracts.Protocol.ExecutionContext;

namespace MateOS.UnitTests.Protocol;

/// <summary>
/// E5 → E7 注入的纯函数部分：<c>context_refs</c> 快照合入 <c>memory_refs</c>。
/// </summary>
/// <remarks>
/// <para>
/// 只覆盖 <see cref="ExecutionPayloadMapper.WithMemoryRefs"/> —— 它是整条注入链路上
/// 唯一不需要 DB 的一环。
/// </para>
/// <para>
/// <b>未覆盖</b>：<c>MemoryRefInjector.InjectAsync</c> 的可见性过滤
/// （跨 Project 零泄漏、PERSONAL 仅归属者可见）依赖真实 PG，归集成测试；
/// 见 AgentBoard doc 249 §3.1 的验收项。**本文件不能替代那条验收**。
/// </para>
/// </remarks>
public sealed class MemoryRefsInjectionTests
{
    [Fact]
    public void 注入的memory_refs应能被契约mapper按序读回()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        string merged = ExecutionPayloadMapper.WithMemoryRefs(null, [first, second]);

        ExecutionContext context = ExecutionPayloadMapper.ToContext(merged);

        Assert.NotNull(context.MemoryRefs);
        Assert.Equal(new[] { first, second }, context.MemoryRefs!.ToArray());
    }

    [Fact]
    public void 注入应保留快照里原有的键()
    {
        Guid memoryId = Guid.NewGuid();

        string merged = ExecutionPayloadMapper.WithMemoryRefs(
            """{"project_id":"p1","work_item_id":"w1"}""",
            [memoryId]);

        ExecutionContext context = ExecutionPayloadMapper.ToContext(merged);

        // 原有键一个不丢
        Assert.Equal("p1", context.Refs!["project_id"]!.GetValue<string>());
        Assert.Equal("w1", context.Refs!["work_item_id"]!.GetValue<string>());

        // 同时带上了 memory_refs
        Assert.Equal(new[] { memoryId }, context.MemoryRefs!.ToArray());
    }

    [Fact]
    public void 注入遇到非法JSON应重建为契约形状的对象()
    {
        Guid memoryId = Guid.NewGuid();

        string merged = ExecutionPayloadMapper.WithMemoryRefs("not-json", [memoryId]);

        ExecutionContext context = ExecutionPayloadMapper.ToContext(merged);

        // refs 在契约里必须是 object（dispatch.json 是 additionalProperties: false）。
        // 非法原文不得以字符串形式留在快照里 —— 那会让下游形状非法。
        JsonObject refs = context.Refs!.AsObject();

        Assert.Single(refs);
        Assert.True(refs.ContainsKey("memory_refs"));
        Assert.Equal(new[] { memoryId }, context.MemoryRefs!.ToArray());
    }
}
