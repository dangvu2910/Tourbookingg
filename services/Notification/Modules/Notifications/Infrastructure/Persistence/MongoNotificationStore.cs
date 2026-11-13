using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using Tourbooking.Distributed;

namespace Tourbooking.Notifications;

public sealed class MongoNotificationStore : INotificationStore
{
    private readonly IMongoDatabase database;
    private readonly IMongoCollection<NotificationRecord> notifications;
    private readonly IMongoCollection<AdminNotificationSeen> adminSeen;

    private static readonly FilterDefinition<NotificationRecord> AdminFeedFilter = Builders<NotificationRecord>.Filter.Or(
        Builders<NotificationRecord>.Filter.In(n => n.Type, [AdminFeed.BookingCreated, AdminFeed.ContactCreated]),
        Builders<NotificationRecord>.Filter.And(
            Builders<NotificationRecord>.Filter.Eq(n => n.Type, AdminFeed.BookingUpdated),
            Builders<NotificationRecord>.Filter.Eq(n => n.Status, AdminFeed.PaymentSubmittedStatus)));

    public MongoNotificationStore(IConfiguration config)
    {
        if (!BsonClassMap.IsClassMapRegistered(typeof(NotificationRecord)))
            BsonClassMap.RegisterClassMap<NotificationRecord>(map =>
            {
                map.AutoMap();
                map.SetIgnoreExtraElements(true);
                map.MapIdMember(n => n.EventId).SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
                map.MapMember(n => n.TotalPrice).SetSerializer(new NullableSerializer<decimal>(new DecimalSerializer(BsonType.Decimal128)));
            });
        if (!BsonClassMap.IsClassMapRegistered(typeof(AdminNotificationSeen)))
            BsonClassMap.RegisterClassMap<AdminNotificationSeen>(map =>
            {
                map.AutoMap();
                map.MapIdMember(s => s.AdminId);
            });
        var settings = MongoClientSettings.FromConnectionString(config["MongoDB:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDB connection is required."));
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(3);
        database = new MongoClient(settings)
            .GetDatabase(config["MongoDB:Database"] ?? "NotificationDB");
        notifications = database.GetCollection<NotificationRecord>(config["MongoDB:Collection"] ?? "notifications");
        adminSeen = database.GetCollection<AdminNotificationSeen>("admin_seen");
    }

    public Task InitializeAsync(CancellationToken ct = default) => notifications.Indexes.CreateManyAsync(
    [
        new CreateIndexModel<NotificationRecord>(Builders<NotificationRecord>.IndexKeys
            .Ascending(n => n.UserId).Descending(n => n.CreatedAt)),
        new CreateIndexModel<NotificationRecord>(Builders<NotificationRecord>.IndexKeys
            .Ascending(n => n.Type).Descending(n => n.CreatedAt))
    ], ct);

    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
        return true;
    }

    public async Task<bool> StoreAsync(IntegrationEvent evt, CancellationToken ct)
    {
        try
        {
            await notifications.InsertOneAsync(new NotificationRecord
            {
                EventId = evt.EventId, Type = evt.Type, ResourceId = evt.ResourceId,
                UserId = evt.UserId, Email = evt.Email, CorrelationId = evt.CorrelationId,
                Message = NotificationDb.FormatMessage(evt), CreatedAt = evt.OccurredAt,
                Status = evt.Status, TotalPrice = AdminFeed.ReadTotalPrice(evt)
            }, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false; // _id is EventId: concurrent delivery is deduplicated by MongoDB.
        }
    }

    public Task<List<NotificationRecord>> ListAsync(string? userId, bool admin, CancellationToken ct) =>
        notifications.Find(admin ? Builders<NotificationRecord>.Filter.Empty :
                Builders<NotificationRecord>.Filter.Eq(n => n.UserId, userId))
            .SortByDescending(n => n.CreatedAt).ThenByDescending(n => n.EventId)
            .Limit(admin ? 200 : 100).ToListAsync(ct);

    public Task<List<NotificationRecord>> ListAdminFeedAsync(int limit, CancellationToken ct) =>
        notifications.Find(AdminFeedFilter).SortByDescending(n => n.CreatedAt).ThenByDescending(n => n.EventId)
            .Limit(Math.Clamp(limit, 1, 50)).ToListAsync(ct);

    public Task<long> CountAdminFeedSinceAsync(DateTime since, CancellationToken ct) =>
        notifications.CountDocumentsAsync(Builders<NotificationRecord>.Filter.And(AdminFeedFilter,
            Builders<NotificationRecord>.Filter.Gt(n => n.CreatedAt, since)), cancellationToken: ct);

    public async Task<DateTime> GetOrStartAdminSeenAsync(string adminId, DateTime now, CancellationToken ct)
    {
        var filter = Builders<AdminNotificationSeen>.Filter.Eq(s => s.AdminId, adminId);
        try
        {
            var seen = await adminSeen.FindOneAndUpdateAsync(filter,
                Builders<AdminNotificationSeen>.Update.SetOnInsert(s => s.SeenAt, now),
                new FindOneAndUpdateOptions<AdminNotificationSeen> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
                ct);
            return seen.SeenAt;
        }
        catch (MongoCommandException ex) when (ex.Code == 11000)
        {
            // Two first requests raced on the upsert; the other one created the document.
            return (await adminSeen.Find(filter).FirstAsync(ct)).SeenAt;
        }
    }

    public Task MarkAdminSeenAsync(string adminId, DateTime seenAt, CancellationToken ct) =>
        adminSeen.UpdateOneAsync(Builders<AdminNotificationSeen>.Filter.Eq(s => s.AdminId, adminId),
            Builders<AdminNotificationSeen>.Update.Max(s => s.SeenAt, seenAt),
            new UpdateOptions { IsUpsert = true }, ct);
}
