namespace Pulse.Core;

/// <summary>Fires when a metric leaves its allowed [Min, Max] range.</summary>
public sealed record ThresholdRule(string Metric, double? Min, double? Max)
{
    public string Name => $"{Metric} outside [{Min?.ToString() ?? "-inf"}, {Max?.ToString() ?? "+inf"}]";

    public Alert? Evaluate(TelemetryReading reading)
    {
        if (!string.Equals(reading.Metric, Metric, StringComparison.OrdinalIgnoreCase))
            return null;

        var breached = (Min is { } min && reading.Value < min) || (Max is { } max && reading.Value > max);
        return breached ? new Alert(reading.DeviceId, reading.Metric, Name, reading.Value, reading.Timestamp) : null;
    }
}
