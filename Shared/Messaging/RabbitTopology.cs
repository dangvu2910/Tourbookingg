using RabbitMQ.Client;

namespace Tourbooking.Distributed;

public static class RabbitTopology
{
    public const string Exchange = "tourbooking.events";
    public const string Queue = "tourbooking.notifications";
    public const string RetryQueue = "tourbooking.notifications.retry";
    public const string DeadQueue = "tourbooking.notifications.dead";

    public static ConnectionFactory Factory(IConfiguration config) => new()
    {
        HostName = config["RabbitMQ:Host"] ?? "localhost",
        Port = config.GetValue("RabbitMQ:Port", 5672),
        UserName = config["RabbitMQ:Username"] ?? "guest",
        Password = config["RabbitMQ:Password"] ?? "guest",
        AutomaticRecoveryEnabled = true,
        NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
        RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
    };

    public static async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadQueue, true, false, false, cancellationToken: ct);
        await channel.QueueDeclareAsync(Queue, true, false, false, arguments: new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = DeadQueue
        }, cancellationToken: ct);
        await channel.QueueBindAsync(Queue, Exchange, "#", cancellationToken: ct);
        await channel.QueueDeclareAsync(RetryQueue, true, false, false, arguments: new Dictionary<string, object?>
        {
            ["x-message-ttl"] = 5000,
            ["x-dead-letter-exchange"] = Exchange
        }, cancellationToken: ct);
    }
}
