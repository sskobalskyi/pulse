using Pulse.Core;

namespace Pulse.Tests;

public class AlertTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly ThresholdRule[] Rules = [new("temperature", -10, 80, Hysteresis: 2)];

    private static TelemetryReading Reading(double value, int second = 0, string device = "dev-1") =>
        new(device, "temperature", value, T0.AddSeconds(second));

    private static IReadOnlyList<Alert> Run(AlertTracker tracker, params TelemetryReading[] readings)
    {
        var batch = tracker.Evaluate(readings, Rules);
        batch.Commit();
        return batch.Alerts;
    }

    [Fact]
    public void Breach_raises_once_however_long_it_lasts()
    {
        var tracker = new AlertTracker();

        var alerts = Run(tracker, Reading(90, 0), Reading(91, 1), Reading(95, 2));

        var alert = Assert.Single(alerts);
        Assert.Equal(AlertState.Raised, alert.State);
        Assert.Equal(90, alert.Value);
        Assert.Equal(T0, alert.Timestamp);
    }

    [Fact]
    public void Staying_in_the_dead_band_does_not_resolve()
    {
        var tracker = new AlertTracker();
        Run(tracker, Reading(90));

        Assert.Empty(Run(tracker, Reading(79, 1), Reading(78.5, 2)));
    }

    [Fact]
    public void Coming_back_past_the_margin_resolves_once()
    {
        var tracker = new AlertTracker();
        Run(tracker, Reading(90));

        var alerts = Run(tracker, Reading(77, 1), Reading(70, 2));

        var alert = Assert.Single(alerts);
        Assert.Equal(AlertState.Resolved, alert.State);
        Assert.Equal(77, alert.Value);
    }

    [Fact]
    public void Value_flapping_around_the_bound_raises_once_instead_of_every_crossing()
    {
        var tracker = new AlertTracker();

        var alerts = Run(tracker, Reading(81, 0), Reading(79.5, 1), Reading(80.5, 2), Reading(79, 3), Reading(81, 4));

        Assert.Equal([AlertState.Raised], alerts.Select(a => a.State));
    }

    [Fact]
    public void A_new_breach_after_resolving_raises_again()
    {
        var tracker = new AlertTracker();

        var alerts = Run(tracker, Reading(90, 0), Reading(50, 1), Reading(90, 2));

        Assert.Equal([AlertState.Raised, AlertState.Resolved, AlertState.Raised], alerts.Select(a => a.State));
    }

    [Fact]
    public void Healthy_readings_alone_produce_nothing() =>
        Assert.Empty(Run(new AlertTracker(), Reading(20), Reading(21, 1)));

    [Fact]
    public void Devices_are_tracked_independently()
    {
        var tracker = new AlertTracker();
        Run(tracker, Reading(90, device: "dev-1"));

        var alerts = Run(tracker, Reading(90, device: "dev-2"), Reading(50, device: "dev-1"));

        Assert.Equal(
            [("dev-2", AlertState.Raised), ("dev-1", AlertState.Resolved)],
            alerts.Select(a => (a.DeviceId, a.State)));
    }

    [Fact]
    public void Uncommitted_batch_is_evaluated_again_from_the_same_state()
    {
        var tracker = new AlertTracker();
        var readings = new[] { Reading(90) };

        var first = tracker.Evaluate(readings, Rules); // persisting failed, so Commit was never called
        var redelivered = tracker.Evaluate(readings, Rules);

        Assert.Single(first.Alerts);
        Assert.Single(redelivered.Alerts);
        Assert.Equal(AlertState.Raised, redelivered.Alerts[0].State);
    }

    [Fact]
    public void Restored_state_lets_a_recovery_resolve_after_a_restart()
    {
        var tracker = new AlertTracker();
        tracker.Restore([new AlertKey("dev-1", "temperature", Rules[0].Name)]);

        Assert.Empty(Run(tracker, Reading(90))); // still breached, already announced before the restart
        var alerts = Run(tracker, Reading(50, 1));

        Assert.Equal(AlertState.Resolved, Assert.Single(alerts).State);
    }
}
