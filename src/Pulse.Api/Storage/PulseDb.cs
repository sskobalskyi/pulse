using Dapper;
using Npgsql;
using NpgsqlTypes;
using Pulse.Core;

namespace Pulse.Api.Storage;

public sealed class PulseDb(NpgsqlDataSource dataSource)
{
    static PulseDb() => SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());

    public async Task InsertReadingsAsync(IReadOnlyList<TelemetryReading> readings, CancellationToken ct)
    {
        // Kafka delivers at least once, so a batch can arrive again after a rebalance or a failed commit.
        // Rows already stored (same device, metric and timestamp) are skipped instead of duplicated.
        // One set-based INSERT from arrays keeps the batch to a single round trip.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO readings (device_id, metric, value, ts)
            SELECT * FROM unnest(@deviceIds, @metrics, @values, @timestamps)
            ON CONFLICT (device_id, metric, ts) DO NOTHING
            """, conn);

        cmd.Parameters.Add(new NpgsqlParameter<string[]>("deviceIds", readings.Select(r => r.DeviceId).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("metrics", readings.Select(r => r.Metric).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<double[]>("values", readings.Select(r => r.Value).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<DateTime[]>("timestamps", readings.Select(r => r.Timestamp.UtcDateTime).ToArray())
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
        });

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertAlertsAsync(IReadOnlyList<Alert> alerts, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO alerts (device_id, metric, rule, value, ts, state, kind)
            VALUES (@DeviceId, @Metric, @Rule, @Value, @Timestamp, @State, @Kind)
            ON CONFLICT (device_id, metric, rule, ts, state) DO NOTHING
            """,
            alerts.Select(a => new { a.DeviceId, a.Metric, a.Rule, a.Value, a.Timestamp, State = a.State.ToString(), Kind = a.Kind.ToString() }),
            cancellationToken: ct));
    }

    /// <summary>The (device, metric, rule) combinations of one kind whose most recent alert is still Raised.</summary>
    public async Task<IReadOnlyList<AlertKey>> GetActiveAlertKeysAsync(AlertKind kind, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AlertKeyRow>(new CommandDefinition("""
            SELECT device_id AS DeviceId, metric AS Metric, rule AS Rule
            FROM (
                SELECT DISTINCT ON (device_id, metric, rule) device_id, metric, rule, state
                FROM alerts
                WHERE kind = @kind
                ORDER BY device_id, metric, rule, ts DESC
            ) latest
            WHERE state = 'Raised'
            """, new { kind = kind.ToString() }, cancellationToken: ct));
        return rows.Select(r => new AlertKey(r.DeviceId, r.Metric, r.Rule)).ToList();
    }

    /// <summary>
    /// Runs <paramref name="work"/> only if no other instance holds the advisory lock <paramref name="lockKey"/>.
    /// The lock belongs to a database session, so it is released if this process dies, and the connection is
    /// reset when it goes back to the pool even if the explicit unlock were skipped.
    /// Returns false, without running anything, when someone else holds it.
    /// </summary>
    public async Task<bool> TryRunExclusiveAsync(long lockKey, Func<Task> work, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var acquired = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_lock(@lockKey)", new { lockKey }, cancellationToken: ct));
        if (!acquired) return false;

        try
        {
            await work();
            return true;
        }
        finally
        {
            // Not cancellable: the lock must not outlive the work because the caller was shutting down.
            await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_advisory_unlock(@lockKey)", new { lockKey }));
        }
    }

    /// <summary>Records that these series just reported. The time is the database clock at receipt, not the device's.</summary>
    public async Task TouchLastSeenAsync(IReadOnlyList<TelemetryReading> readings, CancellationToken ct)
    {
        var series = readings.Select(r => (r.DeviceId, r.Metric)).Distinct().ToArray();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO device_last_seen (device_id, metric, last_seen_at)
            SELECT s.device_id, s.metric, now() FROM unnest(@deviceIds, @metrics) AS s(device_id, metric)
            -- A retired device must not come back to life as "silent forever" because old messages were still in Kafka.
            WHERE NOT EXISTS (SELECT 1 FROM devices d WHERE d.id = s.device_id AND d.state = 'retired')
            ON CONFLICT (device_id, metric) DO UPDATE SET last_seen_at = EXCLUDED.last_seen_at
            """, conn);

        cmd.Parameters.Add(new NpgsqlParameter<string[]>("deviceIds", series.Select(s => s.DeviceId).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("metrics", series.Select(s => s.Metric).ToArray()));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Series whose last reading arrived longer ago than <paramref name="timeout"/>.</summary>
    public async Task<IReadOnlyList<SilentSeries>> GetSilentSeriesAsync(TimeSpan timeout, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SilentRow>(new CommandDefinition("""
            SELECT device_id AS DeviceId, metric AS Metric, extract(epoch FROM now() - last_seen_at) AS SilentSeconds
            FROM device_last_seen
            WHERE last_seen_at < now() - @timeout
            """, new { timeout }, cancellationToken: ct));
        return rows.Select(r => new SilentSeries(r.DeviceId, r.Metric, TimeSpan.FromSeconds(r.SilentSeconds))).ToList();
    }

    /// <summary>
    /// Every series that has ever reported, with its latest stored reading and whether it has gone quiet.
    /// Driven by <c>device_last_seen</c> rather than recent readings, so a silent device stays visible
    /// instead of vanishing from the list exactly when it matters.
    /// </summary>
    public async Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(TimeSpan silentAfter, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DeviceRow>(new CommandDefinition("""
            SELECT s.device_id AS DeviceId,
                   s.metric AS Metric,
                   r.value AS Value,
                   r.ts AS Timestamp,
                   s.last_seen_at AS LastSeenAt,
                   s.last_seen_at < now() - @silentAfter AS Silent
            FROM device_last_seen s
            LEFT JOIN LATERAL (
                SELECT value, ts FROM readings
                WHERE device_id = s.device_id AND metric = s.metric
                ORDER BY ts DESC
                LIMIT 1
            ) r ON true
            ORDER BY s.device_id, s.metric
            """, new { silentAfter }, cancellationToken: ct));

        // Value and Timestamp are null when the readings have aged out of retention but the series is still known.
        return rows.Select(r => new DeviceSummary(r.DeviceId, r.Metric, r.Value, r.Timestamp, r.LastSeenAt, r.Silent)).ToList();
    }

    public async Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(string deviceId, string metric, TimeSpan window, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // Short windows read raw data; longer ones read the 1-minute continuous aggregate.
        var sql = window <= TimeSpan.FromMinutes(15)
            ? """
              SELECT ts AS Timestamp, value AS Avg, value AS Min, value AS Max
              FROM readings
              WHERE device_id = @deviceId AND metric = @metric AND ts > now() - @window
              ORDER BY ts
              """
            : """
              SELECT bucket AS Timestamp, avg_value AS Avg, min_value AS Min, max_value AS Max
              FROM readings_1m
              WHERE device_id = @deviceId AND metric = @metric AND bucket > now() - @window
              ORDER BY bucket
              """;
        var rows = await conn.QueryAsync<SeriesPoint>(new CommandDefinition(sql, new { deviceId, metric, window }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Alert>> GetAlertsAsync(int limit, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AlertRow>(new CommandDefinition("""
            SELECT device_id AS DeviceId, metric AS Metric, rule AS Rule, value AS Value, ts AS Timestamp, state AS State, kind AS Kind
            FROM alerts ORDER BY ts DESC LIMIT @limit
            """, new { limit }, cancellationToken: ct));
        return rows.Select(r => new Alert(
            r.DeviceId, r.Metric, r.Rule, r.Value, r.Timestamp, Enum.Parse<AlertState>(r.State), Enum.Parse<AlertKind>(r.Kind))).ToList();
    }

    // Dapper binds these by property name; the public contracts stay immutable records.
    private sealed class AlertRow
    {
        public string DeviceId { get; set; } = "";
        public string Metric { get; set; } = "";
        public string Rule { get; set; } = "";
        public double Value { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public string State { get; set; } = "";
        public string Kind { get; set; } = "";
    }

    private sealed class SilentRow
    {
        public string DeviceId { get; set; } = "";
        public string Metric { get; set; } = "";
        public double SilentSeconds { get; set; }
    }

    private sealed class DeviceRow
    {
        public string DeviceId { get; set; } = "";
        public string Metric { get; set; } = "";
        public double? Value { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public bool Silent { get; set; }
    }

    private sealed class AlertKeyRow
    {
        public string DeviceId { get; set; } = "";
        public string Metric { get; set; } = "";
        public string Rule { get; set; } = "";
    }
}

/// <param name="Value">Latest stored reading, or null if it has aged out of retention.</param>
/// <param name="Timestamp">Device time of that reading.</param>
/// <param name="LastSeenAt">When the server last received anything for this series.</param>
/// <param name="Silent">True when nothing has arrived for longer than the no-data timeout.</param>
public sealed record DeviceSummary(string DeviceId, string Metric, double? Value, DateTimeOffset? Timestamp, DateTimeOffset LastSeenAt, bool Silent);

public sealed record SeriesPoint(DateTimeOffset Timestamp, double Avg, double Min, double Max);
