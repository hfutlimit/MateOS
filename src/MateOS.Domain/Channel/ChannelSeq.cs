namespace MateOS.Domain.Channel;

/// <summary>
/// Channel 消息 seq 单调递增分配的不变量（E3 §3 channel_seq_counters / §7 F3）。
/// </summary>
/// <remarks>
/// <para>
/// 真正的 seq 分配走 SQL 事务：<c>UPDATE channel_seq_counters
/// SET next_seq = next_seq + 1 WHERE channel_id = ? RETURNING next_seq - 1</c>，
/// 由 API 层在 <c>SaveChanges</c> 之前执行，落到 <c>messages.channel_seq_counters</c> 单行。
/// </para>
/// <para>
/// 本类只放纯函数不变量：客户端幂等判定、seq 边界校验、resume 边界。
/// 单测可在不连 DB 的情况下覆盖这些规则；SQL 路径由集成测试覆盖。
/// </para>
/// </remarks>
public static class ChannelSeq
{
    /// <summary>seq 从 1 开始（DDL：<c>next_seq BIGINT NOT NULL DEFAULT 1</c>）。</summary>
    public const long FirstSeq = 1L;

    /// <summary>
    /// 校验客户端提供的 <c>since_seq</c> 是否合法（用于 GET /channels/:id/messages?since_seq=N）。
    /// </summary>
    /// <param name="sinceSeq">客户端拉取起点。<c>null</c> 表示从头开始。</param>
    /// <returns>合法则 <c>null</c>；非法则返人类可读错误。</returns>
    public static string? ValidateSinceSeq(long? sinceSeq)
    {
        if (sinceSeq is null)
        {
            return null;
        }

        if (sinceSeq < 0)
        {
            return "since_seq 不能为负数";
        }

        // 超过 BigInt.MaxValue 直接拒绝（实际不可能，但 SQL 路径可能有 off-by-one 风险）
        if (sinceSeq > long.MaxValue - 1)
        {
            return "since_seq 超过 long 上限";
        }

        return null;
    }

    /// <summary>
    /// 校验客户端提供的 <c>limit</c> 是否合法。
    /// </summary>
    public static string? ValidateLimit(int? limit, int maxLimit = 200)
    {
        if (limit is null)
        {
            return null;
        }

        if (limit <= 0)
        {
            return "limit 必须为正整数";
        }

        if (limit > maxLimit)
        {
            return $"limit 不能超过 {maxLimit}";
        }

        return null;
    }

    /// <summary>
    /// 计算 resume 续传应返回的下一段起点。
    /// </summary>
    /// <remarks>
    /// 客户端给 since_seq=N → 期望返 seq > N 的消息，所以起点是 N + 1。
    /// </remarks>
    public static long NextSeqToReturn(long sinceSeq) => sinceSeq + 1;

    /// <summary>
    /// 判断一条消息的 seq 是否应该被 since_seq 过滤掉。
    /// </summary>
    public static bool IsAfterSince(long messageSeq, long sinceSeq) => messageSeq > sinceSeq;
}
