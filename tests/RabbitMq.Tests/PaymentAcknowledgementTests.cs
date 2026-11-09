extern alias PaymentModule;

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tourbooking.Models;

namespace RabbitMq.Tests;

public class PaymentAcknowledgementTests
{
    private sealed class ConcurrentUpdate : SaveChangesInterceptor
    {
        public Func<Task>? BeforeSave { get; set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var update = BeforeSave;
            BeforeSave = null;
            if (update != null) await update();
            return result;
        }
    }

    private sealed class TempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "reservation")]
    [InlineData(false, "cancel")]
    [InlineData(false, "paid")]
    [InlineData(false, "duplicate")]
    [InlineData(false, "large-qr")]
    [InlineData(false, "missing-bank")]
    public async Task AcknowledgementUsesCurrentStateAndNeverDuplicatesTransfer(bool cancelled, string? concurrent)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var interceptor = new ConcurrentUpdate();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BankTransfer:BankCode"] = "BIDV",
            ["BankTransfer:AccountNumber"] = concurrent == "missing-bank" ? "" : "123456789"
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
            .AddScoped(_ => new ApplicationDbContext(options));
        services.AddIdentity<ApplicationUser, IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddControllersWithViews();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        // Deliberately leave the local user projection empty: a newly signed-in
        // customer's trusted principal must work before that cache refreshes.
        db.Tours.Add(new Tour { TourId = 1, Name = "Test tour", Location = "Test", Price = 100000 });
        var booking = new Booking { UserId = "customer", TourId = 1, Status = "PendingConfirmation", SeatReservationState = "Held" };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var staleVersion = Convert.ToBase64String(booking.Version);
        var payment = new Payment { BookingId = booking.BookingId, UserId = "customer", Status = "PendingConfirmation", Amount = 3000000000m };
        db.Payments.Add(payment);
        booking.SeatReservationState = cancelled ? "Released" : "Confirmed";
        if (cancelled) booking.Status = "Cancelled";
        await db.SaveChangesAsync();
        Assert.NotEqual(staleVersion, Convert.ToBase64String(booking.Version));
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "customer")], "test")) };
        var controller = new PaymentModule::Tourbooking.Controllers.HomeController(db, config)
        {
            ControllerContext = new ControllerContext { HttpContext = http,
                RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
                ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor
                { ControllerName = "Home", ActionName = "ConfirmPayment" } },
            TempData = new TempDataDictionary(http, new TempDataProvider())
        };

        if (concurrent is "large-qr" or "missing-bank")
        {
            var page = Assert.IsType<ViewResult>(await controller.Payment(booking.BookingId));
            var model = Assert.IsType<PaymentModule::Tourbooking.ViewModels.PaymentPageViewModel>(page.Model);
            if (concurrent == "large-qr") Assert.Contains("amount=3000000000", model.QrImageUrl);
            else Assert.Empty(model.QrImageUrl);
        }

        if (concurrent is "reservation" or "cancel" or "paid" or "duplicate")
        {
            interceptor.BeforeSave = async () =>
            {
                await using var other = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
                var current = await other.Bookings.SingleAsync();
                var currentPayment = await other.Payments.SingleAsync();
                if (concurrent == "reservation") current.RescheduleNote = "Background reservation update";
                if (concurrent == "cancel") current.Status = "Cancelled";
                if (concurrent is "paid" or "duplicate")
                {
                    current.Status = concurrent == "paid" ? "Confirmed" : "Processing";
                    currentPayment.Status = concurrent == "paid" ? "Paid" : "Processing";
                    currentPayment.TransactionCode = "existing";
                    if (concurrent == "paid") currentPayment.PaidAt = DateTime.UtcNow;
                }
                await other.SaveChangesAsync();
            };
        }
        Assert.IsType<RedirectToActionResult>(await controller.ConfirmPayment(booking.BookingId, "first", staleVersion));
        Assert.IsType<RedirectToActionResult>(await controller.ConfirmPayment(booking.BookingId, "duplicate", staleVersion));
        var isCancelled = cancelled || concurrent == "cancel";
        Assert.Equal(isCancelled ? "Cancelled" : concurrent == "paid" ? "Confirmed" : "Processing", booking.Status);
        Assert.Equal(isCancelled ? "PendingConfirmation" : concurrent == "paid" ? "Paid" : "Processing", payment.Status);
        Assert.Equal(isCancelled ? null : concurrent is "paid" or "duplicate" ? "existing" : "first", payment.TransactionCode);
        if (concurrent == "paid") Assert.NotNull(payment.PaidAt);
        Assert.Single(await db.Payments.ToListAsync());
    }

    [Theory]
    [InlineData("Paid", "RefundPending")]
    [InlineData("RefundPending", "RefundPending")]
    [InlineData("Refunded", "Refunded")]
    [InlineData("PendingConfirmation", "Cancelled")]
    public void RepeatedCancellationPreservesRefundState(string original, string expected)
    {
        var payment = new Payment { Status = original };
        payment.Cancel();
        payment.Cancel();
        Assert.Equal(expected, payment.Status);
    }
}
