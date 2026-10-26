using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Tourbooking.Models;

namespace Tourbooking.Distributed;

public sealed class BookingPaymentConsumer(IServiceScopeFactory scopes, IConfiguration config,
    ILogger<BookingPaymentConsumer> logger, string? workflowService = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var service = workflowService ?? config["Service:Name"]!;
        var queue = "tourbooking.workflow." + service;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = await RabbitTopology.Factory(config).CreateConnectionAsync(ct);
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
                await RabbitTopology.DeclareAsync(channel, ct);
                await channel.QueueDeclareAsync(queue + ".dead", true, false, false, cancellationToken: ct);
                await channel.QueueDeclareAsync(queue, true, false, false, new Dictionary<string, object?>
                {
                    ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = queue + ".dead"
                }, cancellationToken: ct);
                await channel.QueueBindAsync(queue, RabbitTopology.Exchange, service == "payment" ? "booking.#" : "payment.#", cancellationToken: ct);
                await channel.QueueDeclareAsync(queue + ".retry", true, false, false, new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = 5000, ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = queue
                }, cancellationToken: ct);
                await channel.BasicQosAsync(0, 1, false, ct);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    try
                    {
                        var evt = JsonSerializer.Deserialize<IntegrationEvent>(delivery.Body.Span) ?? throw new JsonException();
                        using var scope = scopes.CreateScope();
                        await HandleAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), evt, config, ct, service);
                        await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Workflow event failed: {MessageId}", delivery.BasicProperties.MessageId);
                        var retries = delivery.BasicProperties.Headers?.TryGetValue("retry-count", out var count) == true ? Convert.ToInt32(count) : 0;
                        if (ex is JsonException || retries >= 5)
                            await channel.BasicNackAsync(delivery.DeliveryTag, false, false, ct);
                        else
                        {
                            try
                            {
                                await channel.BasicPublishAsync("", queue + ".retry", true, new BasicProperties
                                {
                                    Persistent = true, ContentType = "application/json", MessageId = delivery.BasicProperties.MessageId,
                                    Headers = new Dictionary<string,object?> { ["retry-count"] = retries + 1 }
                                }, delivery.Body, ct);
                                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                            }
                            catch { if (channel.IsOpen) await channel.BasicNackAsync(delivery.DeliveryTag, false, true, ct); }
                        }
                    }
                };
                await channel.BasicConsumeAsync(queue, false, consumer, ct);
                while (channel.IsOpen && !ct.IsCancellationRequested) await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Workflow disconnected; retrying");
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public static async Task HandleAsync(ApplicationDbContext db, IntegrationEvent evt, IConfiguration config,
        CancellationToken ct, string? workflowService = null)
    {
        var service = workflowService ?? db.EventSource;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.InboxMessages.AnyAsync(x => x.Id == evt.EventId, ct)) return;
        if (service == "payment" && evt.Source == "booking")
        {
            // Old pre-migration events have no data. Current booking snapshots reconcile those separately.
            if (evt.Data != null)
            {
                var data = JsonSerializer.Deserialize<BookingEventData>(evt.Data) ?? throw new JsonException();
                var payment = await db.Payments.FirstOrDefaultAsync(p => p.BookingId == data.BookingId, ct);
                if (payment == null)
                {
                    payment = new Payment { BookingId = data.BookingId, UserId = data.UserId, Amount = data.TotalPrice,
                        Method = "BankTransfer", Status = "PendingConfirmation", BankReference = $"VOY-{data.BookingId}",
                        BankName = config["BankTransfer:BankCode"], BankAccountNumber = config["BankTransfer:AccountNumber"],
                        BankAccountName = config["BankTransfer:AccountName"] };
                    db.Payments.Add(payment);
                }
                if (data.Status == "Confirmed" && payment.Status is not ("Paid" or "RefundPending"))
                { payment.Status = "Paid"; payment.PaidAt = DateTime.UtcNow; }
                else if (data.Status == "Cancelled")
                    payment.Status = payment.Status is "Paid" or "RefundPending" ? "RefundPending" : "Cancelled";
            }
        }
        else if (service == "booking" && evt.Source == "payment")
        {
            // Payment events carry BookingId explicitly, avoiding cross-database lookups.
            if (evt.Data != null)
            {
                var data = JsonSerializer.Deserialize<PaymentEventData>(evt.Data) ?? throw new JsonException();
                var booking = await db.Bookings.FindAsync([data.BookingId], ct);
                if (booking != null && booking.Status is not ("Cancelled" or "Confirmed"))
                {
                    if (evt.Status == "Processing") booking.Status = "Processing";
                    if (evt.Status == "Paid") booking.Status = "Confirmed";
                }
            }
        }
        db.InboxMessages.Add(new InboxMessage { Id = evt.EventId, ProcessedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
