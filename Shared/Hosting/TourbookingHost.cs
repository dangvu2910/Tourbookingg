using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tourbooking.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using System.Reflection;

namespace Tourbooking.Distributed;

public static class TourbookingHost
{
public static async Task RunAsync(string[] args, string service = "standalone", params string[] controllerAssemblies)
{
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name });
builder.Configuration["Service:Name"] = service;

// Add services to the container.
var dataDirectory = builder.Configuration["Database:Directory"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);
var databaseName = builder.Configuration["Database:Name"] ?? service;
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(
    $"Data Source={Path.Combine(dataDirectory, databaseName + ".db")};Foreign Keys=False;Default Timeout=30"));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 6;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});
builder.Services.AddDataProtection().SetApplicationName("Tourbooking.SharedAuth")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeysPath"]
        ?? Path.Combine(builder.Environment.ContentRootPath, ".keys")));
builder.Services.ConfigureApplicationCookie(options =>
{
    if (service is not ("identity" or "standalone"))
        options.Events.OnValidatePrincipal = SharedSessionValidation.ValidateAsync;
    options.Cookie.Name = ".Tourbooking.Auth";
    options.Cookie.Path = "/";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 401;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 403;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
var mvc = builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<ConcurrencyFilter>();
    options.Filters.Add<ProjectionRefreshFilter>();
    if (service is not ("standalone" or "identity" or "catalog" or "booking" or "payment" or "review" or "support" or "reporting" or "notification"))
        options.Conventions.Add(new BackendActionConvention(service));
});
foreach (var assemblyName in controllerAssemblies)
    mvc.AddApplicationPart(Assembly.Load(new AssemblyName(assemblyName)));
builder.Services.AddSingleton<Microsoft.AspNetCore.Mvc.Routing.IUrlHelperFactory, ServiceUrlHelperFactory>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("sql", tags: ["ready"]);
builder.Services.AddHttpClient();
if ((service is "booking" or "payment") && builder.Configuration.GetValue("RabbitMQ:Enabled", false))
    builder.Services.AddHostedService<BookingPaymentConsumer>();
if (service == "booking-platform" && builder.Configuration.GetValue("RabbitMQ:Enabled", false))
{
    builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider =>
        new BookingPaymentConsumer(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<ILogger<BookingPaymentConsumer>>(), "booking"));
    builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider =>
        new BookingPaymentConsumer(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<ILogger<BookingPaymentConsumer>>(), "payment"));
}
if ((service is "booking" or "payment" or "support" or "identity" or "catalog" or "review" or "standalone") && builder.Configuration.GetValue("RabbitMQ:Enabled", false))
    builder.Services.AddHostedService<OutboxDispatcher>();
if ((service is "booking-platform" or "customer-experience") && builder.Configuration.GetValue("RabbitMQ:Enabled", false))
    builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Backend ports are private in Compose; only the trusted gateway can reach them.
    if (builder.Configuration.GetValue("Service:TrustGateway", false))
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    var correlationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    context.Response.Headers["X-Service-Name"] = service;
    await next();
});
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapGet("/service-info", () => new { service, node = Environment.MachineName });
app.MapGet("/internal/data", async (HttpContext http, ApplicationDbContext db) =>
{
    var key = builder.Configuration["Services:InternalKey"];
    if (string.IsNullOrEmpty(key) || http.Request.Headers["X-Service-Key"] != key
        || !ServiceData.OwnedServices(service).Contains(service)) return Results.Unauthorized();
    return Results.Ok(await ProjectionStore.ExportAsync(db, service, ct: http.RequestAborted));
});
app.MapGet("/internal/data/{owner}", async (string owner, HttpContext http, ApplicationDbContext db) =>
{
    var key = builder.Configuration["Services:InternalKey"];
    if (string.IsNullOrEmpty(key) || http.Request.Headers["X-Service-Key"] != key) return Results.Unauthorized();
    if (!ServiceData.OwnedServices(service).Contains(owner)) return Results.NotFound();
    return Results.Ok(await ProjectionStore.ExportAsync(db, owner, ct: http.RequestAborted));
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(handler => handler.Run(async context =>
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("Không thể xử lý yêu cầu. Vui lòng thử lại.");
    }));
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (builder.Configuration.GetValue("Https:Redirect", true)) app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

if (service is "identity" or "standalone")
    app.MapGet("/internal/auth/session", (HttpContext http) =>
    {
        var key = builder.Configuration["Services:InternalKey"];
        if (string.IsNullOrEmpty(key) || http.Request.Headers["X-Service-Key"] != key)
            return Results.Unauthorized();
        if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
        return Results.Ok(http.User.Claims.Select(c => new SharedSessionValidation.SessionClaim(c.Type, c.Value)));
    });

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapControllers();

using (var initializationScope = app.Services.CreateScope())
    await initializationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
if (service is "identity" or "standalone")
{
    using var scope = app.Services.CreateScope();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await EnsureRolesAndAdminAsync(builder.Configuration, roleManager, userManager);
}

if (service is "catalog" or "standalone")
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    if (builder.Configuration.GetValue("Database:SeedDemo", false) && !await dbContext.Tours.AnyAsync())
    {
        dbContext.Tours.AddRange(
            new Tour { Name = "Khám phá Đà Nẵng", Location = "Đà Nẵng", Price = 2500000, Description = "Tour 3 ngày 2 đêm khám phá biển và phố cổ.", ImageUrl = "/images/92193f72-0b4b-4797-80d0-2cfa322aaf12_danang.jpg", CategoryId = 1 },
            new Tour { Name = "Nghỉ dưỡng Phú Quốc", Location = "Phú Quốc", Price = 4200000, Description = "Tour 4 ngày 3 đêm nghỉ dưỡng biển đảo.", ImageUrl = "/images/615d428f-d575-4362-9f21-fd32c6e44e88_phuquoc.webp", CategoryId = 2 },
            new Tour { Name = "Trải nghiệm Sa Pa", Location = "Sa Pa", Price = 3200000, Description = "Tour 3 ngày 2 đêm khám phá núi rừng Tây Bắc.", ImageUrl = "/images/49f1bae9-5626-4873-b228-067f6d54a252_sapa.png", CategoryId = 1 });
        await dbContext.SaveChangesAsync();
    }
}


await app.RunAsync();
}

static async Task EnsureRolesAndAdminAsync(
    IConfiguration configuration,
    RoleManager<IdentityRole> roleManager,
    UserManager<ApplicationUser> userManager)
{
    var roles = new[] { "Admin", "User" };
    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
        }
    }

    var adminEmail = configuration["AdminUser:Email"];
    var adminPassword = configuration["AdminUser:Password"];
    if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
    {
        return;
    }

    var adminUser = await userManager.FindByEmailAsync(adminEmail);
    if (adminUser == null)
    {
        adminUser = new ApplicationUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow
        };

        var createResult = await userManager.CreateAsync(adminUser, adminPassword);
        if (!createResult.Succeeded)
        {
            return;
        }
    }

    if (!await userManager.IsInRoleAsync(adminUser, "Admin"))
    {
        await userManager.AddToRoleAsync(adminUser, "Admin");
    }
}

public sealed class BackendActionConvention(string backend) : IApplicationModelConvention
{
    private readonly HashSet<string> owners = ServiceBoundary.Owners(backend).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers.ToArray())
        {
            foreach (var action in controller.Actions.ToArray())
                if (!owners.Contains(ServiceBoundary.Owner(controller.ControllerName, action.ActionName)))
                    controller.Actions.Remove(action);
            if (controller.Actions.Count == 0)
                application.Controllers.Remove(controller);
        }
    }
}


}
