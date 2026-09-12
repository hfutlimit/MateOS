using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MateOS.Api.Persistence;

/// <summary>
/// <c>channel_seq_counters</c> 的原子 seq 分配（E3 §3）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意走 ADO.NET 而不是 <c>db.Database.SqlQuery&lt;long&gt;</c>：后者要求 SQL
/// <b>可组合</b>（必须以 SELECT 开头），而这里是一条 <c>UPDATE … RETURNING</c>，
/// 会抛 <c>'FromSql' or 'SqlQuery' was called with non-composable SQL</c>，
/// 表现为「发消息直接 500」。
/// </para>
/// <para>
/// 必须在<b>调用方的事务内</b>执行，与消息 INSERT 保持原子：先加后取，
/// 事务回滚时 seq 一并回退，不会留下空洞。
/// </para>
/// <para>
/// E3 与 S2（Memory 写 channel 消息）两条路径共用同一实现，避免各写一份后行为分叉。
/// </para>
/// </remarks>
public static class ChannelSeqAllocator
{
    public static async Task<long> AllocateAsync(
        MateOSDbContext db,
        Guid channelId,
        CancellationToken ct)
    {
        DbConnection connection = db.Database.GetDbConnection();

        // EF 的查询 API 会自己开关连接，原生命令得自己来；
        // 只在「调用前是关着的」时候由我们关闭，避免把外层连接提前关掉。
        bool openedHere = connection.State != ConnectionState.Open;

        if (openedHere)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            using DbCommand command = connection.CreateCommand();

            command.CommandText = """
                UPDATE channel_seq_counters
                   SET next_seq = next_seq + 1
                 WHERE channel_id = @channel_id
                 RETURNING next_seq - 1
                """;

            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "channel_id";
            parameter.Value = channelId;
            command.Parameters.Add(parameter);

            object? result = await command.ExecuteScalarAsync(ct);

            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }
}
