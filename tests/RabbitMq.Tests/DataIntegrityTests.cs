using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tourbooking.Distributed;
using Tourbooking.Models;
using Tourbooking.Notifications;

namespace RabbitMq.Tests;

public class DataIntegrityTests
{
    private static ApplicationDbContext Context() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer(Environment.GetEnvironmentVariable("TEST_DB") ?? throw new InvalidOperationException("Run integration tests using the Compose tests profile.")).Options,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:Enabled"] = "true", ["Service:Name"] = "booking"
        }).Build());

    private static async Task<Booking> NewBooking(ApplicationDbContext db)
    {
        var user = await db.Users.FirstAsync();
        var tour = await db.Tours.FirstAsync();
        return new Booking
        {
            RequestId = Guid.NewGuid(), TourId = tour.TourId, UserId = user.Id,
            Email = user.Email!, FullName = "Data integrity test", PhoneNumber = "0901234567",
            GuestCount = 1, TotalPrice = tour.Price, TravelDate = DateTime.UtcNow.AddDays(60), Status = "PendingConfirmation"
        };
    }

    [Fact]
    public async Task BookingAndOutboxRollbackTogether()
    {
        await using var db = Context();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var booking = await NewBooking(db);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        Assert.True(await db.OutboxMessages.AnyAsync(m => m.Type == "booking.created" && m.Payload.Contains($"\"ResourceId\":\"{booking.BookingId}\"")));
        await transaction.RollbackAsync();
        await using var check = Context();
        Assert.False(await check.Bookings.AnyAsync(b => b.RequestId == booking.RequestId));
        Assert.False(await check.OutboxMessages.AnyAsync(m => m.Payload.Contains($"\"ResourceId\":\"{booking.BookingId}\"") && m.Type == "booking.created"));
    }

    [Fact]
    public async Task ConcurrentUpdatesRejectStaleVersion()
    {
        await using var seed = Context();
        var booking = await NewBooking(seed);
        seed.Bookings.Add(booking);
        await seed.SaveChangesAsync();
        await using var first = Context();
        await using var second = Context();
        var a = await first.Bookings.SingleAsync(b => b.BookingId == booking.BookingId);
        var b = await second.Bookings.SingleAsync(b => b.BookingId == booking.BookingId);
        a.Status = "Confirmed";
        await first.SaveChangesAsync();
        b.Status = "Cancelled";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var check = Context();
        Assert.Equal("Confirmed", (await check.Bookings.SingleAsync(x => x.BookingId == booking.BookingId)).Status);
    }

    [Fact]
    public async Task DuplicateEventsProduceOneNotification()
    {
        var options = new DbContextOptionsBuilder<NotificationDb>()
            .UseSqlServer(Environment.GetEnvironmentVariable("TEST_NOTIFICATION_DB") ?? throw new InvalidOperationException("Run using Compose tests profile.")).Options;
        var evt = new IntegrationEvent(Guid.NewGuid(), "booking.created", "test", Guid.NewGuid().ToString(),
            "dedup-test", "test@example.test", "Pending", DateTime.UtcNow, Guid.NewGuid().ToString("N"));
        await using var first = new NotificationDb(options);
        await using var second = new NotificationDb(options);
        var results = await Task.WhenAll(first.StoreAsync(evt, CancellationToken.None), second.StoreAsync(evt, CancellationToken.None));
        Assert.Single(results, saved => saved);
        await using var check = new NotificationDb(options);
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.EventId == evt.EventId));
    }
}
