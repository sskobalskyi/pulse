using System.Text.Json.Serialization;

namespace Pulse.Core;

/// <summary>The metrics the platform knows about, including the safety sensors of an automated rig.</summary>
public static class MetricCatalog
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "temperature", "air_humidity", "soil_moisture", "ph", "light", "airflow", "tank_level", "overflow", "flow",
    };

    public static bool IsKnown(string? metric) => metric is not null && Names.Contains(metric);
}

public static class ActuatorKinds
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "pump", "heater", "lamp", "fan", "humidifier",
    };
}

/// <summary>The raw sensor values that mean "dry" and "wet", stored so readings can be scaled later.</summary>
public sealed record Calibration(double DryRaw, double WetRaw, DateTimeOffset CalibratedAt);

/// <summary>One sensor channel. A device can expose several channels of one metric, for example one per zone.</summary>
public sealed record Channel(
    string Id,
    string Metric,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Calibration? Calibration = null);

/// <summary>Limits the device itself enforces, whatever the cloud asks for.</summary>
public sealed record HardLimits(int MaxRunSeconds, int MinIntervalSeconds, int DailyCapSeconds);

public sealed record ActuatorCapability(
    string Id,
    string Kind,
    HardLimits HardLimits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ThermalCutoff = null);

public sealed record Capabilities(IReadOnlyList<Channel> Channels, IReadOnlyList<ActuatorCapability> Actuators);

/// <param name="Connection">online or offline, from how recently the device reported, or unknown if it never has.</param>
public sealed record Device(
    string Id,
    string Model,
    string State,
    string? OwnerId,
    string Connection,
    DateTimeOffset? LastSeenAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Firmware,
    Capabilities Capabilities);

/// <param name="CredentialHash">Salted hash of the device's broker password. Opaque to Pulse.</param>
/// <param name="ClaimTokenHash">SHA-256 of the one-time claim token, see <see cref="ClaimTokens"/>.</param>
public sealed record DeviceRegistration(
    string Id,
    string Model,
    Capabilities Capabilities,
    string CredentialHash,
    string ClaimTokenHash,
    DateTimeOffset ClaimTokenExpiresAt);
