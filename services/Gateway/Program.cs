using Tourbooking.Distributed;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
var routes = new List<RouteConfig>();
void Route(string id, string path, string service, int order = 0) => routes.Add(new RouteConfig
{
    RouteId = id, ClusterId = service, Order = order, Match = new RouteMatch { Path = path },
    Transforms = [new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" }]
});
foreach (var action in new[] { "Bookings", "CancelBooking", "RescheduleBooking", "Reviews", "VoteReview" })
    Route($"account-{action}", $"/Account/{action}/{{**rest}}", ServiceBoundary.Owner("Account", action));
foreach (var action in new[] { "Booking", "Payment", "ConfirmPayment", "Contact", "ContactDetails", "PostReply" })
    Route($"home-{action}", $"/Home/{action}/{{**rest}}", ServiceBoundary.Owner("Home", action));
foreach (var action in new[] { "Users", "ExportUsers", "EditUser", "DeleteUser", "CreateUser", "Tours", "ExportToursExcel" })
    Route($"admin-{action}", $"/Admin/{action}/{{**rest}}", ServiceBoundary.Owner("Admin", action));
Route("account", "/Account/{**rest}", "identity", 10);
Route("admin", "/Admin/{**rest}", "booking", 10);
Route("notifications", "/Notifications/{**rest}", "booking");
Route("identity-api", "/api/identity/{**rest}", "identity");
Route("booking-api", "/api/bookings/{**rest}", "booking");
Route("notification-api", "/api/notifications/{**rest}", "notification");
Route("catalog-api", "/api/tours/{**rest}", "catalog");
Route("fallback", "/{**rest}", "catalog", 100);
var clusters = new[] { "identity", "catalog", "booking", "notification" }.Select((service, index) => new ClusterConfig
{
    ClusterId = service,
    Destinations = new Dictionary<string, DestinationConfig>
    {
        [service] = new() { Address = builder.Configuration[$"Services:{service}"] ?? $"http://localhost:{5101 + index}" }
    },
    HealthCheck = new HealthCheckConfig
    {
        Active = new ActiveHealthCheckConfig { Enabled = true, Interval = TimeSpan.FromSeconds(10), Timeout = TimeSpan.FromSeconds(3), Path = "/health/ready", Policy = "ConsecutiveFailures" }
    }
}).ToArray();
builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);
builder.Services.AddHealthChecks();
builder.Services.AddHttpClient();
var app = builder.Build();
app.MapHealthChecks("/health/live");
app.MapGet("/health/ready", async (IHttpClientFactory factory) =>
{
    using var client = factory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(3);
    var checks = await Task.WhenAll(clusters.Select(async cluster =>
    {
        try
        {
            using var response = await client.GetAsync(cluster.Destinations!.Values.First().Address + "/health/ready");
            return new { service = cluster.ClusterId, healthy = response.IsSuccessStatusCode };
        }
        catch { return new { service = cluster.ClusterId, healthy = false }; }
    }));
    return Results.Json(checks, statusCode: checks.All(c => c.healthy) ? 200 : 503);
});
app.MapReverseProxy();
await app.RunAsync();
