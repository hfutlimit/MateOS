using System.Net;
using MateOS.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MateOS.IntegrationTests;

/// <summary>
/// 启动真实的 API 宿主，连 Docker Compose 里的 PostgreSQL / Redis。
/// </summary>
/// <remarks>
/// <para>
/// 从容器内运行时，宿主容器与被测 PG 容器不是同一个，因此连接串用
/// <c>host.docker.internal</c> 指向宿主机映射端口（PG 55432 / Redis 16379）。
/// </para>
/// <para>
/// 用独立的 <c>mateos_test</c> 库：测试要 TRUNCATE 所有业务表，不能清掉本地开发数据。
/// </para>
/// <para>
/// 刻意<b>不</b>引入 Testcontainers：本机 Docker 已由 compose 提供实例，
/// 再起一套会让「测试失败」与「compose 没起来」两种原因混在一起，排查反而更慢。
/// </para>
/// </remarks>
public sealed class MateOsApiFixture : WebApplicationFactory<Program>
{
    /// <summary>
    /// 测试库连接串。默认走宿主端口映射（<c>host.docker.internal</c>）；
    /// 用 <c>--network mateos_default</c> 跑时可用 <c>MATEOS_TEST_POSTGRES</c> 改成容器名直连，
    /// 这样绕开宿主端口与防火墙。
    /// </summary>
    public static string PostgresConnection =>
        Environment.GetEnvironmentVariable("MATEOS_TEST_POSTGRES")
        ?? "Host=host.docker.internal;Port=55432;Database=mateos_test;Username=mateos;Password=mateos_dev_only";

    public static string RedisConnection =>
        Environment.GetEnvironmentVariable("MATEOS_TEST_REDIS") ?? "host.docker.internal:16379";

    private const string TestSigningKey = "mateos-integration-test-signing-key-0123456789abcdef";

    /// <summary>
    /// 32 字节 AES-256 主密钥的 base64 编码。测试专用，与生产无关。
    /// </summary>
    private static readonly string TestCryptoKeyBase64 =
        Convert.ToBase64String(new byte[32]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
            0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20,
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("ConnectionStrings:Postgres", PostgresConnection);
        builder.UseSetting("ConnectionStrings:Redis", RedisConnection);
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("Jwt:Issuer", "mateos");
        builder.UseSetting("Jwt:Audience", "mateos-api");
        builder.UseSetting("Agent:CryptoKey", TestCryptoKeyBase64);
        builder.UseSetting("Database:AutoMigrate", "true");
    }

    /// <summary>
    /// 清空所有业务表与测试相关的 Redis 状态，让每个用例从确定状态开始。
    /// </summary>
    /// <remarks>
    /// Redis 也必须清：限流计数（<c>rl:*</c>）和 refresh 令牌（<c>auth:rt:*</c>）会在用例之间存活。
    /// 只清 PG 的话，「第 6 次登录才 429」这类断言在同一个窗口内重跑时会直接失败。
    /// 用的是前缀精确匹配而非 FLUSHDB —— 测试与开发可能共用同一个 Redis。
    /// </remarks>
    public async Task ResetAsync()
    {
        using IServiceScope scope = Services.CreateScope();

        MateOSDbContext db = scope.ServiceProvider.GetRequiredService<MateOSDbContext>();

        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE users, organizations, organization_members, teams, team_members,
                     projects, project_members, audit_logs, outbox_events,
                     channels, channel_members, channel_seq_counters, messages, attachments,
                     credentials, agents, agent_project_membership, agent_tokens,
                     agent_executions, execution_attempts, execution_events, agent_dispatch_inbox,
                     permissions, triggers, collaboration_requests, decision_records,
                     memory_proposals, memory_items,
                     work_management_webhooks, work_relations, work_comments, work_items,
                     work_item_bindings, work_management_connections
            RESTART IDENTITY CASCADE;
            """);

        IConnectionMultiplexer redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();

        foreach (string pattern in new[] { "rl:*", "auth:rt:*" })
        {
            foreach (EndPoint endpoint in redis.GetEndPoints())
            {
                RedisKey[] keys = [.. redis.GetServer(endpoint).Keys(pattern: pattern)];

                if (keys.Length > 0)
                {
                    await redis.GetDatabase().KeyDeleteAsync(keys);
                }
            }
        }
    }
}
