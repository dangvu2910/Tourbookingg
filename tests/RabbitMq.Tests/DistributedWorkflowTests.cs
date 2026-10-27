using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace RabbitMq.Tests;

public class DistributedWorkflowTests
{
    private static readonly string BaseUrl = Environment.GetEnvironmentVariable("TOURBOOKING_URL") ?? "http://localhost:8080";
    private static HttpClient NewClient() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
        { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };

    private static string Field(string html, string name)
    {
        var tag = Regex.Matches(html, "<input[^>]+>").Select(m => m.Value)
            .FirstOrDefault(t => t.Contains($"name=\"{name}\"", StringComparison.Ordinal));
        Assert.NotNull(tag);
        var match = Regex.Match(tag, "value=\"([^\"]*)\"");
        Assert.True(match.Success, $"Missing value for {name}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<string> Register(HttpClient client)
    {
        var email = $"test-{Guid.NewGuid():N}@example.test";
        var page = await client.GetStringAsync("/Account/Register");
        using var response = await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email, ["Password"] = "Test!Password123", ["ConfirmPassword"] = "Test!Password123",
            ["__RequestVerificationToken"] = Field(page, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return email;
    }

    [Fact]
    public async Task InternalDataIsNotExposedThroughGateway()
    {
        using var client = NewClient();
        using var response = await client.GetAsync("/internal/data");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SupportReplyNotifiesTicketOwnerAsynchronously()
    {
        using var customer = NewClient();
        var email = await Register(customer);
        var contact = await customer.GetStringAsync("/Home/Contact");
        using var created = await customer.PostAsync("/Home/Contact", new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["FullName"] = "Support Test", ["Email"] = email, ["Subject"] = "Integration support",
            ["Message"] = "Please help", ["__RequestVerificationToken"] = Field(contact, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        contact = await customer.GetStringAsync("/Home/Contact");
        var match = Regex.Match(contact, "/Home/ContactDetails/(\\d+)");
        Assert.True(match.Success, "New ticket must be visible to its owner.");
        var id = match.Groups[1].Value;
        using var admin = NewClient();
        var login = await admin.GetStringAsync("/Account/Login");
        using var loggedIn = await admin.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["Email"] = Environment.GetEnvironmentVariable("TEST_ADMIN_EMAIL")!,
            ["Password"] = Environment.GetEnvironmentVariable("TEST_ADMIN_PASSWORD")!,
            ["__RequestVerificationToken"] = Field(login, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, loggedIn.StatusCode);
        var contacts = await admin.GetStringAsync("/Admin/Contacts");
        using var replied = await admin.PostAsync("/Admin/ReplyContact", new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["contactInquiryId"] = id, ["adminReply"] = "Support integration response",
            ["__RequestVerificationToken"] = Field(contacts, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, replied.StatusCode);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var notifications = JsonDocument.Parse(await customer.GetStringAsync("/api/notifications"));
            if (notifications.RootElement.EnumerateArray().Any(n => n.GetProperty("type").GetString() == "contact.reply.created"
                && n.GetProperty("resourceId").GetString() == id)) return;
            await Task.Delay(1000);
        }
        Assert.Fail("Support reply was not delivered to the ticket owner within 30 seconds.");
    }

    [Fact]
    public async Task GatewayRoutesByServiceAndProtectsPrivateApis()
    {
        using var client = NewClient();
        foreach (var path in new[] { "/", "/Home/Index" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("X-Service-Name"));
            Assert.Contains("Đi để thấy thế giới rộng hơn", await response.Content.ReadAsStringAsync());
        }
        foreach (var (path, service) in new[] { ("/Tours", "catalog"), ("/Account/Login", "identity") })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(service, response.Headers.GetValues("X-Service-Name").Single());
        }
        using (var contact = await client.GetAsync("/Home/Contact"))
        {
            Assert.Equal(HttpStatusCode.Redirect, contact.StatusCode);
            Assert.Equal("customer", contact.Headers.GetValues("X-Service-Name").Single());
            Assert.Contains("/Account/Login", contact.Headers.Location?.ToString());
        }
        foreach (var path in new[] { "/api/identity/me", "/api/bookings", "/api/notifications" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var tours = await client.GetAsync("/api/tours");
        Assert.Equal(HttpStatusCode.OK, tours.StatusCode);
    }

    [Fact]
    public async Task GroupedServicesExposeTheirModulesThroughLogicalRoutes()
    {
        using var client = NewClient();
        foreach (var (path, service) in new[]
        {
            ("/Home/Payment?bookingId=1", "booking"), ("/Account/Reviews", "customer"),
            ("/Admin/Contacts", "customer"), ("/Admin/Dashboard", "booking"),
            ("/Admin/ExportUsers", "booking"), ("/Admin/ExportToursExcel", "booking"),
            ("/Admin/ExportBookingsExcel", "booking")
        })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(service, response.Headers.GetValues("X-Service-Name").Single());
        }

        using var backend = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        foreach (var url in new[]
        {
            "http://booking:8080/Home/Payment", "http://booking:8080/Home/Booking",
            "http://customer:8080/Admin/Contacts", "http://customer:8080/Account/Reviews",
            "http://booking:8080/Admin/Bookings"
        })
        {
            using var response = await backend.GetAsync(url);
            Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        }

        var accountPage = await client.GetStringAsync("/Account/AccessDenied");
        Assert.Contains("href=\"/Home/Contact\"", accountPage);
        Assert.Contains("href=\"/Home/Index\"", accountPage);
        using var stylesheet = await client.GetAsync("/assets/identity/css/layout.css");
        Assert.Equal(HttpStatusCode.OK, stylesheet.StatusCode);
        Assert.Equal("identity", stylesheet.Headers.GetValues("X-Service-Name").Single());
        using var identityAdminStylesheet = await client.GetAsync("/assets/identity/css/admin-dashboard.css");
        Assert.Equal(HttpStatusCode.OK, identityAdminStylesheet.StatusCode);
        Assert.Equal("identity", identityAdminStylesheet.Headers.GetValues("X-Service-Name").Single());
        using var adminStylesheet = await client.GetAsync("/assets/booking/css/admin-dashboard.css");
        Assert.Equal(HttpStatusCode.OK, adminStylesheet.StatusCode);
        Assert.Equal("booking", adminStylesheet.Headers.GetValues("X-Service-Name").Single());
    }

    [Fact]
    public async Task BookingRoundTripSharesAuthenticationIsIdempotentAndNotifies()
    {
        using var client = NewClient();
        var email = await Register(client);
        using var me = await client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var tours = JsonDocument.Parse(await client.GetStringAsync("/api/tours"));
        Assert.True(tours.RootElement.GetArrayLength() > 0);
        var tour = tours.RootElement[0];
        var tourId = tour.GetProperty("tourId").GetInt32();
        var price = tour.GetProperty("price").GetDecimal();
        var page = await client.GetStringAsync($"/Home/Booking?tourId={tourId}");
        var body = new Dictionary<string, string>
        {
            ["TourId"] = tourId.ToString(), ["RequestId"] = Field(page, "RequestId"),
            ["FullName"] = "Integration Test", ["Email"] = email, ["PhoneNumber"] = "0901234567",
            ["TravelDate"] = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"), ["GuestCount"] = "2",
            ["__RequestVerificationToken"] = Field(page, "__RequestVerificationToken")
        };
        using var created = await client.PostAsync("/Home/Booking", new FormUrlEncodedContent(body));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var location = created.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.Contains("Payment", location);
        var bookingPage = await client.GetStringAsync(location);
        Assert.Contains("__RequestVerificationToken", bookingPage);
        Assert.Contains("action=\"/Home/ConfirmPayment\"", bookingPage);
        using var repeated = await client.PostAsync("/Home/Booking", new FormUrlEncodedContent(body));
        Assert.Equal(location, repeated.Headers.Location?.ToString());
        var bookings = JsonDocument.Parse(await client.GetStringAsync("/api/bookings"));
        Assert.Equal(1, bookings.RootElement.GetArrayLength());
        var booking = bookings.RootElement[0];
        Assert.Equal(price * 2 + 165000m, booking.GetProperty("totalPrice").GetDecimal());
        var bookingId = booking.GetProperty("bookingId").GetInt32();

        using var confirmed = await client.PostAsync("/Home/ConfirmPayment", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["bookingId"] = bookingId.ToString(), ["transactionCode"] = "TEST-TRANSFER",
            ["__RequestVerificationToken"] = Field(bookingPage, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);
        var updated = JsonDocument.Parse(await client.GetStringAsync($"/api/bookings/{bookingId}"));
        Assert.Equal("Processing", updated.RootElement.GetProperty("status").GetString());

        using var admin = NewClient();
        var adminPage = await admin.GetStringAsync("/Account/Login");
        using var loggedIn = await admin.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = Environment.GetEnvironmentVariable("TEST_ADMIN_EMAIL") ?? throw new InvalidOperationException("Missing TEST_ADMIN_EMAIL"),
            ["Password"] = Environment.GetEnvironmentVariable("TEST_ADMIN_PASSWORD") ?? throw new InvalidOperationException("Missing TEST_ADMIN_PASSWORD"),
            ["__RequestVerificationToken"] = Field(adminPage, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, loggedIn.StatusCode);
        foreach (var (path, service) in new[] { ("/Admin/Users", "identity"), ("/Admin/Tours", "catalog"), ("/Admin/Bookings", "booking"), ("/Admin/Contacts", "customer"), ("/Admin/Dashboard", "booking") })
        {
            using var response = await admin.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(service, response.Headers.GetValues("X-Service-Name").Single());
        }
        var adminBookings = await admin.GetStringAsync("/Admin/Bookings");
        using var approved = await admin.PostAsync("/Admin/ConfirmPaymentReceived", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = bookingId.ToString(),
            ["__RequestVerificationToken"] = Field(adminBookings, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        var approvedBooking = JsonDocument.Parse(await client.GetStringAsync($"/api/bookings/{bookingId}"));
        Assert.Equal("Confirmed", approvedBooking.RootElement.GetProperty("status").GetString());
        using var reSubmitted = await client.PostAsync("/Home/ConfirmPayment", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["bookingId"] = bookingId.ToString(),
            ["__RequestVerificationToken"] = Field(bookingPage, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, reSubmitted.StatusCode);
        var stillConfirmed = JsonDocument.Parse(await client.GetStringAsync($"/api/bookings/{bookingId}"));
        Assert.Equal("Confirmed", stillConfirmed.RootElement.GetProperty("status").GetString());

        using var other = NewClient();
        await Register(other);
        using var denied = await other.GetAsync($"/api/bookings/{bookingId}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var adminOnly = await client.GetAsync("/api/bookings/outbox");
        Assert.Equal(HttpStatusCode.Forbidden, adminOnly.StatusCode);
        using var csrfDenied = await client.PostAsync("/Home/ConfirmPayment", new FormUrlEncodedContent(new Dictionary<string, string> { ["bookingId"] = bookingId.ToString() }));
        Assert.Equal(HttpStatusCode.BadRequest, csrfDenied.StatusCode);

        var notified = false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var notifications = JsonDocument.Parse(await client.GetStringAsync("/api/notifications"));
            notified = notifications.RootElement.EnumerateArray().Any(n => n.GetProperty("type").GetString() == "booking.created"
                && n.GetProperty("resourceId").GetString() == bookingId.ToString())
                && notifications.RootElement.EnumerateArray().Any(n => n.GetProperty("type").GetString() == "booking.updated"
                    && n.GetProperty("resourceId").GetString() == bookingId.ToString()
                    && n.GetProperty("message").GetString()!.Contains("đã được xác nhận", StringComparison.Ordinal));
            if (notified) break;
            await Task.Delay(1000);
        }
        Assert.True(notified, "Booking status notification was not delivered within 30 seconds");
        var notificationsPage = await client.GetStringAsync("/Notifications");
        Assert.Contains("đã được xác nhận", WebUtility.HtmlDecode(notificationsPage));
    }

    [Fact]
    public async Task BookingCreatesPaymentDetailsBeforeReturning()
    {
        using var client = NewClient();
        var email = await Register(client);
        var tours = JsonDocument.Parse(await client.GetStringAsync("/api/tours"));
        Assert.True(tours.RootElement.GetArrayLength() > 0);
        var tourId = tours.RootElement[0].GetProperty("tourId").GetInt32();
        var page = await client.GetStringAsync($"/Home/Booking?tourId={tourId}");
        using var created = await client.PostAsync("/Home/Booking", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["TourId"] = tourId.ToString(), ["RequestId"] = Field(page, "RequestId"),
            ["FullName"] = "Immediate Payment Test", ["Email"] = email, ["PhoneNumber"] = "0901234567",
            ["TravelDate"] = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"), ["GuestCount"] = "1",
            ["__RequestVerificationToken"] = Field(page, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var paymentPage = await client.GetStringAsync(created.Headers.Location!.ToString());
        Assert.Contains("action=\"/Home/ConfirmPayment\"", paymentPage);
        Assert.Contains("Tài khoản nhận:", paymentPage);
        Assert.Contains("Mã booking: VOY-", paymentPage);

        using var bookings = JsonDocument.Parse(await client.GetStringAsync("/api/bookings"));
        var booking = Assert.Single(bookings.RootElement.EnumerateArray());
        Assert.Equal("PendingConfirmation", booking.GetProperty("status").GetString());
    }
}
