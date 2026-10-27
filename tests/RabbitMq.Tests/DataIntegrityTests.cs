using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tourbooking.Distributed;
using Tourbooking.Models;
using Tourbooking.Notifications;

namespace RabbitMq.Tests;

public class DataIntegrityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tourbooking-tests", Guid.NewGuid().ToString("N"));
    private ApplicationDbContext Context()
    {
        Directory.CreateDirectory(_folder);
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_folder, "booking.db")};Foreign Keys=False;Default Timeout=30").Options,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["RabbitMQ:Enabled"] = "true", ["Service:Name"] = "booking" }).Build());
        db.Database.EnsureCreated();
        if (!db.Users.Any())
        {
            db.ApplyingProjection = true;
            db.Users.Add(new ApplicationUser { Id = "test-user", UserName = "user@example.test", Email = "user@example.test" });
            db.Tours.Add(new Tour { Name = "Test", Location = "Test", Price = 1234567.89m });
            db.SaveChangesAsync().GetAwaiter().GetResult();
            db.ApplyingProjection = false;
        }
        return db;
    }

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
        Directory.CreateDirectory(_folder);
        var options = new DbContextOptionsBuilder<NotificationDb>()
            .UseSqlite($"Data Source={Path.Combine(_folder, "notification.db")};Default Timeout=30").Options;
        var evt = new IntegrationEvent(Guid.NewGuid(), "booking.created", "test", Guid.NewGuid().ToString(),
            "dedup-test", "test@example.test", "Pending", DateTime.UtcNow, Guid.NewGuid().ToString("N"));
        await using var first = new NotificationDb(options);
        await first.Database.EnsureCreatedAsync();
        await using var second = new NotificationDb(options);
        var results = await Task.WhenAll(first.StoreAsync(evt, CancellationToken.None), second.StoreAsync(evt, CancellationToken.None));
        Assert.Single(results, saved => saved);
        await using var check = new NotificationDb(options);
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.EventId == evt.EventId));
    }
    [Fact]
    public async Task ForeignTablesCannotBeWritten()
    {
        Directory.CreateDirectory(_folder);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["Service:Name"] = "customer", ["RabbitMQ:Enabled"] = "true" }).Build();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_folder, "customer.db")};Foreign Keys=False").Options, config);
        await db.Database.EnsureCreatedAsync();
        db.Payments.Add(new Payment { BookingId = 1, UserId = "test-user", Amount = 123.45m });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SupportRepliesCreateDurableEvents()
    {
        Directory.CreateDirectory(_folder);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["Service:Name"] = "support", ["RabbitMQ:Enabled"] = "true" }).Build();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_folder, "support.db")};Foreign Keys=False").Options, config);
        await db.Database.EnsureCreatedAsync();
        var ticket = new ContactInquiry { UserId = "customer", Email = "user@example.test", FullName = "Test", Subject = "Help", Message = "Hello" };
        db.ContactInquiries.Add(ticket);
        await db.SaveChangesAsync();
        db.ContactInquiryReplies.Add(new ContactInquiryReply { ContactInquiryId = ticket.ContactInquiryId, IsFromAdmin = true, Message = "Reply" });
        await db.SaveChangesAsync();
        var evt = await db.OutboxMessages.SingleAsync(x => x.Type == "contact.reply.created");
        Assert.Contains("customer", evt.Payload);
    }

    [Fact]
    public async Task BookingAndPaymentCanBeCreatedInOneTransactionWithoutPaymentEvents()
    {
        await using var db = Context();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var booking = await NewBooking(db);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        db.Payments.Add(new Payment
        {
            BookingId = booking.BookingId, UserId = booking.UserId, Amount = booking.TotalPrice,
            Method = "BankTransfer", Status = "PendingConfirmation"
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        Assert.Single(await db.Payments.Where(p => p.BookingId == booking.BookingId).ToListAsync());
        Assert.False(await db.OutboxMessages.AnyAsync(m => m.Type.StartsWith("payment.")));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
