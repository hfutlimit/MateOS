using MateOS.Domain.Channel;

namespace MateOS.Api.Channels;

/// <summary>
/// 单连接限速器：滑动窗口算法（detailed/03 §6：10 msgs/sec）。
/// </summary>
/// <remarks>
/// <para>
/// 维护最近 1 秒内的接收时间戳环形缓冲；每条入帧前调用 <see cref="TryAccept"/>。
/// </para>
/// <para>
/// V1 简化：纯内存、无分布式协调（同一 actor 多端连接时每端独立计数）。
/// 集群限速留 V2+ 接入 Redis Lua。
/// </para>
/// </remarks>
public sealed class WsRateLimiter
{
    private readonly Queue<DateTime> _timestamps = new();
    private readonly object _lock = new();

    public WsRateLimiter(int maxFramesPerSec = WsEnvelope.MaxFramesPerSec)
    {
        MaxFramesPerSec = maxFramesPerSec;
    }

    public int MaxFramesPerSec { get; }

    /// <summary>是否允许通过一帧。返回 true 即允许，false 即被限速。</summary>
    public bool TryAccept()
    {
        DateTime now = DateTime.UtcNow;

        lock (_lock)
        {
            DateTime windowStart = now - TimeSpan.FromSeconds(1);

            while (_timestamps.TryPeek(out DateTime oldest) && oldest < windowStart)
            {
                _timestamps.Dequeue();
            }

            if (_timestamps.Count >= MaxFramesPerSec)
            {
                return false;
            }

            _timestamps.Enqueue(now);
            return true;
        }
    }

    /// <summary>连接关闭时清空缓冲（防 GC 滞留）。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _timestamps.Clear();
        }
    }
}
