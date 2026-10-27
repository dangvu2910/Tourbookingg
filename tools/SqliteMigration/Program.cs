using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tourbooking.Distributed;
using Tourbooking.Notifications;

if (args.FirstOrDefault() == "--replay")
{
    if (args.Length != 4 || !ServiceData.Services.Contains(args[1]) || !File.Exists(args[3]))
        throw new ArgumentException("Usage: --replay <service> <sinceUtc> <existingDatabasePath>");
    var since = DateTime.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Service:Name"] = args[1] }).Build();
    await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlite($"Data Source={Path.GetFullPath(args[3])};Mode=ReadWrite;Foreign Keys=False").Options, config);
    var events = await db.OutboxMessages.Where(m => m.Source == args[1] && m.CreatedAt >= since).ToListAsync();
    foreach (var evt in events) { evt.PublishedAt = null; evt.LockedUntil = null; evt.LockId = null; evt.LastError = null; }
    await db.SaveChangesAsync();
    Console.WriteLine($"Events scheduled for replay: {events.Count}");
    return;
}

if (args.FirstOrDefault() == "--consolidate")
{
    if (args.Length != 3)
        throw new ArgumentException("Usage: --consolidate <projectRoot> <emptyStagingDirectory>");
    await ConsolidateSqliteAsync(args[1], args[2]);
    return;
}

// Source credentials are environment-only and never included in output or reports.
var sourceConnection = Environment.GetEnvironmentVariable("MIGRATION_SQL_CONNECTION");
var notificationConnection = Environment.GetEnvironmentVariable("MIGRATION_NOTIFICATION_CONNECTION");
if (string.IsNullOrWhiteSpace(sourceConnection))
{
    var password = Environment.GetEnvironmentVariable("SQL_PASSWORD") ?? throw new InvalidOperationException("Set MIGRATION_SQL_CONNECTION or SQL_PASSWORD.");
    var host = Environment.GetEnvironmentVariable("MIGRATION_SQL_HOST") ?? "sqlserver";
    var cs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder { DataSource = host, InitialCatalog = "TourBookingDb", UserID = "sa", Password = password, TrustServerCertificate = true };
    sourceConnection = cs.ConnectionString;
    cs.InitialCatalog = "TourBookingNotifications";
    notificationConnection ??= cs.ConnectionString;
}
if (string.IsNullOrWhiteSpace(notificationConnection)) throw new InvalidOperationException("Set MIGRATION_NOTIFICATION_CONNECTION to preserve notification data.");
var output = Path.GetFullPath(args.FirstOrDefault() ?? "data/sqlite");
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new InvalidOperationException("Destination must be empty. Existing databases will never be overwritten.");
Directory.CreateDirectory(output);
await using var source = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(sourceConnection).Options);
await using var notificationSource = new NotificationDb(new DbContextOptionsBuilder<NotificationDb>().UseSqlServer(notificationConnection).Options);
var snapshots = new Dictionary<string, List<TableSnapshot>>();
foreach (var owner in ServiceData.Services.Where(s => s != "reporting"))
    snapshots[owner] = await ProjectionStore.ExportAsync(source, owner, publicOnly: false);
var outbox = await source.OutboxMessages.AsNoTracking().ToListAsync();
var notifications = await notificationSource.Notifications.AsNoTracking().ToListAsync();
var report = new List<object>();
foreach (var service in ServiceBoundary.Backends.Where(s => s != "notification"))
{
    var folder = Path.Combine(output, service);
    Directory.CreateDirectory(folder);
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Service:Name"] = service }).Build();
    await using var target = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlite($"Data Source={Path.Combine(folder, service + ".db")};Foreign Keys=False").Options, config);
    await target.Database.EnsureCreatedAsync();
    await using var tx = await target.Database.BeginTransactionAsync();
    var ownedServices = ServiceData.OwnedServices(service);
    foreach (var (owner, tables) in snapshots.Where(s => ownedServices.Contains(s.Key)
        || ownedServices.Any(serviceOwner => ServiceData.Needs(serviceOwner, s.Key))))
    {
        foreach (var original in tables)
        {
            var snapshot = original;
            if (!ownedServices.Contains(owner) && original.Table.StartsWith("AspNet"))
            {
                if (original.Table is not ("AspNetUsers" or "AspNetRoles" or "AspNetUserRoles")) continue;
                snapshot = original with { Rows = original.Rows.Select(row => row.ToDictionary(p => p.Key,
                    p => p.Key == "PasswordHash" ? JsonSerializer.SerializeToElement<string?>(null) : p.Value)).ToList() };
            }
            var entity = target.Model.GetEntityTypes().Single(e => e.GetTableName() == snapshot.Table);
            await ProjectionStore.WriteTableAsync(target, entity, snapshot, default);
            var method = typeof(ProjectionStore).GetMethod(nameof(ProjectionStore.ReadAsync))!.MakeGenericMethod(entity.ClrType);
            var actual = await (Task<List<Dictionary<string,JsonElement>>>)method.Invoke(null, [target, false, CancellationToken.None])!;
            var expectedHash = Hash(snapshot.Rows);
            var actualHash = Hash(actual);
            if (expectedHash != actualHash) throw new InvalidOperationException($"Verification failed: {service}/{snapshot.Table}");
            report.Add(new { service, table = snapshot.Table, owner, count = actual.Count, sha256 = actualHash });
            Console.WriteLine($"Verified {service}/{snapshot.Table}: {actual.Count}");
        }
    }
    // Preserve historical events without automatically replaying published events.
    var events = outbox.Where(m => ownedServices.Contains(m.Source)
        || service == "booking" && !ServiceData.Services.Contains(m.Source)).ToList();
    target.OutboxMessages.AddRange(events);
    target.ApplyingProjection = true;
    await target.SaveChangesAsync();
    await tx.CommitAsync();
    var check = await target.Database.SqlQueryRaw<string>("PRAGMA integrity_check").ToListAsync();
    if (check.Single() != "ok") throw new InvalidOperationException("SQLite integrity check failed.");
}
var notificationFolder = Path.Combine(output, "notification");
Directory.CreateDirectory(notificationFolder);
await using (var target = new NotificationDb(new DbContextOptionsBuilder<NotificationDb>()
    .UseSqlite($"Data Source={Path.Combine(notificationFolder, "notification.db")}").Options))
{
    await target.Database.EnsureCreatedAsync();
    target.Notifications.AddRange(notifications);
    await target.SaveChangesAsync();
    var actual = await target.Notifications.AsNoTracking().ToListAsync();
    if (HashObjects(notifications) != HashObjects(actual)) throw new InvalidOperationException("Notification verification failed.");
    report.Add(new { service = "notification", table = "Notifications", count = actual.Count, sha256 = HashObjects(actual) });
}
await File.WriteAllTextAsync(Path.Combine(output, "migration-report.json"), JsonSerializer.Serialize(new { completedAtUtc = DateTime.UtcNow, verified = true, tables = report }, new JsonSerializerOptions { WriteIndented = true }));
// Archive historical outbox rows even if a retired/standalone source no longer has a dispatcher.
await File.WriteAllTextAsync(Path.Combine(output, "legacy-outbox.json"), JsonSerializer.Serialize(outbox));
Console.WriteLine("Migration complete. SQL Server was read only. Report: " + Path.Combine(output, "migration-report.json"));

// SQL Server decimal(18,2) and SQLite scaled integers may serialize 1.00 and 1.
// Compare numeric values canonically, while keeping all non-numeric values exact.
static string Hash(List<Dictionary<string,JsonElement>> rows) => HashObjects(rows.Select(r => string.Join("|", r.OrderBy(p => p.Key).Select(p =>
    JsonSerializer.Serialize(p.Key) + ":" + (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDecimal(out var number)
        ? number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture) : p.Value.GetRawText())))));
static string HashObjects<T>(IEnumerable<T> rows) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows.Select(r => JsonSerializer.Serialize(r)).Order(StringComparer.Ordinal)))));

static async Task ConsolidateSqliteAsync(string projectRootArgument, string destinationArgument)
{
    static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    var projectRoot = Path.GetFullPath(projectRootArgument);
    var destination = Path.GetFullPath(destinationArgument);
    if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        throw new InvalidOperationException("The staging directory must be empty.");

    var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var service in new[] { "Identity", "Catalog", "Booking", "Payment", "Review", "Support", "Notification" })
    {
        var path = Path.Combine(projectRoot, "services", service, "data", service.ToLowerInvariant() + ".db");
        if (!File.Exists(path))
            path = Path.Combine(projectRoot, "sqlite", service.ToLowerInvariant(), service.ToLowerInvariant() + ".db");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required {service} SQLite database was not found.", path);
        sourcePaths[service] = path;
    }

    Directory.CreateDirectory(destination);
    var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (backend, sourceService, databaseName) in new[]
    {
        ("identity", "Identity", "identity"),
        ("catalog", "Catalog", "catalog"),
        ("booking", "Booking", "booking"),
        ("customer", "Review", "customer"),
        ("notification", "Notification", "notification")
    })
    {
        var folder = Path.Combine(destination, backend);
        Directory.CreateDirectory(folder);
        var targetPath = Path.Combine(folder, databaseName + ".db");
        await using var source = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sourcePaths[sourceService], Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        await source.OpenAsync();
        await using (var backup = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = targetPath }.ToString()))
        {
            await backup.OpenAsync();
            source.BackupDatabase(backup);
        }
        destinations[backend] = targetPath;
    }

    var merged = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["booking"] = ["Payments"],
        ["customer"] = ["TourReviews", "TourReviewVotes", "ContactInquiries", "ContactInquiryReplies"]
    };
    var mergeSources = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["booking"] = [sourcePaths["Payment"]],
        ["customer"] = [sourcePaths["Support"]]
    };
    var report = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);

    foreach (var backend in new[] { "booking", "customer" })
    {
        var targetPath = destinations[backend];
        var tableCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var target = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = targetPath }.ToString());
        await target.OpenAsync();
        using var transaction = target.BeginTransaction();
        var mergeTables = merged[backend].Concat(["OutboxMessages", "InboxMessages"]).ToArray();
        foreach (var sourcePath in mergeSources[backend])
        {
            await using var attach = target.CreateCommand();
            attach.Transaction = transaction;
            attach.CommandText = "ATTACH DATABASE $path AS legacy";
            attach.Parameters.AddWithValue("$path", sourcePath);
            await attach.ExecuteNonQueryAsync();

            foreach (var table in mergeTables)
            {
                var columns = new List<string>();
                await using (var schema = target.CreateCommand())
                {
                    schema.Transaction = transaction;
                    schema.CommandText = $"PRAGMA legacy.table_info({Quote(table)})";
                    await using var reader = await schema.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
                }
                if (columns.Count == 0)
                    throw new InvalidOperationException($"Missing {table} in source database {sourcePath}.");

                var columnList = string.Join(", ", columns.Select(Quote));
                if (table is "OutboxMessages" or "InboxMessages")
                {
                    var sameId = table == "OutboxMessages" ? "Id" : "Id";
                    var different = string.Join(" OR ", columns.Select(column =>
                        $"NOT (destination.{Quote(column)} IS legacy.{Quote(column)})"));
                    await using var conflict = target.CreateCommand();
                    conflict.Transaction = transaction;
                    conflict.CommandText = $"SELECT COUNT(*) FROM main.{Quote(table)} AS destination JOIN legacy.{Quote(table)} AS legacy ON destination.{Quote(sameId)} = legacy.{Quote(sameId)} WHERE {different}";
                    if (Convert.ToInt64(await conflict.ExecuteScalarAsync()) != 0)
                        throw new InvalidOperationException($"Conflicting {table} IDs while merging {Path.GetFileName(sourcePath)}.");
                    await using var merge = target.CreateCommand();
                    merge.Transaction = transaction;
                    merge.CommandText = $"INSERT OR IGNORE INTO main.{Quote(table)} ({columnList}) SELECT {columnList} FROM legacy.{Quote(table)}";
                    await merge.ExecuteNonQueryAsync();
                }
                else
                {
                    await using var merge = target.CreateCommand();
                    merge.Transaction = transaction;
                    merge.CommandText = $"DELETE FROM main.{Quote(table)}; INSERT INTO main.{Quote(table)} ({columnList}) SELECT {columnList} FROM legacy.{Quote(table)}";
                    await merge.ExecuteNonQueryAsync();
                }
            }

        }

        foreach (var table in mergeTables)
        {
            await using var count = target.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = $"SELECT COUNT(*) FROM main.{Quote(table)}";
            tableCounts[table] = Convert.ToInt64(await count.ExecuteScalarAsync());
        }
        await transaction.CommitAsync();

        await using var integrity = target.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        var result = (string?)await integrity.ExecuteScalarAsync();
        if (result != "ok")
            throw new InvalidOperationException($"SQLite integrity check failed for {backend}: {result}");
        report[backend] = tableCounts;
    }

    foreach (var (backend, path) in destinations.Where(item => item.Key is "identity" or "catalog" or "notification"))
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        if ((string?)await integrity.ExecuteScalarAsync() != "ok")
            throw new InvalidOperationException($"SQLite integrity check failed for {backend}.");
    }

    await File.WriteAllTextAsync(Path.Combine(destination, "consolidation-report.json"),
        JsonSerializer.Serialize(new { completedAtUtc = DateTime.UtcNow, verified = true, databases = report }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Verified consolidated databases and written report to {destination}.");
}
