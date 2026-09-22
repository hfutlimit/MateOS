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
/// watchdog worker 配置（detailed/10 §2 B2 的 ack_wait / max_redispatch）。
/// </summary>
/// <remarks>
/// <para>
/// 字段用秒数（<c>double</c>）+ <see cref="TimeSpan"/> 派生属性，
/// 是为了 .NET Configuration binder 直接从 <c>"2"</c> 转 <c>double</c>
/// 不需要 <c>"00:00:02"</c> 字符串形式。生产用秒数调 ack_wait 比 TimeSpan 字符串
/// 直观得多（<c>MATEOS__DispatchAckWatchdog__AckWaitSeconds=2</c>）。
/// </para>
/// <list type="number">
///   <item>DI 注册的 <see cref="IOptions{T}"/></item>
///   <item>环境变量 <c>MATEOS__DispatchAckWatchdog__AckWaitSeconds</c> /
///       <c>MATEOS__DispatchAckWatchdog__MaxRedispatch</c></item>
///   <item>领域默认值（15s / 3 次）</item>
/// </list>
/// e2e 测试通过 <c>WebApplicationFactory.UseSetting("DispatchAckWatchdog:AckWaitSeconds", "2")</c>
/// 注入更短的 ack_wait 让 B2 重派在测试时间内触发（默认 15s × 3 = 45s 不现实）。
/// </remarks>
public sealed class DispatchAckWatchdogOptions
{
    /// <summary>等 ACK 的秒数；过期未收到 → 重派。默认 15s。</summary>
    public double AckWaitSeconds { get; set; } = DispatchAckWatchdogRunner.DefaultAckWait.TotalSeconds;

    /// <summary>同一 attempt 最多重派次数。默认 3。</summary>
    public int MaxRedispatch { get; set; } = DispatchAckWatchdogRunner.DefaultMaxRedispatch;

    /// <summary>scan poll 间隔秒数。默认 2s。</summary>
    public double PollIntervalSeconds { get; set; } = 2;

    /// <summary>单批 scan 的最大 candidate 数（防独占）。默认 32。</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>派生 <see cref="TimeSpan"/>。</summary>
    public TimeSpan AckWait => TimeSpan.FromSeconds(Math.Max(1, AckWaitSeconds));

    /// <summary>派生 <see cref="TimeSpan"/>。</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(0.1, PollIntervalSeconds));
}

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
    private readonly TimeSpan _ackWait;
    private readonly int _maxRedispatch;
    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;

    public DispatchAckWatchdogWorker(
        IServiceProvider services,
        ILogger<DispatchAckWatchdogWorker> log,
        Microsoft.Extensions.Options.IOptions<DispatchAckWatchdogOptions> options)
    {
        _services = services;
        _log = log;
        DispatchAckWatchdogOptions opt = options.Value;
        _ackWait = opt.AckWait;
        _maxRedispatch = opt.MaxRedispatch;
        _pollInterval = opt.PollInterval;
        _batchSize = opt.BatchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "DispatchAckWatchdogWorker 启动，poll {Interval}s / ack_wait {Wait}s / max_redispatch {Max}",
            _pollInterval.TotalSeconds, _ackWait.TotalSeconds, _maxRedispatch);

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
                await Task.Delay(_pollInterval, stoppingToken);
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
        DateTimeOffset cutoff = now - _ackWait;

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
                    LIMIT {_batchSize}
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
                candidate, now, _ackWait, _maxRedispatch);

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
