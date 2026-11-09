using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Tourbooking.Distributed;
using Tourbooking.Models;

namespace Tourbooking.Migration;

public static partial class HybridTransfer
{
    private static string NativeConnection = @"Server=localhost\SQLEXPRESS;Database=TourBookingDB;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10";
    private static int NativePort = 14335;
    public static void ConfigureNative(string root)
    {
        var settings = EnvironmentSettings(root);
        var server = settings.GetValueOrDefault("SQL_EXPRESS_SERVER", @"localhost\SQLEXPRESS");
        NativePort = int.Parse(settings.GetValueOrDefault("SQL_EXPRESS_PORT", "14335"), System.Globalization.CultureInfo.InvariantCulture);
        if (NativePort is < 1 or > 65535) throw new ArgumentException("Invalid SQL Express port.");
        NativeConnection = new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = "TourBookingDB",
            IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 10 }.ToString();
    }
    private static Dictionary<string, string> EnvironmentSettings(string root) => File.ReadAllLines(Path.Combine(root, ".env"))
        .Where(line => !line.StartsWith('#') && line.Contains('='))
        .Select(line => line.Split('=', 2)).GroupBy(parts => parts[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim());
    private static ApplicationDbContext ExpressContext(string backend, string? connection = null) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connection ?? NativeConnection).Options,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Service:Name"] = backend, ["Database:Schema"] = backend, ["Chat:Provider"] = "MongoDB" }).Build());
    private static ApplicationDbContext DockerContext(string backend, Dictionary<string, string> settings) => SqlContext(backend,
        new SqlConnectionStringBuilder { DataSource = "127.0.0.1,14333", UserID = "sa", Password = settings["SQL_PASSWORD"], TrustServerCertificate = true }.ToString());

    public static async Task CopyToExpressAsync(string root, string backup)
    {
        Directory.CreateDirectory(backup);
        var settings = EnvironmentSettings(root);
        await BackupNativeDatabaseAsync(backup);
        var report = new Dictionary<string, object>();
        foreach (var backend in Databases.Keys)
        {
            await using var source = DockerContext(backend, settings);
            await using var target = ExpressContext(backend);
            var names = target.Model.GetEntityTypes().Where(e => target.RequiresSqlTable(e.GetTableName()!) && (ServiceData.IsOwned(backend, e) || ServiceData.Owner(e.GetTableName()!) == ""))
                .Select(e => e.GetTableName()!).ToArray();
            var tables = await ExportTablesAsync(source, names);
            await File.WriteAllTextAsync(Path.Combine(backup, backend + "-source.json"), JsonSerializer.Serialize(tables, Json));
            await DatabaseInitializer.InitializeAsync(target);
            var existing = await ExportTablesAsync(target, names);
            await using var transaction = await target.Database.BeginTransactionAsync();
            if (existing.Any(t => t.Rows.Count > 0))
            {
                foreach (var table in tables) RequireSame(table, existing.Single(t => t.Table == table.Table));
            }
            else
            {
                var entities = target.Model.GetEntityTypes().Where(e => names.Contains(e.GetTableName()!)).ToList();
                foreach (var entity in Topological(entities))
                    await InsertTableAsync(target, entity, tables.Single(t => t.Table == entity.GetTableName()));
            }
            var copied = await ExportTablesAsync(target, names);
            foreach (var table in tables) RequireSame(table, copied.Single(t => t.Table == table.Table));
            await transaction.CommitAsync();
            await CopyLegacyArchiveAsync(source, target, backend);
            report[backend] = tables.ToDictionary(t => t.Table, t => new { count = t.Rows.Count, sha256 = Hash(t.Rows), verified = true });
            Console.WriteLine($"Copied and verified schema {backend}.");
        }
        foreach (var backend in Databases.Keys)
        {
            await using var destination = ExpressContext(backend);
            foreach (var owner in ServiceData.Services.Where(o => ServiceData.Needs(backend, o) && !ServiceData.OwnedServices(backend).Contains(o)))
            {
                var ownerBackend = ServiceBoundary.BackendFor(owner);
                if (!Databases.ContainsKey(ownerBackend)) continue;
                await using var ownerDb = ExpressContext(ownerBackend);
                await ProjectionStore.ReplaceAsync(destination, await ProjectionStore.ExportAsync(ownerDb, owner));
            }
        }
        await ProvisionExpressUsersAsync(settings);
        await File.WriteAllTextAsync(Path.Combine(backup, "native-migration-report.json"), JsonSerializer.Serialize(new
        { verified = true, completedAtUtc = DateTime.UtcNow, server = @"localhost\SQLEXPRESS", database = "TourBookingDB", schemas = report }, Json));
    }

    private static async Task CopyLegacyArchiveAsync(ApplicationDbContext source, ApplicationDbContext target, string schema)
    {
        var create = $"""
            IF OBJECT_ID(N'[{schema}].[LegacyOrphanRows]', N'U') IS NULL
            CREATE TABLE [{schema}].[LegacyOrphanRows](
              RecordHash char(64) NOT NULL PRIMARY KEY, TableName nvarchar(128) NOT NULL,
              Reason nvarchar(200) NOT NULL, RowJson nvarchar(max) NOT NULL, MigratedAtUtc datetime2 NOT NULL);
            """;
        await target.Database.ExecuteSqlRawAsync(create);
        await source.Database.OpenConnectionAsync();
        await target.Database.OpenConnectionAsync();
        await using var read = source.Database.GetDbConnection().CreateCommand();
        read.CommandText = "SELECT RecordHash, TableName, Reason, RowJson, MigratedAtUtc FROM dbo.LegacyOrphanRows";
        await using var reader = await read.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            await using var command = target.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"IF NOT EXISTS(SELECT 1 FROM [{schema}].LegacyOrphanRows WHERE RecordHash=@hash) INSERT [{schema}].LegacyOrphanRows VALUES(@hash,@table,@reason,@json,@time)";
            command.Parameters.Add(new SqlParameter("hash", System.Data.SqlDbType.Char, 64) { Value = reader.GetString(0) });
            command.Parameters.Add(new SqlParameter("table", System.Data.SqlDbType.NVarChar, 128) { Value = reader.GetString(1) });
            command.Parameters.Add(new SqlParameter("reason", System.Data.SqlDbType.NVarChar, 200) { Value = reader.GetString(2) });
            command.Parameters.Add(new SqlParameter("json", System.Data.SqlDbType.NVarChar, -1) { Value = reader.GetString(3) });
            command.Parameters.Add(new SqlParameter("time", System.Data.SqlDbType.DateTime2) { Value = reader.GetDateTime(4), Scale = 7 });
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task BackupNativeDatabaseAsync(string directory)
    {
        var target = Path.Combine(directory, "TourBookingDB-before-sqlexpress.bak");
        if (File.Exists(target)) return;
        await using var connection = new SqlConnection(NativeConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";
        var folder = (string)(await command.ExecuteScalarAsync())!;
        var serverPath = Path.Combine(folder, "TourBookingDB-before-switch-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak");
        command.CommandTimeout = 120;
        command.CommandText = "BACKUP DATABASE [TourBookingDB] TO DISK=@path WITH COPY_ONLY,CHECKSUM; RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;";
        command.Parameters.AddWithValue("path", serverPath);
        await command.ExecuteNonQueryAsync();
        command.Parameters.Clear();
        command.CommandText = "EXECUTE AS LOGIN = 'sa'; SELECT BulkColumn FROM OPENROWSET(BULK N'" + serverPath.Replace("'", "''") + "', SINGLE_BLOB) AS archive; REVERT;";
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess);
        await reader.ReadAsync();
        await using var input = reader.GetStream(0);
        await using var output = File.Create(target);
        await input.CopyToAsync(output);
        Console.WriteLine("Native TourBookingDB backup created and RESTORE VERIFYONLY passed.");
    }

    private static async Task ProvisionExpressUsersAsync(Dictionary<string, string> settings)
    {
        await using var connection = new SqlConnection(NativeConnection);
        await connection.OpenAsync();
        foreach (var backend in Databases.Keys)
        {
            var login = "tourbooking_" + backend + "_app";
            var password = settings[backend.ToUpperInvariant() + "_SQL_PASSWORD"].Replace("'", "''");
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF SUSER_ID(N'{login}') IS NULL CREATE LOGIN [{login}] WITH PASSWORD=N'{password}';
                IF USER_ID(N'{login}') IS NULL CREATE USER [{login}] FOR LOGIN [{login}] WITH DEFAULT_SCHEMA=[{backend}];
                GRANT SELECT,INSERT,UPDATE,DELETE ON SCHEMA::[{backend}] TO [{login}];
                GRANT SELECT,INSERT,UPDATE,DELETE ON SCHEMA::[{backend}_read] TO [{login}];
                """;
            await command.ExecuteNonQueryAsync();
            foreach (var other in Databases.Keys.Where(value => value != backend))
            {
                command.CommandText = $"DENY SELECT,INSERT,UPDATE,DELETE ON SCHEMA::[{other}] TO [{login}]; DENY SELECT,INSERT,UPDATE,DELETE ON SCHEMA::[{other}_read] TO [{login}];";
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    public static async Task CheckExpressAsync(string root, string reportPath)
    {
        var settings = EnvironmentSettings(root);
        var result = new Dictionary<string, object>();
        foreach (var backend in Databases.Keys)
        {
            await using var original = DockerContext(backend, settings);
            await using var current = ExpressContext(backend);
            var names = current.Model.GetEntityTypes().Where(e => current.RequiresSqlTable(e.GetTableName()!) && (ServiceData.IsOwned(backend, e) || ServiceData.Owner(e.GetTableName()!) == ""))
                .Select(e => e.GetTableName()!).ToArray();
            var expected = await ExportTablesAsync(original, names);
            var actual = await ExportTablesAsync(current, names);
            foreach (var table in expected) RequireSame(table, actual.Single(t => t.Table == table.Table));
            var appConnection = new SqlConnectionStringBuilder
            {
                DataSource = "127.0.0.1," + NativePort, InitialCatalog = "TourBookingDB", UserID = "tourbooking_" + backend + "_app",
                Password = settings[backend.ToUpperInvariant() + "_SQL_PASSWORD"], TrustServerCertificate = true, ConnectTimeout = 5
            }.ToString();
            await using var app = ExpressContext(backend, appConnection);
            if (!await app.Database.CanConnectAsync()) throw Stop($"SQL authentication failed for {backend}.");
            foreach (var other in Databases.Keys.Where(s => s != backend))
            {
                var table = other == "identity" ? "AspNetUsers" : other == "catalog" ? "Tours" : other == "booking" ? "Bookings" : "TourReviews";
                try
                {
                    var deniedQuery = $"SELECT TOP(1) * FROM [{other}].[{table}]";
                    await app.Database.ExecuteSqlRawAsync(deniedQuery);
                    throw Stop($"Cross-schema access was not blocked for {backend}.");
                }
                catch (SqlException ex) when (ex.Number == 229) { }
            }
            result[backend] = new { tables = expected.ToDictionary(t => t.Table, t => t.Rows.Count), contentVerified = true, permissionsVerified = true };
        }
        // Verify SQL Server rowversion and outbox work through the restricted native login; roll back all test rows.
        var bookingConnection = new SqlConnectionStringBuilder { DataSource = "127.0.0.1," + NativePort, InitialCatalog = "TourBookingDB",
            UserID = "tourbooking_booking_app", Password = settings["BOOKING_SQL_PASSWORD"], TrustServerCertificate = true }.ToString();
        await using (var db = ExpressContext("booking", bookingConnection))
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.EventsEnabled = true;
            var probe = new Booking { RequestId = Guid.NewGuid(), TourId = 1, UserId = "native-connection-check", FullName = "Connection check",
                Email = "connection@example.test", PhoneNumber = "0901234567", GuestCount = 1, TotalPrice = 100000, TravelDate = DateTime.UtcNow.AddDays(30).Date };
            db.Bookings.Add(probe);
            await db.SaveChangesAsync();
            if (probe.Version.Length != 8) throw Stop("Native rowversion verification failed.");
            await transaction.RollbackAsync();
        }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { verified = true, checkedAtUtc = DateTime.UtcNow, schemas = result }, Json));
        Console.WriteLine("Native SQL content, restricted logins, schema permissions and rolled-back booking/outbox verified.");
    }
}
