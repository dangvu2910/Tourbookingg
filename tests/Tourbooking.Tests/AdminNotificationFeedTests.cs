using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tourbooking.Distributed;
using Tourbooking.Notifications;

namespace Tourbooking.Tests;

public class AdminNotificationFeedTests
{
    private static IntegrationEvent Event(string type, string status, DateTime at, decimal? price = null) => new(
        Guid.NewGuid(), type, "booking", "42", "user-1", "khach@example.test", status, at, Guid.NewGuid().ToString("N"),
        price is null ? null : JsonSerializer.Serialize(new { BookingId = 42, UserId = "user-1", TotalPrice = price, Status = status }));

    private static async Task<(SqliteConnection, NotificationDb)> Store()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new NotificationDb(new DbContextOptionsBuilder<NotificationDb>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    [Fact]
    public void NewBookingMessageNamesCustomerPriceAndLinksToPendingBookings()
    {
        var evt = Event("booking.created", "Pending", DateTime.UtcNow, 12500000m);
        var record = new NotificationRecord { Type = evt.Type, ResourceId = evt.ResourceId, Email = evt.Email,
            Status = evt.Status, TotalPrice = AdminFeed.ReadTotalPrice(evt) };

        Assert.Equal(12500000m, record.TotalPrice);
        Assert.True(AdminFeed.IsRelevant(record));
        var message = AdminFeed.Message(record);
        Assert.Contains("#42", message);
        Assert.Contains("khach@example.test", message);
        Assert.Contains("12.500.000", message);
        Assert.Equal("/Admin/Bookings?status=Pending", AdminFeed.Link(record));
    }

    [Fact]
    public void OnlyActionableEventsReachTheAdminBell()
    {
        Assert.True(AdminFeed.IsRelevant(new NotificationRecord { Type = "booking.updated", Status = "Processing" }));
        Assert.True(AdminFeed.IsRelevant(new NotificationRecord { Type = "contact.created" }));
        Assert.False(AdminFeed.IsRelevant(new NotificationRecord { Type = "booking.updated", Status = "Confirmed" }));
        Assert.False(AdminFeed.IsRelevant(new NotificationRecord { Type = "contact.reply.created" }));
        Assert.Null(AdminFeed.ReadTotalPrice(Event("booking.created", "Pending", DateTime.UtcNow) with { Data = "{not json" }));
    }

    [Fact]
    public async Task UnreadCountStartsAtFirstVisitGrowsWithNewBookingsAndResetsWhenSeen()
    {
        var (connection, db) = await Store();
        await using var _ = connection;
        await using var __ = db;
        var start = DateTime.UtcNow;
        await db.StoreAsync(Event("booking.created", "Pending", start.AddMinutes(-5), 1000m), CancellationToken.None);

        // First visit: older history is not "new" for this admin.
        var seen = await db.GetOrStartAdminSeenAsync("admin-1", start, CancellationToken.None);
        Assert.Equal(0, await db.CountAdminFeedSinceAsync(seen, CancellationToken.None));

        await db.StoreAsync(Event("booking.created", "Pending", start.AddSeconds(1), 2000m), CancellationToken.None);
        await db.StoreAsync(Event("booking.updated", "Processing", start.AddSeconds(2)), CancellationToken.None);
        await db.StoreAsync(Event("booking.updated", "Confirmed", start.AddSeconds(3)), CancellationToken.None);
        Assert.Equal(2, await db.CountAdminFeedSinceAsync(seen, CancellationToken.None));

        var feed = await db.ListAdminFeedAsync(10, CancellationToken.None);
        Assert.Equal(3, feed.Count);
        Assert.Equal(["booking.updated", "booking.created", "booking.created"], feed.Select(n => n.Type));
        Assert.Equal([true, true, false], feed.Select(n => AdminFeed.ToItem(n, seen).Unread));

        // Another admin keeps an independent state.
        var other = await db.GetOrStartAdminSeenAsync("admin-2", start.AddSeconds(10), CancellationToken.None);
        Assert.Equal(0, await db.CountAdminFeedSinceAsync(other, CancellationToken.None));

        await db.MarkAdminSeenAsync("admin-1", start.AddSeconds(5), CancellationToken.None);
        seen = await db.GetOrStartAdminSeenAsync("admin-1", DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, await db.CountAdminFeedSinceAsync(seen, CancellationToken.None));
        // Marking an older time never moves the read position backwards.
        await db.MarkAdminSeenAsync("admin-1", start, CancellationToken.None);
        Assert.Equal(seen, await db.GetOrStartAdminSeenAsync("admin-1", DateTime.UtcNow, CancellationToken.None));
    }
}
