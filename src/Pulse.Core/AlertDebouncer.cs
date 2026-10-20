namespace Pulse.Core;

/// <summary>
/// Suppresses repeats of the same alert (device, metric, rule) for a cooldown period, measured in event time.
/// A device that stays out of range raises one alert per cooldown instead of one per reading.
/// State is in memory: after a restart or a partition rebalance the first breach alerts again.
/// Not thread-safe; use one instance per consumer.
/// </summary>
public sealed class AlertDebouncer(TimeSpan cooldown)
{
    private const int PurgeThreshold = 10_000;

    private readonly Dictionary<(string Device, string Metric, string Rule), DateTimeOffset> _lastRaised = new();

    public bool ShouldRaise(Alert alert)
    {
        var key = (alert.DeviceId, alert.Metric, alert.Rule);

        // Out-of-order readings older than the last alert fall inside the window too (negative difference).
        if (_lastRaised.TryGetValue(key, out var last) && alert.Timestamp - last < cooldown)
            return false;

        if (_lastRaised.Count >= PurgeThreshold)
            Purge(alert.Timestamp);

        _lastRaised[key] = alert.Timestamp;
        return true;
    }

    /// <summary>Forgets everything, so the next breach of every key alerts again.</summary>
    public void Reset() => _lastRaised.Clear();

    private void Purge(DateTimeOffset now)
    {
        foreach (var key in _lastRaised.Where(e => now - e.Value >= cooldown).Select(e => e.Key).ToList())
            _lastRaised.Remove(key);
    }
}
