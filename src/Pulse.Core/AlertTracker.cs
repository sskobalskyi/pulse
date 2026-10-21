namespace Pulse.Core;

/// <summary>
/// Turns a stream of readings into alert state changes: one <see cref="AlertState.Raised"/> when a rule is
/// breached and one <see cref="AlertState.Resolved"/> when it clears, nothing in between.
/// Evaluation works on a copy of the state and only <see cref="AlertBatch.Commit"/> makes it stick, so a batch
/// that fails to persist is evaluated again from the same starting point when it is redelivered.
/// Not thread-safe; use one instance per consumer.
/// </summary>
public sealed class AlertTracker
{
    private HashSet<AlertKey> _active = [];

    /// <summary>Replaces the state, e.g. with the alerts still raised according to storage after a restart.</summary>
    public void Restore(IEnumerable<AlertKey> active) => _active = [.. active];

    /// <summary>The same rules for every reading.</summary>
    public AlertBatch Evaluate(IEnumerable<TelemetryReading> readings, IReadOnlyList<ThresholdRule> rules) =>
        Evaluate(readings, _ => rules);

    /// <param name="rulesFor">The rules in force for a reading, which can differ per device and channel.</param>
    /// <remarks>
    /// An alert belongs to a rule by name, and the name carries the range. When the rules of a channel change, an alert
    /// raised under a rule that no longer applies would stay raised for ever, because nothing evaluates it any more.
    /// So the first reading of a channel in a batch resolves such alerts, before its own rules are looked at. That also
    /// cleans up after a restart, because the state is restored from storage.
    /// </remarks>
    public AlertBatch Evaluate(IEnumerable<TelemetryReading> readings, Func<TelemetryReading, IReadOnlyList<ThresholdRule>> rulesFor)
    {
        var next = new HashSet<AlertKey>(_active);
        var alerts = new List<Alert>();
        var reconciled = new HashSet<(string Device, string Channel)>();

        foreach (var reading in readings)
        {
            var rules = rulesFor(reading);

            if (reconciled.Add((reading.DeviceId, reading.ChannelId)))
            {
                var current = rules.Where(r => r.Classify(reading) != RuleVerdict.NotApplicable).Select(r => r.Name).ToHashSet();
                var stale = next.Where(k => k.DeviceId == reading.DeviceId && k.Channel == reading.ChannelId && !current.Contains(k.Rule)).ToList();
                foreach (var key in stale)
                {
                    next.Remove(key);
                    alerts.Add(new Alert(key.DeviceId, key.Metric, key.Rule, reading.Value, reading.Timestamp, AlertState.Resolved, Channel: key.Channel));
                }
            }

            foreach (var rule in rules)
            {
                var verdict = rule.Classify(reading);
                if (verdict is RuleVerdict.NotApplicable or RuleVerdict.Hold) continue;

                var key = new AlertKey(reading.DeviceId, reading.Metric, rule.Name, reading.ChannelId);
                if (verdict == RuleVerdict.Breach && next.Add(key))
                    alerts.Add(new Alert(key.DeviceId, key.Metric, key.Rule, reading.Value, reading.Timestamp, AlertState.Raised, Channel: key.Channel));
                else if (verdict == RuleVerdict.Clear && next.Remove(key))
                    alerts.Add(new Alert(key.DeviceId, key.Metric, key.Rule, reading.Value, reading.Timestamp, AlertState.Resolved, Channel: key.Channel));
            }
        }

        return new AlertBatch(alerts, () => _active = next);
    }
}

/// <summary>
/// What an alert is about: one rule on one channel of one device. The metric rides along because it is what a person
/// recognises, and is determined by the channel.
/// </summary>
public sealed record AlertKey(string DeviceId, string Metric, string Rule, string Channel)
{
    /// <summary>For the common case of a channel named after its metric.</summary>
    public AlertKey(string deviceId, string metric, string rule) : this(deviceId, metric, rule, metric)
    {
    }
}

public sealed class AlertBatch(IReadOnlyList<Alert> alerts, Action commit)
{
    public IReadOnlyList<Alert> Alerts => alerts;

    /// <summary>Call after the alerts have been stored.</summary>
    public void Commit() => commit();
}
