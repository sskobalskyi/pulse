using System.Text.Json.Serialization;

namespace Pulse.Core;

/// <summary>A metric the platform knows, with the unit it is measured in and the range a real sensor can produce.</summary>
public sealed record MetricSpec(string Name, string Unit, double Min, double Max);

/// <summary>
/// The metrics the platform knows about, including the safety sensors of an automated rig. A reading of a known metric
/// outside its range is a faulty sensor or a unit mix-up, and is refused rather than stored. Metrics outside the
/// catalog are not validated: they are accepted as they always were.
/// </summary>
public static class MetricCatalog
{
    private static readonly Dictionary<string, MetricSpec> Specs = new MetricSpec[]
    {
        new("temperature", "°C", -55, 125),
        new("air_humidity", "%", 0, 100),
        new("soil_moisture", "%", 0, 100),
        new("ph", "pH", 0, 14),
        new("light", "lx", 0, 200_000),
        new("airflow", "m/s", 0, 60),
        new("tank_level", "%", 0, 100),
        new("overflow", "", 0, 1), // a flag: 0 dry, 1 water at the overflow sensor
        new("flow", "L/min", 0, 100),
    }.ToDictionary(s => s.Name, StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Names = new HashSet<string>(Specs.Keys, StringComparer.Ordinal);

    public static bool IsKnown(string? metric) => metric is not null && Specs.ContainsKey(metric);

    public static MetricSpec? Find(string? metric) => metric is not null && Specs.TryGetValue(metric, out var spec) ? spec : null;

    /// <summary>
    /// Null if the value is plausible for the metric, or if the metric is not in the catalog.
    /// Otherwise a sentence saying what is wrong.
    /// </summary>
    public static string? Validate(string metric, double value)
    {
        var spec = Find(metric);
        if (spec is null || (value >= spec.Min && value <= spec.Max)) return null;

        return $"{metric} must be between {spec.Min:0.##} and {spec.Max:0.##} {spec.Unit}".TrimEnd() + $", got {value:0.##}";
    }
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
