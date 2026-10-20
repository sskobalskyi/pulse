namespace Pulse.Core;

public static class Topics
{
    /// <summary>Raw readings, keyed by device id so one device always lands on one partition (per-device ordering).</summary>
    public const string Readings = "pulse.readings";

    public const string Alerts = "pulse.alerts";
}
