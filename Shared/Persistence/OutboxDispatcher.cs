using System.Text;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace Tourbooking.Distributed;

public sealed class OutboxDispatcher(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await RabbitTopology.Factory(configuration).CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
                await RabbitTopology.DeclareAsync(channel, stoppingToken);
                while (!stoppingToken.IsCancellationRequested && channel.IsOpen)
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var source = configuration["Service:Name"] ?? "standalone";
                    var now = DateTime.UtcNow;
                    var leaseId = Guid.NewGuid();
                    await using (var transaction = await db.Database.BeginTransactionAsync(stoppingToken))
                    {
                        // One instance per SQLite service. Expired leases recover after a crash.
                        var batch = await db.OutboxMessages.Where(m => m.Source == source && m.PublishedAt == null
                            && (m.LockedUntil == null || m.LockedUntil < now)).OrderBy(m => m.CreatedAt).Take(20).ToListAsync(stoppingToken);
                        foreach (var item in batch)
                        {
                            item.LockId = leaseId;
                            item.LockedUntil = now.AddMinutes(2);
                            item.Attempts++;
                        }
                        await db.SaveChangesAsync(stoppingToken);
                        await transaction.CommitAsync(stoppingToken);
                    }
                    var messages = await db.OutboxMessages.Where(m => m.LockId == leaseId).ToListAsync(stoppingToken);
                    foreach (var message in messages)
                    {
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            timeout.CancelAfter(TimeSpan.FromSeconds(10));
                            await channel.BasicPublishAsync(RabbitTopology.Exchange, message.Type, mandatory: true,
                                new BasicProperties { Persistent = true, MessageId = message.Id.ToString(), ContentType = "application/json" },
                                Encoding.UTF8.GetBytes(message.Payload), timeout.Token);
                            message.PublishedAt = DateTime.UtcNow;
                            message.LastError = null;
                        }
                        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                        {
                            message.LastError = ex.Message[..Math.Min(1000, ex.Message.Length)];
                            message.LockedUntil = DateTime.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(message.Attempts, 6))));
                            logger.LogWarning(ex, "Publish failed for event {EventId}; retained in outbox", message.Id);
                        }
                        message.LockId = null;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Outbox unavailable; reconnecting in 5 seconds");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
