using System.Text.Json;
using MateOS.Api.Persistence;
using MateOS.Domain.Outbox;
using DbOutboxEvent = MateOS.Api.Persistence.OutboxEvent;

namespace MateOS.Api.Outbox;

/// <summary>
/// Transactional outbox 写入器（D7/I6 纪律：与业务同事务）。
/// </summary>
/// <remarks>
/// <para>
/// 调用方传入已 attach 的 <see cref="MateOSDbContext"/>（保证与业务在同事务），
/// 直接 <c>db.OutboxEvents.Add</c>。relay（<see cref="OutboxRelayWorker"/>）用 SKIP LOCKED
/// 拉取后只做 transport，事实源仍在 DB。
/// </para>
/// </remarks>
public sealed class OutboxWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 在调用方事务内追加 outbox 事件。
    /// </summary>
    /// <remarks>
    /// 行为：
    /// <list type="bullet">
    ///   <item>event_type 必须在 <see cref="OutboxEventType.Canonical"/> 内</item>
    ///   <item>aggregate_id 用于 relay 路由（如 execution.completed → aggregate_id=execution_id）</item>
    ///   <item>idempotency_key：消费者侧去重（V1 简单用 aggregate_id+event_type 拼接）</item>
    /// </list>
    /// </remarks>
    public DbOutboxEvent Append(
        MateOSDbContext db,
        string aggregateType,
        Guid aggregateId,
        string eventType,
        object payload,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        string? validationError = OutboxEventType.Validate(eventType);
        if (validationError is not null)
        {
            throw new ArgumentException(validationError, nameof(eventType));
        }

        string effectiveKey = idempotencyKey ?? $"{aggregateType}:{aggregateId}:{eventType}";

        var ev = new DbOutboxEvent
        {
            Id = Guid.NewGuid(),
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload, s_jsonOptions),
            IdempotencyKey = effectiveKey,
            Status = OutboxStatusMap.PendingValue,
            AttemptCount = 0,
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.OutboxEvents.Add(ev);
        return ev;
    }
}
