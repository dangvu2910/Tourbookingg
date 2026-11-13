using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using RabbitMQ.Client;
using Tourbooking.Distributed;

namespace Tourbooking.Chat;

public sealed class ChatOutboxDispatcher(MongoChatStore store, IConfiguration config,
    ILogger<ChatOutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.InitializeAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await RabbitTopology.ConnectAsync(config, stoppingToken);
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
                await RabbitTopology.DeclareAsync(channel, stoppingToken);
                while (channel.IsOpen && !stoppingToken.IsCancellationRequested)
                {
                    var documents = await store.Conversations.Find(Builders<ChatDocument>.Filter.SizeGt(c => c.PendingEvents, 0))
                        .Limit(20).ToListAsync(stoppingToken);
                    foreach (var document in documents)
                    foreach (var payload in document.PendingEvents)
                    {
                        var evt = JsonSerializer.Deserialize<IntegrationEvent>(payload)!;
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        await channel.BasicPublishAsync(RabbitTopology.Exchange, evt.Type, true,
                            new BasicProperties { Persistent = true, ContentType = "application/json", MessageId = evt.EventId.ToString() },
                            Encoding.UTF8.GetBytes(payload), timeout.Token);
                        await store.Conversations.UpdateOneAsync(c => c.Id == document.Id,
                            Builders<ChatDocument>.Update.Pull(c => c.PendingEvents, payload), cancellationToken: stoppingToken);
                    }
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chat outbox unavailable; messages remain pending");
                try { await Task.Delay(5000, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
