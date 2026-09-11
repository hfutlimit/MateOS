using System.Text.Json;
using MateOS.Api.Channels;
using MateOS.Api.Persistence;
using MateOS.Domain.Agent;
using MateOS.Domain.Channel;
using Microsoft.EntityFrameworkCore;
using DbAgentExecution = MateOS.Api.Persistence.AgentExecution;
using DbAgentDispatchInbox = MateOS.Api.Persistence.AgentDispatchInbox;
using DbExecutionAttempt = MateOS.Api.Persistence.ExecutionAttempt;

namespace MateOS.Api.Agents;

/// <summary>
/// 把 <c>execution.dispatch</c> 实时推给 Agent 的活跃 WS 会话（M3b Phase 2）。
/// </summary>
/// <remarks>
/// <para>
/// 定位：<b>纯 transport，不是事实源</b>。它只读 DB 已有的 dispatch 事实并推一帧，
/// <b>不写库</b>——尤其不写 <c>dispatch_sent_at</c>：
/// 那个字段记录的是「Runtime 决定派发」的时刻（与 Execution 同事务写入），
/// 而本方法只是一个投递尝试，失败与否不该改写业务事实。
/// </para>
/// <para>
/// 因此推送失败<b>不是错误</b>：Agent 仍可用 <c>GET /agents/{id}/executions/inbox</c> 轮询兜底。
/// 这条兜底路径必须一直保留——WS 只在 Agent 在线时有意义，
/// 把「离线 Agent 拿不到工作」变成阻塞性故障是最容易犯的错。
/// </para>
/// <para>
/// 重复投递是<b>允许</b>的（立即推 + 后续 relay/看门狗可重推）：
/// 客户端按 <c>(execution_id, attempt_no)</c> 幂等，重复收到只重绑会话并再回 ACK，
/// 不得重复执行（detailed/10 §4）。
/// </para>
/// </remarks>
public sealed class AgentDispatchNotifier(
    MateOSDbContext db,
    WsSender sender,
    ILogger<AgentDispatchNotifier> log)
{
    /// <summary>
    /// 推送一次 dispatch。
    /// </summary>
    /// <param name="executionId">Execution id（<c>agent_executions.id</c>）。</param>
    /// <param name="source">触发来源（仅用于日志排查：e7-create / e4-accept / work-assign）。</param>
    /// <returns>至少有一个 Agent 会话收到返回 <c>true</c>；无会话或已终态返回 <c>false</c>。</returns>
    public async Task<bool> PushDispatchAsync(Guid executionId, string source, CancellationToken ct)
    {
        DbAgentExecution? execution = await db.AgentExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executionId, ct);

        if (execution is null)
        {
            log.LogWarning("推送 dispatch 失败：execution {ExecutionId} 不存在（source={Source}）",
                executionId, source);
            return false;
        }

        // 终态 / 无 active attempt：没有可派发的动作。
        // 无 attempt 属数据异常（§4.2 表中「无 attempt」一行），记 warning 而不是静默。
        if (!ExecutionStatusMap.TryParse(execution.Status, out ExecutionStatus status) || status.IsTerminal())
        {
            log.LogDebug("跳过 dispatch 推送：execution {ExecutionId} 已是终态 {Status}", executionId, execution.Status);
            return false;
        }

        if (execution.ActiveAttemptNo is not { } attemptNo)
        {
            log.LogWarning("跳过 dispatch 推送：execution {ExecutionId} 没有 active attempt（数据异常）", executionId);
            return false;
        }

        DbExecutionAttempt? attempt = await db.ExecutionAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.ExecutionId == executionId && a.AttemptNo == attemptNo, ct);

        if (attempt is null)
        {
            log.LogWarning("跳过 dispatch 推送：execution {ExecutionId} attempt_no={AttemptNo} 不存在",
                executionId, attemptNo);
            return false;
        }

        string idempotencyKey = await db.AgentDispatchInbox
            .AsNoTracking()
            .Where(x => x.ExecutionId == executionId && x.AttemptNo == attemptNo)
            .Select(x => x.IdempotencyKey)
            .FirstOrDefaultAsync(ct) ?? $"execution:{executionId}:{attemptNo}";

        long? deadlineS = execution.DeadlineAt is { } deadline
            ? Math.Max(0, (long)(deadline - DateTimeOffset.UtcNow).TotalSeconds)
            : null;

        var envelope = new
        {
            id = Guid.NewGuid().ToString("N"),
            type = WsMessageTypeMap.ExecutionDispatchValue,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new
            {
                execution_id = execution.Id,
                attempt_no = attemptNo,
                collaboration_request_id = execution.CollaborationRequestId,
                work_item_ref = execution.WorkItemRef,
                input = ParseJsonOrNull(execution.Input),
                context_refs = ParseJsonOrNull(execution.ContextRefs),
                deadline_s = deadlineS,
                idempotency_key = idempotencyKey,
            },
        };

        bool delivered = await sender.SendToAgentAsync(execution.AgentId, envelope, ct);

        if (delivered)
        {
            log.LogInformation(
                "已实时推送 execution.dispatch：execution={ExecutionId} attempt={AttemptNo} agent={AgentId} source={Source}",
                executionId, attemptNo, execution.AgentId, source);
        }
        else
        {
            // 不算失败：Agent 可能根本没在线，轮询 inbox 是设计内的兜底
            log.LogInformation(
                "Agent 无活跃 WS 会话，dispatch 留待轮询领取：execution={ExecutionId} agent={AgentId} source={Source}",
                executionId, execution.AgentId, source);
        }

        return delivered;
    }

    /// <summary>
    /// 把 jsonb 文本解析成可嵌入 envelope 的 <see cref="JsonElement"/>。
    /// </summary>
    /// <remarks>
    /// 必须 <c>Clone()</c>：<see cref="JsonDocument"/> 一释放，它的 RootElement 就失效
    /// （后续序列化会抛 ObjectDisposedException），而这里必须立刻释放文档。
    /// 解析失败只丢这个字段、不让整条 dispatch 发不出去——「指令送不到」
    /// 比「指令里少一个字段」危害大得多。
    /// </remarks>
    private JsonElement? ParseJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "dispatch payload 里的 jsonb 字段无法解析，该字段以 null 下发");
            return null;
        }
    }
}
