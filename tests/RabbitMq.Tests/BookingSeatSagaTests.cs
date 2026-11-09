using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tourbooking.Contracts;
using Tourbooking.Distributed;
using Tourbooking.Models;

namespace RabbitMq.Tests;

public class BookingSeatSagaTests
{
    private sealed class Client(string mode) : ISeatReservationClient
    {
        public List<string> Calls { get; } = [];
        public Guid? UnavailableReservation { get; set; }
        public Task<ReservedSeats> ReserveAsync(ReserveSeats request, CancellationToken ct) =>
            Task.FromResult(new ReservedSeats(request.RequestId, request.TourId, request.UserId, request.TravelDate,
                request.GuestCount, "Held", DateTime.UtcNow.AddMinutes(15), 100, "Test", "Test", new(8,0), new(18,0)));
        public Task<ReservedSeats> ConfirmAsync(Guid id, CancellationToken ct)
        {
            Calls.Add("confirm:" + id);
            if (mode.Contains("expired")) throw new ReservationException("RESERVATION_EXPIRED", "Expired");
            if (mode == "offline" || id == UnavailableReservation) throw new HttpRequestException("Catalog unavailable");
            return Task.FromResult(new ReservedSeats(id, 1, "user", DateTime.UtcNow.Date.AddDays(30), 1,
                "Confirmed", DateTime.UtcNow.AddMinutes(15), 100, "Test", "Test", new(8,0), new(18,0)));
        }
        public Task<ReservedSeats> ReleaseAsync(Guid id, CancellationToken ct)
        {
            Calls.Add("release:" + id);
            return Task.FromResult(new ReservedSeats(id, 1, "user", DateTime.UtcNow.Date.AddDays(30), 1,
                "Released", DateTime.UtcNow, 100, "Test", "Test", new(8,0), new(18,0)));
        }
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("expired")]
    [InlineData("offline")]
    [InlineData("cancelled")]
    [InlineData("reschedule-confirmed")]
    [InlineData("reschedule-expired")]
    [InlineData("reschedule-confirmed-processing")]
    [InlineData("reschedule-expired-processing")]
    [InlineData("expired-refund")]
    public async Task ReconciliationConfirmsRetriesReleasesAndCompensatesDurably(string mode)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Service:Name"] = "booking", ["RabbitMQ:Enabled"] = "true" }).Build();
        var client = new Client(mode);
        using var services = new ServiceCollection().AddSingleton<ISeatReservationClient>(client)
            .AddScoped(_ => new ApplicationDbContext(options, config)).BuildServiceProvider();
        var id = Guid.NewGuid();
        var previous = mode.StartsWith("reschedule") ? Guid.NewGuid() : (Guid?)null;
        var oldDate = DateTime.UtcNow.Date.AddDays(20);
        var newDate = oldDate.AddDays(1);
        int bookingId;
        await using (var db = new ApplicationDbContext(options, config))
        {
            await db.Database.EnsureCreatedAsync();
            var booking = new Booking { TourId = 1, UserId = "user", GuestCount = 1, TravelDate = newDate,
                FullName = "Test", Email = "t@example.test", PhoneNumber = "0901234567", TotalPrice = 100,
                SeatReservationId = id, SeatReservationState = "Held", SeatReservationToRelease = previous,
                SeatPreviousTravelDate = previous.HasValue ? oldDate : null,
                Status = mode == "cancelled" ? "Cancelled" : "PendingConfirmation" };
            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            bookingId = booking.BookingId;
            db.Payments.Add(new Payment { BookingId = bookingId, UserId = "user", Amount = 100,
                Status = mode.EndsWith("processing") ? "Processing" : mode == "expired-refund" ? "RefundPending" : previous.HasValue ? "Paid" : "Processing" });
            await db.SaveChangesAsync();
        }
        var worker = new BookingSeatReconciler(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<BookingSeatReconciler>.Instance);
        await worker.ReconcileOnceAsync(default);
        await using var check = new ApplicationDbContext(options, config);
        var saved = await check.Bookings.AsNoTracking().SingleAsync();
        var payment = await check.Payments.AsNoTracking().SingleAsync();
        switch (mode)
        {
            case "offline": Assert.Equal("Held", saved.SeatReservationState); Assert.Equal("PendingConfirmation", saved.Status); break;
            case "expired": Assert.Equal("Cancelled", saved.Status); Assert.Equal("Cancelled", payment.Status); break;
            case "expired-refund": Assert.Equal("Cancelled", saved.Status); Assert.Equal("RefundPending", payment.Status); break;
            case "cancelled": Assert.Equal("Released", saved.SeatReservationState); Assert.Contains("release:" + id, client.Calls); break;
            case "reschedule-expired":
                Assert.Equal(previous, saved.SeatReservationId); Assert.Equal(oldDate, saved.TravelDate);
                Assert.Equal("Confirmed", saved.Status); Assert.Equal("Paid", payment.Status);
                Assert.DoesNotContain("release:" + previous, client.Calls); break;
            case "reschedule-expired-processing":
                Assert.Equal(previous, saved.SeatReservationId); Assert.Equal(oldDate, saved.TravelDate);
                Assert.Equal("Processing", saved.Status); Assert.Equal("Processing", payment.Status);
                Assert.DoesNotContain("release:" + previous, client.Calls); break;
            default:
                Assert.Equal("Confirmed", saved.SeatReservationState);
                if (previous.HasValue)
                {
                    Assert.Equal(new[] { "confirm:" + id, "release:" + previous }, client.Calls);
                    Assert.Equal(mode.EndsWith("processing") ? "Processing" : "Confirmed", saved.Status);
                }
                break;
        }
        if (mode != "offline")
        {
            var calls = client.Calls.Count;
            await worker.ReconcileOnceAsync(default);
            Assert.Equal(calls, client.Calls.Count);
        }
    }

    [Fact]
    public async Task FailedReservationDoesNotBlockOtherBookingsInBatch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var client = new Client("confirmed") { UnavailableReservation = Guid.NewGuid() };
        var secondReservation = Guid.NewGuid();
        using var services = new ServiceCollection().AddSingleton<ISeatReservationClient>(client)
            .AddScoped(_ => new ApplicationDbContext(options)).BuildServiceProvider();
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Bookings.AddRange(
                new Booking { TourId = 1, SeatReservationId = client.UnavailableReservation, SeatReservationState = "Held" },
                new Booking { TourId = 1, SeatReservationId = secondReservation, SeatReservationState = "Held" });
            await db.SaveChangesAsync();
        }
        var worker = new BookingSeatReconciler(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<BookingSeatReconciler>.Instance);
        await worker.ReconcileOnceAsync(default);
        await using var check = new ApplicationDbContext(options);
        var bookings = await check.Bookings.OrderBy(b => b.BookingId).ToListAsync();
        Assert.Equal("Held", bookings[0].SeatReservationState);
        Assert.Equal("Confirmed", bookings[1].SeatReservationState);
        Assert.Contains("confirm:" + secondReservation, client.Calls);
    }
}
