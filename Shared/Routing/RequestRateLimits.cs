using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Tourbooking.Distributed;

public static class RequestRateLimits
{
    public static PartitionedRateLimiter<HttpContext> Create(int loginLimit = 10, int commandLimit = 20) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!HttpMethods.IsPost(context.Request.Method)) return RateLimitPartition.GetNoLimiter("read");
            var path = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant();
            var login = path is "/account/login" or "/account/register" or "/api/v1/auth/login" or "/api/identity/login";
            var command = path is "/home/booking" or "/home/confirmpayment" or "/home/contact" or "/home/postreply"
                or "/admin/replycontact" or "/admin/confirmpaymentreceived" or "/admin/updatebookingstatus"
                or "/account/cancelbooking" or "/account/reschedulebooking" or "/account/votereview" or "/api/identity/logout"
                || context.Request.Path.StartsWithSegments("/api/v1");
            if (!login && !command) return RateLimitPartition.GetNoLimiter("other");
            var caller = !login ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
            caller ??= context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter((login ? "login:" : "command:") + caller,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = login ? loginLimit : commandLimit, Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0, AutoReplenishment = true
                });
        });

    public static IServiceCollection AddRequestRateLimits(this IServiceCollection services) => services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = Create();
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = (context, _) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ValueTask.CompletedTask;
        };
    });
}
