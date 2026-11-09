using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Tourbooking.Distributed;

public sealed record TableSnapshot(string Table, List<Dictionary<string, JsonElement>> Rows);

public static class ProjectionStore
{
    private static readonly HashSet<string> UserSummaryFields =
        ["Id", "UserName", "NormalizedUserName", "FullName", "Email", "NormalizedEmail", "PhoneNumber", "CreatedAt"];

    public static async Task<List<TableSnapshot>> ExportAsync(ApplicationDbContext db, string owner, bool publicOnly = true, CancellationToken ct = default)
    {
        var result = new List<TableSnapshot>();
        foreach (var entity in db.Model.GetEntityTypes().Where(e => ServiceData.Owner(e.GetTableName()!) == owner))
        {
            var table = entity.GetTableName()!;
            if (publicOnly && table == "SeatReservations") continue;
            if (db.Database.IsSqlServer() && !db.RequiresSqlTable(table)) continue;
            if (publicOnly && table.StartsWith("AspNet", StringComparison.Ordinal) && table != "AspNetUsers") continue;
            var method = typeof(ProjectionStore).GetMethod(nameof(ReadAsync))!.MakeGenericMethod(entity.ClrType);
            var rows = await (Task<List<Dictionary<string, JsonElement>>>)method.Invoke(null, [db, publicOnly, ct])!;
            result.Add(new(table, rows));
        }
        return result;
    }

    public static async Task<List<Dictionary<string, JsonElement>>> ReadAsync<T>(ApplicationDbContext db, bool publicOnly, CancellationToken ct) where T : class
    {
        var metadata = db.Model.FindEntityType(typeof(T))!;
        var rows = await db.Set<T>().AsNoTracking().ToListAsync(ct);
        return rows.Select(row => metadata.GetProperties()
            .Where(p => !publicOnly || metadata.GetTableName() != "AspNetUsers" || UserSummaryFields.Contains(p.Name))
            .ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.PropertyInfo!.GetValue(row), p.ClrType))).ToList();
    }

    public static async Task ReplaceAsync(ApplicationDbContext db, IEnumerable<TableSnapshot> tables, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource='projection-refresh', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000; IF @result<0 THROW 51000,'Projection refresh lock unavailable',1;", ct);
        var snapshots = tables.ToArray();
        foreach (var snapshot in snapshots)
        {
            var entity = db.Model.GetEntityTypes().Single(e => e.GetTableName() == snapshot.Table);
            var owner = ServiceData.Owner(snapshot.Table);
            if (owner == "" || ServiceData.OwnedServices(db.EventSource).Contains(owner))
                throw new InvalidOperationException("Cannot replace an owned table through projections.");
            if (db.Database.IsSqlServer() && !db.RequiresSqlTable(snapshot.Table)) continue;
            await WriteTableAsync(db, entity, snapshot, ct);
        }
        await tx.CommitAsync(ct);
    }

    public static async Task WriteTableAsync(ApplicationDbContext db, IEntityType entity, TableSnapshot snapshot, CancellationToken ct)
    {
        static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
        var qualifiedTable = (entity.GetSchema() is { } schema ? Quote(schema) + "." : "") + Quote(entity.GetTableName()!);
        // Identifiers come exclusively from validated EF metadata; values below are parameterized.
        var deleteSql = "DELETE FROM " + qualifiedTable;
        await db.Database.ExecuteSqlRawAsync(deleteSql, ct);
        var props = entity.GetProperties().Where(p => !db.Database.IsSqlServer() || p.GetColumnType() != "rowversion").ToArray();
        var sql = $"INSERT INTO {qualifiedTable} ({string.Join(",", props.Select(p => Quote(p.GetColumnName())) )}) VALUES ({string.Join(",", props.Select((_, i) => "{" + i + "}"))})";
        var identityInsert = db.Database.IsSqlServer() && props.Any(p => SqlServerPropertyExtensions.GetValueGenerationStrategy(p) == SqlServerValueGenerationStrategy.IdentityColumn);
        var identityOnSql = "SET IDENTITY_INSERT " + qualifiedTable + " ON";
        var identityOffSql = "SET IDENTITY_INSERT " + qualifiedTable + " OFF";
        if (identityInsert) await db.Database.ExecuteSqlRawAsync(identityOnSql, ct);
        try
        {
        foreach (var row in snapshot.Rows)
        {
            var values = props.Select((p, i) =>
            {
                var summaryDefault = entity.GetTableName() == "AspNetUsers" && !UserSummaryFields.Contains(p.Name);
                var value = summaryDefault ? (p.ClrType.IsValueType ? Activator.CreateInstance(p.ClrType) : null)
                    : row.TryGetValue(p.Name, out var json) ? json.Deserialize(p.ClrType)
                    : p.IsNullable ? null : throw new InvalidOperationException($"Required projection field {p.Name} is missing.");
                var providerValue = p.GetTypeMapping().Converter?.ConvertToProvider(value) ?? value;
                return db.Database.IsSqlServer()
                    ? (object)new Microsoft.Data.SqlClient.SqlParameter("p" + i, providerValue ?? DBNull.Value)
                    : new Microsoft.Data.Sqlite.SqliteParameter("p" + i, providerValue ?? DBNull.Value);
            }).ToArray();
            await db.Database.ExecuteSqlRawAsync(sql, values, ct);
        }
        }
        finally
        {
            if (identityInsert) await db.Database.ExecuteSqlRawAsync(identityOffSql, ct);
        }
    }
}
