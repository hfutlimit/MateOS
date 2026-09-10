namespace MateOS.Domain.Execution;

/// <summary>一条 <c>execution.event</c> 到达时，Runtime 对其的接收判定（03 §3.2 的三态判断）。</summary>
public enum EventIngestOutcome
{
    /// <summary><c>seq == expected</c>：写入事件 + 推进连续位点。</summary>
    Accepted,

    /// <summary><c>seq &lt; expected</c> 或 <c>provider_event_id</c> 重放：静默忽略，<b>不抛错</b>。</summary>
    Duplicate,

    /// <summary><c>seq &gt; expected</c>：拒收，并向 Agent 推 <c>execution.resume_request</c> 要求从 <c>expected</c> 重发。</summary>
    Gap,
}

/// <summary>被判定为重复的原因，用于区分指标与日志。</summary>
public enum DuplicateCause
{
    /// <summary>seq 落在游标之后（最常见：同一事件重发）。</summary>
    SeqBehindCursor,

    /// <summary>同一 <c>provider_event_id</c> 又带了新 seq（客户端幂等失效，属异常信号）。</summary>
    ProviderEventIdReplay,
}

/// <summary>一次事件接收的判定结果。不可变。</summary>
/// <param name="Outcome">三态判定。</param>
/// <param name="LastPersistedSeq">判定后的连续位点（仅 <see cref="EventIngestOutcome.Accepted"/> 时会前进）。</param>
/// <param name="ExpectedSeq">判定时要求的 seq（= 判定前的 <c>LastPersistedSeq + 1</c>）。</param>
/// <param name="Cause">仅重复时非空。</param>
public sealed record EventIngestResult(
    EventIngestOutcome Outcome,
    long LastPersistedSeq,
    long ExpectedSeq,
    DuplicateCause? Cause = null)
{
    /// <summary>是否需要 Runtime 主动推 <c>execution.resume_request</c>（03 §3.2 gap 分支）。</summary>
    public bool RequiresResumeProbe => Outcome is EventIngestOutcome.Gap;

    /// <summary>gap 场景下要求 Agent 从这里重发；其余为 <c>null</c>。</summary>
    public long? ResumeFromSeq => Outcome is EventIngestOutcome.Gap ? ExpectedSeq : null;

    /// <summary>是否应当落库（只有 Accepted 才写 <c>execution_events</c>）。</summary>
    public bool ShouldPersist => Outcome is EventIngestOutcome.Accepted;
}

/// <summary>
/// Attempt 的事件连续游标（contiguous cursor）。
/// </summary>
/// <remarks>
/// <para>
/// 依据 detailed/03 §3.2。此处刻意修掉了 v0.4.2 的 <c>MAX(seq)</c> 缺陷：
/// 若 seq 40 已落库、41 丢包、42 到达，<c>MAX(seq)=42</c> 会让 Agent 从 43 续发，<b>41 永久丢失</b>。
/// 连续位点保证「无 gap 才能前进」。
/// </para>
/// <para>
/// 本类只做纯内存判定，不落库。持久化契约见 SYSTEM_DESIGN §5.2：
/// <c>execution_attempts.last_persisted_seq</c> 与 <c>UNIQUE(attempt_id, provider_event_id)</c>。
/// 调用方必须在同一事务内「先读 FOR UPDATE → 判定 → 写入 → 更新位点」。
/// </para>
/// <para>线程安全：非线程安全。单 attempt 的事件必须串行处理（Runtime 按 attempt 串行化）。</para>
/// </remarks>
public sealed class AttemptEventCursor
{
    private readonly HashSet<string> _seenProviderEventIds = new(StringComparer.Ordinal);
    private long _lastPersistedSeq;

    /// <summary>构造一条新游标。</summary>
    /// <param name="lastPersistedSeq">
    /// 已持久化的连续位点。重建游标（Runtime 重启 / 进程换手）时必须传入 DB 真实值，
    /// 传错会让重复事件被当成新事件写入。
    /// </param>
    /// <param name="persistedProviderEventIds">
    /// 可选的已落库 <c>provider_event_id</c> 集合（用于跨进程重建去重表）。
    /// </param>
    public AttemptEventCursor(long lastPersistedSeq = 0, IEnumerable<string>? persistedProviderEventIds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastPersistedSeq);

        _lastPersistedSeq = lastPersistedSeq;

        if (persistedProviderEventIds is not null)
        {
            foreach (string id in persistedProviderEventIds)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(id);
                _seenProviderEventIds.Add(id);
            }
        }
    }

    /// <summary>当前连续位点。Agent 必须从 <see cref="ExpectedSeq"/> 续发（03 §3.3）。</summary>
    public long LastPersistedSeq => _lastPersistedSeq;

    /// <summary>下一条可接受的 seq。</summary>
    public long ExpectedSeq => _lastPersistedSeq + 1;

    /// <summary>已接受的 <c>provider_event_id</c> 数量（诊断用）。</summary>
    public int AcceptedEventCount => _seenProviderEventIds.Count;

    /// <summary>
    /// 接收一条事件并推进游标。这是 03 §3.2 那段伪码的等价实现。
    /// </summary>
    /// <param name="providerEventId">协议级幂等键，必填。</param>
    /// <param name="seq">Agent 给出的单调序号。</param>
    public EventIngestResult Ingest(string providerEventId, long seq)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerEventId);

        long expected = ExpectedSeq;

        // ① seq 落后于游标 → 重复 / stale：忽略，不抛错（03 §3.2）
        if (seq < expected)
        {
            return new EventIngestResult(EventIngestOutcome.Duplicate, _lastPersistedSeq, expected, DuplicateCause.SeqBehindCursor);
        }

        // ② seq 越过游标 → gap：拒收，并要求 Agent 从 expected 重发（03 §3.2）
        if (seq > expected)
        {
            return new EventIngestResult(EventIngestOutcome.Gap, _lastPersistedSeq, expected);
        }

        // ③ seq 正确，但幂等键重放 → 仍按重复处理（UNIQUE(attempt_id, provider_event_id) 的语义）
        //    注意先判定再推进：Add 失败则不改变任何状态。
        if (!_seenProviderEventIds.Add(providerEventId))
        {
            return new EventIngestResult(EventIngestOutcome.Duplicate, _lastPersistedSeq, expected, DuplicateCause.ProviderEventIdReplay);
        }

        // ④ 写入 + 推进位点（调用方负责在同一事务内落库）
        _lastPersistedSeq = seq;
        return new EventIngestResult(EventIngestOutcome.Accepted, _lastPersistedSeq, expected);
    }
}
