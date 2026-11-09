using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Tourbooking.Chat;
using Tourbooking.Distributed;
using Tourbooking.Models;
using Tourbooking.Notifications;

namespace Tourbooking.Migration;

public static partial class HybridTransfer
{
    public static async Task VerifyAsync(string directory, string reportPath)
    {
        var source = await LoadAsync(Path.Combine(directory, "working"));
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var sqlBase = config["Migration:SqlServer"] ?? throw Stop("Missing SQL migration credential.");
        var mongoConnection = config["Migration:MongoDB"] ?? throw Stop("Missing Mongo migration credential.");
        var sqlReport = new Dictionary<string, object>();
        foreach (var (backend, tables) in source.Tables)
        {
            await using var db = SqlContext(backend, sqlBase);
            await db.Database.OpenConnectionAsync();
            var actual = await ExportTablesAsync(db, tables.Select(t => t.Table));
            var tableReport = new Dictionary<string, object>();
            foreach (var expected in tables)
            {
                var active = actual.Single(t => t.Table == expected.Table);
                var archived = await ReadOrphansAsync(db, expected.Table);
                var combined = active.Rows.Concat(archived).ToList();
                var keyProperties = db.Model.GetEntityTypes().Single(e => e.GetTableName() == expected.Table).FindPrimaryKey()!.Properties;
                string Key(Dictionary<string, JsonElement> row) => string.Join("|", keyProperties.Select(p => Canonical(row[p.Name])));
                var byKey = combined.ToDictionary(Key, StringComparer.Ordinal);
                foreach (var row in expected.Rows)
                    if (!byKey.TryGetValue(Key(row), out var saved) || Hash([row]) != Hash([saved]))
                        throw Stop($"Post-start content verification failed for {Databases[backend]}.{expected.Table}.");
                tableReport[expected.Table] = new { sourceCount = expected.Rows.Count, activeCount = active.Rows.Count,
                    archivedCount = archived.Count, verified = true };
            }
            sqlReport[Databases[backend]] = tableReport;
        }
        var chat = new MongoChatStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Chat:ConnectionString"] = mongoConnection }).Build());
        var documents = await chat.Conversations.Find(Builders<ChatDocument>.Filter.Empty).ToListAsync();
        foreach (var inquiry in source.Conversations)
        {
            var expected = new ChatDocument { Id = inquiry.ContactInquiryId, Inquiry = inquiry,
                Replies = source.Replies.Where(r => r.ContactInquiryId == inquiry.ContactInquiryId).OrderBy(r => r.ContactInquiryReplyId).ToList() };
            var actual = documents.SingleOrDefault(d => d.Id == expected.Id) ?? throw Stop("A migrated conversation is missing.");
            actual.PendingEvents = []; // Dispatcher publication state may advance after startup.
            if (HashObjects([expected], mongo: true) != HashObjects([actual], mongo: true)) throw Stop("Post-start chat content verification failed.");
        }
        _ = new MongoNotificationStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["MongoDB:ConnectionString"] = mongoConnection }).Build());
        var savedNotifications = await new MongoClient(mongoConnection).GetDatabase("NotificationDB")
            .GetCollection<NotificationRecord>("notifications").Find(Builders<NotificationRecord>.Filter.Empty).ToListAsync();
        var notificationsById = savedNotifications.ToDictionary(n => n.EventId);
        foreach (var notification in source.Notifications)
            if (!notificationsById.TryGetValue(notification.EventId, out var saved) || HashObjects([notification], mongo: true) != HashObjects([saved], mongo: true))
                throw Stop("Post-start notification content verification failed.");
        var output = new { verifiedAtUtc = DateTime.UtcNow, verified = true, sql = sqlReport,
            chat = new { conversations = source.Conversations.Count, messages = source.Replies.Count, verified = true },
            notifications = new { sourceCount = source.Notifications.Count, destinationCount = savedNotifications.Count, verified = true } };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(output, Json));
        Console.WriteLine(JsonSerializer.Serialize(output, Json));
    }
    public static async Task StorageAsync(string output)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(config["Migration:SqlServer"])
        { InitialCatalog = "master" }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_NAME(database_id), name, physical_name FROM sys.master_files WHERE DB_NAME(database_id) IN ('IdentityDB','CatalogDB','BookingDB','CustomerDB') ORDER BY database_id, file_id";
        var files = new List<object>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) files.Add(new { database = reader.GetString(0), logicalName = reader.GetString(1), containerPath = reader.GetString(2) });
        var mongo = new MongoClient(config["Migration:MongoDB"]);
        var collections = new Dictionary<string, Dictionary<string, long>>();
        foreach (var name in new[] { "ChatDB", "NotificationDB" })
        {
            var database = mongo.GetDatabase(name);
            var names = await (await database.ListCollectionNamesAsync()).ToListAsync();
            collections[name] = new();
            foreach (var collection in names) collections[name][collection] = await database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        }
        var json = JsonSerializer.Serialize(new { sqlFiles = files, mongoDataDirectory = "/data/db", collections }, Json);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, json);
        Console.WriteLine(json);
    }
    private static readonly (string Backend, string Path)[] Sources =
    [
        ("identity", "services/Identity/data/identity.db"), ("catalog", "services/Catalog/data/catalog.db"),
        ("booking", "services/booking/data/booking.db"), ("customer", "services/customer/data/customer.db"),
        ("notification", "services/Notification/data/notification.db")
    ];
    private static readonly Dictionary<string, string> Databases = new()
    { ["identity"] = "IdentityDB", ["catalog"] = "CatalogDB", ["booking"] = "BookingDB", ["customer"] = "CustomerDB" };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task SnapshotAsync(string root, string output)
    {
        root = Path.GetFullPath(root); output = Path.GetFullPath(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw Stop("Snapshot destination must be empty.");
        Directory.CreateDirectory(output);
        var manifest = new List<object>();
        foreach (var (backend, relativePath) in Sources)
        {
            var sourcePath = Path.Combine(root, relativePath);
            if (!File.Exists(sourcePath)) throw Stop($"Missing source database for {backend}.");
            await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await source.OpenAsync();
            await using var integrity = source.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            if ((string?)await integrity.ExecuteScalarAsync() != "ok") throw Stop($"SQLite integrity check failed for {backend}.");
            var destination = Path.Combine(output, backend + ".db");
            await using (var target = new SqliteConnection($"Data Source={destination};Pooling=False"))
            {
                await target.OpenAsync();
                source.BackupDatabase(target);
            }
            manifest.Add(new { backend, source = relativePath, sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination))) });
        }
        // Changes required by the new model apply only to disposable working copies.
        var working = Path.Combine(output, "working");
        Directory.CreateDirectory(working);
        foreach (var (backend, _) in Sources)
        {
            var path = Path.Combine(working, backend + ".db");
            File.Copy(Path.Combine(output, backend + ".db"), path);
            if (backend == "notification") continue;
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            var names = new HashSet<string>();
            await using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "PRAGMA table_info('Bookings')";
                await using var reader = await schema.ExecuteReaderAsync();
                while (await reader.ReadAsync()) names.Add(reader.GetString(1));
            }
            foreach (var name in new[] { "TourNameSnapshot", "TourLocationSnapshot", "DepartureTimeSnapshot", "ReturnTimeSnapshot" })
            {
                if (names.Contains(name)) continue;
                await using var command = connection.CreateCommand();
                command.CommandText = $"ALTER TABLE Bookings ADD COLUMN [{name}] TEXT NULL";
                await command.ExecuteNonQueryAsync();
            }
        }
        var snapshot = await LoadAsync(working);
        var counts = snapshot.Tables.ToDictionary(p => p.Key, p => p.Value.ToDictionary(t => t.Table, t => t.Rows.Count));
        var pending = snapshot.Outbox.ToDictionary(p => p.Key, p => p.Value.Count(e => e.PublishedAt == null));
        var info = new { completedAtUtc = DateTime.UtcNow, files = manifest, sql = counts,
            chat = new { conversations = snapshot.Conversations.Count, messages = snapshot.Replies.Count },
            notifications = snapshot.Notifications.Count, pendingOutbox = pending };
        await File.WriteAllTextAsync(Path.Combine(output, "snapshot-manifest.json"), JsonSerializer.Serialize(info, Json));
        Console.WriteLine(JsonSerializer.Serialize(new { sql = counts, chat = snapshot.Conversations.Count,
            messages = snapshot.Replies.Count, notifications = snapshot.Notifications.Count, pendingOutbox = pending }, Json));
    }

    public static async Task ApplyAsync(string sourceDirectory, string reportPath)
    {
        var source = await LoadAsync(Path.Combine(sourceDirectory, "working"));
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var sqlBase = config["Migration:SqlServer"] ?? throw Stop("Missing SQL migration credential.");
        var mongoConnection = config["Migration:MongoDB"] ?? throw Stop("Missing Mongo migration credential.");
        var report = new Dictionary<string, object>();
        // Only an empty isolated destination, or an already-identical completed phase, is accepted.
        foreach (var (backend, tables) in source.Tables)
        {
            Console.WriteLine($"Preparing {Databases[backend]}...");
            await using var db = SqlContext(backend, sqlBase);
            var originalTables = tables.Select(t => new TableSnapshot(t.Table, t.Rows.ToList())).ToList();
            var orphans = SeparateOrphans(tables, db.Model);
            if (orphans.Count > 0) Console.WriteLine($"{Databases[backend]}: preserving {orphans.Count} legacy orphan associations in LegacyOrphanRows.");
            await DatabaseInitializer.InitializeAsync(db);
            var entities = db.Model.GetEntityTypes().Where(e => tables.Any(t => t.Table == e.GetTableName())).ToList();
            var existing = await ExportTablesAsync(db, tables.Select(t => t.Table));
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (existing.Any(t => t.Rows.Count != 0))
            {
                foreach (var table in tables) RequireSame(table, existing.Single(t => t.Table == table.Table));
                Console.WriteLine($"{Databases[backend]} already matches this snapshot; no overwrite.");
            }
            else
            {
                foreach (var entity in Topological(entities))
                    await InsertTableAsync(db, entity, tables.Single(t => t.Table == entity.GetTableName()));
                var copied = await ExportTablesAsync(db, tables.Select(t => t.Table));
                foreach (var table in tables) RequireSame(table, copied.Single(t => t.Table == table.Table));
            }
            await SaveOrphansAsync(db, orphans);
            var active = await ExportTablesAsync(db, tables.Select(t => t.Table));
            foreach (var original in originalTables)
            {
                var combined = active.Single(t => t.Table == original.Table).Rows.ToList();
                combined.AddRange(await ReadOrphansAsync(db, original.Table));
                RequireSame(original, new(original.Table, combined));
            }
            await transaction.CommitAsync();
            report[Databases[backend]] = originalTables.ToDictionary(t => t.Table,
                t => new { sourceCount = t.Rows.Count, destinationCount = active.Single(a => a.Table == t.Table).Rows.Count,
                    archivedCount = orphans.Count(o => o.Table == t.Table), sha256 = Hash(t.Rows), verified = true });
        }

        var chat = new MongoChatStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Chat:ConnectionString"] = mongoConnection }).Build());
        await chat.InitializeAsync(default);
        var documents = source.Conversations.Select(inquiry => new ChatDocument
        {
            Id = inquiry.ContactInquiryId, Inquiry = inquiry,
            Replies = source.Replies.Where(r => r.ContactInquiryId == inquiry.ContactInquiryId).OrderBy(r => r.ContactInquiryReplyId).ToList(),
            PendingEvents = source.Outbox["customer"].Where(e => e.Source == "support" && e.PublishedAt == null)
                .Where(e => JsonSerializer.Deserialize<IntegrationEvent>(e.Payload)?.ResourceId == inquiry.ContactInquiryId.ToString()).Select(e => e.Payload).ToList()
        }).ToList();
        if (source.Replies.Any(r => !source.Conversations.Any(c => c.ContactInquiryId == r.ContactInquiryId)))
            throw Stop("Source contains orphan chat messages.");
        var pendingChat = source.Outbox["customer"].Where(e => e.Source == "support" && e.PublishedAt == null).ToList();
        if (documents.Sum(d => d.PendingEvents.Count) != pendingChat.Count) throw Stop("Pending chat events contain orphan references.");
        var existingChat = await chat.Conversations.Find(Builders<ChatDocument>.Filter.Empty).ToListAsync();
        if (existingChat.Count == 0 && documents.Count != 0) await chat.Conversations.InsertManyAsync(documents);
        existingChat = await chat.Conversations.Find(Builders<ChatDocument>.Filter.Empty).ToListAsync();
        if (HashObjects(documents, mongo: true) != HashObjects(existingChat, mongo: true)) throw Stop("Chat content verification failed; no cutover.");
        await SetCounterAsync(chat.Database, "conversation", Math.Max(source.Conversations.Select(c => c.ContactInquiryId).DefaultIfEmpty().Max(), await SequenceAsync(sourceDirectory, "ContactInquiries")));
        await SetCounterAsync(chat.Database, "message", Math.Max(source.Replies.Select(c => c.ContactInquiryReplyId).DefaultIfEmpty().Max(), await SequenceAsync(sourceDirectory, "ContactInquiryReplies")));
        await ArchiveAsync(chat.Database, "legacy_outbox", source.Outbox["customer"].Where(e => e.Source == "support").Select(e => (e.Id.ToString(), (object)e)));
        await ArchiveAsync(chat.Database, "legacy_inbox", source.Inbox["customer"].Select(e => (e.Id.ToString(), (object)e)));
        report["ChatDB"] = new { conversations = documents.Count, messages = source.Replies.Count, pendingEvents = pendingChat.Count,
            archivedOutbox = source.Outbox["customer"].Count(e => e.Source == "support"), archivedInbox = source.Inbox["customer"].Count, verified = true };

        var notificationConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["MongoDB:ConnectionString"] = mongoConnection }).Build();
        await new MongoNotificationStore(notificationConfig).InitializeAsync(); // Registers the application's BSON contract.
        var mongo = new MongoClient(mongoConnection);
        var notifications = mongo.GetDatabase("NotificationDB").GetCollection<NotificationRecord>("notifications");
        var savedNotifications = await notifications.Find(Builders<NotificationRecord>.Filter.Empty).ToListAsync();
        if (savedNotifications.Count == 0 && source.Notifications.Count != 0) await notifications.InsertManyAsync(source.Notifications);
        savedNotifications = await notifications.Find(Builders<NotificationRecord>.Filter.Empty).ToListAsync();
        if (HashObjects(source.Notifications, mongo: true) != HashObjects(savedNotifications, mongo: true))
            throw Stop("Notification content verification failed; no cutover.");
        report["NotificationDB"] = new { sourceCount = source.Notifications.Count, destinationCount = savedNotifications.Count,
            sha256 = HashObjects(source.Notifications, mongo: true), verified = true };

        // Populate each backend's local read models from the newly imported SQL owner data.
        foreach (var backend in Databases.Keys)
        {
            await using var destination = SqlContext(backend, sqlBase);
            foreach (var owner in ServiceData.Services.Where(owner => ServiceData.Needs(backend, owner)
                && !ServiceData.OwnedServices(backend).Contains(owner)))
            {
                var ownerBackend = ServiceBoundary.BackendFor(owner);
                if (!Databases.ContainsKey(ownerBackend)) continue;
                await using var ownerDb = SqlContext(ownerBackend, sqlBase);
                await ProjectionStore.ReplaceAsync(destination, await ProjectionStore.ExportAsync(ownerDb, owner));
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            completedAtUtc = DateTime.UtcNow, verified = true, databases = report,
            conversions = new[] { "SQLite scaled integers become SQL decimal money without changing their value.",
                "SQL Server generates new 8-byte rowversions; reload old forms.", "MongoDB timestamps use UTC millisecond precision.",
                "Pending dispatcher leases are released; event IDs and publication state are preserved.",
                "Only owner tables are imported; local read models are rebuilt from sanitized owner snapshots." }
        }, Json));
        Console.WriteLine("Migration verified. Original SQLite files and previous SQL/Mongo stack were not modified.");
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
    }

    private static async Task<Snapshot> LoadAsync(string directory)
    {
        var contexts = Databases.Keys.ToDictionary(k => k, k => SqliteContext(k, Path.Combine(directory, k + ".db")));
        try
        {
            var tables = new Dictionary<string, List<TableSnapshot>>();
            var outbox = new Dictionary<string, List<OutboxMessage>>();
            var inbox = new Dictionary<string, List<InboxMessage>>();
            foreach (var (backend, db) in contexts)
            {
                var owners = ServiceData.OwnedServices(backend).Where(o => o != "support");
                tables[backend] = [];
                foreach (var owner in owners) tables[backend].AddRange(await ProjectionStore.ExportAsync(db, owner, publicOnly: false));
                outbox[backend] = await db.OutboxMessages.AsNoTracking().ToListAsync();
                inbox[backend] = await db.InboxMessages.AsNoTracking().ToListAsync();
                var sqlOutbox = outbox[backend].Where(e => backend != "customer" || e.Source != "support").Select(e =>
                {
                    var copy = JsonSerializer.Deserialize<OutboxMessage>(JsonSerializer.Serialize(e))!;
                    if (copy.PublishedAt == null) { copy.LockId = null; copy.LockedUntil = null; }
                    return copy;
                }).ToList();
                tables[backend].Add(new("OutboxMessages", Rows(sqlOutbox)));
                tables[backend].Add(new("InboxMessages", Rows(inbox[backend])));
            }
            await using var notification = new NotificationDb(new DbContextOptionsBuilder<NotificationDb>()
                .UseSqlite(ReadOnly(Path.Combine(directory, "notification.db"))).Options);
            return new(tables, outbox, inbox, await contexts["customer"].ContactInquiries.AsNoTracking().ToListAsync(),
                await contexts["customer"].ContactInquiryReplies.AsNoTracking().ToListAsync(), await notification.Notifications.AsNoTracking().ToListAsync());
        }
        finally { foreach (var db in contexts.Values) await db.DisposeAsync(); }
    }

    private static string ReadOnly(string path) => new SqliteConnectionStringBuilder
    { DataSource = path, Mode = SqliteOpenMode.ReadOnly, ForeignKeys = false, Pooling = false }.ToString();
    private static IConfiguration OwnerConfig(string backend) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Service:Name"] = backend }).Build();
    private static ApplicationDbContext SqliteContext(string backend, string path) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(ReadOnly(path)).Options, OwnerConfig(backend));
    private static ApplicationDbContext SqlContext(string backend, string connection) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(
            new SqlConnectionStringBuilder(connection) { InitialCatalog = Databases[backend] }.ToString()).Options, OwnerConfig(backend));
    private static List<Dictionary<string, JsonElement>> Rows<T>(IEnumerable<T> values) => values.Select(v =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(v))!).ToList();
    private static async Task<List<TableSnapshot>> ExportTablesAsync(ApplicationDbContext db, IEnumerable<string> tableNames)
    {
        var result = new List<TableSnapshot>();
        foreach (var table in tableNames)
        {
            var entity = db.Model.GetEntityTypes().Single(e => e.GetTableName() == table);
            var method = typeof(ProjectionStore).GetMethod(nameof(ProjectionStore.ReadAsync))!.MakeGenericMethod(entity.ClrType);
            result.Add(new(table, await (Task<List<Dictionary<string, JsonElement>>>)method.Invoke(null, [db, false, CancellationToken.None])!));
        }
        return result;
    }
    private static IEnumerable<IEntityType> Topological(List<IEntityType> entities)
    {
        var remaining = entities.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(e => e.GetForeignKeys().All(f => f.PrincipalEntityType == e || !remaining.Contains(f.PrincipalEntityType)))
                ?? throw Stop("Owned SQL table dependencies contain a cycle.");
            remaining.Remove(next); yield return next;
        }
    }
    private sealed record Orphan(string Table, string Reason, Dictionary<string, JsonElement> Row);
    private static List<Orphan> SeparateOrphans(List<TableSnapshot> tables, IModel model)
    {
        var result = new List<Orphan>();
        bool changed;
        do
        {
            changed = false;
            foreach (var table in tables)
            foreach (var foreignKey in model.GetEntityTypes().Single(e => e.GetTableName() == table.Table).GetForeignKeys())
            {
                var parent = tables.FirstOrDefault(t => t.Table == foreignKey.PrincipalEntityType.GetTableName());
                if (parent == null) continue; // Cross-service logical references are not SQL foreign keys.
                static string Key(Dictionary<string, JsonElement> row, IEnumerable<IProperty> properties) =>
                    string.Join("|", properties.Select(p => Canonical(row[p.Name])));
                var keys = parent.Rows.Select(row => Key(row, foreignKey.PrincipalKey.Properties)).ToHashSet(StringComparer.Ordinal);
                foreach (var row in table.Rows.ToList())
                {
                    if (foreignKey.Properties.Any(p => row[p.Name].ValueKind == JsonValueKind.Null)) continue;
                    if (keys.Contains(Key(row, foreignKey.Properties))) continue;
                    result.Add(new(table.Table, foreignKey.GetConstraintName()!, row));
                    table.Rows.Remove(row); changed = true;
                }
            }
        } while (changed);
        return result;
    }
    private static async Task SaveOrphansAsync(ApplicationDbContext db, List<Orphan> rows)
    {
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'dbo.LegacyOrphanRows', N'U') IS NULL
            CREATE TABLE dbo.LegacyOrphanRows (
                RecordHash char(64) NOT NULL PRIMARY KEY,
                TableName nvarchar(128) NOT NULL,
                Reason nvarchar(200) NOT NULL,
                RowJson nvarchar(max) NOT NULL,
                MigratedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            """);
        foreach (var row in rows)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Table + "\n" + Hash([row.Row]))));
            var json = JsonSerializer.Serialize(row.Row);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "SELECT RowJson FROM dbo.LegacyOrphanRows WHERE RecordHash = @hash";
            command.Parameters.Add(new SqlParameter("hash", System.Data.SqlDbType.Char, 64) { Value = hash });
            var existing = await command.ExecuteScalarAsync();
            if (existing != null && (string)existing != json) throw Stop("Conflicting legacy orphan archive; no cutover.");
            if (existing != null) continue;
            command.CommandText = "INSERT INTO dbo.LegacyOrphanRows(RecordHash, TableName, Reason, RowJson) VALUES (@hash, @table, @reason, @json)";
            command.Parameters.Add(new SqlParameter("table", System.Data.SqlDbType.NVarChar, 128) { Value = row.Table });
            command.Parameters.Add(new SqlParameter("reason", System.Data.SqlDbType.NVarChar, 200) { Value = row.Reason });
            command.Parameters.Add(new SqlParameter("json", System.Data.SqlDbType.NVarChar, -1) { Value = json });
            await command.ExecuteNonQueryAsync();
        }
    }
    private static async Task<List<Dictionary<string, JsonElement>>> ReadOrphansAsync(ApplicationDbContext db, string table)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT RowJson FROM dbo.LegacyOrphanRows WHERE TableName = @table";
        command.Parameters.Add(new SqlParameter("table", System.Data.SqlDbType.NVarChar, 128) { Value = table });
        var rows = new List<Dictionary<string, JsonElement>>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(0))!);
        return rows;
    }
    private static async Task InsertTableAsync(ApplicationDbContext db, IEntityType entity, TableSnapshot snapshot)
    {
        static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
        var tableName = (entity.GetSchema() is { } schema ? Quote(schema) + "." : "") + Quote(snapshot.Table);
        var properties = entity.GetProperties().Where(p => p.GetColumnType() != "rowversion").ToArray();
        var identity = properties.Any(p => SqlServerPropertyExtensions.GetValueGenerationStrategy(p) == SqlServerValueGenerationStrategy.IdentityColumn);
        var identityOn = "SET IDENTITY_INSERT " + tableName + " ON";
        var identityOff = "SET IDENTITY_INSERT " + tableName + " OFF";
        if (identity) await db.Database.ExecuteSqlRawAsync(identityOn);
        try
        {
            foreach (var row in snapshot.Rows)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                command.CommandText = $"INSERT INTO {tableName} ({string.Join(",", properties.Select(p => Quote(p.GetColumnName())))}) VALUES ({string.Join(",", properties.Select((_, i) => "@p" + i))})";
                foreach (var (property, index) in properties.Select((p, i) => (p, i)))
                {
                    var value = row[property.Name].Deserialize(property.ClrType);
                    // EF's SQL mapping preserves datetime2 precision, money scale and TimeOnly.
                    command.Parameters.Add(property.GetRelationalTypeMapping().CreateParameter(command, "p" + index, value, property.IsNullable));
                }
                await command.ExecuteNonQueryAsync();
            }
        }
        finally { if (identity) await db.Database.ExecuteSqlRawAsync(identityOff); }
    }
    private static void RequireSame(TableSnapshot source, TableSnapshot destination)
    {
        if (Hash(source.Rows) != Hash(destination.Rows)) throw Stop($"Content verification failed for {source.Table}; previous stack is untouched.");
    }
    private static string Hash(List<Dictionary<string, JsonElement>> rows) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", rows.Select(row => string.Join("|", row.Where(p => p.Key != "Version").OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value)))).Order(StringComparer.Ordinal)))));
    private static string Canonical(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number.ToString("G29", CultureInfo.InvariantCulture);
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length > 10 && text[10] == 'T'
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return JsonSerializer.Serialize(date.UtcDateTime.ToString("O"));
        return value.GetRawText();
    }
    private static string HashObjects<T>(IEnumerable<T> values, bool mongo) => Hash(values.Select(v =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(mongo ? BsonSerializer.Deserialize<T>(v.ToBson()) : v))!).ToList());
    private static async Task ArchiveAsync(IMongoDatabase database, string collectionName, IEnumerable<(string Id, object Value)> entries)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);
        foreach (var (id, value) in entries)
        {
            var document = new BsonDocument { ["_id"] = id, ["json"] = JsonSerializer.Serialize(value, value.GetType()) };
            var previous = await collection.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync();
            if (previous != null && previous["json"] != document["json"]) throw Stop("Conflicting archived chat event.");
            if (previous == null) await collection.InsertOneAsync(document);
        }
    }
    private static async Task SetCounterAsync(IMongoDatabase database, string name, int value) =>
        await database.GetCollection<BsonDocument>("counters").UpdateOneAsync(new BsonDocument("_id", name),
            new BsonDocument("$max", new BsonDocument("value", value)), new UpdateOptions { IsUpsert = true });
    private static async Task<int> SequenceAsync(string directory, string table)
    {
        await using var connection = new SqliteConnection(ReadOnly(Path.Combine(directory, "working", "customer.db")));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq FROM sqlite_sequence WHERE name = $name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
    }
    private static InvalidOperationException Stop(string safeMessage)
    {
        var error = new InvalidOperationException(safeMessage); error.Data["SafeMessage"] = safeMessage; return error;
    }
    private sealed record Snapshot(Dictionary<string, List<TableSnapshot>> Tables, Dictionary<string, List<OutboxMessage>> Outbox,
        Dictionary<string, List<InboxMessage>> Inbox, List<ContactInquiry> Conversations, List<ContactInquiryReply> Replies, List<NotificationRecord> Notifications);
}
