using Pulse.Core;

namespace Pulse.Tests;

public class ThresholdRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly ThresholdRule Temperature = new("temperature", -10, 80);

    private static TelemetryReading Reading(string metric, double value) => new("dev-1", metric, value, Now);

    [Theory]
    [InlineData(-10)]
    [InlineData(21.5)]
    [InlineData(80)]
    public void Value_inside_range_does_not_alert(double value) =>
        Assert.Null(Temperature.Evaluate(Reading("temperature", value)));

    [Theory]
    [InlineData(-10.01)]
    [InlineData(80.01)]
    public void Value_outside_range_alerts(double value)
    {
        var alert = Temperature.Evaluate(Reading("temperature", value));

        Assert.NotNull(alert);
        Assert.Equal("dev-1", alert.DeviceId);
        Assert.Equal(value, alert.Value);
        Assert.Equal(Now, alert.Timestamp);
    }

    [Fact]
    public void Other_metrics_are_ignored() =>
        Assert.Null(Temperature.Evaluate(Reading("humidity", 1000)));

    [Fact]
    public void Metric_match_is_case_insensitive() =>
        Assert.NotNull(Temperature.Evaluate(Reading("Temperature", 200)));

    [Fact]
    public void Open_ended_rule_only_checks_the_bound_it_has()
    {
        var battery = new ThresholdRule("battery", 10, null);

        Assert.NotNull(battery.Evaluate(Reading("battery", 5)));
        Assert.Null(battery.Evaluate(Reading("battery", 1_000_000)));
    }
}
