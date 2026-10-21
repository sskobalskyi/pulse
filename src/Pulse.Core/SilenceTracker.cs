namespace Pulse.Core;

/// <summary>A channel of a device that has not reported for longer than the timeout.</summary>
/// <param name="Timeout">The timeout of a rule of its own, or null when the platform default applies.</param>
public sealed record SilentSeries(string DeviceId, string Metric, TimeSpan SilentFor, string Channel, TimeSpan? Timeout = null)
{
    /// <summary>For the common case of a channel named after its metric.</summary>
    public SilentSeries(string deviceId, string metric, TimeSpan silentFor) : this(deviceId, metric, silentFor, metric)
    {
    }
}

/// <summary>
/// Turns "which series are silent right now" into alert state changes: <see cref="AlertState.Raised"/> when a
/// series falls silent, <see cref="AlertState.Resolved"/> when it is no longer in the silent set.
/// Like <see cref="AlertTracker"/>, evaluation only takes effect on <see cref="AlertBatch.Commit"/>,
/// so a batch that failed to persist is evaluated again from the same state.
/// Not thread-safe; use one instance per monitor.
/// </summary>
public sealed class SilenceTracker(TimeSpan timeout)
{
    private HashSet<AlertKey> _active = [];

    /// <summary>The rule name for the platform default timeout.</summary>
    public string RuleName => NameFor(timeout);

    public static string NameFor(TimeSpan timeout) => $"no data for {timeout.TotalSeconds:0}s";

    /// <summary>Replaces the state, e.g. with the no-data alerts still raised according to storage after a restart.</summary>
    public void Restore(IEnumerable<AlertKey> active) => _active = [.. active];

    public AlertBatch Evaluate(IReadOnlyCollection<SilentSeries> silent, DateTimeOffset now)
    {
        var next = new HashSet<AlertKey>(_active);
        var alerts = new List<Alert>();
        var silentKeys = new HashSet<AlertKey>();

        foreach (var series in silent)
        {
            var key = new AlertKey(series.DeviceId, series.Metric, series.Timeout is { } own ? NameFor(own) : RuleName, series.Channel);
            silentKeys.Add(key);

            if (next.Add(key))
                alerts.Add(new Alert(key.DeviceId, key.Metric, key.Rule, Math.Round(series.SilentFor.TotalSeconds, 1), now, AlertState.Raised, AlertKind.NoData, key.Channel));
        }

        // Anything that was silent and no longer is has started reporting again. This also cleans up alerts
        // raised under an older timeout, whose rule name differs from the current one.
        foreach (var key in _active.Where(k => !silentKeys.Contains(k)))
        {
            next.Remove(key);
            alerts.Add(new Alert(key.DeviceId, key.Metric, key.Rule, 0, now, AlertState.Resolved, AlertKind.NoData, key.Channel));
        }

        return new AlertBatch(alerts, () => _active = next);
    }
}
