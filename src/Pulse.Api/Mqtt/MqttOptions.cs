namespace Pulse.Api.Mqtt;

public sealed class MqttOptions
{
    public const string Section = "Mqtt";

    /// <summary>Off by default, so Pulse runs alone without a broker, as it does without one for events.</summary>
    public bool Enabled { get; set; }

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 1883;

    /// <summary>The bridge's own account on the broker. Devices have theirs, and may only use their own topics.</summary>
    public string? UserName { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// What the broker sends with its authentication and authorization requests to /mqtt/auth and /mqtt/acl, as `Authorization: Bearer ...`.
    /// Empty leaves those endpoints off, which is how Pulse runs against a broker that lets everyone in.
    /// </summary>
    public string? HttpAuthSecret { get; set; }

    /// <summary>
    /// Set it to give the bridge a session the broker keeps while it is away, so what devices said meanwhile (an answer to a
    /// command above all) is waiting when it is back. Needs a name no other instance uses. Without it the bridge starts clean.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Instances of Pulse share one subscription under this name, so the broker gives each message to one of them
    /// instead of to all.
    /// </summary>
    public string SharedGroup { get; set; } = "pulse";

    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);
}
