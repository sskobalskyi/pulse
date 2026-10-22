namespace Pulse.Core;

public enum RuleVerdict
{
    /// <summary>The reading is for a different metric.</summary>
    NotApplicable,

    /// <summary>Outside [Min, Max].</summary>
    Breach,

    /// <summary>Inside the range by more than the hysteresis margin.</summary>
    Clear,

    /// <summary>Inside the range but within the margin of a bound: the dead band, the state must not change.</summary>
    Hold,
}

/// <summary>
/// A metric must stay inside [Min, Max]. Hysteresis is the margin a value has to come back by before the
/// breach counts as over, so a value hovering around a bound does not flip the state on every reading.
/// </summary>
public sealed record ThresholdRule(string Metric, double? Min, double? Max, double Hysteresis = 0)
{
    public string Name => $"{Metric} outside [{Min?.ToString() ?? "-inf"}, {Max?.ToString() ?? "+inf"}]";

    /// <summary>Which way a value is outside the range, below or above, or null if it is inside.</summary>
    public string? DirectionOf(double value) =>
        Min is { } min && value < min ? "below" : Max is { } max && value > max ? "above" : null;

    public RuleVerdict Classify(TelemetryReading reading)
    {
        if (!string.Equals(reading.Metric, Metric, StringComparison.OrdinalIgnoreCase))
            return RuleVerdict.NotApplicable;

        var value = reading.Value;
        if ((Min is { } min && value < min) || (Max is { } max && value > max))
            return RuleVerdict.Breach;

        var clearOfMin = Min is not { } lo || value >= lo + Hysteresis;
        var clearOfMax = Max is not { } hi || value <= hi - Hysteresis;
        return clearOfMin && clearOfMax ? RuleVerdict.Clear : RuleVerdict.Hold;
    }
}
