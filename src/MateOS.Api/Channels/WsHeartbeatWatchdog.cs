using System.Net.WebSockets;
using MateOS.Domain.Channel;

namespace MateOS.Api.Channels;

/// <summary>
/// WS 心跳 watchdog：定时扫描 <see cref="WsConnectionRegistry"/>，
/// 关闭超过 <see cref="WsEnvelope.HeartbeatTimeoutSec"/> 秒未活跃的连接。
/// </summary>
/// <remarks>
/// <para>
/// 间隔 30s 跑一次（detailed/03 §1 heartbeat 30s × 3 = 90s 超时）。
/// </para>
/// <para>
/// 关闭时调 <c>CloseOutputAsync</c>（half-close）：让客户端先收到 close 帧再断开，
/// 比直接 abort 干净（不会留下半开连接污染下次 subscribe 计数）。
/// </para>
/// </remarks>
public sealed class WsHeartbeatWatchdog : BackgroundService
{
    private readonly WsConnectionRegistry _registry;
    private readonly ILogger<WsHeartbeatWatchdog> _log;

    public WsHeartbeatWatchdog(WsConnectionRegistry registry, ILogger<WsHeartbeatWatchdog> log)
    {
        _registry = registry;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(WsEnvelope.HeartbeatIntervalSec);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            DateTime now = DateTime.UtcNow;
            DateTime threshold = now - TimeSpan.FromSeconds(WsEnvelope.HeartbeatTimeoutSec);

            foreach (WsConnection connection in _registry.EnumerateAll())
            {
                if (connection.LastHeartbeatUtc < threshold)
                {
                    _log.LogInformation("WS heartbeat timeout session={SessionId} last={Last}",
                        connection.SessionId, connection.LastHeartbeatUtc);

                    try
                    {
                        if (connection.Socket.State == WebSocketState.Open)
                        {
                            await connection.Socket.CloseOutputAsync(
                                WebSocketCloseStatus.PolicyViolation,
                                "heartbeat timeout",
                                CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "WS 超时关闭异常 session={SessionId}", connection.SessionId);
                    }

                    // 连接终会在 WsEndpoint 的 finally 里 unregister
                }
            }
        }
    }
}
