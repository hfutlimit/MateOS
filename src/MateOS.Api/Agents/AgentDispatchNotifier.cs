using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MateOS.Api.Channels;
using MateOS.Api.Persistence;
using MateOS.Contracts.Protocol;
using MateOS.Domain.Agent;
using Microsoft.EntityFrameworkCore;
using DbAgentExecution = MateOS.Api.Persistence.AgentExecution;
using DbExecutionAttempt = MateOS.Api.Persistence.ExecutionAttempt;
using DbWorkItem = MateOS.Api.Persistence.WorkItem;

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
/// payload 用 <see cref="ExecutionDispatchPayload"/> 构造，<b>不写匿名对象</b>：
/// 契约层（<c>MateOS.Contracts</c>）是 wire 形状的事实源，匿名字段名拼错不会有任何编译期提示，
/// 而契约层有 schema 校验（<c>contracts/schemas/execution/dispatch.json</c>）。
/// 轮询 inbox 返回同一个类型 —— 两条通道一份形状。
/// </para>
/// <para>
/// 因此推送失败<b>不是错误</b>：Agent 仍可用 <c>GET /agents/{id}/executions/inbox</c> 轮询兜底。
/// 这条兜底路径必须一直保留——WS 只在 Agent 在线时有意义，
/// 把「离线 Agent 拿不到工作」变成阻塞性故障是最容易犯的错。
/// </para>
/// <para>
/// 重复投递是<b>允许</b>的：客户端按 <c>(execution_id, attempt_no)</c> 幂等，
/// 重复收到只重绑会话并再回 ACK，不得重复执行（detailed/10 §4）。
/// </para>
/// </remarks>
public sealed class AgentDispatchNotifier(
    MateOSDbContext db,
    WsSender sender,
    ILogger<AgentDispatchNotifier> log)
{
    /// <summary>
    /// payload 序列化选项：snake_case，且<b>不忽略 null</b>。
    /// </summary>
    /// <remarks>
    /// 不忽略 null 是刻意的：契约 schema 把可空字段（collaboration_request_id /
    /// work_item_ref / deadline_s）列为 required，用「字段缺失」表达「没有值」会让
    /// 客户端无法区分「不支持」与「本次为空」，也让推送与轮询的形状分叉
    /// （WsSender 的 envelope 级选项是忽略 null 的）。
    /// </remarks>
    private static readonly JsonSerializerOptions s_contractOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,

        // 必须与 HTTP 侧（ASP.NET Core 默认 Encoder）取同一套转义策略。
        // 默认 JavaScriptEncoder 会把中文写成 \uXXXX，于是同一份 dispatch
        // 经 WS 推送与 HTTP 轮询拿到**字节不同**的 JSON —— 而这两条通道
        // 形状逐字段一致是被反复声明的硬约束（SDK 只应有一份解析代码）。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
        // 无 attempt 属数据异常（03 §4.2 表中「无 attempt」一行），记 warning 而不是静默。
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

        WorkItemRef? workItemRef = await LoadWorkItemRefAsync(execution.WorkItemRef, ct);

        int? deadlineS = execution.DeadlineAt is { } deadline
            ? (int)Math.Clamp((long)(deadline - DateTimeOffset.UtcNow).TotalSeconds, 0, int.MaxValue)
            : null;

        var payload = new ExecutionDispatchPayload(
            ExecutionId: execution.Id,
            AttemptNo: attemptNo,
            CollaborationRequestId: execution.CollaborationRequestId,
            WorkItemRef: workItemRef,
            Input: ExecutionPayloadMapper.ToInput(execution.Input),
            Context: ExecutionPayloadMapper.ToContext(execution.ContextRefs),
            DeadlineS: deadlineS,
            IdempotencyKey: idempotencyKey);

        var envelope = new Envelope(
            Type: EnvelopeTypes.ExecutionDispatch,
            Id: Guid.NewGuid(),
            Ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload: JsonSerializer.SerializeToNode(payload, s_contractOptions));

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
    /// 取 WorkItem 引用（provider_key + work_item_id + external_ref）。
    /// </summary>
    /// <remarks>
    /// 契约要求对象而非裸 id：Agent 需要知道这条工作属于哪个 Provider 才能做后续动作
    /// （builtin 与 jira 的回写方式完全不同）。work_item 已被删除时返回 null，
    /// 而不是让整条 dispatch 推不出去。
    /// </remarks>
    private async Task<WorkItemRef?> LoadWorkItemRefAsync(Guid? workItemId, CancellationToken ct)
    {
        if (workItemId is not { } id)
        {
            return null;
        }

        DbWorkItem? item = await db.WorkItems
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id, ct);

        if (item is null)
        {
            log.LogWarning("dispatch 引用的 work item {WorkItemId} 已不存在，work_item_ref 以 null 下发", id);
            return null;
        }

        return new WorkItemRef(item.ProviderKey, item.Id.ToString(), item.ExternalRef);
    }
}
