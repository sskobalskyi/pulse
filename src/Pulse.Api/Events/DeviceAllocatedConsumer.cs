using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pulse.Api.Observability;
using Pulse.Api.Registry;
using Pulse.Core;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Pulse.Api.Events;

/// <summary>
/// Registers a device when Greenhaus announces that it was packed for an order (<c>device.allocated.v1</c>).
/// Registration is idempotent on the device id, so a message that arrives twice, or after a restart, changes nothing the second time.
/// That is what the contract's "consumers are idempotent" needs, without a table of message ids.
/// </summary>
/// <remarks>
/// A message that can never succeed (not JSON, not what the contract describes, or a registration the registry refuses)
/// is rejected without being requeued, and goes to the dead-letter queue <c>pulse.dead-letters</c> where a person can look at it.
/// Retrying would only repeat the failure for ever. A failure that may pass, such as the database being down, is requeued
/// after a pause.
/// </remarks>
public sealed class DeviceAllocatedConsumer(
    IOptions<EventsOptions> options,
    DeviceRegistry registry,
    TimeProvider time,
    ILogger<DeviceAllocatedConsumer> logger) : BackgroundService
{
    public const string Queue = "pulse.device-allocated";
    public const string DeadLetterExchange = "integration.events.dlx";
    public const string DeadLetterQueue = "pulse.dead-letters";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled) return;

        // The broker may not be up yet when Pulse starts, so keep trying instead of giving up and never consuming.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(o.RabbitMq, stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not start consuming {Event}, trying again", EventNames.DeviceAllocated);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ConsumeAsync(RabbitMqOptions o, CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = o.HostName,
            Port = o.Port,
            UserName = o.UserName,
            Password = o.Password,
            VirtualHost = o.VirtualHost,
            ClientProvidedName = "pulse-api-consumer",
        };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(EventNames.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, "", cancellationToken: ct);
        await channel.QueueDeclareAsync(
            Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadLetterExchange },
            cancellationToken: ct);
        await channel.QueueBindAsync(Queue, EventNames.Exchange, EventNames.DeviceAllocated, cancellationToken: ct);
        await channel.BasicQosAsync(0, 10, false, ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, ct);
        await channel.BasicConsumeAsync(Queue, autoAck: false, consumer, ct);
        logger.LogInformation("Consuming {Event} from {Queue}", EventNames.DeviceAllocated, Queue);

        // Stay here for as long as Pulse runs. The client reconnects by itself if the broker goes away for a while.
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var messageId = delivery.BasicProperties.MessageId ?? "(no id)";
        using var activity = StartActivity(delivery);

        try
        {
            var message = JsonSerializer.Deserialize<DeviceAllocatedMessage>(Encoding.UTF8.GetString(delivery.Body.Span), Json);
            if (message is null) throw new FormatException("The message is empty.");

            var registration = message.ToRegistration();
            var problems = RegistrationValidator.Validate(registration, time.GetUtcNow());
            if (problems.Count > 0) throw new FormatException(string.Join("; ", problems));

            var (device, created) = await registry.RegisterAsync(registration, ct);
            logger.LogInformation("Device {DeviceId} {Outcome} from message {MessageId}", device.Id, created ? "registered" : "was already registered", messageId);
            PulseTelemetry.EventsConsumed.Add(1,
                new KeyValuePair<string, object?>("event", EventNames.DeviceAllocated),
                new KeyValuePair<string, object?>("outcome", created ? "registered" : "duplicate"));

            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // Retrying cannot make this message valid. Park it where a person can read it.
            logger.LogError(ex, "Rejecting {Event} message {MessageId}, it goes to {Queue}", EventNames.DeviceAllocated, messageId, DeadLetterQueue);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            PulseTelemetry.EventsConsumed.Add(1,
                new KeyValuePair<string, object?>("event", EventNames.DeviceAllocated),
                new KeyValuePair<string, object?>("outcome", "dead_lettered"));
            await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Probably the database. It may be back in a moment, so try the message again, but not in a tight loop.
            logger.LogWarning(ex, "Could not handle {Event} message {MessageId}, it will be tried again", EventNames.DeviceAllocated, messageId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            PulseTelemetry.EventsConsumed.Add(1,
                new KeyValuePair<string, object?>("event", EventNames.DeviceAllocated),
                new KeyValuePair<string, object?>("outcome", "retried"));
            await Task.Delay(RetryDelay, ct);
            await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: true, ct);
        }
    }

    /// <summary>A span that continues the trace of whoever published the message, so a trace crosses the projects.</summary>
    private static Activity? StartActivity(BasicDeliverEventArgs delivery)
    {
        var parent = default(ActivityContext);
        if (delivery.BasicProperties.Headers?.TryGetValue("traceparent", out var raw) == true && raw is byte[] bytes)
            ActivityContext.TryParse(Encoding.UTF8.GetString(bytes), null, isRemote: true, out parent);

        var activity = PulseTelemetry.Source.StartActivity($"{EventNames.DeviceAllocated} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", EventNames.Exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", EventNames.DeviceAllocated);
        return activity;
    }
}
