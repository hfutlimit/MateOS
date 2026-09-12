using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Http;
using MateOS.Api.Observability;
using MateOS.Api.Outbox;
using MateOS.Api.Persistence;
using MateOS.Domain.Agent;
using MateOS.Domain.Routing;
using Microsoft.EntityFrameworkCore;
using DbAgentExecution = MateOS.Api.Persistence.AgentExecution;
using DbCollaborationRequest = MateOS.Api.Persistence.CollaborationRequest;
using DbDecisionRecord = MateOS.Api.Persistence.DecisionRecord;

namespace MateOS.Api.Routing;

/// <summary>
/// 一次 CR 决策的入参。
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="ActorType"/> / <paramref name="ActorId"/> 由<b>调用方</b>决定，
/// 不在这里硬编码：同一个决策既可能由 Agent 自己提交（agent_token），
/// 也可能由人代 Agent 提交（<c>/internal</c> 兼容入口），
/// 两者记的审计主体不同，但<b>业务事实必须一致</b>。
/// </para>
/// </remarks>
public sealed record DecisionCommand(
    CrDecision Decision,
    string? Reason,
    JsonElement? Needs,
    bool? AnalysisCapability,
    int? AnalysisContextScore,
    bool? AnalysisPermission,
    string ActorType,
    Guid ActorId);

/// <summary>决策落库后产出的事实。</summary>
public sealed record DecisionApplyOutcome(
    DbDecisionRecord Record,
    Guid? AcceptedExecutionId,
    DateTimeOffset DecidedAt);

/// <summary>要么产出事实、要么带一个可直接返回的 HTTP 错误。</summary>
public sealed record DecisionApplyResult(DecisionApplyOutcome? Outcome, IResult? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// CR 决策的唯一落地点（CAS 迁移 → decision_record → ACCEPT 时创建 Execution → 推送）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么抽出来</b>：决策有两条入口 —— Agent 自己提交（<c>/agents/{id}/collaboration-requests/{crId}/decision</c>）
/// 与人代提交（<c>/internal/collaboration-requests/{crId}/decision</c>）。
/// 两处各写一份必然会分叉，而分叉的后果是「人代提交能创建 Execution、Agent 自己提交创建不出来」
/// 这类只在某一条路径上出现的隐性缺陷。
/// </para>
/// <para>
/// 副作用顺序是<b>语义要求</b>，不能随意调整：
/// CAS 先于 decision_record 写入（避免一个 CR 出现两条决策记录）；
/// dispatch 推送放在事务提交<b>之后</b>（先推后提交的话，Agent 可能在 CR 仍是 PENDING、
/// 甚至整笔被回滚时就开始执行）。
/// </para>
/// </remarks>
public static class DecisionApplier
{
    public static async Task<DecisionApplyResult> ApplyAsync(
        DbCollaborationRequest cr,
        DecisionCommand command,
        MateOSDbContext db,
        OutboxWriter outboxWriter,
        AgentDispatchNotifier notifier,
        AuditWriter audit,
        HttpContext http,
        CancellationToken ct)
    {
        CrStatus current = CrStatusMap.TryParse(cr.Status, out CrStatus parsed) ? parsed : CrStatus.PENDING;

        // v0.4.1 状态机：PENDING → 终态；其他拒
        CrStatus target = command.Decision switch
        {
            CrDecision.ACCEPT => CrStatus.ACCEPTED,
            CrDecision.REJECT => CrStatus.REJECTED,
            CrDecision.NEED_CONTEXT => CrStatus.NEED_CONTEXT,
            _ => CrStatus.CANCELLED,
        };

        string? transitionError = CrStateMachine.WhyCannotTransition(current, target);

        if (transitionError is not null)
        {
            return Fail(ApiErrors.ConflictResult(ApiErrors.Conflict, transitionError));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid? acceptedExecutionId = null;

        // v0.4.3 关键：ACCEPT 时 E4 同步调 E7 创建 Execution
        if (command.Decision is CrDecision.ACCEPT)
        {
            acceptedExecutionId = await DispatchExecutionAsync(cr, db, ct);

            // M7 outbox：fallback 兜底事件（即使 E4 同步创建已成功，relay 也会 idempotency 去重）
            if (acceptedExecutionId is { } execId)
            {
                outboxWriter.Append(
                    db,
                    aggregateType: "collaboration",
                    aggregateId: cr.Id,
                    eventType: Domain.Outbox.OutboxEventType.CollaborationAccepted,
                    payload: new { cr_id = cr.Id, execution_id = execId, agent_id = cr.TargetAgentId },
                    idempotencyKey: $"collab.accepted:{cr.Id}");
            }
        }

        // CAS 终态守卫（避免 PENDING 状态被并发决策改两次）
        string newStatus = target.ToDbValue();

        int rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE collaboration_requests
               SET status = {newStatus},
                   resolved_at = {now},
                   target_execution_id = {acceptedExecutionId}
               WHERE id = {cr.Id} AND status = {CrStatusMap.PendingValue}", ct);

        if (rows == 0)
        {
            return Fail(ApiErrors.ConflictResult(ApiErrors.Conflict, "CR 状态已被其他决策覆盖"));
        }

        var record = new DbDecisionRecord
        {
            Id = Guid.NewGuid(),
            CollaborationRequestId = cr.Id,
            Decision = command.Decision.ToDbValue(),
            Reason = command.Reason,
            Needs = command.Needs?.GetRawText(),
            AnalysisCapability = command.AnalysisCapability,
            AnalysisContextScore = command.AnalysisContextScore,
            AnalysisPermission = command.AnalysisPermission,
            AcceptedExecutionId = acceptedExecutionId,
            ActorType = command.ActorType,
            ActorId = command.ActorId,
            DecidedAt = now,
        };
        db.DecisionRecords.Add(record);

        audit.Record(http, new AuditEntry(
            command.ActorType, command.ActorId, AuditActions.DecisionRecorded,
            TargetType: "collaboration_request", TargetId: cr.Id,
            Detail: new
            {
                decision = command.Decision.ToDbValue(),
                execution_id = acceptedExecutionId,
            }));

        await db.SaveChangesAsync(ct);

        // 放在 CR 状态提交之后：先推后提交的话，Agent 可能在 CR 仍是 PENDING
        // （甚至被回滚）时就开始执行。
        if (acceptedExecutionId is { } pushedExecutionId)
        {
            await notifier.PushDispatchAsync(pushedExecutionId, source: "e4-accept", ct);
        }

        await db.Entry(cr).ReloadAsync(ct);

        return new DecisionApplyResult(new DecisionApplyOutcome(record, acceptedExecutionId, now), null);
    }

    private static DecisionApplyResult Fail(IResult error) => new(null, error);

    /// <summary>
    /// v0.4.3 D9 口径：E4 同步直调 E7 创建 Execution（详细设计 01 §1 T+13）。
    /// </summary>
    /// <remarks>
    /// V1 在同一进程内直接写库（避免 HTTP 自调用带来的 idempotency_key 二次设计）；
    /// 失败返 <c>null</c>，由 M7 outbox relay 兜底重试。
    /// </remarks>
    private static async Task<Guid?> DispatchExecutionAsync(
        DbCollaborationRequest cr,
        MateOSDbContext db,
        CancellationToken ct)
    {
        if (cr.TargetAgentId is null)
        {
            return null; // 没指定 agent（理论上 Resolver 选了 agent 才会接受）
        }

        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid executionId = Guid.NewGuid();

            db.AgentExecutions.Add(new DbAgentExecution
            {
                Id = executionId,
                CollaborationRequestId = cr.Id,
                AgentId = cr.TargetAgentId.Value,
                WorkItemRef = null,
                Status = ExecutionStatusMap.PendingDbValue,
                LastPersistedSeq = 0,
                ActiveAttemptNo = 1,
                AttemptCount = 1,
                Input = cr.TriggerRef,
                ContextRefs = cr.ContextRefs,
                CreatedAt = now,
                DispatchSentAt = now,
            });

            db.ExecutionAttempts.Add(new ExecutionAttempt
            {
                Id = Guid.NewGuid(),
                ExecutionId = executionId,
                AttemptNo = 1,
                Status = AttemptStatusMap.DispatchedDbValue,
                DispatchSentAt = now,
                LastPersistedSeq = 0,
                CreatedAt = now,
            });

            db.AgentDispatchInbox.Add(new AgentDispatchInbox
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = $"cr-{cr.Id}",
                AgentId = cr.TargetAgentId.Value,
                ExecutionId = executionId,
                AttemptNo = 1,
                DispatchedAt = now,
            });

            await db.SaveChangesAsync(ct);

            return executionId;
        }
        catch
        {
            // v0.4.3 §1 T+14b outbox 兜底：失败时返 null，由 M7 outbox relay 重试
            return null;
        }
    }
}
