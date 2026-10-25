using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Tourbooking.Distributed;

public sealed record TableSnapshot(string Table, List<Dictionary<string, JsonElement>> Rows);

public static class ProjectionStore
{
    public static async Task<List<TableSnapshot>> ExportAsync(ApplicationDbContext db, string owner, bool publicOnly = true, CancellationToken ct = default)
    {
        var result = new List<TableSnapshot>();
        foreach (var entity in db.Model.GetEntityTypes().Where(e => ServiceData.Owner(e.GetTableName()!) == owner))
        {
            var table = entity.GetTableName()!;
            if (publicOnly && table is "AspNetUserTokens" or "AspNetUserLogins" or "AspNetUserClaims" or "AspNetRoleClaims") continue;
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
        return rows.Select(row => metadata.GetProperties().ToDictionary(p => p.Name, p =>
            JsonSerializer.SerializeToElement(publicOnly && p.Name is "PasswordHash" or "AuthenticatorKey" ? null : p.PropertyInfo!.GetValue(row), p.ClrType))).ToList();
    }

    public static async Task ReplaceAsync(ApplicationDbContext db, IEnumerable<TableSnapshot> tables, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var snapshot in tables)
        {
            var entity = db.Model.GetEntityTypes().Single(e => e.GetTableName() == snapshot.Table);
            var owner = ServiceData.Owner(snapshot.Table);
            if (owner == "" || owner == db.EventSource) throw new InvalidOperationException("Cannot replace an owned table through projections.");
            await WriteTableAsync(db, entity, snapshot, ct);
        }
        await tx.CommitAsync(ct);
    }

    public static async Task WriteTableAsync(ApplicationDbContext db, IEntityType entity, TableSnapshot snapshot, CancellationToken ct)
    {
        static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
        // Identifiers come exclusively from validated EF metadata; values below are parameterized.
        var deleteSql = "DELETE FROM " + Quote(entity.GetTableName()!);
        await db.Database.ExecuteSqlRawAsync(deleteSql, ct);
        var props = entity.GetProperties().ToArray();
        var sql = $"INSERT INTO {Quote(snapshot.Table)} ({string.Join(",", props.Select(p => Quote(p.GetColumnName())) )}) VALUES ({string.Join(",", props.Select((_, i) => "{" + i + "}"))})";
        foreach (var row in snapshot.Rows)
        {
            var values = props.Select((p, i) =>
            {
                var value = row[p.Name].Deserialize(p.ClrType);
                var providerValue = p.GetTypeMapping().Converter?.ConvertToProvider(value) ?? value;
                return (object)new Microsoft.Data.Sqlite.SqliteParameter("p" + i, providerValue ?? DBNull.Value);
            }).ToArray();
            await db.Database.ExecuteSqlRawAsync(sql, values, ct);
        }
    }
}
