using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tourbooking.Notifications;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = builder.Configuration["Database:Directory"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDbContext<NotificationDb>(options => options.UseSqlite(
    $"Data Source={Path.Combine(dataDirectory, "notification.db")};Default Timeout=30"));
builder.Services.AddDataProtection().SetApplicationName("Tourbooking.SharedAuth")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeysPath"] ?? ".keys"));
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddCookie(IdentityConstants.ApplicationScheme, options =>
{
    options.Cookie.Name = ".Tourbooking.Auth";
    options.Cookie.Path = "/";
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddSingleton<ConsumerState>();
builder.Services.AddHostedService<NotificationConsumer>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<NotificationDb>().Database.EnsureCreatedAsync();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { service = "notification", status = "live" }));
app.MapGet("/health/ready", async (NotificationDb db, ConsumerState state) =>
    await db.Database.CanConnectAsync() && state.Connected ? Results.Ok() : Results.StatusCode(503));
app.MapGet("/api/notifications", async (ClaimsPrincipal user, NotificationDb db) =>
{
    var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
    return Results.Ok(await db.Notifications.AsNoTracking().Where(n => n.UserId == userId)
        .OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync());
}).RequireAuthorization();
app.MapGet("/api/notifications/admin", async (NotificationDb db) => Results.Ok(
    await db.Notifications.AsNoTracking().OrderByDescending(n => n.CreatedAt).Take(200).ToListAsync()))
    .RequireAuthorization(policy => policy.RequireRole("Admin"));
await app.RunAsync();
