using Pulse.Core;

namespace Pulse.Tests;

public class RuleValidatorTests
{
    private static readonly ChannelRef[] Declared = [new("soil-1", "soil_moisture"), new("soil-2", "soil_moisture"), new("air-t", "temperature")];

    private static IReadOnlyList<RuleProblem> Check(params DeviceRule[] rules) => RuleValidator.Validate(rules, Declared);

    private static DeviceRule Threshold(string channel = "soil-1", double? min = 30, double? max = 80, double? margin = 5) =>
        new(channel, RuleKinds.Threshold, min, max, margin);

    private static DeviceRule NoData(string channel = "soil-1", int? seconds = 120) =>
        new(channel, RuleKinds.NoData, TimeoutSeconds: seconds);

    [Fact]
    public void Valid_rules_of_both_kinds_pass() =>
        Assert.Empty(Check(Threshold(), NoData(), Threshold("soil-2", min: null), NoData("air-t", 10)));

    [Fact]
    public void An_empty_list_is_valid_and_means_the_platform_defaults() =>
        Assert.Empty(Check());

    [Fact]
    public void A_missing_list_is_refused() =>
        Assert.Single(RuleValidator.Validate(null, Declared));

    [Fact]
    public void A_rule_about_a_channel_the_device_does_not_have_is_unknown_channel()
    {
        var problem = Assert.Single(Check(Threshold("soil-9")));

        Assert.Equal("unknown_channel", problem.Code);
        Assert.Contains("soil-9", problem.Message);
    }

    [Theory]
    [InlineData("alarm")]
    [InlineData("")]
    [InlineData("Threshold")]
    public void An_unknown_kind_is_refused(string kind) =>
        Assert.Contains(Check(new DeviceRule("soil-1", kind, 1, 2)), p => p.Message.Contains("kind must be"));

    [Fact]
    public void A_threshold_needs_at_least_one_bound() =>
        Assert.Contains(Check(Threshold(min: null, max: null)), p => p.Message.Contains("at least one of min and max"));

    [Fact]
    public void A_threshold_with_only_one_bound_is_fine_with_any_margin()
    {
        Assert.Empty(Check(Threshold(min: 30, max: null, margin: 500)));
        Assert.Empty(Check(Threshold(min: null, max: 80, margin: 500)));
    }

    [Theory]
    [InlineData(80, 30)]
    [InlineData(50, 50)]
    public void The_minimum_must_be_below_the_maximum(double min, double max) =>
        Assert.Contains(Check(Threshold(min: min, max: max)), p => p.Message.Contains("min must be below max"));

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void The_margin_cannot_be_negative_or_not_a_number(double margin) =>
        Assert.Contains(Check(Threshold(margin: margin)), p => p.Message.Contains("margin must be zero or more"));

    [Fact]
    public void Bounds_must_be_finite_numbers()
    {
        Assert.Contains(Check(Threshold(min: double.NegativeInfinity)), p => p.Message.Contains("min must be a finite number"));
        Assert.Contains(Check(Threshold(max: double.NaN)), p => p.Message.Contains("max must be a finite number"));
    }

    [Theory]
    [InlineData(10, 20, 5)] // two margins of 5 meet at the middle: nothing could clear
    [InlineData(10, 20, 6)]
    public void A_margin_that_leaves_no_room_to_clear_is_refused(double min, double max, double margin) =>
        Assert.Contains(Check(Threshold(min: min, max: max, margin: margin)), p => p.Message.Contains("could never clear"));

    [Fact]
    public void A_margin_just_under_half_the_range_is_allowed() =>
        Assert.Empty(Check(Threshold(min: 10, max: 20, margin: 4.9)));

    [Fact]
    public void A_threshold_cannot_carry_a_timeout() =>
        Assert.Contains(Check(new DeviceRule("soil-1", RuleKinds.Threshold, 1, 2, 0, 60)), p => p.Message.Contains("belongs to no_data"));

    [Theory]
    [InlineData(null)]
    [InlineData(9)]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_no_data_rule_needs_a_timeout_of_at_least_ten_seconds(int? seconds) =>
        Assert.NotEmpty(Check(NoData(seconds: seconds)));

    [Fact]
    public void The_timeout_cannot_exceed_a_week()
    {
        Assert.Empty(Check(NoData(seconds: RuleValidator.MaxTimeoutSeconds)));
        Assert.Contains(Check(NoData(seconds: RuleValidator.MaxTimeoutSeconds + 1)), p => p.Message.Contains("at most"));
    }

    [Fact]
    public void A_no_data_rule_cannot_carry_bounds() =>
        Assert.Contains(Check(new DeviceRule("soil-1", RuleKinds.NoData, 1, null, null, 60)), p => p.Message.Contains("belong to threshold"));

    [Fact]
    public void A_channel_has_at_most_one_rule_of_each_kind()
    {
        Assert.Contains(Check(Threshold(), Threshold(min: 10)), p => p.Message.Contains("only one rule of each kind"));
        Assert.Empty(Check(Threshold(), NoData())); // one of each is fine
    }

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var problems = Check(Threshold("soil-9"), NoData(seconds: 1), new DeviceRule("soil-2", "alarm"));

        Assert.True(problems.Count >= 3, string.Join("; ", problems.Select(p => p.Message)));
    }
}

public class EffectiveRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly ThresholdRule[] Defaults = [new("soil_moisture", 20, 90, 5), new("temperature", -10, 80, 2)];

    private static TelemetryReading Soil(string channel, double value = 50, string device = "pot") =>
        new(device, "soil_moisture", value, Now, channel);

    [Fact]
    public void A_device_with_no_rules_of_its_own_gets_the_platform_defaults()
    {
        var rules = EffectiveRules.DefaultsOnly(Defaults);

        Assert.Same(Defaults, rules.For(Soil("soil-1")));
    }

    [Fact]
    public void A_channel_with_a_rule_of_its_own_uses_only_that()
    {
        var custom = new Dictionary<(string, string), DeviceRule> { [("pot", "soil-1")] = new("soil-1", RuleKinds.Threshold, 40, 70, 3) };
        var rules = new EffectiveRules(Defaults, custom);

        var applied = Assert.Single(rules.For(Soil("soil-1")));

        Assert.Equal(("soil_moisture", 40.0, 70.0, 3.0), (applied.Metric, applied.Min, applied.Max, applied.Hysteresis));
    }

    [Fact]
    public void Another_channel_of_the_same_device_keeps_the_defaults()
    {
        var custom = new Dictionary<(string, string), DeviceRule> { [("pot", "soil-1")] = new("soil-1", RuleKinds.Threshold, 40, 70, 3) };

        Assert.Same(Defaults, new EffectiveRules(Defaults, custom).For(Soil("soil-2")));
    }

    [Fact]
    public void Another_device_with_a_channel_of_the_same_name_keeps_the_defaults()
    {
        var custom = new Dictionary<(string, string), DeviceRule> { [("pot", "soil-1")] = new("soil-1", RuleKinds.Threshold, 40, 70, 3) };

        Assert.Same(Defaults, new EffectiveRules(Defaults, custom).For(Soil("soil-1", device: "other-pot")));
    }

    [Fact]
    public void A_rule_without_a_margin_means_no_hysteresis()
    {
        var custom = new Dictionary<(string, string), DeviceRule> { [("pot", "soil-1")] = new("soil-1", RuleKinds.Threshold, 40, 70) };

        Assert.Equal(0, new EffectiveRules(Defaults, custom).For(Soil("soil-1")).Single().Hysteresis);
    }
}

public class AlertsFollowRuleChangesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static TelemetryReading Soil(double value, int second, string channel = "soil-1") =>
        new("pot", "soil_moisture", value, T0.AddSeconds(second), channel);

    private static IReadOnlyList<ThresholdRule> Range(double min, double max) => [new("soil_moisture", min, max, 0)];

    private static IReadOnlyList<Alert> Run(AlertTracker tracker, Func<TelemetryReading, IReadOnlyList<ThresholdRule>> rulesFor, params TelemetryReading[] readings)
    {
        var batch = tracker.Evaluate(readings, rulesFor);
        batch.Commit();
        return batch.Alerts;
    }

    [Fact]
    public void Tightening_the_range_raises_under_the_new_rule_and_resolves_the_old_one()
    {
        var tracker = new AlertTracker();
        Run(tracker, _ => Range(30, 80), Soil(20, 0)); // dry under [30, 80]

        var alerts = Run(tracker, _ => Range(10, 90), Soil(20, 1)); // the range is relaxed, 20 is fine now

        var resolved = Assert.Single(alerts);
        Assert.Equal(AlertState.Resolved, resolved.State);
        Assert.Contains("[30, 80]", resolved.Rule);
    }

    [Fact]
    public void A_value_that_breaches_the_new_range_resolves_the_old_alert_and_raises_a_new_one()
    {
        var tracker = new AlertTracker();
        Run(tracker, _ => Range(30, 80), Soil(20, 0));

        var alerts = Run(tracker, _ => Range(25, 85), Soil(20, 1));

        Assert.Equal([AlertState.Resolved, AlertState.Raised], alerts.Select(a => a.State));
        Assert.Contains("[30, 80]", alerts[0].Rule);
        Assert.Contains("[25, 85]", alerts[1].Rule);
    }

    [Fact]
    public void An_unchanged_rule_resolves_nothing()
    {
        var tracker = new AlertTracker();
        Run(tracker, _ => Range(30, 80), Soil(20, 0));

        Assert.Empty(Run(tracker, _ => Range(30, 80), Soil(21, 1)));
    }

    [Fact]
    public void Only_the_channel_whose_rule_changed_is_touched()
    {
        var tracker = new AlertTracker();
        Run(tracker, _ => Range(30, 80), Soil(20, 0, "soil-1"), Soil(20, 0, "soil-2"));

        var alerts = Run(tracker, r => r.Channel == "soil-1" ? Range(10, 90) : Range(30, 80), Soil(20, 1, "soil-1"), Soil(20, 1, "soil-2"));

        var resolved = Assert.Single(alerts);
        Assert.Equal("soil-1", resolved.ChannelId);
    }

    [Fact]
    public void A_channel_that_does_not_report_is_not_touched_in_that_batch()
    {
        var tracker = new AlertTracker();
        Run(tracker, _ => Range(30, 80), Soil(20, 0, "soil-1"));

        // Only soil-2 reports, so nothing is known about soil-1 and its alert stays.
        Assert.Empty(Run(tracker, _ => Range(10, 90), Soil(50, 1, "soil-2")));
    }

    [Fact]
    public void A_state_restored_after_a_restart_is_reconciled_with_the_current_rules_on_the_next_reading()
    {
        var tracker = new AlertTracker();
        tracker.Restore([new AlertKey("pot", "soil_moisture", "soil_moisture outside [30, 80]", "soil-1")]); // from before the rule changed

        var alerts = Run(tracker, _ => Range(10, 90), Soil(20, 0));

        Assert.Equal(AlertState.Resolved, Assert.Single(alerts).State);
    }

    [Fact]
    public void The_same_rules_for_every_reading_behave_as_they_always_did()
    {
        var tracker = new AlertTracker();
        var batch = tracker.Evaluate([Soil(20, 0), Soil(21, 1), Soil(50, 2)], Range(30, 80));

        Assert.Equal([AlertState.Raised, AlertState.Resolved], batch.Alerts.Select(a => a.State));
    }
}

public class NoDataTimeoutPerSeriesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_channel_with_a_timeout_of_its_own_is_named_after_it()
    {
        var tracker = new SilenceTracker(TimeSpan.FromSeconds(60));

        var batch = tracker.Evaluate(
        [
            new SilentSeries("pot", "soil_moisture", TimeSpan.FromSeconds(45), "soil-1", TimeSpan.FromSeconds(30)),
            new SilentSeries("pot", "soil_moisture", TimeSpan.FromSeconds(90), "soil-2"),
        ], T0);

        Assert.Equal(["no data for 30s", "no data for 60s"], batch.Alerts.Select(a => a.Rule));
    }

    [Fact]
    public void Changing_the_timeout_of_a_silent_channel_replaces_its_alert()
    {
        var tracker = new SilenceTracker(TimeSpan.FromSeconds(60));
        tracker.Evaluate([new SilentSeries("pot", "soil_moisture", TimeSpan.FromSeconds(90), "soil-1", TimeSpan.FromSeconds(30))], T0).Commit();

        var batch = tracker.Evaluate([new SilentSeries("pot", "soil_moisture", TimeSpan.FromSeconds(100), "soil-1", TimeSpan.FromSeconds(60))], T0.AddSeconds(10));

        // The two alerts are about different rules, so their order does not matter.
        Assert.Equivalent(new[] { ("no data for 30s", AlertState.Resolved), ("no data for 60s", AlertState.Raised) }, batch.Alerts.Select(a => (a.Rule, a.State)).ToArray());
    }

    [Fact]
    public void The_name_for_a_timeout_is_stable()
    {
        Assert.Equal("no data for 60s", SilenceTracker.NameFor(TimeSpan.FromMinutes(1)));
        Assert.Equal("no data for 90s", SilenceTracker.NameFor(TimeSpan.FromSeconds(90)));
    }
}
