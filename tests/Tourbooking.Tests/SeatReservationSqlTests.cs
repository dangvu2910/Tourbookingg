using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tourbooking.Catalog.Reservations;
using Tourbooking.Contracts;
using Tourbooking.Distributed;
using Tourbooking.Models;

namespace Tourbooking.Tests;

public sealed class ReservationSqlFactAttribute : FactAttribute
{
    public ReservationSqlFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOURBOOKING_RESERVATION_SQL")))
            Skip = "Requires an isolated TourbookingReservationTest_ SQL database.";
    }
}

public class SeatReservationSqlTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static ApplicationDbContext Context()
    {
        var connection = Environment.GetEnvironmentVariable("TOURBOOKING_RESERVATION_SQL")!;
        if (!new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection).InitialCatalog.StartsWith("TourbookingReservationTest_", StringComparison.Ordinal))
            throw new InvalidOperationException("Reservation tests refuse to run against a non-test database.");
        return new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connection).Options,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["Service:Name"] = "catalog", ["Database:Schema"] = "catalog" }).Build());
    }
    private static async Task<int> CreateTour()
    {
        await using var db = Context();
        await DatabaseInitializer.InitializeAsync(db);
        var tour = new Tour { Name = "Reservation test", Location = "Test", Price = 250000 };
        db.Tours.Add(tour);
        await db.SaveChangesAsync();
        return tour.TourId;
    }
    private static async Task<ReservedSeats> Reserve(ReserveSeats request, TimeProvider clock)
    {
        await using var db = Context();
        return await new SeatReservationService(new SqlSeatReservationRepository(db, clock), clock).ReserveAsync(request, default);
    }
    [ReservationSqlFact]
    public async Task ConcurrentRequestsCannotExceedTwentySeats()
    {
        var tourId = await CreateTour();
        var clock = new Clock();
        var date = clock.Now.UtcDateTime.Date.AddDays(30);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try { await Reserve(new(Guid.NewGuid(), tourId, "user" + index, date, 7), clock); return true; }
            catch (ReservationException ex) when (ex.Code == "INSUFFICIENT_CAPACITY") { return false; }
        }));
        Assert.Equal(2, results.Count(success => success));
        await Reserve(new(Guid.NewGuid(), tourId, "last", date, 6), clock);
        var full = await Assert.ThrowsAsync<ReservationException>(() => Reserve(new(Guid.NewGuid(), tourId, "overflow", date, 1), clock));
        Assert.Equal("INSUFFICIENT_CAPACITY", full.Code);
        await using var check = Context();
        Assert.Equal(20, await check.SeatReservations.Where(r => r.TourId == tourId).SumAsync(r => r.GuestCount));
    }
    [ReservationSqlFact]
    public async Task RetriesAreIdempotentAndChangedPayloadConflicts()
    {
        var tourId = await CreateTour();
        var clock = new Clock();
        var request = new ReserveSeats(Guid.NewGuid(), tourId, "same-user", clock.Now.UtcDateTime.Date.AddDays(30), 3);
        var results = await Task.WhenAll(Reserve(request, clock), Reserve(request, clock));
        Assert.Equal(results[0], results[1]);
        var conflict = await Assert.ThrowsAsync<ReservationException>(() => Reserve(request with { GuestCount = 4 }, clock));
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
        await using var db = Context();
        Assert.Equal(1, await db.SeatReservations.CountAsync(r => r.Id == request.RequestId));
    }
    [ReservationSqlFact]
    public async Task ExpiredUncommittedHoldFreesCapacityAndCannotConfirmLate()
    {
        var tourId = await CreateTour();
        var clock = new Clock();
        var request = new ReserveSeats(Guid.NewGuid(), tourId, "abandoned", clock.Now.UtcDateTime.Date.AddDays(30), 20);
        await Reserve(request, clock);
        clock.Now = clock.Now.AddMinutes(16);
        await using var db = Context();
        var repo = new SqlSeatReservationRepository(db, clock);
        var expired = await Assert.ThrowsAsync<ReservationException>(() => repo.ConfirmAsync(request.RequestId, default));
        Assert.Equal("RESERVATION_EXPIRED", expired.Code);
        await Reserve(request with { RequestId = Guid.NewGuid(), UserId = "replacement" }, clock);
    }
    [ReservationSqlFact]
    public async Task ConfirmedSeatsStayReservedUntilIdempotentRelease()
    {
        var tourId = await CreateTour();
        var clock = new Clock();
        var request = new ReserveSeats(Guid.NewGuid(), tourId, "confirmed", clock.Now.UtcDateTime.Date.AddDays(30), 20);
        await Reserve(request, clock);
        await using var db = Context();
        var repo = new SqlSeatReservationRepository(db, clock);
        await repo.ConfirmAsync(request.RequestId, default);
        clock.Now = clock.Now.AddMinutes(16);
        await Assert.ThrowsAsync<ReservationException>(() => Reserve(request with { RequestId = Guid.NewGuid() }, clock));
        await repo.ReleaseAsync(request.RequestId, default);
        await repo.ReleaseAsync(request.RequestId, default);
        await Reserve(request with { RequestId = Guid.NewGuid() }, clock);
    }
}
