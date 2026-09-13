using System.Net;
using MateOS.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MateOS.E2ETests;

/// <summary>
/// e2e 测试宿主：连 Docker Compose 起的 PG/Redis，提供 <c>ResetAsync()</c> 清库。
/// </summary>
/// <remarks>
/// <para>
/// 跟 <see cref="MateOS.IntegrationTests.MateOsApiFixture"/> 同源不同实例 —— 不能直接跨
/// assembly 复用 <c>internal class</c>，且 e2e 与 integration 关注点不同（e2e 走
/// 真实 dispatch 推送路径，integration 走纯 API）。代码片段必须保持一致。
/// </para>
/// <para>
/// <b>刻意不引入 Testcontainers</b>：本机 Docker 已由 compose 提供实例，再起一套会让
/// "测试失败" 与 "compose 没起来" 两种原因混在一起，排查反而更慢。
/// </para>
/// </remarks>
public sealed class MateOsE2EApp : WebApplicationFactory<Program>
{
    public static string PostgresConnection =>
        Environment.GetEnvironmentVariable("MATEOS_TEST_POSTGRES")
        ?? "Host=host.docker.internal;Port=55432;Database=mateos_test;Username=mateos;Password=mateos_dev_only";

    public static string RedisConnection =>
        Environment.GetEnvironmentVariable("MATEOS_TEST_REDIS") ?? "host.docker.internal:16379";

    private const string TestSigningKey = "mateos-e2e-test-signing-key-0123456789abcdef";

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

    /// <summary>把 fixture 转成 <see cref="IServiceProvider"/>，e2e 测试用它直接拿 DbContext 断言。</summary>
    public IServiceProvider Services2 => Services;

    /// <summary>清空所有业务表与测试相关 Redis 状态。</summary>
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
