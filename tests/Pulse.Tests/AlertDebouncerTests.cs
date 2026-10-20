using Pulse.Core;

namespace Pulse.Tests;

public class AlertDebouncerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    private static Alert Alert(string device, DateTimeOffset at, string metric = "temperature", string rule = "r") =>
        new(device, metric, rule, 95, at);

    [Fact]
    public void First_alert_is_raised() =>
        Assert.True(new AlertDebouncer(Cooldown).ShouldRaise(Alert("dev-1", T0)));

    [Fact]
    public void Repeat_within_cooldown_is_suppressed()
    {
        var debouncer = new AlertDebouncer(Cooldown);
        debouncer.ShouldRaise(Alert("dev-1", T0));

        Assert.False(debouncer.ShouldRaise(Alert("dev-1", T0.AddSeconds(1))));
        Assert.False(debouncer.ShouldRaise(Alert("dev-1", T0.AddSeconds(59))));
    }

    [Fact]
    public void Repeat_after_cooldown_is_raised_again()
    {
        var debouncer = new AlertDebouncer(Cooldown);
        debouncer.ShouldRaise(Alert("dev-1", T0));

        Assert.True(debouncer.ShouldRaise(Alert("dev-1", T0 + Cooldown)));
    }

    [Fact]
    public void Suppressed_repeats_do_not_extend_the_cooldown()
    {
        var debouncer = new AlertDebouncer(Cooldown);
        debouncer.ShouldRaise(Alert("dev-1", T0));
        debouncer.ShouldRaise(Alert("dev-1", T0.AddSeconds(50)));

        Assert.True(debouncer.ShouldRaise(Alert("dev-1", T0.AddSeconds(61))));
    }

    [Fact]
    public void Different_device_metric_or_rule_are_independent()
    {
        var debouncer = new AlertDebouncer(Cooldown);
        debouncer.ShouldRaise(Alert("dev-1", T0));

        Assert.True(debouncer.ShouldRaise(Alert("dev-2", T0)));
        Assert.True(debouncer.ShouldRaise(Alert("dev-1", T0, metric: "humidity")));
        Assert.True(debouncer.ShouldRaise(Alert("dev-1", T0, rule: "other")));
    }

    [Fact]
    public void Out_of_order_older_reading_is_suppressed()
    {
        var debouncer = new AlertDebouncer(Cooldown);
        debouncer.ShouldRaise(Alert("dev-1", T0));

        Assert.False(debouncer.ShouldRaise(Alert("dev-1", T0.AddSeconds(-5))));
    }

    [Fact]
    public void Reset_lets_the_next_alert_through()
    {
        var debouncer = new AlertDebouncer(Cooldown);
        debouncer.ShouldRaise(Alert("dev-1", T0));

        debouncer.Reset();

        Assert.True(debouncer.ShouldRaise(Alert("dev-1", T0.AddSeconds(1))));
    }

    [Fact]
    public void Zero_cooldown_raises_every_alert()
    {
        var debouncer = new AlertDebouncer(TimeSpan.Zero);
        debouncer.ShouldRaise(Alert("dev-1", T0));

        Assert.True(debouncer.ShouldRaise(Alert("dev-1", T0)));
    }
}
