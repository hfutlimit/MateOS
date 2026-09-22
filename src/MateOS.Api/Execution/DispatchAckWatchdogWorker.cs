using MateOS.Api.Agents;
using MateOS.Api.Outbox;
using MateOS.Api.Persistence;
using MateOS.Domain.Agent;
using MateOS.Domain.Execution;
using MateOS.Domain.Outbox;
using Microsoft.EntityFrameworkCore;
using AgentExecutionEntity = MateOS.Api.Persistence.AgentExecution;
using ExecutionAttemptEntity = MateOS.Api.Persistence.ExecutionAttempt;
using DomainAttemptStatus = MateOS.Domain.Agent.AttemptStatus;
using DomainExecutionStatus = MateOS.Domain.Agent.ExecutionStatus;
using DbOutboxEvent = MateOS.Api.Persistence.OutboxEvent;

namespace MateOS.Api.Execution;

/// <summary>

/// <summary>
/// B2 dispatch_ack watchdog worker：周期性扫描未收到 ACK 的 attempt，按
/// <see cref="DispatchAckWatchdogRunner.PlanForCandidate"/> 决定重派 / 放弃。
/// </summary>
/// <remarks>
/// <para>
/// 依据：detailed/03 §7.1 + detailed/10 §2 B2。
/// </para>
/// <para>
/// V1 简化版（与 009 migration 注释一致）：
/// </para>
/// <list type="bullet">
///   <item>不主动推 <c>execution.resume_request</c> 探测 —— 让
///     <see cref="AckWatchdogAction.SendResumeProbe"/> 走 <c>KeepWaiting</c> 路径，
///     下个 cycle 真正重派</item>
///   <item>每次 cycle 单事务 + <c>FOR UPDATE SKIP LOCKED</c> 拉一批 candidate，
///     避免多副本并发抢同一行</item>
///   <item>重派调 <see cref="AgentDispatchNotifier.PushDispatchAsync"/>，让它按
///     现有路径再生成一帧 envelope（provider_event_id 新增 → 客户端去重自然处理）</item>
///   <item>放弃时把 <c>execution_attempts.status</c> 从 <c>DISPATCHED</c> 转
///     <c>FAILED</c>，<c>agent_executions.status</c> 转 <c>FAILED</c>，
///     并写 <c>execution.completed</c> outbox 事件（D7 纪律）</item>
/// </list>
/// </remarks>
public sealed class DispatchAckWatchdogWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DispatchAckWatchdogWorker> _log;
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_ackWait = DispatchAckWatchdogRunner.DefaultAckWait;
    private const int s_maxRedispatch = DispatchAckWatchdogRunner.DefaultMaxRedispatch;
    private const int s_batchSize = 32;

    public DispatchAckWatchdogWorker(IServiceProvider services, ILogger<DispatchAckWatchdogWorker> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "DispatchAckWatchdogWorker 启动，poll {Interval}s / ack_wait {Wait}s / max_redispatch {Max}",
            s_pollInterval.TotalSeconds, s_ackWait.TotalSeconds, s_maxRedispatch);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed = await ScanOnceAsync(stoppingToken);
                if (processed > 0)
                {
                    _log.LogDebug("watchdog 本轮处理 {Count} 个 candidate", processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "DispatchAckWatchdogWorker scan 异常");
            }

            try
            {
                await Task.Delay(s_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<int> ScanOnceAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        MateOSDbContext db = scope.ServiceProvider.GetRequiredService<MateOSDbContext>();
        AgentDispatchNotifier notifier = scope.ServiceProvider.GetRequiredService<AgentDispatchNotifier>();
        OutboxWriter outboxWriter = scope.ServiceProvider.GetRequiredService<OutboxWriter>();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset cutoff = now - s_ackWait;

        // 候选条件：attempt DISPATCHED 还没 ACK；execution 仍处于活跃态。
        // SKIP LOCKED 保证多副本不会抢同一行。
        List<DispatchAckCandidateRaw> raws = await db.Database
            .SqlQuery<DispatchAckCandidateRaw>(
                $@"SELECT e.id AS ExecutionId, a.id AS AttemptId, a.attempt_no AS AttemptNo,
                          e.agent_id AS AgentId, a.dispatch_sent_at AS DispatchSentAt,
                          e.dispatch_redispatch_count AS CurrentRedispatchCount,
                          e.last_resume_probe_at AS LastResumeProbeAt
                     FROM execution_attempts a
                     JOIN agent_executions e ON e.id = a.execution_id
                    WHERE a.status = 'DISPATCHED'
                      AND a.dispatch_acked_at IS NULL
                      AND a.dispatch_sent_at < {cutoff}
                      AND e.status IN ('PENDING','RUNNING')
                    ORDER BY a.dispatch_sent_at ASC
                    LIMIT {s_batchSize}
                    FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);

        if (raws.Count == 0)
        {
            return 0;
        }

        List<DispatchAckCandidate> candidates = raws
            .Select(r => new DispatchAckCandidate(
                ExecutionId: r.ExecutionId,
                AttemptId: r.AttemptId,
                AttemptNo: r.AttemptNo,
                AgentId: r.AgentId,
                DispatchSentAt: r.DispatchSentAt,
                CurrentRedispatchCount: r.CurrentRedispatchCount,
                LastResumeProbeAt: r.LastResumeProbeAt))
            .ToList();

        int processed = 0;
        foreach (DispatchAckCandidate candidate in candidates)
        {
            DispatchAckWatchdogPlan plan = DispatchAckWatchdogRunner.PlanForCandidate(
                candidate, now, s_ackWait, s_maxRedispatch);

            switch (plan.Action)
            {
                case AckWatchdogAction.KeepWaiting:
                case AckWatchdogAction.SendResumeProbe:
                    // V1 简化：都先 KeepWaiting，等下一个 cycle（resume_probe
                    // 留作 V2 字段，不在此写）。
                    break;

                case AckWatchdogAction.RedispatchSameAttempt:
                    await ApplyRedispatchAsync(db, notifier, candidate, plan, ct);
                    processed++;
                    break;

                case AckWatchdogAction.GiveUpAndFail:
                    await ApplyGiveUpAsync(db, outboxWriter, candidate, plan, ct);
                    processed++;
                    break;

                default:
                    _log.LogWarning("未知的 AckWatchdogAction 值 {Value}", plan.Action);
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        return processed;
    }

    private async Task ApplyRedispatchAsync(
        MateOSDbContext db,
        AgentDispatchNotifier notifier,
        DispatchAckCandidate candidate,
        DispatchAckWatchdogPlan plan,
        CancellationToken ct)
    {
        AgentExecutionEntity execution = await db.AgentExecutions
            .FirstAsync(e => e.Id == candidate.ExecutionId, ct);

        execution.DispatchRedispatchCount = plan.NewRedispatchCount;
        execution.LastRedispatchedAt = plan.PlannedAt;

        _log.LogWarning(
            "B2 watchdog 重派 execution={ExecutionId} attempt={AttemptNo} redispatch_count={Count}",
            candidate.ExecutionId, candidate.AttemptNo, plan.NewRedispatchCount);

        // 不传 (execution, attempt) — notifier 实际签名是 (executionId, source, ct)
        await notifier.PushDispatchAsync(execution.Id, "watchdog-redispatch", ct);
    }

    private async Task ApplyGiveUpAsync(
        MateOSDbContext db,
        OutboxWriter outboxWriter,
        DispatchAckCandidate candidate,
        DispatchAckWatchdogPlan plan,
        CancellationToken ct)
    {
        AgentExecutionEntity execution = await db.AgentExecutions
            .FirstAsync(e => e.Id == candidate.ExecutionId, ct);

        ExecutionAttemptEntity attempt = await db.ExecutionAttempts
            .FirstAsync(a => a.Id == candidate.AttemptId, ct);

        attempt.Status = DomainAttemptStatus.FAILED.ToDbValue();
        attempt.CompletedAt = plan.PlannedAt;

        execution.Status = DomainExecutionStatus.FAILED.ToDbValue();
        execution.CompletedAt = plan.PlannedAt;
        execution.DispatchRedispatchCount = plan.NewRedispatchCount;
        execution.LastRedispatchedAt = plan.PlannedAt;

        outboxWriter.Append(
            db,
            aggregateType: "agent_execution",
            aggregateId: execution.Id,
            eventType: OutboxEventType.ExecutionCompleted,
            payload: new
            {
                execution_id = execution.Id,
                final_status = "FAILED",
                reason = "dispatch_ack_watchdog_giveup",
                attempt_no = candidate.AttemptNo,
                redispatch_count = plan.NewRedispatchCount,
                planned_at = plan.PlannedAt,
            },
            idempotencyKey: $"agent_execution:{execution.Id}:execution.completed:watchdog");

        _log.LogError(
            "B2 watchdog 放弃 execution={ExecutionId} attempt={AttemptNo} redispatch_count={Count} → FAILED",
            candidate.ExecutionId, candidate.AttemptNo, plan.NewRedispatchCount);
    }

    /// <summary>
    /// SqlQuery 用的扁平投影（Postgres timestamp → DateTimeOffset）。
    /// </summary>
    private sealed class DispatchAckCandidateRaw
    {
        public Guid ExecutionId { get; set; }
        public Guid AttemptId { get; set; }
        public int AttemptNo { get; set; }
        public Guid AgentId { get; set; }
        public DateTimeOffset DispatchSentAt { get; set; }
        public int CurrentRedispatchCount { get; set; }
        public DateTimeOffset? LastResumeProbeAt { get; set; }
    }
}
