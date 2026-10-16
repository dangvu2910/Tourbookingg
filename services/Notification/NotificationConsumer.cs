using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Tourbooking.Distributed;

namespace Tourbooking.Notifications;

public sealed class NotificationConsumer(IServiceScopeFactory scopes, IConfiguration config,
    ConsumerState state, ILogger<NotificationConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await RabbitTopology.Factory(config).CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);
                await RabbitTopology.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 10, false, stoppingToken);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        var evt = JsonSerializer.Deserialize<IntegrationEvent>(delivery.Body.Span)
                            ?? throw new JsonException("Empty event");
                        if (evt.EventId == Guid.Empty || string.IsNullOrWhiteSpace(evt.Type)) throw new JsonException("Invalid event envelope");
                        using var scope = scopes.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<NotificationDb>();
                        await db.StoreAsync(evt, stoppingToken);
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                        logger.LogInformation("Handled {EventType} {EventId} trace={CorrelationId}", evt.Type, evt.EventId, evt.CorrelationId);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Notification processing failed for {MessageId}", delivery.BasicProperties.MessageId);
                        try
                        {
                            var retries = delivery.BasicProperties.Headers?.TryGetValue("retry-count", out var count) == true
                                ? Convert.ToInt32(count) : 0;
                            if (ex is JsonException || retries >= 5)
                            {
                                await channel.BasicNackAsync(delivery.DeliveryTag, false, false, stoppingToken);
                                return;
                            }
                            // Confirm the retry copy before acknowledging the original delivery.
                            var properties = new BasicProperties
                            {
                                Persistent = true, ContentType = "application/json",
                                MessageId = delivery.BasicProperties.MessageId,
                                Headers = new Dictionary<string, object?> { ["retry-count"] = retries + 1 }
                            };
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            timeout.CancelAfter(TimeSpan.FromSeconds(10));
                            await channel.BasicPublishAsync("", RabbitTopology.RetryQueue, true, properties, delivery.Body, timeout.Token);
                            await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
                        }
                        catch (Exception retryError)
                        {
                            logger.LogWarning(retryError, "Retry publish failed; returning original message to queue");
                            if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, stoppingToken);
                        }
                    }
                };
                await channel.BasicConsumeAsync(RabbitTopology.Queue, false, consumer, stoppingToken);
                state.Connected = true;
                while (channel.IsOpen && connection.IsOpen && !stoppingToken.IsCancellationRequested)
                    await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Consumer disconnected; retrying in 5 seconds"); }
            finally { state.Connected = false; }
            try { await Task.Delay(5000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
