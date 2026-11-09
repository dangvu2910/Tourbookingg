using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Tourbooking.Distributed;
using Tourbooking.Models;

namespace Tourbooking.Chat;

public sealed class ChatDocument
{
    public int Id { get; set; }
    public ContactInquiry Inquiry { get; set; } = new();
    public List<ContactInquiryReply> Replies { get; set; } = [];
    public List<string> PendingEvents { get; set; } = [];
}

public sealed class MongoChatStore : IChatStore
{
    public IMongoDatabase Database { get; }
    public IMongoCollection<ChatDocument> Conversations { get; }
    public MongoChatStore(IConfiguration config)
    {
        if (!BsonClassMap.IsClassMapRegistered(typeof(ContactInquiry)))
            BsonClassMap.RegisterClassMap<ContactInquiry>(map => { map.AutoMap(); map.UnmapMember(c => c.User); });
        if (!BsonClassMap.IsClassMapRegistered(typeof(ContactInquiryReply)))
            BsonClassMap.RegisterClassMap<ContactInquiryReply>(map =>
            {
                map.AutoMap(); map.UnmapMember(r => r.User); map.UnmapMember(r => r.ContactInquiry);
            });
        var settings = MongoClientSettings.FromConnectionString(config["Chat:ConnectionString"]
            ?? throw new InvalidOperationException("Chat MongoDB connection is required."));
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(3);
        Database = new MongoClient(settings)
            .GetDatabase(config["Chat:Database"] ?? "ChatDB");
        Conversations = Database.GetCollection<ChatDocument>(config["Chat:Collection"] ?? "conversations");
    }

    public Task InitializeAsync(CancellationToken ct) => Conversations.Indexes.CreateOneAsync(
        new CreateIndexModel<ChatDocument>(Builders<ChatDocument>.IndexKeys
            .Ascending(c => c.Inquiry.UserId).Descending(c => c.Inquiry.UpdatedAt)), cancellationToken: ct);

    private async Task<int> NextIdAsync(string name, CancellationToken ct)
    {
        var counter = await Database.GetCollection<BsonDocument>("counters").FindOneAndUpdateAsync(
            new BsonDocument("_id", name), new BsonDocument("$inc", new BsonDocument("value", 1)),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
        return counter["value"].AsInt32;
    }

    public async Task<List<ContactInquiry>> ListAsync(string? userId, bool admin, string? status, CancellationToken ct)
    {
        var f = Builders<ChatDocument>.Filter;
        var filter = admin ? f.Empty : f.Eq(c => c.Inquiry.UserId, userId);
        if (status != null) filter &= f.Eq(c => c.Inquiry.Status, status);
        return await Conversations.Find(filter).SortByDescending(c => c.Inquiry.UpdatedAt).ThenByDescending(c => c.Id)
            .Limit(200).Project(c => c.Inquiry).ToListAsync(ct);
    }
    public async Task<ContactInquiry?> GetAsync(int id, CancellationToken ct) =>
        await Conversations.Find(c => c.Id == id).Project(c => c.Inquiry).FirstOrDefaultAsync(ct);
    public async Task<List<ContactInquiryReply>> RepliesAsync(int id, int after, CancellationToken ct)
    {
        var replies = await Conversations.Find(c => c.Id == id).Project(c => c.Replies).FirstOrDefaultAsync(ct);
        return replies?.Where(r => r.ContactInquiryReplyId > after).OrderBy(r => r.ContactInquiryReplyId).ToList() ?? [];
    }
    public async Task CreateAsync(ContactInquiry inquiry, CancellationToken ct)
    {
        inquiry.ContactInquiryId = await NextIdAsync("conversation", ct);
        await Conversations.InsertOneAsync(new ChatDocument
        {
            Id = inquiry.ContactInquiryId, Inquiry = inquiry,
            PendingEvents = [Event("contact.created", inquiry, null)]
        }, cancellationToken: ct);
    }
    public async Task ReplyAsync(ContactInquiry inquiry, ContactInquiryReply reply, CancellationToken ct)
    {
        reply.ContactInquiryReplyId = await NextIdAsync("message", ct);
        // A single MongoDB document atomically stores the message, status and outbox.
        // Bound embedded history to keep well below MongoDB's document size limit.
        var update = Builders<ChatDocument>.Update
            .Set(c => c.Inquiry.Status, inquiry.Status).Set(c => c.Inquiry.UpdatedAt, inquiry.UpdatedAt)
            .Push(c => c.Replies, reply).Push(c => c.PendingEvents, Event("contact.reply.created", inquiry, reply));
        if (reply.IsFromAdmin)
            update = update.Set(c => c.Inquiry.AdminReply, reply.Message).Set(c => c.Inquiry.RepliedAt, reply.CreatedAt)
                .Set(c => c.Inquiry.RepliedByAdminId, reply.UserId);
        var result = await Conversations.UpdateOneAsync(
            Builders<ChatDocument>.Filter.Eq(c => c.Id, inquiry.ContactInquiryId) &
            Builders<ChatDocument>.Filter.Not(Builders<ChatDocument>.Filter.SizeGte(c => c.Replies, 1000)), update,
            cancellationToken: ct);
        if (result.MatchedCount != 1) throw new InvalidOperationException("Conversation missing or message limit reached.");
    }
    private static string Event(string type, ContactInquiry inquiry, ContactInquiryReply? reply) =>
        JsonSerializer.Serialize(new IntegrationEvent(Guid.NewGuid(), type, "support", inquiry.ContactInquiryId.ToString(),
            inquiry.UserId, inquiry.Email, reply == null ? inquiry.Status : reply.IsFromAdmin ? "AdminReplied" : "CustomerReplied",
            DateTime.UtcNow, Activity.Current?.GetTagItem("correlation.id")?.ToString() ?? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N")));
}
