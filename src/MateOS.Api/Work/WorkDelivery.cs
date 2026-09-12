using System.Text.Json;
using MateOS.Api.Observability;
using MateOS.Api.Outbox;
using MateOS.Api.Persistence;
using MateOS.Domain.Agent;
using MateOS.Domain.Routing;
using MateOS.Domain.Work;
using Microsoft.EntityFrameworkCore;
using DbAgent = MateOS.Api.Persistence.Agent;
using DbAgentExecution = MateOS.Api.Persistence.AgentExecution;
using DbAgentDispatchInbox = MateOS.Api.Persistence.AgentDispatchInbox;
using DbCollaborationRequest = MateOS.Api.Persistence.CollaborationRequest;
using DbDecisionRecord = MateOS.Api.Persistence.DecisionRecord;
using DbExecutionAttempt = MateOS.Api.Persistence.ExecutionAttempt;
using DbTrigger = MateOS.Api.Persistence.Trigger;
using DbWorkItem = MateOS.Api.Persistence.WorkItem;

namespace MateOS.Api.Work;

/// <summary>
/// Work Delivery 主链路（S3）：WorkItem → Agent 接手 → 执行 → 产出回流到 Work。
/// </summary>
/// <remarks>
/// <para>
/// 一次「指派」产生的事实（全部写在<b>同一个事务</b>里，任一步失败则整体回滚）：
/// <list type="number">
///   <item>Trigger（<c>WORK_ITEM</c>）+ CollaborationRequest（<c>ACCEPTED</c>，<c>target_agent_id</c> 已定）</item>
///   <item>DecisionRecord（<c>ACCEPT</c>，actor = 指派人）—— CR 6 态与决策链保持可审计</item>
///   <item>Execution（<c>work_item_ref</c> = WorkItem）+ Attempt + dispatch inbox</item>
///   <item>WorkItem：<c>assignee_type=AGENT</c> / <c>assignee_id</c> / <c>status=IN_PROGRESS</c></item>
/// </list>
/// </para>
/// <para>
/// 为什么不复用 <c>MentionResolver</c>：Resolution 的输入是「能力 + 权限 + 空闲 slot」，
/// 而「PO 指定某个 agent」是<b>人已经做完的判断</b>。再走一遍排序只会让结果不确定
/// （人点的是 A，Resolver 可能选 B）。因此这里是 direct dispatch，但协议面
/// （CR / decision / execution / dispatch inbox）与 E4 完全一致，Agent 端无差别。
/// </para>
/// </remarks>
public static class WorkDelivery
{
    /// <summary>指派结果。</summary>
    public sealed record AssignmentResult(
        Guid TriggerId,
        Guid CollaborationRequestId,
        Guid ExecutionId,
        bool Idempotent);

    /// <summary>
    /// 把 WorkItem 指派给 Agent 并创建 Execution。
    /// </summary>
    /// <remarks>
    /// 幂等键固定为 <c>work-item:{workItemId}</c>：重复点击「指派」不会产生第二个 Execution
    /// （E7 侧 <c>UNIQUE(collaboration_request_id)</c> 也兜一层）。
    /// </remarks>
    public static async Task<AssignmentResult> AssignAsync(
        MateOSDbContext db,
        OutboxWriter outboxWriter,
        AuditWriter audit,
        HttpContext http,
        DbWorkItem item,
        DbAgent agent,
        Guid actorUserId,
        int deadlineS,
        CancellationToken ct)
    {
        string idempotencyKey = $"work-item:{item.Id}";

        DbCollaborationRequest? existingCr = await db.CollaborationRequests
            .FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);

        if (existingCr is not null)
        {
            DbAgentExecution? existingExecution = existingCr.TargetExecutionId is { } execId
                ? await db.AgentExecutions.FirstOrDefaultAsync(e => e.Id == execId, ct)
                : null;

            if (existingExecution is null)
            {
                // CR 与 Execution 在同一事务写入，因此「CR 存在但没有 Execution」= 数据不一致。
                // 这里必须响亮失败：继续往下走会撞 UNIQUE(idempotency_key) 变成难懂的 500。
                throw new InvalidOperationException(
                    $"work item {item.Id} 已有 collaboration_request {existingCr.Id} 但 target_execution_id 缺失，" +
                    "数据不一致（assign 的 CR 与 Execution 必须同事务写入）");
            }

            return new AssignmentResult(
                existingCr.TriggerId, existingCr.Id, existingExecution.Id, Idempotent: true);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid triggerId = Guid.NewGuid();
        Guid crId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        Guid decisionId = Guid.NewGuid();

        // trigger_ref 用 work_item 最小投影：Agent 侧据此知道「在给哪张卡干活」
        string triggerRefJson = JsonSerializer.Serialize(new
        {
            work_item_id = item.Id,
            project_id = item.ProjectId,
            title = item.Title,
            type = item.Type,
        });

        string contextRefsJson = JsonSerializer.Serialize(new
        {
            project_id = item.ProjectId,
            work_item_id = item.Id,
        });

        var trigger = new DbTrigger
        {
            Id = triggerId,
            TriggerType = TriggerTypeMap.WorkItemValue,
            TriggerRef = triggerRefJson,
            FromActorType = TriggerActorTypeMap.UserValue,
            FromActorId = actorUserId,
            CapturedAt = now,
        };

        // CR 直接落 ACCEPTED：人已指定 agent，不存在「待决策」窗口。
        // 先过一遍状态机，保证这条捷径与 E4 的合法迁移口径一致。
        string? transitionError = CrStateMachine.WhyCannotTransition(CrStatus.PENDING, CrStatus.ACCEPTED);

        if (transitionError is not null)
        {
            throw new InvalidOperationException(
                $"PENDING → ACCEPTED 被状态机拒绝，direct dispatch 前提失效：{transitionError}");
        }

        var cr = new DbCollaborationRequest
        {
            Id = crId,
            TriggerId = triggerId,
            TriggerType = TriggerTypeMap.WorkItemValue,
            TriggerRef = triggerRefJson,
            RequestKind = CrRequestKindMap.WorkItemExecutionValue,
            FromActorType = TriggerActorTypeMap.UserValue,
            FromActorId = actorUserId,
            TargetAgentId = agent.Id,
            RequiredCapabilities = agent.Capabilities,
            ContextRefs = contextRefsJson,
            Status = CrStatusMap.AcceptedValue,
            TargetExecutionId = executionId,
            DeadlineS = deadlineS,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            ResolvedAt = now,
        };

        var decision = new DbDecisionRecord
        {
            Id = decisionId,
            CollaborationRequestId = crId,
            Decision = CrDecisionMap.AcceptValue,
            Reason = "work item assigned by user（direct dispatch，不经 Resolver）",
            AnalysisCapability = true,
            AnalysisContextScore = 100,
            AnalysisPermission = true,
            AcceptedExecutionId = executionId,
            // 决策主体必须是 AGENT：DDL 的 CHECK 只允许 ('AGENT','SYSTEM')，
            // 因为这条记录表达的是「Agent 的判断」—— 这里是被人指派后直接接受。
            // 「谁指派的」由 trigger.from_actor 表达，不在这里重复；
            // 写 USER 会撞 decision_records_actor_type_check（23514），指派直接 500。
            ActorType = AuditActorTypes.Agent,
            ActorId = agent.Id,
            DecidedAt = now,
        };

        var execution = new DbAgentExecution
        {
            Id = executionId,
            CollaborationRequestId = crId,
            AgentId = agent.Id,
            WorkItemRef = item.Id,
            Status = ExecutionStatusMap.PendingDbValue,
            LastPersistedSeq = 0,
            ActiveAttemptNo = 1,
            AttemptCount = 1,
            Input = triggerRefJson,
            ContextRefs = contextRefsJson,
            CreatedAt = now,
            DispatchSentAt = now,
            DeadlineAt = now.AddSeconds(deadlineS),
        };

        var attempt = new DbExecutionAttempt
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            AttemptNo = 1,
            Status = AttemptStatusMap.DispatchedDbValue,
            DispatchSentAt = now,
            LastPersistedSeq = 0,
            CreatedAt = now,
        };

        var inbox = new DbAgentDispatchInbox
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = $"cr-{crId}",
            AgentId = agent.Id,
            ExecutionId = executionId,
            AttemptNo = 1,
            DispatchedAt = now,
        };

        // WorkItem 前推：指派即进入 IN_PROGRESS（canonical category 由状态派生，不手填）
        WorkItemStatus previousStatus = WorkItemStatusMap.TryParse(item.Status, out WorkItemStatus parsed)
            ? parsed
            : WorkItemStatus.OPEN;

        item.AssigneeType = WorkAssigneeTypeMap.AgentValue;
        item.AssigneeId = agent.Id;
        item.Status = WorkItemStatusMap.InProgressValue;
        item.CanonicalStatusCategory = WorkItemStatus.IN_PROGRESS.ToCanonicalCategoryDbValue();
        item.UpdatedAt = now;

        db.Triggers.Add(trigger);
        db.CollaborationRequests.Add(cr);
        db.DecisionRecords.Add(decision);
        db.AgentExecutions.Add(execution);
        db.ExecutionAttempts.Add(attempt);
        db.AgentDispatchInbox.Add(inbox);

        audit.Record(http, new AuditEntry(
            AuditActorTypes.User, actorUserId, AuditActions.WorkItemAssigned,
            TargetType: "work_item", TargetId: item.Id,
            Detail: new
            {
                agent_id = agent.Id,
                collaboration_request_id = crId,
                execution_id = executionId,
                from_status = previousStatus.ToDbValue(),
                to_status = WorkItemStatusMap.InProgressValue,
            }));

        outboxWriter.Append(
            db,
            aggregateType: "execution",
            aggregateId: executionId,
            eventType: Domain.Outbox.OutboxEventType.ExecutionCreated,
            payload: new
            {
                execution_id = executionId,
                agent_id = agent.Id,
                collaboration_request_id = crId,
                work_item_id = item.Id,
            },
            idempotencyKey: $"execution.created:{executionId}");

        await db.SaveChangesAsync(ct);

        return new AssignmentResult(triggerId, crId, executionId, Idempotent: false);
    }
}
