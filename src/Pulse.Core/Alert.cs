using System.Text.Json.Serialization;

namespace Pulse.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertState
{
    /// <summary>The condition started: a metric left its range, or a device went silent.</summary>
    Raised,

    /// <summary>The condition ended: the metric came back past the hysteresis margin, or data resumed.</summary>
    Resolved,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertKind
{
    /// <summary>A metric left its allowed range. <see cref="Alert.Value"/> is the reading.</summary>
    Threshold,

    /// <summary>A device stopped reporting a metric. <see cref="Alert.Value"/> is how many seconds it has been silent.</summary>
    NoData,
}

public sealed record Alert(
    string DeviceId,
    string Metric,
    string Rule,
    double Value,
    DateTimeOffset Timestamp,
    AlertState State = AlertState.Raised,
    AlertKind Kind = AlertKind.Threshold);
