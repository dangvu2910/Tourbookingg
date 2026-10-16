using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tourbooking.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;

namespace Tourbooking.Distributed;

public static class TourbookingHost
{
public static async Task RunAsync(string[] args, string service = "standalone")
{
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(TourbookingHost).Assembly.GetName().Name });
builder.Configuration["Service:Name"] = service;

// Add services to the container.
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))
        .ConfigureWarnings(warnings =>
            warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
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
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<ConcurrencyFilter>();
    if (service != "standalone") options.Conventions.Add(new ServiceBoundaryConvention(service));
}).AddApplicationPart(typeof(TourbookingHost).Assembly);
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("sql", tags: ["ready"]);
builder.Services.AddHttpClient();
if (service is "booking" or "standalone" && builder.Configuration.GetValue("RabbitMQ:Enabled", false))
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

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (builder.Configuration.GetValue("Https:Redirect", true)) app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapControllers();

if (builder.Configuration.GetValue("Database:Initialize", service == "standalone"))
{
    using var scope = app.Services.CreateScope();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    dbContext.Database.Migrate();
    await EnsureRolesAndAdminAsync(builder.Configuration, roleManager, userManager);
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


}

