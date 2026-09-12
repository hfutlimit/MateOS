using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
        await ExecuteScriptAsync(db, BootstrapSql, ct);

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

            // 刻意不用 ExecuteSqlRawAsync：它把 SQL 当**复合格式串**解析（{0} 是参数占位符），
            // 于是迁移脚本里的 JSONB 默认值 '{}'::jsonb 会被当成占位符并抛
            // FormatException（006_routing.sql:57 就踩了这个，导致 006 之后的迁移
            // 在全新库上永远无法应用）。迁移脚本必须原样执行，不做插值。
            await ExecuteScriptAsync(db, sql, ct);

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

    /// <summary>
    /// 原样执行一段迁移脚本（可能含多条语句）。
    /// </summary>
    /// <remarks>
    /// 直接走 ADO.NET 而不是 EF 的 <c>ExecuteSqlRawAsync</c>：后者把 SQL 当复合格式串解析
    /// （<c>{0}</c> 是参数占位符），脚本里的 <c>'{}'::jsonb</c> 会被误当占位符。
    /// 手动把当前事务挂到命令上，保持「DDL 与记账同事务」这个前提。
    /// </remarks>
    private static async Task ExecuteScriptAsync(MateOSDbContext db, string sql, CancellationToken ct)
    {
        DbConnection connection = db.Database.GetDbConnection();

        // EF 的 ExecuteSqlRaw 会自己开关连接，原生命令得自己来。
        // 只在「调用前是关着的」时候由我们打开/关闭，避免把外层已经打开的连接提前关掉。
        bool openedHere = connection.State != ConnectionState.Open;

        if (openedHere)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
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
