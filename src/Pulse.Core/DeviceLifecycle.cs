namespace Pulse.Core;

public enum Lifecycle
{
    /// <summary>Registered, packed for an order, not yet sent.</summary>
    Allocated,

    /// <summary>Sent to the customer. The only state a device can be claimed in.</summary>
    Shipped,

    /// <summary>Bound to an owner, has not reported yet.</summary>
    Claimed,

    /// <summary>Claimed and has sent its first reading.</summary>
    Active,

    /// <summary>Taken out of service for good.</summary>
    Retired,
}

/// <summary>The rules for moving a device through its lifecycle. Allocated, shipped, claimed, active, retired, one way.</summary>
public static class DeviceLifecycle
{
    public static bool CanShip(Lifecycle state) => state == Lifecycle.Allocated;

    public static bool CanClaim(Lifecycle state) => state == Lifecycle.Shipped;

    /// <summary>Any device can be retired, at any point, including one that was never shipped.</summary>
    public static bool CanRetire(Lifecycle state) => state != Lifecycle.Retired;

    /// <summary>The first reading of a claimed device makes it active. Readings never revive a retired one.</summary>
    public static bool ActivatesOnReading(Lifecycle state) => state == Lifecycle.Claimed;

    /// <summary>The wire format is lower case: allocated, shipped, claimed, active, retired.</summary>
    public static string ToWire(this Lifecycle state) => state.ToString().ToLowerInvariant();

    public static Lifecycle Parse(string wire) =>
        Enum.Parse<Lifecycle>(wire, ignoreCase: true);

    public static bool TryParse(string? wire, out Lifecycle state) =>
        Enum.TryParse(wire, ignoreCase: true, out state) && Enum.IsDefined(state);
}
