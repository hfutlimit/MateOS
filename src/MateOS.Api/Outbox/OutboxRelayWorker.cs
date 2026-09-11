using System.Text.Json;
using MateOS.Api.Agents;
using MateOS.Api.Channels;
using MateOS.Api.Persistence;
using MateOS.Domain.Outbox;
using MateOS.Domain.Work;
using Microsoft.EntityFrameworkCore;
using DbOutboxEvent = MateOS.Api.Persistence.OutboxEvent;
using DbWorkComment = MateOS.Api.Persistence.WorkComment;

namespace MateOS.Api.Outbox;

/// <summary>
/// Outbox relay worker：D7/I6 纪律——只做 transport，绝不是事实源。
/// </summary>
/// <remarks>
/// <para>
/// 周期（默认 5s）扫 <c>outbox_events</c>：拉 PENDING 且 next_attempt_at &lt;= now 的事件，
/// 按 event_type 路由到下游 consumer；失败时 attempt_count++ + 指数退避，
/// 5 次后置 DEAD。
/// </para>
/// <para>
/// V1 简化：串行处理（V2 加 FOR UPDATE SKIP LOCKED 并行）。
/// </para>
/// <para>
/// 下游 consumer（M7 V1）：
/// <list type="bullet">
///   <item>execution.completed → <see cref="ExecutionCompletedHandler"/>：写 E3 AGENT_OUTPUT 投影 + WS 推 message.created</item>
///   <item>collaboration.accepted → <see cref="CollaborationAcceptedHandler"/>：兜底重试 E4 同步调 E7 创建 Execution（V2 接入）</item>
/// </list>
/// </para>
/// </remarks>
public sealed class OutboxRelayWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<OutboxRelayWorker> _log;
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(5);

    public OutboxRelayWorker(IServiceProvider services, ILogger<OutboxRelayWorker> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("OutboxRelayWorker 启动，poll 间隔 {Interval}s", s_pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "OutboxRelayWorker poll 异常");
            }

            try
            {
                await Task.Delay(s_pollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using IServiceScope scope = _services.CreateScope();
        MateOSDbContext db = scope.ServiceProvider.GetRequiredService<MateOSDbContext>();
        WsSender wsSender = scope.ServiceProvider.GetRequiredService<WsSender>();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var pending = await db.OutboxEvents
            .Where(e => e.Status == OutboxStatusMap.PendingValue && e.NextAttemptAt <= now)
            .OrderBy(e => e.NextAttemptAt)
            .Take(20)  // 单批上限 20
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return;
        }

        _log.LogDebug("Outbox relay 拉取 {Count} 个 pending 事件", pending.Count);

        foreach (DbOutboxEvent ev in pending)
        {
            try
            {
                await DispatchAsync(db, wsSender, ev, ct);
                ev.Status = OutboxStatusMap.PublishedValue;
                ev.PublishedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                ev.AttemptCount++;
                ev.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

                if (OutboxRetryPolicy.ShouldMarkDead(ev.AttemptCount))
                {
                    ev.Status = OutboxStatusMap.DeadValue;
                    _log.LogError(ex, "Outbox event 达 MaxAttempts 置 DEAD：id={Id} event_type={EventType}",
                        ev.Id, ev.EventType);
                }
                else
                {
                    ev.NextAttemptAt = DateTimeOffset.UtcNow + OutboxRetryPolicy.NextDelay(ev.AttemptCount);
                    _log.LogWarning(ex, "Outbox event 处理失败（attempt {Attempt}，下次重试 {Next}）：{EventType}",
                        ev.AttemptCount, ev.NextAttemptAt, ev.EventType);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 路由单个事件到下游 consumer。
    /// </summary>
    private static async Task DispatchAsync(
        MateOSDbContext db,
        WsSender wsSender,
        DbOutboxEvent ev,
        CancellationToken ct)
    {
        switch (ev.EventType)
        {
            case OutboxEventType.ExecutionCompleted:
                await ExecutionCompletedHandler.HandleAsync(db, wsSender, ev, ct);
                break;

            case OutboxEventType.CollaborationAccepted:
                // V1 占位：M4b 已 E4 同步调 E7 创建了 Execution（M3b 的 execution_id 落库），
                // 此事件的 idempotency_key 保证不重复创建。V2 可加 outbox 重试逻辑。
                break;

            case OutboxEventType.ExecutionCreated:
            case OutboxEventType.ExecutionCancelled:
            case OutboxEventType.CollaborationTimeout:
                // V1 占位：未实现
                break;

            default:
                throw new InvalidOperationException($"未知的 outbox event_type：{ev.EventType}");
        }
    }
}

/// <summary>
/// execution.completed 下游：写 E3 AGENT_OUTPUT 投影消息 + WS 推 message.created。
/// </summary>
internal static class ExecutionCompletedHandler
{
    public static async Task HandleAsync(
        MateOSDbContext db,
        WsSender wsSender,
        DbOutboxEvent ev,
        CancellationToken ct)
    {
        // 解析 payload
        JsonElement payload;
        try
        {
            payload = JsonDocument.Parse(string.IsNullOrEmpty(ev.Payload) ? "{}" : ev.Payload).RootElement;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"outbox payload 非合法 JSON：{ex.Message}", ex);
        }

        Guid executionId = ev.AggregateId;
        string? statusStr = payload.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
        string? outputMarkdown = payload.TryGetProperty("output_markdown", out JsonElement om) ? om.GetString() : null;
        Guid? channelId = payload.TryGetProperty("channel_id", out JsonElement ch) && ch.ValueKind == JsonValueKind.String
            ? Guid.Parse(ch.GetString()!) : null;
        long? messageSeq = payload.TryGetProperty("message_seq", out JsonElement ms) && ms.ValueKind == JsonValueKind.Number
            ? ms.GetInt64() : null;

        // S3：Work Delivery 回流——execution 关联了 work_item 时把结果推回 Work 面
        await WorkItemExecutionFeedback.HandleAsync(db, executionId, statusStr, outputMarkdown, ct);

        // v0.4.3 详细设计 01 §1 T+23：E4 收到 execution.completed → E3 写 AGENT_OUTPUT 投影
        // V1 简化：仅 status=SUCCEEDED + 有 channel_id 时写 channel message
        if (statusStr != "SUCCEEDED" || channelId is null || messageSeq is null)
        {
            return;
        }

        // 已在 channel 写过 message（v0.4.3 由 E4 T+23 协调）；relay 阶段只推 WS message.created
        // （V1 stub：直接广播，避免重复写 DB；v0.5+ 加 idempotency）

        // 拉 channel 全部在线订阅者，推 message.created
        string contentJson = JsonSerializer.Serialize(new
        {
            execution_ref = executionId,
            text = outputMarkdown ?? "(无输出)",
        });

        // 注：v0.4.3 详细设计 01 §1 T+24-25 由 E3 写 messages + 推 message.created
        // V1 stub：outbox 阶段不重复写 DB message（E3 端 PostMessage 已写），但推 WS 广播
        await wsSender.BroadcastToChannelAsync(channelId.Value, new
        {
            id = Guid.NewGuid().ToString("N"),
            type = "message.created",
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            payload = new
            {
                channel_id = channelId.Value,
                message = new
                {
                    id = executionId,
                    seq = messageSeq,
                    sender_type = "AGENT",
                    content_type = "AGENT_OUTPUT",
                    content = JsonDocument.Parse(contentJson).RootElement,
                    created_at = DateTimeOffset.UtcNow,
                },
            },
        }, ct);
    }
}

/// <summary>
/// S3 Work Delivery 回流：execution 终态 → WorkItem 状态前推 + SYSTEM 评论。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ExecutionCompletedHandler"/> 共用 relay 的同一个 DbContext 与事务：
/// 「outbox 事件置 PUBLISHED」与「work item 回流」要么一起提交要么一起回滚，
/// 因此 relay 崩溃重试不会产生重复评论。
/// </para>
/// <para>
/// 刻意<b>不</b>把 WorkItem 直接推到 DONE：Agent 说「做完了」不等于人认可交付。
/// 前推到 IN_REVIEW，把判断权留给 Needs You / 人。这是 Human Attention 模型的一致性要求。
/// </para>
/// </remarks>
internal static class WorkItemExecutionFeedback
{
    private const int MaxCommentChars = 2000;

    public static async Task HandleAsync(
        MateOSDbContext db,
        Guid executionId,
        string? status,
        string? outputMarkdown,
        CancellationToken ct)
    {
        AgentExecution? execution = await db.AgentExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId, ct);

        if (execution?.WorkItemRef is not { } workItemId)
        {
            return;
        }

        WorkItem? item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);

        if (item is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string body;

        switch (status)
        {
            case "SUCCEEDED":
            {
                if (WorkItemStatusMap.TryParse(item.Status, out WorkItemStatus current)
                    && current is WorkItemStatus.IN_PROGRESS)
                {
                    item.Status = WorkItemStatusMap.InReviewValue;
                    item.CanonicalStatusCategory = WorkItemStatus.IN_REVIEW.ToCanonicalCategoryDbValue();
                }

                string excerpt = string.IsNullOrWhiteSpace(outputMarkdown)
                    ? "（无输出）"
                    : Truncate(outputMarkdown, MaxCommentChars);

                body = $"Agent 执行完成，work item 进入 IN_REVIEW 等待人确认。\n\nexecution: {executionId}\n\n{excerpt}";
                break;
            }

            case "FAILED":
            {
                // 失败不改变 work item 状态：Agent 失败是执行层事实，
                // 「这张卡该怎么办」是人的判断（重派 / 拆分 / 关掉）。
                body = $"Agent 执行失败，work item 状态保持 {item.Status}，等待人介入。\n\nexecution: {executionId}";
                break;
            }

            default:
                // CANCELLED 不回流：通常是人的主动取消，不需要再解释一遍
                return;
        }

        item.UpdatedAt = now;

        db.WorkComments.Add(new DbWorkComment
        {
            Id = Guid.NewGuid(),
            WorkItemId = item.Id,
            AuthorType = WorkCommentAuthorTypeMap.SystemValue,
            AuthorId = null,
            Body = body,
            CreatedAt = now,
        });
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + " …";
}
