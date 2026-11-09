using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Tourbooking.Distributed;

public static class ApiBearerAuthentication
{
    public const string StampClaim = "stamp_hash";
    public static string HashStamp(string? stamp) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp ?? "")));
    public static TokenValidationParameters Validation(IConfiguration config) => new()
    {
        ValidateIssuer = true, ValidIssuer = config["Jwt:Issuer"] ?? "tourbooking.identity",
        ValidateAudience = true, ValidAudience = config["Jwt:Audience"] ?? "tourbooking.api",
        ValidateIssuerSigningKey = true, IssuerSigningKey = Key(config),
        ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
        ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = ClaimTypes.Name, RoleClaimType = ClaimTypes.Role,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
    };
    private static SymmetricSecurityKey Key(IConfiguration config)
    {
        var key = config["Jwt:SigningKey"] ?? throw new InvalidOperationException("JWT signing key is required.");
        if (Encoding.UTF8.GetByteCount(key) < 32) throw new InvalidOperationException("JWT signing key must contain at least 32 bytes.");
        return new(Encoding.UTF8.GetBytes(key));
    }
    public static string Issue(IConfiguration config, IEnumerable<Claim> claims, DateTime utcNow) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            config["Jwt:Issuer"] ?? "tourbooking.identity", config["Jwt:Audience"] ?? "tourbooking.api",
            claims.Append(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))),
            utcNow, utcNow.AddMinutes(15), new SigningCredentials(Key(config), SecurityAlgorithms.HmacSha256)));

    public static void AddApiBearer(this IServiceCollection services, IConfiguration config,
        Func<TokenValidatedContext, Task>? validateIdentity = null)
    {
        if (string.IsNullOrEmpty(config["Jwt:SigningKey"])) return; // Existing standalone/SQLite installations retain cookie auth.
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = "api-or-cookie";
            options.DefaultChallengeScheme = "api-or-cookie";
            options.DefaultForbidScheme = "api-or-cookie";
        }).AddPolicyScheme("api-or-cookie", null, options => options.ForwardDefaultSelector = context =>
            context.Request.Headers.ContainsKey("Authorization") ? JwtBearerDefaults.AuthenticationScheme : IdentityConstants.ApplicationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = Validation(config);
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = validateIdentity ?? ValidateThroughIdentityAsync,
                OnChallenge = context =>
                {
                    if (context.HttpContext.Items.ContainsKey("IdentityUnavailable"))
                    {
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    }
                    return Task.CompletedTask;
                }
            };
        });
    }
    private static async Task ValidateThroughIdentityAsync(TokenValidatedContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        using var client = context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        using var message = new HttpRequestMessage(HttpMethod.Get,
            (config["Services:identity"] ?? "http://localhost:5101").TrimEnd('/') + "/internal/auth/token-session");
        message.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToString());
        message.Headers.Add("X-Service-Key", config["Services:InternalKey"]);
        message.Headers.Add("X-Correlation-ID", context.HttpContext.Items["CorrelationId"]?.ToString());
        try
        {
            using var response = await client.SendAsync(message, context.HttpContext.RequestAborted);
            if ((int)response.StatusCode is 401 or 403) { context.Fail("Identity rejected this token."); return; }
            response.EnsureSuccessStatusCode();
            var claims = await response.Content.ReadFromJsonAsync<List<SharedSessionValidation.SessionClaim>>(context.HttpContext.RequestAborted);
            if (claims == null || claims.All(c => c.Type != ClaimTypes.NameIdentifier)) { context.Fail("Invalid Identity principal."); return; }
            context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)),
                JwtBearerDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            context.HttpContext.Items["IdentityUnavailable"] = true;
            context.Fail("Identity validation is unavailable.");
        }
    }
}
