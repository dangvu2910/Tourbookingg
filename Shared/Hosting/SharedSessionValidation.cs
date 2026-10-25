using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Tourbooking.Distributed;

// Only Identity owns security stamps and role membership. Read replicas must not renew auth claims.
public static class SharedSessionValidation
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        if (context.Properties.IssuedUtc is { } issued &&
            DateTimeOffset.UtcNow - issued < TimeSpan.FromMinutes(30)) return;
        var services = context.HttpContext.RequestServices;
        var config = services.GetRequiredService<IConfiguration>();
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.Add("X-Service-Key", config["Services:InternalKey"]);
        client.DefaultRequestHeaders.Add("Cookie", context.Request.Headers.Cookie.ToString());
        try
        {
            var address = config["Services:identity"] ?? "http://localhost:5101";
            using var response = await client.GetAsync(address + "/internal/auth/session", context.HttpContext.RequestAborted);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name);
                return;
            }
            response.EnsureSuccessStatusCode();
            var claims = await response.Content.ReadFromJsonAsync<List<SessionClaim>>();
            if (claims == null || claims.Count == 0) return;
            var original = context.Principal!.Identity as ClaimsIdentity;
            context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(
                claims.Select(c => new Claim(c.Type, c.Value)), original?.AuthenticationType ?? "Identity.Application",
                original?.NameClaimType ?? ClaimTypes.Name, original?.RoleClaimType ?? ClaimTypes.Role)));
            context.ShouldRenew = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Preserve the existing signed ticket during temporary Identity outages.
            services.GetRequiredService<ILoggerFactory>().CreateLogger("SharedSessionValidation")
                .LogWarning("Identity session validation unavailable: {Message}", ex.Message);
        }
    }
    public sealed record SessionClaim(string Type, string Value);
}
