using Npgsql;
using Pulse.Api.Storage;
using Pulse.Core;
using Testcontainers.PostgreSql;

namespace Pulse.Tests;

/// <summary>Series keyed by device and channel, on a real TimescaleDB with the real migrations.</summary>
public sealed class ChannelStorageTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb:2.17.2-pg16")
        .WithDatabase("pulse")
        .WithUsername("pulse")
        .WithPassword("pulse")
        .Build();

    private NpgsqlDataSource _dataSource = null!;
    private PulseDb _db = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await TestSchema.ApplyAsync(_dataSource);
        _db = new PulseDb(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static TelemetryReading Soil(string channel, double value, int second = 0, string device = "pot") =>
        new(device, "soil_moisture", value, T0.AddSeconds(second), channel);

    private async Task<long> Count(string sql)
    {
        await using var cmd = _dataSource.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task Aggregate()
    {
        // The aggregate is filled by a background job. A test refreshes it by hand instead of waiting.
        await using var cmd = _dataSource.CreateCommand("CALL refresh_continuous_aggregate('readings_1m', NULL, NULL)");
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Two_channels_of_one_metric_at_the_same_instant_are_both_stored()
    {
        await _db.InsertReadingsAsync([Soil("soil-1", 40), Soil("soil-2", 55)], default);

        Assert.Equal(2, await Count("SELECT count(*) FROM readings"));
    }

    [Fact]
    public async Task A_redelivered_reading_is_still_stored_once_per_channel()
    {
        var batch = new[] { Soil("soil-1", 40), Soil("soil-2", 55) };

        await _db.InsertReadingsAsync(batch, default);
        await _db.InsertReadingsAsync(batch, default);

        Assert.Equal(2, await Count("SELECT count(*) FROM readings"));
    }

    [Fact]
    public async Task A_reading_with_no_channel_is_stored_on_the_channel_named_after_its_metric()
    {
        await _db.InsertReadingsAsync([new TelemetryReading("demo", "temperature", 21.5, T0)], default);

        Assert.Equal(1, await Count("SELECT count(*) FROM readings WHERE device_id = 'demo' AND channel = 'temperature' AND metric = 'temperature'"));
    }

    [Fact]
    public async Task The_device_list_shows_each_channel_with_its_own_latest_value_and_its_metric()
    {
        var readings = new[] { Soil("soil-1", 40), Soil("soil-1", 41, 5), Soil("soil-2", 55) };
        await _db.InsertReadingsAsync(readings, default);
        await _db.TouchLastSeenAsync(readings, default);

        var devices = await _db.GetDevicesAsync(TimeSpan.FromMinutes(1), null, default);

        Assert.Equal(
            [("soil-1", "soil_moisture", 41.0), ("soil-2", "soil_moisture", 55.0)],
            devices.Select(d => (d.Channel!, d.Metric, d.Value!.Value)));
    }

    [Fact]
    public async Task Last_seen_is_tracked_per_channel_so_one_silent_sensor_is_noticed()
    {
        await _db.TouchLastSeenAsync([Soil("soil-1", 40), Soil("soil-2", 55)], default);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await _db.TouchLastSeenAsync([Soil("soil-2", 56)], default); // only zone 2 keeps reporting

        var silent = Assert.Single(await _db.GetSilentSeriesAsync(TimeSpan.FromSeconds(1), default));

        Assert.Equal("soil-1", silent.Channel);
        Assert.Equal("soil_moisture", silent.Metric);
    }

    [Fact]
    public async Task Alerts_are_stored_and_restored_per_channel()
    {
        await _db.InsertAlertsAsync(
        [
            new Alert("pot", "soil_moisture", "dry", 10, T0, AlertState.Raised, AlertKind.Threshold, "soil-1"),
            new Alert("pot", "soil_moisture", "dry", 12, T0, AlertState.Raised, AlertKind.Threshold, "soil-2"), // same instant, other channel
            new Alert("pot", "soil_moisture", "dry", 60, T0.AddMinutes(1), AlertState.Resolved, AlertKind.Threshold, "soil-1"),
        ], default);

        var alerts = await _db.GetAlertsAsync(10, null, default);
        var active = await _db.GetActiveAlertKeysAsync(AlertKind.Threshold, default);

        Assert.Equivalent(
            new[] { ("soil-1", AlertState.Raised), ("soil-2", AlertState.Raised), ("soil-1", AlertState.Resolved) },
            alerts.Select(a => (a.ChannelId, a.State)).ToArray());
        Assert.Equal([new AlertKey("pot", "soil_moisture", "dry", "soil-2")], active); // zone 1 resolved, zone 2 still raised
    }

    [Fact]
    public async Task Raw_series_has_the_readings_of_one_channel_in_ascending_order_within_the_window()
    {
        await _db.InsertReadingsAsync(
            [Soil("soil-1", 30, 20), Soil("soil-1", 10, 0), Soil("soil-1", 20, 10), Soil("soil-2", 99, 5), Soil("soil-1", 99, 30)], default);

        var points = await _db.GetChannelSeriesAsync("pot", "soil-1", T0, T0.AddSeconds(30), SeriesResolution.Raw, default);

        Assert.Equal([10.0, 20.0, 30.0], points.Select(p => p.Avg)); // the other channel and the reading at 'to' are out
        Assert.Equal([T0, T0.AddSeconds(10), T0.AddSeconds(20)], points.Select(p => p.Timestamp));
    }

    [Fact]
    public async Task A_raw_series_that_is_too_long_keeps_the_latest_points_in_order()
    {
        var many = Enumerable.Range(0, PulseDb.MaxRawPoints + 5).Select(i => Soil("soil-1", i, i)).ToList();
        await _db.InsertReadingsAsync(many, default);

        var points = await _db.GetChannelSeriesAsync("pot", "soil-1", T0, T0.AddHours(6), SeriesResolution.Raw, default);

        Assert.Equal(PulseDb.MaxRawPoints, points.Count);
        Assert.Equal(5, points[0].Avg); // the five oldest were left out
        Assert.Equal(PulseDb.MaxRawPoints + 4, points[^1].Avg);
        Assert.Equal(points.OrderBy(p => p.Timestamp).Select(p => p.Avg), points.Select(p => p.Avg));
    }

    [Fact]
    public async Task One_minute_series_has_average_minimum_and_maximum_of_each_minute()
    {
        await _db.InsertReadingsAsync(
            [Soil("soil-1", 10, 0), Soil("soil-1", 30, 20), Soil("soil-1", 50, 70), Soil("soil-2", 99, 10)], default);
        await Aggregate();

        var points = await _db.GetChannelSeriesAsync("pot", "soil-1", T0, T0.AddMinutes(10), SeriesResolution.OneMinute, default);

        Assert.Equal(2, points.Count);
        Assert.Equal((T0, 20.0, 10.0, 30.0), (points[0].Timestamp, points[0].Avg, points[0].Min, points[0].Max));
        Assert.Equal((T0.AddMinutes(1), 50.0, 50.0, 50.0), (points[1].Timestamp, points[1].Avg, points[1].Min, points[1].Max));
    }

    [Fact]
    public async Task Hourly_series_weights_each_minute_by_how_many_readings_it_holds()
    {
        // Minute one has two readings averaging 10. Minute two has one reading of 40.
        // A plain average of the two averages would say 25. The true average of the three readings is 20.
        await _db.InsertReadingsAsync([Soil("soil-1", 8, 0), Soil("soil-1", 12, 10), Soil("soil-1", 40, 65)], default);
        await Aggregate();

        var hour = Assert.Single(await _db.GetChannelSeriesAsync("pot", "soil-1", T0, T0.AddHours(2), SeriesResolution.OneHour, default));

        Assert.Equal(T0, hour.Timestamp);
        Assert.Equal(20.0, hour.Avg, 6);
        Assert.Equal(8.0, hour.Min);
        Assert.Equal(40.0, hour.Max);
    }

    [Fact]
    public async Task The_dashboards_series_query_still_finds_a_reading_by_its_metric_when_no_channel_was_named()
    {
        await _db.InsertReadingsAsync([new TelemetryReading("demo", "temperature", 21.5, DateTimeOffset.UtcNow)], default);

        var points = await _db.GetSeriesAsync("demo", "temperature", TimeSpan.FromMinutes(5), default);

        Assert.Equal([21.5], points.Select(p => p.Avg));
    }
}
