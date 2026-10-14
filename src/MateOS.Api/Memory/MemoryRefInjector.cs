using System.Text.Json;
using System.Text.Json.Nodes;
using MateOS.Api.Persistence;
using MateOS.Contracts.Protocol;
using MateOS.Domain.Memory;
using Microsoft.EntityFrameworkCore;

namespace MateOS.Api.Memory;

/// <summary>
/// E5 → E7 注入：把「已批准记忆」的 id 写进 execution 的 <c>context_refs</c> 快照的
/// <c>memory_refs</c> 键，供 <c>execution.dispatch</c> 下发（S2 DoD 的
/// 「propose → 人审 → 检索 → dispatch 注入」闭环的最后一段）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须在这里写、而不是在推送时补</b>：<c>context_refs</c> 是<b>执行快照</b>，
/// 一旦落库就不再变；WS 推送与 HTTP 轮询两条通道共用
/// <see cref="ExecutionPayloadMapper"/> 读同一份快照。若改在推送侧补，两条通道会各算一次，
/// 退化成「推送有、轮询没有」。
/// </para>
/// <para>
/// <b>契约约束</b>：<c>contracts/schemas/execution/dispatch.json</c> 是
/// <c>additionalProperties: false</c>，<c>context</c> 只认
/// <c>memory_refs / recent_messages / permissions / refs</c> 四个键。
/// 本类<b>只写 memory_refs</b>，不新增字段；<c>recent_messages</c> / <c>permissions</c>
/// 是 V2 的注入位（<c>contracts/README.md</c> §4 明确「注入三件套 S1 未实现」）。
/// </para>
/// <para>
/// <b>与「检索」的关系</b>（<c>detailed/05</c> §6.3）：可见性子句完全对齐 ——
/// <c>(PERSONAL AND owner_user_id = 触发者)</c> 并集
/// <c>(PROJECT AND project_id = 本执行所属 project)</c>。
/// PERSONAL 行<b>不参与 project 过滤</b>，否则「PERSONAL 记忆跨 Project 可被自己读到」不成立。
/// </para>
/// </remarks>
public static class MemoryRefInjector
{
    /// <summary>单次注入的记忆条数上限。</summary>
    /// <remarks>
    /// <para>
    /// 取 <c>detailed/05</c> §6.3 检索的 <c>LIMIT 20</c>。
    /// </para>
    /// <para>
    /// <b>⚠️ 待拍板</b>：「注入」的选择策略（条数 / 排序 / 是否按关键词过滤）在
    /// PRD、SYSTEM_DESIGN、<c>detailed/05</c>、E5 epic 中<b>均无明文定义</b> ——
    /// 文档只声明了 E7 会注入 <c>memory_refs</c>。
    /// 原型 <c>docs/UI design/P5-channel-prototype.html</c> 的展示文案写「注入 2 条项目记忆」，
    /// 与 §6.3 的 20 不一致。此处取 20（与检索一致）并登记为开放项，不自行发明新数字。
    /// </para>
    /// </remarks>
    public const int MaxRefs = 20;

    /// <summary>
    /// 计算并合入 <c>memory_refs</c>。返回新的 <c>context_refs</c> JSON 字符串。
    /// </summary>
    /// <param name="db">调用方已在事务内的 DbContext（与 Execution 同一次 SaveChanges）。</param>
    /// <param name="contextRefsJson">原始快照（自由形态 jsonb）；应为 <c>{project_id, ...}</c>。</param>
    /// <param name="prompt">检索词。<c>null</c>/空 时不按关键词过滤（退化为按 scope 取最近 N 条）。</param>
    /// <param name="actorUserId">
    /// 触发者（人）。<c>null</c> 表示触发主体是 Agent 或系统 —— 此时<b>不注入 PERSONAL</b>，
    /// 因为 PERSONAL 记忆必属某个 user（DDL <c>chk_memory_item_scope_owner</c>），
    /// 而 Agent 与 user 之间没有 owner 映射。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>保守原则</b>：拿不到 <c>project_id</c> 且没有 <paramref name="actorUserId"/> 时
    /// 直接原样返回，不注入。宁可少注入，不可越 scope。
    /// </para>
    /// <para>
    /// <b>不做</b>：不写 <c>recent_messages</c> / <c>permissions</c>；不改 <c>refs</c>；
    /// 不按 <c>accessible_project_ids()</c> 展开（本注入的服务端已知唯一目标 project，
    /// 展开会把「用户可访问的其它 project」的记忆一并带进这次执行 —— 那是越权）。
    /// </para>
    /// <para>
    /// <b>关键词过滤的通配符</b>：沿用现有 <c>MemoryEndpoints.SearchAsync</c> 的口径
    /// （ILIKE，未转义 <c>%</c> / <c>_</c>）。此处刻意保持一致以免「搜索」与「注入」
    /// 对同一词给出不同结果；未转义本身是既有行为，已随 §1.1 偏差一并登记，不在本次改。
    /// </para>
    /// </remarks>
    public static async Task<string?> InjectAsync(
        MateOSDbContext db,
        string? contextRefsJson,
        string? prompt,
        Guid? actorUserId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        Guid? projectId = ReadGuid(contextRefsJson, "project_id");

        if (projectId is null && actorUserId is null)
        {
            // 无从判定可见范围 → 不注入（宁少不漏）
            return contextRefsJson;
        }

        IQueryable<MemoryItem> query = db.MemoryItems;

        // 可见性：PERSONAL（触发者本人）∪ PROJECT（本执行所属 project）。
        // 关键：PERSONAL 分支不带 project 过滤，否则「个人记忆跨 Project 可被自己读到」不成立。
        query = query.Where(i =>
            (i.Type == MemoryTypeMap.PersonalValue && actorUserId != null && i.OwnerUserId == actorUserId)
            || (i.Type != MemoryTypeMap.PersonalValue && projectId != null && i.ProjectId == projectId));

        // 有检索词时按词过滤（对齐现有 SearchAsync 的 ILIKE 口径）
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            string needle = prompt.Length > 256 ? prompt[..256] : prompt;

            query = query.Where(i =>
                EF.Functions.ILike(i.SearchText, $"%{needle}%")
                || EF.Functions.ILike(i.Title, $"%{needle}%"));
        }

        List<Guid> refs = await query
            .OrderByDescending(i => i.CreatedAt)
            .Take(MaxRefs)
            .Select(i => i.Id)
            .ToListAsync(ct);

        return refs.Count == 0
            ? contextRefsJson
            : ExecutionPayloadMapper.WithMemoryRefs(contextRefsJson, refs);
    }

    /// <summary>从自由形态快照里读一个 GUID 值；非法 JSON 或缺键一律返回 <c>null</c>。</summary>
    private static Guid? ReadGuid(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is JsonObject obj
                && obj.TryGetPropertyValue(key, out JsonNode? value)
                && value is JsonValue jsonValue
                && jsonValue.TryGetValue(out string? text)
                && Guid.TryParse(text, out Guid id))
            {
                return id;
            }
        }
        catch (JsonException)
        {
            // 非法 JSON → 当作没有该键
        }

        return null;
    }
}
