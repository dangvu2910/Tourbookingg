using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Tourbooking.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Data.Sqlite;
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
if ((service is "booking" or "payment" or "support" or "identity" or "catalog" or "review" or "standalone") && builder.Configuration.GetValue("RabbitMQ:Enabled", false))
    builder.Services.AddHostedService<OutboxDispatcher>();
if ((service is "customer") && builder.Configuration.GetValue("RabbitMQ:Enabled", false))
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
{
    var dbContext = initializationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    await EnsureTourScheduleColumnsAsync(dbContext);
}
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
    if (builder.Configuration.GetValue("Database:SeedDemo", false))
    {
        var demoTours = new[]
        {
            new Tour { Name = "Khám phá Đà Nẵng", Location = "Đà Nẵng", Price = 2500000, Description = "Tour 3 ngày 2 đêm khám phá biển và phố cổ.", ImageUrl = "/images/92193f72-0b4b-4797-80d0-2cfa322aaf12_danang.jpg", CategoryId = 1 },
            new Tour { Name = "Nghỉ dưỡng Phú Quốc", Location = "Phú Quốc", Price = 4200000, Description = "Tour 4 ngày 3 đêm nghỉ dưỡng biển đảo.", ImageUrl = "/images/615d428f-d575-4362-9f21-fd32c6e44e88_phuquoc.webp", CategoryId = 2 },
            new Tour { Name = "Trải nghiệm Sa Pa", Location = "Sa Pa", Price = 3200000, Description = "Tour 3 ngày 2 đêm khám phá núi rừng Tây Bắc.", ImageUrl = "/images/49f1bae9-5626-4873-b228-067f6d54a252_sapa.png", CategoryId = 1 },
            new Tour { Name = "Khám phá Đà Lạt mộng mơ", Location = "Đà Lạt, Lâm Đồng", Price = 3100000, Description = "Tour 3 ngày 2 đêm khám phá khí hậu mát lành, hồ và đồi thông Đà Lạt.", ImageUrl = "/images/cfe5b58f-2e42-4e27-b1e0-c211eee6f621_dalat2.jpg", CategoryId = 1 },
            new Tour { Name = "Hành trình di sản Hội An", Location = "Hội An, Quảng Nam", Price = 3700000, Description = "Tour 3 ngày 2 đêm tham quan phố cổ, làng nghề và bờ biển Hội An.", ImageUrl = "/images/839e713c-76f9-40d3-9764-758b56220ae0_hoian.webp", CategoryId = 1 },
            new Tour { Name = "Tour biển Nha Trang", Location = "Nha Trang, Khánh Hòa", Price = 4500000, Description = "Tour 3 ngày 2 đêm nghỉ dưỡng biển và trải nghiệm các điểm đến nổi bật tại Nha Trang.", ImageUrl = "/images/6637f545-5106-4d4a-8350-c3d31518c907_nhatrang.webp", CategoryId = 2 },
            new Tour { Name = "Khám phá Hà Giang", Location = "Hà Giang", Price = 5500000, Description = "Tour 4 ngày 3 đêm qua cao nguyên đá, đèo núi và những bản làng vùng cao.", ImageUrl = "/images/1f54d9c9-5508-4baf-a143-5ab31fa6073b_hagiang.jpg", CategoryId = 1 },
            new Tour { Name = "Du lịch Côn Đảo", Location = "Côn Đảo, Bà Rịa - Vũng Tàu", Price = 6900000, Description = "Tour 3 ngày 2 đêm khám phá biển đảo, thiên nhiên và các di tích tại Côn Đảo.", ImageUrl = "/images/d229ab6e-8453-422a-aaa7-943097563bd6_condao.jpg", CategoryId = 2 },
            new Tour { Name = "Tour khám phá Huế", Location = "Huế, Thừa Thiên Huế", Price = 2900000, Description = "Tour 3 ngày 2 đêm tìm hiểu cố đô, lăng tẩm và ẩm thực đặc trưng xứ Huế.", ImageUrl = "/images/1a85b0b7-62b8-41eb-ac0c-e64eb313df5b_hue.jpg", CategoryId = 1 },
            new Tour { Name = "Khám phá Mộc Châu", Location = "Mộc Châu, Sơn La", Price = 2800000, Description = "Tour 2 ngày 1 đêm thăm đồi chè, thác nước và cao nguyên Mộc Châu.", ImageUrl = "/images/d3785924-8d64-4c9a-82d0-3cde250c3723_mocchau.jpg", CategoryId = 1 },
            new Tour { Name = "Tour miền Tây sông nước", Location = "Miền Tây Nam Bộ", Price = 2400000, Description = "Tour 2 ngày 1 đêm trải nghiệm chợ nổi, miệt vườn và đời sống miền Tây.", ImageUrl = "/images/8c18b822-a807-48e3-b471-ef035840c58c_mientay.jpeg", CategoryId = 2 },
            new Tour { Name = "Khám phá đảo Bình Ba", Location = "Bình Ba, Khánh Hòa", Price = 4100000, Description = "Tour 3 ngày 2 đêm khám phá biển xanh, bãi tắm và hải sản đảo Bình Ba.", ImageUrl = "/images/9dc66f7c-2c48-4127-8d00-2cec5d80b4de_binhbakhanhhoa.jpg", CategoryId = 2 },
            new Tour { Name = "Hành trình Vịnh Hạ Long", Location = "Vịnh Hạ Long, Quảng Ninh", Price = 3900000, Description = "Tour 2 ngày 1 đêm ngắm cảnh vịnh, hang động và các đảo đá nổi tiếng.", ImageUrl = "/images/df90d171-70a7-49f4-9725-290b24feb14f_halong.jpg", CategoryId = 1 },
            new Tour { Name = "Tour trekking Tà Năng", Location = "Tà Năng, Lâm Đồng", Price = 3500000, Description = "Tour trekking 2 ngày 1 đêm dành cho người muốn trải nghiệm cung đường và thiên nhiên Tà Năng.", ImageUrl = "/images/31a31140-e6a8-4664-a487-75c922deebec_trekkingtanang.webp", CategoryId = 1 },
            new Tour { Name = "Khám phá đảo Lý Sơn", Location = "Lý Sơn, Quảng Ngãi", Price = 4700000, Description = "Tour 3 ngày 2 đêm tham quan đảo núi lửa, bãi biển và các thắng cảnh Lý Sơn.", ImageUrl = "/images/5201e709-4b24-46bc-a0aa-e87f460cec9a_daolyson.jpg", CategoryId = 2 },
            new Tour { Name = "Tour biển Quy Nhơn", Location = "Quy Nhơn, Bình Định", Price = 4200000, Description = "Tour 3 ngày 2 đêm khám phá bãi biển, làng chài và cảnh đẹp ven biển Quy Nhơn.", ImageUrl = "/images/3fc471e6-ab81-41a0-aec3-e670dd94c3a4_quynhon.jpg", CategoryId = 2 },
            new Tour { Name = "Khám phá Buôn Ma Thuột", Location = "Buôn Ma Thuột, Đắk Lắk", Price = 3800000, Description = "Tour 3 ngày 2 đêm khám phá văn hóa Tây Nguyên, thác nước và vùng đất cà phê.", ImageUrl = "/images/f5601ad1-5c70-4850-8826-b61f4c37c7ea_buonmathuat.webp", CategoryId = 1 },
            new Tour { Name = "Tour nghỉ dưỡng Vũng Tàu", Location = "Vũng Tàu", Price = 2100000, Description = "Tour 2 ngày 1 đêm nghỉ dưỡng biển và tham quan các điểm nổi bật tại Vũng Tàu.", ImageUrl = "/images/a82908c6-9a7f-4ea0-94f2-d6c8c07c8e69_vungtau.jpg", CategoryId = 2 }
        };

        var existingNames = new HashSet<string>(
            await dbContext.Tours.Select(tour => tour.Name).ToListAsync(),
            StringComparer.OrdinalIgnoreCase);
        var missingTours = demoTours.Where(tour => existingNames.Add(tour.Name)).ToArray();
        if (missingTours.Length > 0)
        {
            dbContext.Tours.AddRange(missingTours);
            await dbContext.SaveChangesAsync();
        }
    }
}


await app.RunAsync();
}

static async Task EnsureTourScheduleColumnsAsync(ApplicationDbContext dbContext)
{
    if (dbContext.Database.GetDbConnection() is not SqliteConnection connection)
        throw new InvalidOperationException("Tour schedule columns require the configured SQLite database.");

    var wasOpen = connection.State == System.Data.ConnectionState.Open;
    if (!wasOpen)
        await connection.OpenAsync();

    try
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA table_info('Tours')";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
        }

        foreach (var (name, defaultValue) in new[]
                 {
                     ("DepartureTime", "08:00:00"),
                     ("ReturnTime", "18:00:00")
                 })
        {
            if (columns.Contains(name))
                continue;

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"ALTER TABLE \"Tours\" ADD COLUMN \"{name}\" TEXT NOT NULL DEFAULT '{defaultValue}'";
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
    finally
    {
        if (!wasOpen)
            await connection.CloseAsync();
    }
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
