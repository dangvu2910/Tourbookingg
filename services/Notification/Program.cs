using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tourbooking.Notifications;
using Tourbooking.Distributed;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = builder.Configuration["Database:Directory"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);
if (builder.Configuration["Database:Provider"] == "MongoDB")
    builder.Services.AddSingleton<INotificationStore, MongoNotificationStore>();
else
{
    builder.Services.AddDbContext<NotificationDb>(options => options.UseSqlite(
        $"Data Source={Path.Combine(dataDirectory, "notification.db")};Default Timeout=30"));
    builder.Services.AddScoped<INotificationStore>(sp => sp.GetRequiredService<NotificationDb>());
}
builder.Services.AddDataProtection().SetApplicationName("Tourbooking.SharedAuth")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeysPath"] ?? ".keys"));
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddCookie(IdentityConstants.ApplicationScheme, options =>
{
    options.Cookie.Name = ".Tourbooking.Auth";
    options.Cookie.Path = "/";
    options.Events.OnValidatePrincipal = Tourbooking.Distributed.SharedSessionValidation.ValidateAsync;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddHttpClient();
builder.Services.AddApiBearer(builder.Configuration);
builder.Services.AddSingleton<ConsumerState>();
builder.Services.AddHostedService<NotificationConsumer>();
var app = builder.Build();
app.UseRouting();
app.UseServiceMetrics();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Service-Node"] = Environment.MachineName;
    await next();
});
app.UseMiddleware<Tourbooking.Distributed.ApiContractMiddleware>();
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<INotificationStore>().InitializeAsync();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { service = "notification", status = "live" }));
// Reading stored notifications does not require the asynchronous consumer to be connected.
app.MapGet("/health/ready", async (INotificationStore db, CancellationToken ct) =>
    await NotificationReadiness.IsReadyAsync(db, ct) ? Results.Ok() : Results.StatusCode(503));
app.MapGet("/api/notifications", async (ClaimsPrincipal user, INotificationStore db, CancellationToken ct) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    return Results.Ok(await db.ListAsync(userId, false, ct));
}).RequireAuthorization();
app.MapGet("/api/notifications/admin", async (INotificationStore db, CancellationToken ct) => Results.Ok(
    await db.ListAsync(null, true, ct)))
    .RequireAuthorization(policy => policy.RequireRole("Admin"));
// Admin bell: unread count since this admin last opened it + latest actionable events (polled by the admin layout).
app.MapGet("/api/notifications/admin/summary", async (ClaimsPrincipal user, INotificationStore db, HttpContext http, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "no-store";
    var adminId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrEmpty(adminId)) return Results.Unauthorized();
    var seenAt = await db.GetOrStartAdminSeenAsync(adminId, DateTime.UtcNow, ct);
    var items = await db.ListAdminFeedAsync(AdminFeed.DefaultLimit, ct);
    var unread = await db.CountAdminFeedSinceAsync(seenAt, ct);
    return Results.Ok(new AdminNotificationSummary(unread, items.Select(n => AdminFeed.ToItem(n, seenAt)).ToList()));
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapPost("/api/notifications/admin/seen", async (ClaimsPrincipal user, INotificationStore db, HttpContext http, CancellationToken ct) =>
{
    // Cookie is SameSite=Lax; the custom header additionally blocks cross-site form posts (it forces a CORS preflight).
    if (http.Request.Headers["X-Requested-With"] != "XMLHttpRequest") return Results.BadRequest();
    var adminId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrEmpty(adminId)) return Results.Unauthorized();
    await db.MarkAdminSeenAsync(adminId, DateTime.UtcNow, ct);
    return Results.NoContent();
}).RequireAuthorization(policy => policy.RequireRole("Admin"));
app.MapGet("/api/v1/notifications",async (ClaimsPrincipal user, INotificationStore db, HttpContext http, CancellationToken ct) =>
{
    http.Items["Pagination"] = new { page = 1, page_size = 100 };
    return Results.Ok((await db.ListAsync(user.FindFirstValue(ClaimTypes.NameIdentifier), false, ct)).Select(NotificationResponse.From));
}).RequireAuthorization();
await app.RunAsync();
