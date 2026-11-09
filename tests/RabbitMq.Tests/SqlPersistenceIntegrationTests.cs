using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tourbooking.Models;

namespace RabbitMq.Tests;

public class SqlPersistenceIntegrationTests
{
    private static ApplicationDbContext Context() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(Environment.GetEnvironmentVariable("TEST_SQL_BOOKING")!).Options,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Service:Name"] = "booking", ["RabbitMQ:Enabled"] = "true" }).Build());
    private static Booking NewBooking() => new()
    {
        RequestId = Guid.NewGuid(), TourId = 1, UserId = "sql-integration-user", FullName = "SQL integration",
        Email = "sql@example.test", PhoneNumber = "0901234567", GuestCount = 1,
        TravelDate = DateTime.UtcNow.AddDays(30).Date, TotalPrice = 100000, TourNameSnapshot = "Historical name"
    };

    [HybridFact]
    public async Task SqlBookingAndOutboxRollbackTogether()
    {
        await using var db = Context();
        var booking = NewBooking();
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var marker = $"\"ResourceId\":\"{booking.BookingId}\"";
        Assert.True(await db.OutboxMessages.AnyAsync(e => e.Type == "booking.created" && e.Payload.Contains(marker)));
        await tx.RollbackAsync();
        await using var check = Context();
        Assert.False(await check.Bookings.AnyAsync(b => b.RequestId == booking.RequestId));
        Assert.False(await check.OutboxMessages.AnyAsync(e => e.Type == "booking.created" && e.Payload.Contains(marker)));
    }

    [HybridFact]
    public async Task SqlRowVersionRejectsConcurrentWriteAndDuplicateRequestId()
    {
        await using var seed = Context();
        var booking = NewBooking();
        seed.Bookings.Add(booking);
        await seed.SaveChangesAsync();
        var marker = $"\"ResourceId\":\"{booking.BookingId}\"";
        try
        {
            await using var first = Context();
            await using var second = Context();
            var a = await first.Bookings.SingleAsync(b => b.BookingId == booking.BookingId);
            var b = await second.Bookings.SingleAsync(b => b.BookingId == booking.BookingId);
            Assert.Equal(8, a.Version.Length);
            a.Status = "Confirmed";
            await first.SaveChangesAsync();
            b.Status = "Cancelled";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
            await using var duplicate = Context();
            var another = NewBooking();
            another.RequestId = booking.RequestId;
            duplicate.Bookings.Add(another);
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            await using var check = Context();
            var persisted = await check.Bookings.AsNoTracking().SingleAsync(x => x.BookingId == booking.BookingId);
            Assert.Equal("Confirmed", persisted.Status);
            Assert.Equal("Historical name", persisted.TourNameSnapshot);
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.OutboxMessages.Where(e => e.Payload.Contains(marker)).ExecuteDeleteAsync();
            await cleanup.Bookings.Where(b => b.BookingId == booking.BookingId).ExecuteDeleteAsync();
        }
    }
}
