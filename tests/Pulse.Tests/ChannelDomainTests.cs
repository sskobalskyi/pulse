using Pulse.Core;

namespace Pulse.Tests;

public class ChannelResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static TelemetryReading Reading(string device, string metric, double value = 1, string? channel = null) =>
        new(device, metric, value, Now, channel);

    private static IReadOnlyDictionary<string, IReadOnlyList<ChannelRef>> Registered(params (string Device, ChannelRef[] Channels)[] devices) =>
        devices.ToDictionary(d => d.Device, d => (IReadOnlyList<ChannelRef>)d.Channels);

    private static readonly ChannelRef[] Pot =
    [
        new("soil-1", "soil_moisture"),
        new("soil-2", "soil_moisture"),
        new("air-t", "temperature"),
    ];

    [Fact]
    public void A_device_nobody_registered_is_taken_as_it_comes()
    {
        var (readings, problems) = ChannelResolver.Resolve([Reading("demo-1", "temperature"), Reading("demo-1", "whatever", channel: "any")], Registered());

        Assert.Empty(problems);
        Assert.Equal([null, "any"], readings.Select(r => r.Channel));
    }

    [Fact]
    public void A_reading_without_a_channel_gets_the_only_channel_of_its_metric()
    {
        var (readings, problems) = ChannelResolver.Resolve([Reading("pot", "temperature")], Registered(("pot", Pot)));

        Assert.Empty(problems);
        Assert.Equal("air-t", readings.Single().Channel);
    }

    [Fact]
    public void A_reading_that_names_a_valid_channel_is_kept()
    {
        var (readings, problems) = ChannelResolver.Resolve([Reading("pot", "soil_moisture", channel: "soil-2")], Registered(("pot", Pot)));

        Assert.Empty(problems);
        Assert.Equal("soil-2", readings.Single().Channel);
    }

    [Fact]
    public void Two_channels_of_one_metric_make_an_unnamed_reading_ambiguous()
    {
        var (readings, problems) = ChannelResolver.Resolve([Reading("pot", "soil_moisture")], Registered(("pot", Pot)));

        var problem = Assert.Single(problems);
        Assert.Equal("ambiguous_channel", problem.Code);
        Assert.Contains("soil-1, soil-2", problem.Message);
        Assert.Empty(readings);
    }

    [Fact]
    public void A_channel_the_device_does_not_have_is_unknown()
    {
        var (_, problems) = ChannelResolver.Resolve([Reading("pot", "soil_moisture", channel: "soil-9")], Registered(("pot", Pot)));

        Assert.Equal("unknown_channel", Assert.Single(problems).Code);
    }

    [Fact]
    public void A_metric_the_device_does_not_measure_is_unknown()
    {
        var (_, problems) = ChannelResolver.Resolve([Reading("pot", "ph")], Registered(("pot", Pot)));

        Assert.Equal("unknown_channel", Assert.Single(problems).Code);
    }

    [Fact]
    public void A_channel_used_for_the_wrong_metric_is_a_mismatch()
    {
        var (_, problems) = ChannelResolver.Resolve([Reading("pot", "temperature", channel: "soil-1")], Registered(("pot", Pot)));

        var problem = Assert.Single(problems);
        Assert.Equal("metric_mismatch", problem.Code);
        Assert.Contains("soil_moisture", problem.Message);
    }

    [Fact]
    public void A_registered_device_with_no_channels_accepts_nothing()
    {
        var (_, problems) = ChannelResolver.Resolve([Reading("bare", "temperature")], Registered(("bare", [])));

        Assert.Equal("unknown_channel", Assert.Single(problems).Code);
    }

    [Fact]
    public void Registered_and_unregistered_devices_can_share_a_batch()
    {
        var (readings, problems) = ChannelResolver.Resolve(
            [Reading("pot", "temperature"), Reading("demo-1", "temperature"), Reading("pot", "soil_moisture", channel: "soil-1")],
            Registered(("pot", Pot)));

        Assert.Empty(problems);
        Assert.Equal(["air-t", null, "soil-1"], readings.Select(r => r.Channel));
        Assert.Equal(["pot", "demo-1", "pot"], readings.Select(r => r.DeviceId)); // order is kept
    }

    [Fact]
    public void A_thousand_identical_mistakes_are_reported_once()
    {
        var batch = Enumerable.Range(0, 1000).Select(_ => Reading("pot", "soil_moisture")).ToList();

        var (_, problems) = ChannelResolver.Resolve(batch, Registered(("pot", Pot)));

        Assert.Single(problems);
    }

    [Fact]
    public void A_blank_channel_counts_as_none()
    {
        var (readings, problems) = ChannelResolver.Resolve([Reading("pot", "temperature", channel: "  ")], Registered(("pot", Pot)));

        Assert.Empty(problems);
        Assert.Equal("air-t", readings.Single().Channel);
    }
}

public class TelemetryChannelTests
{
    [Fact]
    public void A_reading_with_no_channel_is_on_the_channel_named_after_its_metric() =>
        Assert.Equal("temperature", new TelemetryReading("d", "temperature", 1, DateTimeOffset.UtcNow).ChannelId);

    [Fact]
    public void A_named_channel_wins() =>
        Assert.Equal("soil-2", new TelemetryReading("d", "soil_moisture", 1, DateTimeOffset.UtcNow, "soil-2").ChannelId);

    [Fact]
    public void The_wire_format_stays_compatible_with_senders_that_know_nothing_about_channels()
    {
        var reading = System.Text.Json.JsonSerializer.Deserialize<TelemetryReading>(
            """{"deviceId":"d","metric":"temperature","value":21.5,"timestamp":"2026-10-21T12:00:00Z"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Null(reading.Channel);
        Assert.Equal("temperature", reading.ChannelId);
    }

    [Fact]
    public void The_derived_channel_id_is_not_written_to_the_wire()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new TelemetryReading("d", "temperature", 1, DateTimeOffset.UtcNow));

        Assert.DoesNotContain("ChannelId", json);
    }

    [Fact]
    public void Alerts_and_alert_keys_default_their_channel_to_the_metric()
    {
        Assert.Equal("ph", new Alert("d", "ph", "r", 1, DateTimeOffset.UtcNow).ChannelId);
        Assert.Equal("ph", new AlertKey("d", "ph", "r").Channel);
        Assert.Equal("ph", new SilentSeries("d", "ph", TimeSpan.Zero).Channel);
    }

    [Fact]
    public void Alert_keys_of_different_channels_are_different()
    {
        Assert.NotEqual(new AlertKey("d", "soil_moisture", "r", "soil-1"), new AlertKey("d", "soil_moisture", "r", "soil-2"));
        Assert.Equal(new AlertKey("d", "soil_moisture", "r", "soil-1"), new AlertKey("d", "soil_moisture", "r", "soil-1"));
    }
}

public class AlertsPerChannelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly ThresholdRule[] Rules = [new("soil_moisture", 30, 80, Hysteresis: 5)];

    private static TelemetryReading Soil(string channel, double value, int second = 0) =>
        new("pot", "soil_moisture", value, T0.AddSeconds(second), channel);

    [Fact]
    public void Each_channel_of_a_metric_alerts_on_its_own()
    {
        var tracker = new AlertTracker();

        var batch = tracker.Evaluate([Soil("soil-1", 10), Soil("soil-2", 50), Soil("soil-1", 12, 1)], Rules);

        var alert = Assert.Single(batch.Alerts); // zone 1 is dry, zone 2 is fine, and zone 1 only alerts once
        Assert.Equal("soil-1", alert.ChannelId);
        Assert.Equal("soil_moisture", alert.Metric);
    }

    [Fact]
    public void One_channel_recovering_does_not_resolve_the_other()
    {
        var tracker = new AlertTracker();
        tracker.Evaluate([Soil("soil-1", 10), Soil("soil-2", 10)], Rules).Commit();

        var batch = tracker.Evaluate([Soil("soil-1", 60, 1)], Rules);

        var alert = Assert.Single(batch.Alerts);
        Assert.Equal(AlertState.Resolved, alert.State);
        Assert.Equal("soil-1", alert.ChannelId);
    }

    [Fact]
    public void A_silent_channel_is_reported_with_its_channel()
    {
        var tracker = new SilenceTracker(TimeSpan.FromSeconds(60));

        var batch = tracker.Evaluate([new SilentSeries("pot", "soil_moisture", TimeSpan.FromSeconds(90), "soil-2")], T0);

        var alert = Assert.Single(batch.Alerts);
        Assert.Equal("soil-2", alert.ChannelId);
        Assert.Equal("soil_moisture", alert.Metric);
    }

    [Fact]
    public void Two_silent_channels_of_one_metric_are_two_alerts_and_recover_separately()
    {
        var tracker = new SilenceTracker(TimeSpan.FromSeconds(60));
        var first = tracker.Evaluate(
        [
            new SilentSeries("pot", "soil_moisture", TimeSpan.FromSeconds(90), "soil-1"),
            new SilentSeries("pot", "soil_moisture", TimeSpan.FromSeconds(90), "soil-2"),
        ], T0);
        first.Commit();

        var second = tracker.Evaluate([new SilentSeries("pot", "soil_moisture", TimeSpan.FromSeconds(100), "soil-2")], T0.AddSeconds(10));

        Assert.Equal(2, first.Alerts.Count);
        var resolved = Assert.Single(second.Alerts);
        Assert.Equal(AlertState.Resolved, resolved.State);
        Assert.Equal("soil-1", resolved.ChannelId);
    }
}

public class MetricCatalogTests
{
    [Theory]
    [InlineData("temperature", -55)]
    [InlineData("temperature", 125)]
    [InlineData("ph", 0)]
    [InlineData("ph", 14)]
    [InlineData("soil_moisture", 0)]
    [InlineData("soil_moisture", 100)]
    [InlineData("light", 150_000)]
    [InlineData("overflow", 1)]
    [InlineData("flow", 12.5)]
    [InlineData("airflow", 3)]
    [InlineData("tank_level", 40)]
    [InlineData("air_humidity", 55)]
    public void Plausible_values_pass(string metric, double value) =>
        Assert.Null(MetricCatalog.Validate(metric, value));

    [Theory]
    [InlineData("temperature", -55.1)]
    [InlineData("temperature", 125.1)]
    [InlineData("ph", 14.1)]
    [InlineData("ph", -0.1)]
    [InlineData("soil_moisture", 101)]
    [InlineData("soil_moisture", 4095)] // a raw ADC count where a percentage was expected
    [InlineData("overflow", 2)]
    [InlineData("flow", -1)]
    [InlineData("tank_level", 100.5)]
    public void Implausible_values_are_refused_with_a_message_that_names_the_range(string metric, double value)
    {
        var message = MetricCatalog.Validate(metric, value);

        Assert.NotNull(message);
        Assert.StartsWith(metric, message);
        Assert.Contains("between", message);
    }

    [Theory]
    [InlineData("humidity", 5000)]
    [InlineData("battery", -3)]
    [InlineData("anything", 1e12)]
    public void Metrics_outside_the_catalog_are_not_judged(string metric, double value) =>
        Assert.Null(MetricCatalog.Validate(metric, value));

    [Fact]
    public void The_demo_simulators_fault_values_are_still_accepted()
    {
        // The simulator injects out-of-range readings on purpose so alerts fire. They must not be refused as implausible.
        Assert.Null(MetricCatalog.Validate("temperature", 120));
        Assert.Null(MetricCatalog.Validate("humidity", 99));
        Assert.Null(MetricCatalog.Validate("battery", 3));
    }

    [Fact]
    public void Every_cataloged_metric_has_a_unit_and_a_sane_range()
    {
        foreach (var name in MetricCatalog.Names)
        {
            var spec = MetricCatalog.Find(name)!;
            Assert.True(spec.Min < spec.Max, name);
        }

        Assert.Equal(9, MetricCatalog.Names.Count);
    }
}
