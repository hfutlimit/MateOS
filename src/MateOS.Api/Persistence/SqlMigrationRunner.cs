using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace MateOS.Api.Persistence;

/// <summary>
/// 显式 SQL 迁移器（D7 / detailed/09 §3.1：EF Core + 显式 SQL 迁移，不用 EF Migrations）。
/// </summary>
/// <remarks>
/// <para>
/// SQL 脚本以嵌入资源打包（唯一副本在 <c>ops/postgres/migrations</c>，csproj 里配置 LogicalName），
/// 按文件名序执行，已执行的记入 <c>schema_migrations</c>。
/// </para>
/// <para>
/// 每个迁移在<b>独立事务</b>中执行：失败则整体回滚，不会留下半套 schema。
/// 记账 INSERT 与 DDL 同事务，因此不存在「DDL 成功但没记账」的窗口。
/// </para>
/// </remarks>
public sealed class SqlMigrationRunner(MateOSDbContext db, ILogger<SqlMigrationRunner> logger)
{
    private const string ResourcePrefix = "MateOS.Api.Persistence.Migrations.";

    private const string BootstrapSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
          name        TEXT PRIMARY KEY,
          applied_at  TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;

    /// <summary>执行所有未应用的迁移，返回本次新应用的迁移名。</summary>
    public async Task<IReadOnlyList<string>> RunAsync(CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(BootstrapSql, ct);

        List<string> applied = await db.SchemaMigrations
            .AsNoTracking()
            .Select(x => x.Name)
            .ToListAsync(ct);

        var appliedSet = applied.ToHashSet(StringComparer.Ordinal);
        var newlyApplied = new List<string>();

        foreach ((string name, string sql) in LoadScripts())
        {
            if (appliedSet.Contains(name))
            {
                logger.LogDebug("迁移 {Migration} 已应用，跳过", name);
                continue;
            }

            logger.LogInformation("正在应用迁移 {Migration} …", name);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync(sql, ct);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO schema_migrations (name) VALUES ({0})",
                new object[] { name },
                ct);
            await transaction.CommitAsync(ct);

            newlyApplied.Add(name);
            logger.LogInformation("迁移 {Migration} 完成", name);
        }

        return newlyApplied;
    }

    /// <summary>按文件名序加载嵌入的 SQL 脚本。</summary>
    internal static IEnumerable<(string Name, string Sql)> LoadScripts()
    {
        Assembly assembly = typeof(SqlMigrationRunner).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (Name: n[ResourcePrefix.Length..], Sql: ReadResource(assembly, n)));
    }

    private static string ReadResource(Assembly assembly, string logicalName)
    {
        using Stream stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"找不到嵌入资源 {logicalName}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
