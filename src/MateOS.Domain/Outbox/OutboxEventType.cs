namespace MateOS.Domain.Outbox;

/// <summary>
/// Outbox 事件类型（v0.4 E7 + E4 全部 outbox 事件）。
/// </summary>
/// <remarks>
/// <para>
/// M7 主干打通：所有 E4/E7 关键事件经 <c>outbox_events</c> 表 + relay 推送
/// （detailed/01 §1 T+14b outbox 兜底 + detailed/03 §6 D7/I6 纪律）。
/// </para>
/// <list type="bullet">
///   <item>collaboration.accepted：E4 同步直调 E7 失败时由 relay 重试（v0.4.3 §1 T+14b）</item>
///   <item>execution.completed：E7 推 execution 收尾；relay 写 E3 AGENT_OUTPUT 投影</item>
///   <item>execution.cancelled：E7 cancel attempt；relay 推 WS + 写 channel 投影</item>
///   <item>collaboration.timeout：E4 60s lease sweep；relay 推 collaboration.cancelled</item>
/// </list>
/// </remarks>
public static class OutboxEventType
{
    public const string CollaborationAccepted = "collaboration.accepted";
    public const string CollaborationTimeout = "collaboration.timeout";
    public const string ExecutionCreated = "execution.created";
    public const string ExecutionCompleted = "execution.completed";
    public const string ExecutionCancelled = "execution.cancelled";

    public static readonly IReadOnlySet<string> Canonical = new HashSet<string>(StringComparer.Ordinal)
    {
        CollaborationAccepted, CollaborationTimeout,
        ExecutionCreated, ExecutionCompleted, ExecutionCancelled,
    };

    public static string? Validate(string? eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return "event_type 不能为空";
        }

        if (!Canonical.Contains(eventType))
        {
            return $"未知 event_type：{eventType}";
        }

        return null;
    }
}

/// <summary>Outbox 状态（3 态）。</summary>
public enum OutboxStatus
{
    PENDING,
    PUBLISHED,
    DEAD,
}

public static class OutboxStatusMap
{
    public const string PendingValue = "PENDING";
    public const string PublishedValue = "PUBLISHED";
    public const string DeadValue = "DEAD";

    public static string ToDbValue(this OutboxStatus status) => status switch
    {
        OutboxStatus.PENDING => PendingValue,
        OutboxStatus.PUBLISHED => PublishedValue,
        OutboxStatus.DEAD => DeadValue,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未映射的 OutboxStatus"),
    };

    public static bool TryParse(string? value, out OutboxStatus status)
    {
        switch (value)
        {
            case PendingValue: status = OutboxStatus.PENDING; return true;
            case PublishedValue: status = OutboxStatus.PUBLISHED; return true;
            case DeadValue: status = OutboxStatus.DEAD; return true;
            default:
                status = (OutboxStatus)(-1);
                return false;
        }
    }
}

/// <summary>
/// Outbox 重试策略（指数退避，最大 5 次后置 DEAD）。
/// </summary>
public static class OutboxRetryPolicy
{
    public const int MaxAttempts = 5;

    /// <summary>计算下次重试延迟（指数退避，封顶 5min）。</summary>
    /// <remarks>
    /// 序列：attempt 1 → 30s, 2 → 1min, 3 → 2min, 4 → 4min, 5+ → 5min（封顶）。
    /// </remarks>
    public static TimeSpan NextDelay(int attemptCount)
    {
        if (attemptCount <= 0)
        {
            return TimeSpan.FromSeconds(30);
        }

        // 30s * 2^(attempt-1)：1→30, 2→60, 3→120, 4→240, 5→480 → 封顶 300
        int raw = 30 * (1 << Math.Min(attemptCount - 1, 10));
        int seconds = Math.Min(300, raw);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>第 N 次失败后是否置 DEAD。</summary>
    public static bool ShouldMarkDead(int attemptCount) => attemptCount >= MaxAttempts;
}

/// <summary>
/// Capacity invariant 判定（detailed/03 §7.2）。
/// </summary>
/// <remarks>
/// <para>
/// 容量在 Resolver 阶段（E4）已由 lease 预占：
/// <c>tryAcquireSlot(pending_decision) → ACCEPT → promoteLease(execution)</c>。
/// </para>
/// <para>
/// 因此 dispatch 阶段再出现「本地已满」= <b>invariant 被破坏</b>：
/// <list type="bullet">
///   <item>记 <c>capacity.invariant_violation</c> 指标 + 告警（这是 bug 信号）</item>
///   <item>由 E7 按「执行不可达」收尾：重试同一 attempt → 超阈值 <c>FAILED</c> + 进 Inbox</item>
///   <item><b>不</b> 走 Resolver 重新挑人（CR 已是 ACCEPTED 终态；UNIQUE(CR) 1 CR 1 Execution）</item>
/// </list>
/// </remarks>
public static class CapacityInvariant
{
    public const int MaxConcurrency = 32;  // V1 全局软上限（实际按 agent.max_concurrency 收紧）

    /// <summary>判定一个 agent 当前在 in-flight 数 + 新 dispatch 是否超 max_concurrency。</summary>
    public static bool WouldExceed(int currentInFlight, int maxConcurrency)
    {
        return currentInFlight + 1 > Math.Min(maxConcurrency, MaxConcurrency);
    }
}
