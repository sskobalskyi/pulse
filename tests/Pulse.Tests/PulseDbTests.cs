using Npgsql;
using Pulse.Api.Storage;
using Pulse.Core;
using Testcontainers.PostgreSql;

namespace Pulse.Tests;

/// <summary>Runs PulseDb against a real TimescaleDB container initialised from the same schema script the compose stack uses.</summary>
public sealed class PulseDbTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public async Task Redelivered_readings_are_stored_once()
    {
        var batch = new[]
        {
            new TelemetryReading("dev-1", "temperature", 21.5, T0),
            new TelemetryReading("dev-1", "humidity", 40, T0),
            new TelemetryReading("dev-2", "temperature", 19, T0),
        };

        await _db.InsertReadingsAsync(batch, default);
        await _db.InsertReadingsAsync(batch, default); // what a redelivery after a rebalance looks like

        Assert.Equal(3, await Count("readings"));
    }

    [Fact]
    public async Task Duplicates_inside_one_batch_are_stored_once()
    {
        var reading = new TelemetryReading("dev-1", "temperature", 21.5, T0);

        await _db.InsertReadingsAsync([reading, reading], default);

        Assert.Equal(1, await Count("readings"));
    }

    [Fact]
    public async Task New_readings_alongside_old_ones_are_still_inserted()
    {
        await _db.InsertReadingsAsync([new TelemetryReading("dev-1", "temperature", 21.5, T0)], default);

        await _db.InsertReadingsAsync(
        [
            new TelemetryReading("dev-1", "temperature", 21.5, T0),
            new TelemetryReading("dev-1", "temperature", 22.0, T0.AddSeconds(1)),
        ], default);

        Assert.Equal(2, await Count("readings"));
    }

    [Fact]
    public async Task Redelivered_alerts_are_stored_once()
    {
        var alerts = new[] { new Alert("dev-1", "temperature", "temperature outside [-10, 80]", 95, T0) };

        await _db.InsertAlertsAsync(alerts, default);
        await _db.InsertAlertsAsync(alerts, default);

        Assert.Equal(1, await Count("alerts"));
    }

    [Fact]
    public async Task Raised_and_resolved_alerts_round_trip_with_their_state()
    {
        await _db.InsertAlertsAsync(
        [
            new Alert("dev-1", "temperature", "r", 95, T0, AlertState.Raised),
            new Alert("dev-1", "temperature", "r", 70, T0.AddMinutes(5), AlertState.Resolved),
        ], default);

        var alerts = await _db.GetAlertsAsync(10, null, default);

        Assert.Equal([AlertState.Resolved, AlertState.Raised], alerts.Select(a => a.State)); // newest first
        Assert.Equal(T0.AddMinutes(5), alerts[0].Timestamp);
    }

    [Fact]
    public async Task Active_alert_keys_are_those_whose_latest_alert_is_raised()
    {
        await _db.InsertAlertsAsync(
        [
            new Alert("dev-1", "temperature", "r", 95, T0, AlertState.Raised),
            new Alert("dev-1", "temperature", "r", 70, T0.AddMinutes(1), AlertState.Resolved), // resolved again
            new Alert("dev-2", "temperature", "r", 95, T0, AlertState.Raised),                  // still raised
            new Alert("dev-3", "humidity", "r", 99, T0, AlertState.Raised),
            new Alert("dev-3", "humidity", "r", 50, T0.AddMinutes(1), AlertState.Resolved),
            new Alert("dev-3", "humidity", "r", 99, T0.AddMinutes(2), AlertState.Raised),       // raised again
        ], default);

        var active = await _db.GetActiveAlertKeysAsync(AlertKind.Threshold, default);

        Assert.Equivalent(
            new[] { new AlertKey("dev-2", "temperature", "r"), new AlertKey("dev-3", "humidity", "r") },
            active);
    }

    [Fact]
    public async Task Active_alert_keys_are_kept_apart_by_kind()
    {
        await _db.InsertAlertsAsync(
        [
            new Alert("dev-1", "temperature", "temperature outside [-10, 80]", 95, T0, AlertState.Raised, AlertKind.Threshold),
            new Alert("dev-2", "temperature", "no data for 60s", 75, T0, AlertState.Raised, AlertKind.NoData),
        ], default);

        Assert.Equivalent(
            new[] { new AlertKey("dev-1", "temperature", "temperature outside [-10, 80]") },
            await _db.GetActiveAlertKeysAsync(AlertKind.Threshold, default));
        Assert.Equivalent(
            new[] { new AlertKey("dev-2", "temperature", "no data for 60s") },
            await _db.GetActiveAlertKeysAsync(AlertKind.NoData, default));
    }

    [Fact]
    public async Task Alert_kind_round_trips()
    {
        await _db.InsertAlertsAsync([new Alert("dev-1", "temperature", "no data for 60s", 75.5, T0, AlertState.Raised, AlertKind.NoData)], default);

        var alert = Assert.Single(await _db.GetAlertsAsync(10, null, default));

        Assert.Equal(AlertKind.NoData, alert.Kind);
        Assert.Equal(75.5, alert.Value);
    }

    [Fact]
    public async Task Only_series_that_stopped_reporting_are_silent()
    {
        await _db.TouchLastSeenAsync([new TelemetryReading("dev-old", "temperature", 20, T0)], default);
        await DbClock.DelayAsync(_dataSource, TimeSpan.FromSeconds(1.5));
        await _db.TouchLastSeenAsync(
        [
            new TelemetryReading("dev-new", "temperature", 20, T0),
            new TelemetryReading("dev-new", "temperature", 21, T0.AddSeconds(1)), // same series twice in one batch
            new TelemetryReading("dev-new", "humidity", 50, T0),
        ], default);

        var silent = await _db.GetSilentSeriesAsync(TimeSpan.FromSeconds(1), default);

        var series = Assert.Single(silent);
        Assert.Equal(("dev-old", "temperature"), (series.DeviceId, series.Metric));
        Assert.InRange(series.SilentFor.TotalSeconds, 1.4, 5);
    }

    [Fact]
    public async Task Reporting_again_clears_the_silence_and_keeps_one_row_per_series()
    {
        var reading = new TelemetryReading("dev-1", "temperature", 20, T0);
        await _db.TouchLastSeenAsync([reading], default);
        await DbClock.DelayAsync(_dataSource, TimeSpan.FromSeconds(1.5));
        Assert.Single(await _db.GetSilentSeriesAsync(TimeSpan.FromSeconds(1), default));

        await _db.TouchLastSeenAsync([reading], default);

        Assert.Empty(await _db.GetSilentSeriesAsync(TimeSpan.FromSeconds(1), default));
        Assert.Equal(1, await Count("device_last_seen"));
    }

    [Fact]
    public async Task Last_seen_uses_the_server_clock_not_the_device_timestamp()
    {
        // A device with a clock stuck in 2001 must not look silent while it is sending.
        await _db.TouchLastSeenAsync([new TelemetryReading("dev-1", "temperature", 20, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero))], default);

        Assert.Empty(await _db.GetSilentSeriesAsync(TimeSpan.FromSeconds(30), default));
    }

    [Fact]
    public async Task Devices_list_shows_the_latest_reading_of_every_series_that_has_reported()
    {
        var now = DateTimeOffset.UtcNow;
        var readings = new[]
        {
            new TelemetryReading("dev-1", "temperature", 21.5, now.AddSeconds(-5)),
            new TelemetryReading("dev-1", "temperature", 22.0, now.AddSeconds(-1)),
            new TelemetryReading("dev-1", "humidity", 40, now),
            new TelemetryReading("dev-2", "temperature", 19, now),
        };
        await _db.InsertReadingsAsync(readings, default);
        await _db.TouchLastSeenAsync(readings, default);

        var devices = await _db.GetDevicesAsync(TimeSpan.FromSeconds(30), null, default);

        Assert.Equal(
            [("dev-1", "humidity", 40.0), ("dev-1", "temperature", 22.0), ("dev-2", "temperature", 19.0)],
            devices.Select(d => (d.DeviceId, d.Metric, d.Value!.Value)));
        Assert.All(devices, d => Assert.False(d.Silent));
    }

    [Fact]
    public async Task A_silent_series_stays_in_the_devices_list_and_is_flagged()
    {
        var reading = new TelemetryReading("dev-1", "temperature", 20, DateTimeOffset.UtcNow);
        await _db.InsertReadingsAsync([reading], default);
        await _db.TouchLastSeenAsync([reading], default);
        await DbClock.DelayAsync(_dataSource, TimeSpan.FromSeconds(1.5));

        var device = Assert.Single(await _db.GetDevicesAsync(TimeSpan.FromSeconds(1), null, default));

        Assert.True(device.Silent);
        Assert.Equal(20, device.Value); // its last known value is still shown
        Assert.True(await DbClock.NowAsync(_dataSource) - device.LastSeenAt > TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_series_whose_readings_are_gone_is_still_listed_without_a_value()
    {
        // Readings age out after the retention period while the series is still known.
        await _db.TouchLastSeenAsync([new TelemetryReading("dev-1", "temperature", 20, DateTimeOffset.UtcNow)], default);

        var device = Assert.Single(await _db.GetDevicesAsync(TimeSpan.FromSeconds(30), null, default));

        Assert.Null(device.Value);
        Assert.Null(device.Timestamp);
        Assert.False(device.Silent);
    }

    [Fact]
    public async Task Only_one_caller_at_a_time_can_hold_an_exclusive_lock()
    {
        var innerRan = true;
        var outerRan = await _db.TryRunExclusiveAsync(42, async () =>
        {
            innerRan = await _db.TryRunExclusiveAsync(42, () => Task.CompletedTask, default);
            Assert.True(await _db.TryRunExclusiveAsync(43, () => Task.CompletedTask, default)); // another key is independent
        }, default);

        Assert.True(outerRan);
        Assert.False(innerRan);
    }

    [Fact]
    public async Task The_exclusive_lock_is_released_after_the_work_even_when_it_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _db.TryRunExclusiveAsync(42, () => throw new InvalidOperationException("boom"), default));

        Assert.True(await _db.TryRunExclusiveAsync(42, () => Task.CompletedTask, default));
    }

    private async Task<long> Count(string table)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
