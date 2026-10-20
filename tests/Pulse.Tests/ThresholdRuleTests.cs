using Pulse.Core;

namespace Pulse.Tests;

public class ThresholdRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly ThresholdRule Temperature = new("temperature", -10, 80);
    private static readonly ThresholdRule TemperatureWithMargin = new("temperature", -10, 80, Hysteresis: 2);

    private static TelemetryReading Reading(string metric, double value) => new("dev-1", metric, value, Now);

    [Theory]
    [InlineData(-10)]
    [InlineData(21.5)]
    [InlineData(80)]
    public void Value_inside_range_is_clear_without_hysteresis(double value) =>
        Assert.Equal(RuleVerdict.Clear, Temperature.Classify(Reading("temperature", value)));

    [Theory]
    [InlineData(-10.01)]
    [InlineData(80.01)]
    public void Value_outside_range_is_a_breach(double value) =>
        Assert.Equal(RuleVerdict.Breach, Temperature.Classify(Reading("temperature", value)));

    [Theory]
    [InlineData(79.9)]
    [InlineData(78.1)]
    [InlineData(-9.9)]
    [InlineData(-8.1)]
    public void Value_within_the_margin_of_a_bound_is_in_the_dead_band(double value) =>
        Assert.Equal(RuleVerdict.Hold, TemperatureWithMargin.Classify(Reading("temperature", value)));

    [Theory]
    [InlineData(78)]
    [InlineData(-8)]
    [InlineData(20)]
    public void Value_past_the_margin_is_clear(double value) =>
        Assert.Equal(RuleVerdict.Clear, TemperatureWithMargin.Classify(Reading("temperature", value)));

    [Fact]
    public void Other_metrics_are_not_applicable() =>
        Assert.Equal(RuleVerdict.NotApplicable, Temperature.Classify(Reading("humidity", 1000)));

    [Fact]
    public void Metric_match_is_case_insensitive() =>
        Assert.Equal(RuleVerdict.Breach, Temperature.Classify(Reading("Temperature", 200)));

    [Fact]
    public void Open_ended_rule_only_checks_the_bound_it_has()
    {
        var battery = new ThresholdRule("battery", 10, null, Hysteresis: 5);

        Assert.Equal(RuleVerdict.Breach, battery.Classify(Reading("battery", 5)));
        Assert.Equal(RuleVerdict.Hold, battery.Classify(Reading("battery", 12)));
        Assert.Equal(RuleVerdict.Clear, battery.Classify(Reading("battery", 1_000_000)));
    }
}
