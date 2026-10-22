namespace Pulse.Api.Events;

/// <summary>An event waiting in the outbox.</summary>
public sealed record OutboxMessage(
    long Id, Guid MessageId, string RoutingKey, string Payload, string? Traceparent, DateTimeOffset OccurredAt);

/// <summary>Where the relay hands events to. RabbitMQ in production, a recording fake in tests.</summary>
public interface IEventBus
{
    /// <summary>Returns once the broker has taken responsibility for the message, or throws.</summary>
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}
