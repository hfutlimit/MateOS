namespace MateOS.Domain.Agent;

/// <summary>
/// Execution Attempt contiguous cursor（v0.4.3 修复 P1-2）。
/// </summary>
/// <remarks>
/// <para>
/// v0.4.2 bug：<c>last_persisted_seq = MAX(seq)</c> 跳号丢事件（seq 41 lost → 42 persisted
/// → MAX = 42 → Agent 从 43 开始 → seq 41 永久丢失）。详见 detailed/03 §3.2。
/// </para>
/// <para>
/// v0.4.3 修复：cursor 严格 contiguous（不允许跳号）；写入时按 expected 决策：
/// <list type="bullet">
///   <item><c>seq &lt; expected</c> → 重复，忽略（不抛错）</item>
///   <item><c>seq &gt; expected</c> → gap，拒收 + 应主动 resume_request</item>
///   <item><c>seq == expected</c> → 写入，cursor +1</item>
/// </list>
/// </para>
/// <para>
/// 本类只放纯函数不变量（expected / next 计算）；DB 写入在 Api 层（与 transactions 同事务）。
/// </para>
/// </remarks>
public static class AttemptEventCursor
{
    /// <summary>next expected seq（写入前应等于此值）。</summary>
    public static long ExpectedSeq(long lastPersistedSeq) => lastPersistedSeq + 1;

    public enum AcceptDecision
    {
        /// <summary>seq == expected → 写入</summary>
        Accept,
        /// <summary>seq &lt; expected → 重复，忽略</summary>
        Duplicate,
        /// <summary>seq &gt; expected → gap，拒收</summary>
        Gap,
    }

    /// <summary>判定 seq 是否可写入（不连 DB 也能在 Domain 层做规划）。</summary>
    public static AcceptDecision Decide(long seq, long lastPersistedSeq)
    {
        if (seq <= 0)
        {
            return AcceptDecision.Gap; // 非法 seq
        }

        long expected = ExpectedSeq(lastPersistedSeq);

        if (seq < expected)
        {
            return AcceptDecision.Duplicate;
        }

        if (seq > expected)
        {
            return AcceptDecision.Gap;
        }

        return AcceptDecision.Accept;
    }

    /// <summary>写入后更新 cursor（pure）</summary>
    public static long Advance(long lastPersistedSeq, long writtenSeq)
    {
        if (writtenSeq != ExpectedSeq(lastPersistedSeq))
        {
            throw new InvalidOperationException(
                $"AttemptEventCursor 写入必须严格 contiguous：expected={ExpectedSeq(lastPersistedSeq)} written={writtenSeq}");
        }

        return writtenSeq;
    }
}
