using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Tourbooking.Distributed;

namespace Tourbooking.Migration;

public static partial class HybridTransfer
{
    public static async Task VerifyRuntimeAsync(string root, string report)
    {
        var settings = EnvironmentSettings(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Jwt:SigningKey"] = settings["API_JWT_SIGNING_KEY"] }).Build();
        await using var identity = ExpressContext("identity");
        var adminRoleIds = await identity.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).ToListAsync();
        var adminIds = await identity.UserRoles.Where(r => adminRoleIds.Contains(r.RoleId)).Select(r => r.UserId).ToListAsync();
        var now = DateTimeOffset.UtcNow;
        var user = await identity.Users.AsNoTracking().FirstAsync(u => !adminIds.Contains(u.Id) &&
            (!u.LockoutEnabled || u.LockoutEnd == null || u.LockoutEnd < now));
        // Operator-only read smoke test. No user/password/session is changed; token stays in memory.
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.UserName ?? user.Id),
            new Claim(ApiBearerAuthentication.StampClaim, ApiBearerAuthentication.HashStamp(user.SecurityStamp)) };
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(settings.GetValueOrDefault("TOURBOOKING_PUBLIC_URL", "http://localhost:8080")), Timeout = TimeSpan.FromSeconds(30) };
        var checks = new List<object>();
        foreach (var path in new[] { "/api/v1/auth/me", "/api/v1/bookings", "/api/v1/chat", "/api/v1/notifications" })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiBearerAuthentication.Issue(config, claims, DateTime.UtcNow));
            using var response = await client.GetAsync(path);
            if (response.StatusCode != HttpStatusCode.OK) throw Stop($"Bearer runtime check failed for {path}: {(int)response.StatusCode}.");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!body.RootElement.GetProperty("success").GetBoolean()) throw Stop("Runtime envelope indicated failure.");
            checks.Add(new { path, status = 200, bearerValidated = true });
        }
        using (var denied = await client.GetAsync("/api/v1/bookings/outbox"))
        {
            if (denied.StatusCode != HttpStatusCode.Forbidden) throw Stop("Non-admin outbox access was not denied.");
            checks.Add(new { path = "/api/v1/bookings/outbox", status = 403, roleValidated = true });
        }
        using (var denied = await client.GetAsync("/Admin/ExportUsers"))
        {
            if (denied.StatusCode != HttpStatusCode.Forbidden || !denied.Headers.TryGetValues("X-Service-Name", out var backend) || backend.Single() != "identity")
                throw Stop("Account export was not routed to Identity with admin protection.");
            checks.Add(new { path = "/Admin/ExportUsers", status = 403, owner = "identity" });
        }
        var administrator = await identity.Users.AsNoTracking().FirstAsync(u => adminIds.Contains(u.Id) &&
            (!u.LockoutEnabled || u.LockoutEnd == null || u.LockoutEnd < now));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiBearerAuthentication.Issue(config,
            [new Claim(ClaimTypes.NameIdentifier, administrator.Id), new Claim(ApiBearerAuthentication.StampClaim, ApiBearerAuthentication.HashStamp(administrator.SecurityStamp))], DateTime.UtcNow));
        using (var export = await client.GetAsync("/Admin/ExportUsers"))
        {
            if (export.StatusCode != HttpStatusCode.OK || export.Content.Headers.ContentType?.MediaType != "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
                throw Stop("Admin account export failed after moving it to Identity.");
            checks.Add(new { path = "/Admin/ExportUsers", status = 200, adminExportVerified = true });
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiBearerAuthentication.Issue(config, claims, DateTime.UtcNow.AddHours(-1)));
        using (var expired = await client.GetAsync("/api/v1/auth/me"))
        {
            if (expired.StatusCode != HttpStatusCode.Unauthorized) throw Stop("Expired token was not rejected.");
            checks.Add(new { path = "/api/v1/auth/me", status = 401, expiredRejected = true });
        }
        client.DefaultRequestHeaders.Authorization = null;
        foreach (var path in new[] { "/internal/data", "/internal/v1/reservations", "/service-info" })
        {
            using var response = await client.GetAsync(path);
            if (response.StatusCode != HttpStatusCode.NotFound) throw Stop("Private route became public.");
        }
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { verified = true, checkedAtUtc = DateTime.UtcNow, checks }, Json));
        Console.WriteLine("Bearer authentication, user ownership routes, role denial, expiry and private-route isolation verified; no business data written.");
    }
}
