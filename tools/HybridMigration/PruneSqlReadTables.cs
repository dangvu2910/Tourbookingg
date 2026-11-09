using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Tourbooking.Migration;

public static partial class HybridTransfer
{
    public static async Task PruneExpressReadTablesAsync(string root, string backup)
    {
        Directory.CreateDirectory(backup);
        var before = new Dictionary<string, List<Tourbooking.Distributed.TableSnapshot>>();
        var candidates = new List<(string Schema, string Table)>();
        foreach (var service in Databases.Keys)
        {
            await using var db = ExpressContext(service);
            var names = db.Model.GetEntityTypes().Where(e => db.RequiresSqlTable(e.GetTableName()!) && (Tourbooking.Distributed.ServiceData.IsOwned(service, e) ||
                Tourbooking.Distributed.ServiceData.Owner(e.GetTableName()!) == "")).Select(e => e.GetTableName()!).ToArray();
            before[service] = await ExportTablesAsync(db, names);
            candidates.AddRange(db.Model.GetEntityTypes().Where(e =>
                !Tourbooking.Distributed.ServiceData.RequiresTable(service, e.GetTableName()!))
                .Select(e => (service + "_read", e.GetTableName()!)));
        }
        await using var connection = new SqlConnection(NativeConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";
        var serverPath = Path.Combine((string)(await command.ExecuteScalarAsync())!, "TourBookingDB-before-read-trim-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak");
        command.CommandText = "BACKUP DATABASE [TourBookingDB] TO DISK=@path WITH COPY_ONLY,CHECKSUM; RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;";
        command.Parameters.AddWithValue("path", serverPath);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
        await File.WriteAllTextAsync(Path.Combine(backup, "backup-receipt.json"), JsonSerializer.Serialize(new { serverPath, restoreVerifyOnly = true }, Json));
        command.Parameters.Clear();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        command.Transaction = transaction;
        var removed = new List<object>();
        foreach (var (schema, table) in candidates)
        {
            // Both identifiers come from fixed service names and EF metadata. Owner schemas are never candidates.
            command.CommandText = $"IF OBJECT_ID(N'[{schema}].[{table}]',N'U') IS NOT NULL BEGIN SELECT COUNT_BIG(*) FROM [{schema}].[{table}]; DROP TABLE [{schema}].[{table}]; END";
            var count = await command.ExecuteScalarAsync();
            if (count is not null) removed.Add(new { schema, table, rows = Convert.ToInt64(count) });
        }
        // Validate all owner content on the same transaction before committing the cleanup.
        foreach (var service in Databases.Keys)
        {
            await using var db = ExpressContext(service, NativeConnection);
            db.Database.SetDbConnection(connection, contextOwnsConnection: false);
            await db.Database.UseTransactionAsync(transaction);
            var after = await ExportTablesAsync(db, before[service].Select(t => t.Table).ToArray());
            foreach (var original in before[service]) RequireSame(original, after.Single(t => t.Table == original.Table));
        }
        await transaction.CommitAsync();
        await File.WriteAllTextAsync(Path.Combine(backup, "read-table-trim-report.json"), JsonSerializer.Serialize(new
        { verified = true, server = @"localhost\SQLEXPRESS", database = "TourBookingDB", ownerContentUnchanged = true, removed }, Json));
        Console.WriteLine($"Removed {removed.Count} unused read tables; owner data verified unchanged. Backup: {serverPath}");
    }
}
