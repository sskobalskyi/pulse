using System.Text;
using Microsoft.Extensions.Options;
using Pulse.Core;
using RabbitMQ.Client;

namespace Pulse.Api.Events;

/// <summary>
/// Publishes to the <c>integration.events</c> topic exchange with the event name as the routing key, as the contract says.
/// Every message is persistent and carries the headers the contract asks for. The broker confirms each publish, and
/// <see cref="PublishAsync"/> returns only after that, so the relay never marks as published what the broker did not take.
/// </summary>
public sealed class RabbitMqEventBus(IOptions<EventsOptions> options) : IEventBus, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        var channel = await ChannelAsync(ct);

        var properties = new BasicProperties
        {
            MessageId = message.MessageId.ToString(),
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                ["messageId"] = message.MessageId.ToString(),
                ["occurredAt"] = message.OccurredAt.ToString("o"),
                ["schemaVersion"] = EventNames.SchemaVersion,
            },
        };
        if (message.Traceparent is { } traceparent) properties.Headers["traceparent"] = traceparent;

        try
        {
            await channel.BasicPublishAsync(EventNames.Exchange, message.RoutingKey, mandatory: false, properties, Encoding.UTF8.GetBytes(message.Payload), ct);
        }
        catch
        {
            // The channel may be dead. The next publish starts again from a fresh connection.
            await ResetAsync();
            throw;
        }
    }

    private async Task<IChannel> ChannelAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;

            await CloseAsync();
            var o = options.Value.RabbitMq;
            var factory = new ConnectionFactory
            {
                HostName = o.HostName,
                Port = o.Port,
                UserName = o.UserName,
                Password = o.Password,
                VirtualHost = o.VirtualHost,
                ClientProvidedName = "pulse-api-publisher",
            };
            _connection = await factory.CreateConnectionAsync(ct);
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
            await _channel.ExchangeDeclareAsync(EventNames.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await CloseAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseAsync()
    {
        try
        {
            if (_channel is not null) await _channel.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
        }
        catch
        {
            // Closing something that is already broken is not worth reporting.
        }

        _channel = null;
        _connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }
}
