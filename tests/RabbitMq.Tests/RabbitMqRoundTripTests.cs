using System.Text;
using RabbitMQ.Client;

namespace RabbitMq.Tests;

public class RabbitMqRoundTripTests
{
    [Fact]
    public async Task PublishesAndReceivesMessage()
    {
        var factory = new ConnectionFactory
        {
            HostName = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_PORT"), out var port)
                ? port
                : AmqpTcpEndpoint.UseDefaultPort,
            UserName = Environment.GetEnvironmentVariable("RABBITMQ_USERNAME") ?? "guest",
            Password = Environment.GetEnvironmentVariable("RABBITMQ_PASSWORD") ?? "guest"
        };
        var queueName = $"rabbitmq-test-{Guid.NewGuid():N}";
        var message = $"RabbitMQ smoke test {Guid.NewGuid():N}";
        var messageBody = Encoding.UTF8.GetBytes(message);

        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null);
        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: queueName,
            body: messageBody);

        var received = await channel.BasicGetAsync(queueName, autoAck: true);

        Assert.NotNull(received);
        Assert.Equal(message, Encoding.UTF8.GetString(received.Body.Span));
    }
}
