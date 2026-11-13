using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using Tourbooking.Chat;
using Tourbooking.Distributed;
using Tourbooking.Models;
using Tourbooking.Notifications;

namespace Tourbooking.Tests;

public sealed class MongoFactAttribute : FactAttribute
{
    public MongoFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TEST_CHAT_MONGO") == null ||
            Environment.GetEnvironmentVariable("TEST_NOTIFICATION_MONGO") == null)
            Skip = "Requires isolated MongoDB credentials from the hybrid Compose test profile.";
    }
}

public class MongoPersistenceTests
{
    [MongoFact]
    public async Task ConcurrentEventsAreStoredExactlyOnceInMongo()
    {
        var collection = "test_notifications_" + Guid.NewGuid().ToString("N");
        var connection = Environment.GetEnvironmentVariable("TEST_NOTIFICATION_MONGO")!;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["MongoDB:ConnectionString"] = connection, ["MongoDB:Collection"] = collection }).Build();
        var store = new MongoNotificationStore(config);
        var evt = new IntegrationEvent(Guid.NewGuid(), "booking.created", "booking", "test", "test", null, "Pending", DateTime.UtcNow, "test");
        try
        {
            await store.InitializeAsync();
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.StoreAsync(evt, CancellationToken.None)));
            Assert.Single(results, value => value);
            var records = await store.ListAsync("test", false, CancellationToken.None);
            Assert.Single(records);
            Assert.Empty(await store.ListAsync("another-user", false, CancellationToken.None));
        }
        finally { await new MongoClient(connection).GetDatabase("NotificationDB").DropCollectionAsync(collection); }
    }

    [MongoFact]
    public async Task ConcurrentChatMessagesKeepBothMessagesAndAtomicOutbox()
    {
        var collection = "test_conversations_" + Guid.NewGuid().ToString("N");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Chat:ConnectionString"] = Environment.GetEnvironmentVariable("TEST_CHAT_MONGO"), ["Chat:Collection"] = collection }).Build();
        var store = new MongoChatStore(config);
        try
        {
            await store.InitializeAsync(CancellationToken.None);
            var ticket = new ContactInquiry { FullName = "Test", Email = "test@example.test", Subject = "Concurrency", Message = "Help", UserId = "owner" };
            await store.CreateAsync(ticket, CancellationToken.None);
            await Task.WhenAll(Enumerable.Range(0, 2).Select(i => store.ReplyAsync(ticket, new ContactInquiryReply
            { ContactInquiryId = ticket.ContactInquiryId, Message = "reply " + i, UserId = "owner" }, CancellationToken.None)));
            var replies = await store.RepliesAsync(ticket.ContactInquiryId, 0, CancellationToken.None);
            Assert.Equal(2, replies.Count);
            Assert.Equal(2, replies.Select(r => r.ContactInquiryReplyId).Distinct().Count());
            var document = await store.Conversations.Find(c => c.Id == ticket.ContactInquiryId).SingleAsync();
            Assert.Equal(3, document.PendingEvents.Count);
            Assert.Empty(await store.ListAsync("another-user", false, null, CancellationToken.None));
        }
        finally { await store.Database.DropCollectionAsync(collection); }
    }
}
