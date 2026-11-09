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

    public MongoNotificationStore(IConfiguration config)
    {
        if (!BsonClassMap.IsClassMapRegistered(typeof(NotificationRecord)))
            BsonClassMap.RegisterClassMap<NotificationRecord>(map =>
            {
                map.AutoMap();
                map.MapIdMember(n => n.EventId).SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
            });
        var settings = MongoClientSettings.FromConnectionString(config["MongoDB:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDB connection is required."));
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(3);
        database = new MongoClient(settings)
            .GetDatabase(config["MongoDB:Database"] ?? "NotificationDB");
        notifications = database.GetCollection<NotificationRecord>(config["MongoDB:Collection"] ?? "notifications");
    }

    public Task InitializeAsync(CancellationToken ct = default) => notifications.Indexes.CreateOneAsync(
        new CreateIndexModel<NotificationRecord>(Builders<NotificationRecord>.IndexKeys
            .Ascending(n => n.UserId).Descending(n => n.CreatedAt)), cancellationToken: ct);

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
                Message = NotificationDb.FormatMessage(evt), CreatedAt = evt.OccurredAt
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
}
