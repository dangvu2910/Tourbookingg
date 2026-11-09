using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tourbooking.Contracts;
using Tourbooking.Models;
using Tourbooking.ViewModels;

namespace RabbitMq.Tests;

public class BookingCommandTests
{
    private sealed class TempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
    private sealed class Client(ApplicationDbContext db) : ISeatReservationClient
    {
        public int Calls { get; private set; }
        public Task<ReservedSeats> ReserveAsync(ReserveSeats request, CancellationToken ct)
        {
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal("caller", request.UserId);
            Calls++;
            return Task.FromResult(new ReservedSeats(request.RequestId, request.TourId, request.UserId, request.TravelDate,
                request.GuestCount, "Held", DateTime.UtcNow.AddMinutes(15), 100000, "Owner snapshot", "Owner location", new(9,0), new(19,0)));
        }
        public Task<ReservedSeats> ConfirmAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReservedSeats> ReleaseAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CreateUsesOwnerQuoteOutsideTransactionAndRetryDoesNotDuplicateBookingPaymentOrOutbox()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        int tourId;
        await using (var seed = new ApplicationDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            // User ownership comes from the authenticated principal, not a stale profile cache.
            var cachedTour = new Tour { Name = "Old cache", Location = "Old location", Price = 1 };
            seed.Tours.Add(cachedTour);
            await seed.SaveChangesAsync();
            tourId = cachedTour.TourId;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Service:Name"] = "booking", ["RabbitMQ:Enabled"] = "true", ["Reservations:Enabled"] = "true" }).Build();
        var collection = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
            .AddScoped(_ => new ApplicationDbContext(options, config));
        collection.AddIdentity<ApplicationUser,IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        collection.AddControllersWithViews();
        using var provider = collection.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var client = new Client(db);
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "caller")], "test")) };
        var controller = new Tourbooking.Controllers.HomeController(db, scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(), config, client)
        {
            ControllerContext = new ControllerContext { HttpContext = http, RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
                ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor { ControllerName = "Home", ActionName = "Booking" } },
            TempData = new TempDataDictionary(http, new TempDataProvider())
        };
        var request = new BookingCreateViewModel { RequestId = Guid.NewGuid(), TourId = tourId, GuestCount = 2,
            TravelDate = DateTime.Today.AddDays(3), FullName = "Customer", Email = "caller@example.test", PhoneNumber = "0901234567" };
        Assert.IsType<RedirectToActionResult>(await controller.Booking(request));
        Assert.IsType<RedirectToActionResult>(await controller.Booking(request));
        Assert.Equal(1, client.Calls);
        var saved = await db.Bookings.AsNoTracking().SingleAsync();
        Assert.Equal(365000, saved.TotalPrice);
        Assert.Equal("Owner snapshot", saved.TourNameSnapshot);
        Assert.Equal("Held", saved.SeatReservationState);
        Assert.Equal(request.RequestId, saved.SeatReservationId);
        Assert.Equal(saved.TotalPrice, (await db.Payments.AsNoTracking().SingleAsync()).Amount);
        Assert.Single(await db.OutboxMessages.ToListAsync());
        request.GuestCount = 3;
        Assert.IsType<ConflictResult>(await controller.Booking(request));
        Assert.Equal(1, client.Calls);
    }
}
