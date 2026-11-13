using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace Tourbooking.Distributed;

// Read only the local service outbox; a failed observation must never look like zero backlog.
public sealed class OutboxMetrics(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<OutboxMetrics> logger) : BackgroundService
{
    private static readonly Gauge Pending = Metrics.CreateGauge("tourbooking_outbox_pending", "Unpublished events in the owned SQL outbox. Replicas observe the same logical backlog.");
    private static readonly Gauge Healthy = Metrics.CreateGauge("tourbooking_outbox_observation_success", "One when the last local outbox observation succeeded.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Pending.Set(double.NaN);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Database.SetCommandTimeout(3);
                var sources = ServiceData.OwnedServices(configuration["Service:Name"] ?? "standalone");
                Pending.Set(await db.OutboxMessages.CountAsync(message => sources.Contains(message.Source) && message.PublishedAt == null, timeout.Token));
                Healthy.Set(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                Pending.Set(double.NaN);
                Healthy.Set(0);
                logger.LogWarning("Local outbox metrics unavailable: {ErrorType}", error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
