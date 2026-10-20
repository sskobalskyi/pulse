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

        var alerts = await _db.GetAlertsAsync(10, default);

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

        var alert = Assert.Single(await _db.GetAlertsAsync(10, default));

        Assert.Equal(AlertKind.NoData, alert.Kind);
        Assert.Equal(75.5, alert.Value);
    }

    [Fact]
    public async Task Only_series_that_stopped_reporting_are_silent()
    {
        await _db.TouchLastSeenAsync([new TelemetryReading("dev-old", "temperature", 20, T0)], default);
        await Task.Delay(1500);
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
        await Task.Delay(1500);
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

    private async Task<long> Count(string table)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
