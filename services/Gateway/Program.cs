using Tourbooking.Distributed;
using Microsoft.Extensions.FileProviders;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
var webRootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
var repositoryGatewayWebRoot = Path.Combine(builder.Environment.ContentRootPath, "services", "Gateway", "wwwroot");
if (!File.Exists(Path.Combine(webRootPath, "index.html"))
    && File.Exists(Path.Combine(repositoryGatewayWebRoot, "index.html")))
    webRootPath = repositoryGatewayWebRoot;
var webRootFileProvider = new PhysicalFileProvider(webRootPath);
var routes = new List<RouteConfig>();
void Route(string id, string path, string service, int order = 0) => routes.Add(new RouteConfig
{
    RouteId = id, ClusterId = service, Order = order, Match = new RouteMatch { Path = path },
    Transforms = [new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" }]
});
foreach (var backend in ServiceBoundary.Backends)
{
    var service = ServiceBoundary.Owners(backend)[0];
    foreach (var assetRoot in new[] { "css", "js", "lib" })
    {
        routes.Add(new RouteConfig
        {
            RouteId = $"assets-{backend}-{assetRoot}",
            ClusterId = service,
            Match = new RouteMatch { Path = $"/assets/{backend}/{assetRoot}/{{**rest}}" },
            Transforms =
            [
                new Dictionary<string, string> { ["PathRemovePrefix"] = $"/assets/{backend}" },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "true" }
            ]
        });
    }
}
foreach (var (action, service) in ServiceBoundary.ActionOwners)
    Route(action.Replace('/', '-'), $"/{action}/{{**rest}}", service);
Route("account", "/Account/{**rest}", "identity", 10);
Route("admin", "/Admin/{**rest}", "booking", 10);
Route("notifications", "/Notifications/{**rest}", "booking");
Route("identity-api", "/api/identity/{**rest}", "identity");
Route("booking-api", "/api/bookings/{**rest}", "booking");
Route("notification-api", "/api/notifications/{**rest}", "notification");
Route("catalog-api", "/api/tours/{**rest}", "catalog");
Route("fallback", "/{**rest}", "catalog", 100);
var clusters = ServiceBoundary.Services.Select((service, index) => new ClusterConfig
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
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/internal")) { context.Response.StatusCode = 404; return; }
    context.Request.Headers.Remove("X-Service-Key");
    if (context.Request.Path.Equals("/Home/Index", StringComparison.OrdinalIgnoreCase))
        context.Request.Path = "/";
    await next();
});
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRootFileProvider });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = webRootFileProvider
});
app.UseRouting();
app.MapHealthChecks("/health/live");
app.MapGet("/health/ready", () => Results.Ok());
app.MapReverseProxy();
await app.RunAsync();
