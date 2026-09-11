using System.Collections.Concurrent;

namespace MateOS.Api.Channels;

/// <summary>
/// 单个活跃 WS 连接（WebSocket 抽象 + 用户上下文 + 订阅索引）。
/// </summary>
/// <remarks>
/// 不可变壳：除 <see cref="LastHeartbeatUtc"/> 外，连接建立后所有字段都不应改。
/// 订阅 / 取消订阅 / 关闭全部走 <see cref="WsConnectionRegistry"/> 的并发安全方法。
/// </remarks>
public sealed class WsConnection
{
    /// <summary>本地分配的唯一 session id（鉴权后写入）。</summary>
    public required string SessionId { get; init; }

    /// <summary>鉴权后的用户 id（HUMAN / AGENT 都共用此 id 字段）。</summary>
    public required Guid ActorId { get; init; }

    /// <summary>原始 WebSocket（IO 由 middleware 处理）。</summary>
    public required System.Net.WebSockets.WebSocket Socket { get; init; }

    /// <summary>最后心跳时间（UTC）。</summary>
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    /// <summary>当前订阅的 channel 集合（引用同一把锁在 registry 维护）。</summary>
    public HashSet<Guid> SubscribedChannels { get; } = new();
}

/// <summary>
/// 全局 WS 连接注册表（singleton）。
/// </summary>
/// <remarks>
/// <para>
/// 索引：
/// <list type="bullet">
///   <item>按 <c>session_id</c> → <see cref="WsConnection"/></item>
///   <item>按 <c>channel_id</c> → <c>Set&lt;session_id&gt;</c>（广播时直接拿到该 channel 的全部连接）</item>
///   <item>按 <c>actor_id</c> → <c>Set&lt;session_id&gt;</c>（同一用户多端连接）</item>
/// </list>
/// </para>
/// <para>
/// 线程安全：所有数据结构都是 <see cref="ConcurrentDictionary{TKey, TValue}"/>，
/// 订阅集合用 per-connection lock 保护（避免 session 内并发 subscribe / unsubscribe 互踩）。
/// </para>
/// </remarks>
public sealed class WsConnectionRegistry
{
    private readonly ConcurrentDictionary<string, WsConnection> _bySession = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _byChannel = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _byActor = new();

    /// <summary>注册一个新连接。</summary>
    public void Register(WsConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!_bySession.TryAdd(connection.SessionId, connection))
        {
            throw new InvalidOperationException(
                $"WS session_id 重复：{connection.SessionId}");
        }

        ConcurrentDictionary<string, byte> actorSet =
            _byActor.GetOrAdd(connection.ActorId, _ => new ConcurrentDictionary<string, byte>());

        actorSet.TryAdd(connection.SessionId, 0);
    }

    /// <summary>注销并返回对应连接（已关闭的连接先 unregister 再 dispose）。</summary>
    public WsConnection? Unregister(string sessionId)
    {
        if (!_bySession.TryRemove(sessionId, out WsConnection? connection))
        {
            return null;
        }

        // 反向解除：清空该 session 的全部 channel 订阅
        lock (connection.SubscribedChannels)
        {
            foreach (Guid channelId in connection.SubscribedChannels)
            {
                if (_byChannel.TryGetValue(channelId, out ConcurrentDictionary<string, byte>? subs))
                {
                    subs.TryRemove(sessionId, out _);
                }
            }

            connection.SubscribedChannels.Clear();
        }

        if (_byActor.TryGetValue(connection.ActorId, out ConcurrentDictionary<string, byte>? actorSubs))
        {
            actorSubs.TryRemove(sessionId, out _);

            if (actorSubs.IsEmpty)
            {
                _byActor.TryRemove(connection.ActorId, out _);
            }
        }

        return connection;
    }

    /// <summary>给连接加订阅（指定 channel）。幂等。</summary>
    public void Subscribe(string sessionId, Guid channelId)
    {
        WsConnection? connection = GetBySession(sessionId);

        if (connection is null)
        {
            throw new InvalidOperationException($"session_id 不存在：{sessionId}");
        }

        lock (connection.SubscribedChannels)
        {
            if (!connection.SubscribedChannels.Add(channelId))
            {
                return; // 已订阅
            }
        }

        ConcurrentDictionary<string, byte> channelSet =
            _byChannel.GetOrAdd(channelId, _ => new ConcurrentDictionary<string, byte>());

        channelSet.TryAdd(sessionId, 0);
    }

    /// <summary>取消订阅。幂等。</summary>
    public void Unsubscribe(string sessionId, Guid channelId)
    {
        WsConnection? connection = GetBySession(sessionId);

        if (connection is null)
        {
            return;
        }

        lock (connection.SubscribedChannels)
        {
            connection.SubscribedChannels.Remove(channelId);
        }

        if (_byChannel.TryGetValue(channelId, out ConcurrentDictionary<string, byte>? channelSet))
        {
            channelSet.TryRemove(sessionId, out _);

            if (channelSet.IsEmpty)
            {
                _byChannel.TryRemove(channelId, out _);
            }
        }
    }

    /// <summary>取该 channel 的全部活跃 session id 快照。</summary>
    public IReadOnlyCollection<string> GetChannelSubscribers(Guid channelId)
    {
        if (_byChannel.TryGetValue(channelId, out ConcurrentDictionary<string, byte>? subs))
        {
            return subs.Keys.ToArray();
        }

        return Array.Empty<string>();
    }

    public WsConnection? GetBySession(string sessionId) =>
        _bySession.TryGetValue(sessionId, out WsConnection? conn) ? conn : null;

    public int ActiveSessionCount => _bySession.Count;

    /// <summary>遍历全部活跃连接（心跳 watchdog 用）。</summary>
    public IEnumerable<WsConnection> EnumerateAll() => _bySession.Values;
}
