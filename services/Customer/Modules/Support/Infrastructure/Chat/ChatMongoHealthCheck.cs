using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;

namespace Tourbooking.Chat;

public sealed class ChatMongoHealthCheck(MongoChatStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await store.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Chat database unavailable", ex);
        }
    }
}
