using StackExchange.Redis;

namespace MateOS.Api.Auth;

/// <summary>限流判定结果。</summary>
/// <param name="Allowed">是否放行。</param>
/// <param name="CurrentCount">当前窗口内已发生的次数（不含本次）。</param>
/// <param name="RetryAfter">建议的重试间隔，用于 <c>Retry-After</c> 响应头。</param>
public readonly record struct RateLimitDecision(bool Allowed, long CurrentCount, TimeSpan RetryAfter);

/// <summary>
/// 登录限流（E1 §5.4：<c>Redis rl:{email_or_ip}:/auth/login</c> 滑窗 5/min，第 6 次返 429）。
/// </summary>
/// <remarks>
/// <para>用 ZSET 实现真正的滑动窗口（而非固定窗口），Lua 脚本保证「清理 + 计数 + 写入」原子完成。</para>
/// <para>
/// member 用随机值而不是时间戳：同一毫秒内的并发登录若用时间戳做 member 会被 ZADD 覆盖，
/// 导致计数偏小而绕过限流。
/// </para>
/// <para>Redis 全部是 scheduling state（G-1）：限流键丢失只会让计数重置，不影响正确性。</para>
/// </remarks>
public sealed class LoginRateLimiter(IConnectionMultiplexer redis, TimeProvider clock)
{
    /// <summary>窗口内允许的最大次数（E1 §5.4）。</summary>
    public const int LimitPerWindow = 5;

    /// <summary>窗口长度：1 分钟。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string SlidingWindowScript = """
        local key    = KEYS[1]
        local now    = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local limit  = tonumber(ARGV[3])
        local member = ARGV[4]

        redis.call('ZREMRANGEBYSCORE', key, 0, now - window)

        local count = redis.call('ZCARD', key)

        if count >= limit then
            return count
        end

        redis.call('ZADD', key, now, member)
        redis.call('PEXPIRE', key, window)

        return count
        """;

    /// <summary>按 subject（email 或 IP）判定是否放行，并在放行时记账。</summary>
    public async Task<RateLimitDecision> AcquireAsync(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        long nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        long windowMs = (long)Window.TotalMilliseconds;

        RedisResult result = await redis.GetDatabase().ScriptEvaluateAsync(
            SlidingWindowScript,
            keys: [RateKey(subject)],
            values: [nowMs, windowMs, LimitPerWindow, Guid.NewGuid().ToString("N")]);

        long count = (long)result;

        bool allowed = count < LimitPerWindow;

        return new RateLimitDecision(allowed, count, allowed ? TimeSpan.Zero : Window);
    }

    /// <summary>仅查询当前计数，不记账（用于诊断与测试断言）。</summary>
    public async Task<long> PeekAsync(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        long nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();

        await redis.GetDatabase().SortedSetRemoveRangeByScoreAsync(
            RateKey(subject), double.NegativeInfinity, nowMs - (long)Window.TotalMilliseconds);

        return await redis.GetDatabase().SortedSetLengthAsync(RateKey(subject));
    }

    private static RedisKey RateKey(string subject) => $"rl:{subject}:/auth/login";
}
