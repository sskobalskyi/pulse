using Pulse.Core;

namespace Pulse.Api.Commands;

/// <summary>
/// How a command reaches a device. The queue and the API do not care how: the MQTT bridge is one implementation, and
/// a test can plug in another. What comes back from the device goes the other way, into <see cref="CommandStore.ApplyAckAsync"/>.
/// </summary>
public interface ICommandSender
{
    /// <summary>False when nothing can deliver commands, so they wait in the queue until they are delivered or expire.</summary>
    bool Available { get; }

    /// <summary>Hands the command over. Throwing means it did not go and will be tried again.</summary>
    Task SendAsync(DeviceCommand command, CancellationToken ct);
}

/// <summary>The sender when there is no link to devices (no MQTT bridge): commands are accepted and queued, and expire if nothing picks them up.</summary>
public sealed class NoDeviceLink : ICommandSender
{
    public bool Available => false;

    public Task SendAsync(DeviceCommand command, CancellationToken ct) =>
        throw new InvalidOperationException("There is no link to devices, so a command cannot be delivered.");
}
