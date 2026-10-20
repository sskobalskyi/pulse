using Pulse.Core;

namespace Pulse.Tests;

public class SilenceTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static SilentSeries Silent(string device, string metric = "temperature", int seconds = 75) =>
        new(device, metric, TimeSpan.FromSeconds(seconds));

    private static IReadOnlyList<Alert> Run(SilenceTracker tracker, DateTimeOffset now, params SilentSeries[] silent)
    {
        var batch = tracker.Evaluate(silent, now);
        batch.Commit();
        return batch.Alerts;
    }

    [Fact]
    public void A_silent_series_raises_a_no_data_alert_with_how_long_it_has_been_silent()
    {
        var alerts = Run(new SilenceTracker(Timeout), T0, Silent("dev-1", seconds: 75));

        var alert = Assert.Single(alerts);
        Assert.Equal(AlertState.Raised, alert.State);
        Assert.Equal(AlertKind.NoData, alert.Kind);
        Assert.Equal(75, alert.Value);
        Assert.Equal(T0, alert.Timestamp);
        Assert.Equal("no data for 60s", alert.Rule);
    }

    [Fact]
    public void A_series_that_stays_silent_raises_only_once()
    {
        var tracker = new SilenceTracker(Timeout);
        Run(tracker, T0, Silent("dev-1"));

        Assert.Empty(Run(tracker, T0.AddSeconds(10), Silent("dev-1", seconds: 85)));
        Assert.Empty(Run(tracker, T0.AddSeconds(20), Silent("dev-1", seconds: 95)));
    }

    [Fact]
    public void A_series_that_reports_again_is_resolved_once()
    {
        var tracker = new SilenceTracker(Timeout);
        Run(tracker, T0, Silent("dev-1"));

        var alerts = Run(tracker, T0.AddSeconds(10));

        var alert = Assert.Single(alerts);
        Assert.Equal(AlertState.Resolved, alert.State);
        Assert.Equal(AlertKind.NoData, alert.Kind);
        Assert.Equal("dev-1", alert.DeviceId);
        Assert.Empty(Run(tracker, T0.AddSeconds(20)));
    }

    [Fact]
    public void Falling_silent_again_after_recovery_raises_again()
    {
        var tracker = new SilenceTracker(Timeout);

        var alerts = Run(tracker, T0, Silent("dev-1"))
            .Concat(Run(tracker, T0.AddSeconds(10)))
            .Concat(Run(tracker, T0.AddSeconds(20), Silent("dev-1")));

        Assert.Equal([AlertState.Raised, AlertState.Resolved, AlertState.Raised], alerts.Select(a => a.State));
    }

    [Fact]
    public void Series_are_tracked_independently()
    {
        var tracker = new SilenceTracker(Timeout);
        Run(tracker, T0, Silent("dev-1"), Silent("dev-1", "humidity"));

        var alerts = Run(tracker, T0.AddSeconds(10), Silent("dev-1", "humidity"), Silent("dev-2"));

        Assert.Equal(
            [("dev-2", "temperature", AlertState.Raised), ("dev-1", "temperature", AlertState.Resolved)],
            alerts.Select(a => (a.DeviceId, a.Metric, a.State)));
    }

    [Fact]
    public void Nothing_silent_produces_nothing() =>
        Assert.Empty(Run(new SilenceTracker(Timeout), T0));

    [Fact]
    public void Uncommitted_evaluation_is_repeated_from_the_same_state()
    {
        var tracker = new SilenceTracker(Timeout);
        var silent = new[] { Silent("dev-1") };

        var first = tracker.Evaluate(silent, T0); // persisting failed, so Commit was never called
        var second = tracker.Evaluate(silent, T0.AddSeconds(5));

        Assert.Single(first.Alerts);
        Assert.Single(second.Alerts);
        Assert.Equal(AlertState.Raised, second.Alerts[0].State);
    }

    [Fact]
    public void Restored_state_resolves_a_series_that_recovered_while_the_service_was_down()
    {
        var tracker = new SilenceTracker(Timeout);
        tracker.Restore([new AlertKey("dev-1", "temperature", tracker.RuleName)]);

        var alerts = Run(tracker, T0); // dev-1 is no longer silent

        Assert.Equal(AlertState.Resolved, Assert.Single(alerts).State);
    }

    [Fact]
    public void Restored_state_does_not_raise_again_for_a_series_that_is_still_silent()
    {
        var tracker = new SilenceTracker(Timeout);
        tracker.Restore([new AlertKey("dev-1", "temperature", tracker.RuleName)]);

        Assert.Empty(Run(tracker, T0, Silent("dev-1")));
    }

    [Fact]
    public void An_alert_raised_under_an_older_timeout_is_resolved_when_the_series_is_not_silent()
    {
        var tracker = new SilenceTracker(Timeout);
        tracker.Restore([new AlertKey("dev-1", "temperature", "no data for 30s")]);

        var alert = Assert.Single(Run(tracker, T0));

        Assert.Equal(AlertState.Resolved, alert.State);
        Assert.Equal("no data for 30s", alert.Rule);
    }
}
