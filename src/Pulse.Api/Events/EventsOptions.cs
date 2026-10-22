namespace Pulse.Api.Events;

/// <summary>
/// Bound from the <c>Events</c> section. Off by default, so Pulse runs on its own with no broker: nothing is written to
/// the outbox, nothing is published and nothing is consumed.
/// </summary>
public sealed class EventsOptions
{
    public bool Enabled { get; set; }

    /// <summary>How often the relay looks for events that have not been published yet.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a published event stays in the outbox before it is removed.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    public RabbitMqOptions RabbitMq { get; set; } = new();
}

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string VirtualHost { get; set; } = "/";
}
