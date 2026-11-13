using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Prometheus;

namespace Tourbooking.Distributed;

public static class ServiceMetrics
{
    // Opt-in only. The endpoint is private and must be blocked at the public edge.
    public static void UseServiceMetrics(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Metrics:Enabled", false)) return;
        app.UseHttpMetrics(options => options.ReduceStatusCodeCardinality());
        app.MapMetrics("/metrics");
    }
}
