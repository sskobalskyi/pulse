using System.Text.Json.Serialization;

namespace Pulse.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertState
{
    /// <summary>The metric left its allowed range.</summary>
    Raised,

    /// <summary>The metric came back inside the range, past the hysteresis margin.</summary>
    Resolved,
}

public sealed record Alert(
    string DeviceId,
    string Metric,
    string Rule,
    double Value,
    DateTimeOffset Timestamp,
    AlertState State = AlertState.Raised);
