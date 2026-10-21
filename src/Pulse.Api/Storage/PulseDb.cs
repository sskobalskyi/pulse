using Dapper;
using Npgsql;
using NpgsqlTypes;
using Pulse.Core;

namespace Pulse.Api.Storage;

public sealed class PulseDb(NpgsqlDataSource dataSource)
{
    /// <summary>The most points a raw series answers with, so one request cannot ask for millions of rows.</summary>
    public const int MaxRawPoints = 10_000;

    static PulseDb() => SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());

    public async Task InsertReadingsAsync(IReadOnlyList<TelemetryReading> readings, CancellationToken ct)
    {
        // Kafka delivers at least once, so a batch can arrive again after a rebalance or a failed commit.
        // Rows already stored (same device, channel and timestamp) are skipped instead of duplicated.
        // One set-based INSERT from arrays keeps the batch to a single round trip.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO readings (device_id, channel, metric, value, ts)
            SELECT * FROM unnest(@deviceIds, @channels, @metrics, @values, @timestamps)
            ON CONFLICT (device_id, channel, ts) DO NOTHING
            """, conn);

        cmd.Parameters.Add(new NpgsqlParameter<string[]>("deviceIds", readings.Select(r => r.DeviceId).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("channels", readings.Select(r => r.ChannelId).ToArray()));
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
            INSERT INTO alerts (device_id, channel, metric, rule, value, ts, state, kind)
            VALUES (@DeviceId, @Channel, @Metric, @Rule, @Value, @Timestamp, @State, @Kind)
            ON CONFLICT (device_id, channel, rule, ts, state) DO NOTHING
            """,
            alerts.Select(a => new
            {
                a.DeviceId,
                Channel = a.ChannelId,
                a.Metric,
                a.Rule,
                a.Value,
                a.Timestamp,
                State = a.State.ToString(),
                Kind = a.Kind.ToString(),
            }),
            cancellationToken: ct));
    }

    /// <summary>The (device, channel, rule) combinations of one kind whose most recent alert is still Raised.</summary>
    public async Task<IReadOnlyList<AlertKey>> GetActiveAlertKeysAsync(AlertKind kind, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AlertKeyRow>(new CommandDefinition("""
            SELECT device_id AS DeviceId, channel AS Channel, metric AS Metric, rule AS Rule
            FROM (
                SELECT DISTINCT ON (device_id, channel, rule) device_id, channel, metric, rule, state
                FROM alerts
                WHERE kind = @kind
                ORDER BY device_id, channel, rule, ts DESC
            ) latest
            WHERE state = 'Raised'
            """, new { kind = kind.ToString() }, cancellationToken: ct));
        return rows.Select(r => new AlertKey(r.DeviceId, r.Metric, r.Rule, r.Channel)).ToList();
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

    /// <summary>Records that these channels just reported. The time is the database clock at receipt, not the device's.</summary>
    public async Task TouchLastSeenAsync(IReadOnlyList<TelemetryReading> readings, CancellationToken ct)
    {
        var series = readings.Select(r => (r.DeviceId, r.ChannelId, r.Metric)).Distinct().ToArray();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO device_last_seen (device_id, channel, metric, last_seen_at)
            SELECT s.device_id, s.channel, s.metric, now() FROM unnest(@deviceIds, @channels, @metrics) AS s(device_id, channel, metric)
            -- A retired device must not come back to life as "silent forever" because old messages were still in Kafka.
            WHERE NOT EXISTS (SELECT 1 FROM devices d WHERE d.id = s.device_id AND d.state = 'retired')
            ON CONFLICT (device_id, channel) DO UPDATE SET last_seen_at = EXCLUDED.last_seen_at, metric = EXCLUDED.metric
            """, conn);

        cmd.Parameters.Add(new NpgsqlParameter<string[]>("deviceIds", series.Select(s => s.DeviceId).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("channels", series.Select(s => s.ChannelId).ToArray()));
        cmd.Parameters.Add(new NpgsqlParameter<string[]>("metrics", series.Select(s => s.Metric).ToArray()));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Channels whose last reading arrived longer ago than <paramref name="timeout"/>.</summary>
    public async Task<IReadOnlyList<SilentSeries>> GetSilentSeriesAsync(TimeSpan timeout, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SilentRow>(new CommandDefinition("""
            SELECT device_id AS DeviceId, channel AS Channel, metric AS Metric, extract(epoch FROM now() - last_seen_at) AS SilentSeconds
            FROM device_last_seen
            WHERE last_seen_at < now() - @timeout
            """, new { timeout }, cancellationToken: ct));
        return rows.Select(r => new SilentSeries(r.DeviceId, r.Metric, TimeSpan.FromSeconds(r.SilentSeconds), r.Channel)).ToList();
    }

    /// <summary>
    /// Every channel that has ever reported, with its latest stored reading and whether it has gone quiet.
    /// Driven by <c>device_last_seen</c> rather than recent readings, so a silent device stays visible
    /// instead of vanishing from the list exactly when it matters.
    /// </summary>
    /// <param name="ownerScope">Only channels of devices registered to this owner. Null means all, including devices that were never registered.</param>
    public async Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(TimeSpan silentAfter, string? ownerScope, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DeviceRow>(new CommandDefinition("""
            SELECT s.device_id AS DeviceId,
                   s.channel AS Channel,
                   s.metric AS Metric,
                   r.value AS Value,
                   r.ts AS Timestamp,
                   s.last_seen_at AS LastSeenAt,
                   s.last_seen_at < now() - @silentAfter AS Silent
            FROM device_last_seen s
            LEFT JOIN LATERAL (
                SELECT value, ts FROM readings
                WHERE device_id = s.device_id AND channel = s.channel
                ORDER BY ts DESC
                LIMIT 1
            ) r ON true
            WHERE @owner::text IS NULL
               OR EXISTS (SELECT 1 FROM devices d WHERE d.id = s.device_id AND d.owner_id = @owner)
            ORDER BY s.device_id, s.channel
            """, new { silentAfter, owner = ownerScope }, cancellationToken: ct));

        // Value and Timestamp are null when the readings have aged out of retention but the channel is still known.
        return rows.Select(r => new DeviceSummary(r.DeviceId, r.Metric, r.Value, r.Timestamp, r.LastSeenAt, r.Silent, r.Channel)).ToList();
    }

    /// <summary>
    /// The recent history of one channel for the dashboard chart. A reading that names no channel is on the channel
    /// called after its metric, so the dashboard can keep asking for a series by metric.
    /// </summary>
    public async Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(string deviceId, string channelId, TimeSpan window, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // Short windows read raw data; longer ones read the 1-minute continuous aggregate.
        var sql = window <= TimeSpan.FromMinutes(15)
            ? """
              SELECT ts AS Timestamp, value AS Avg, value AS Min, value AS Max
              FROM readings
              WHERE device_id = @deviceId AND channel = @channelId AND ts > now() - @window
              ORDER BY ts
              """
            : """
              SELECT bucket AS Timestamp, avg_value AS Avg, min_value AS Min, max_value AS Max
              FROM readings_1m
              WHERE device_id = @deviceId AND channel = @channelId AND bucket > now() - @window
              ORDER BY bucket
              """;
        var rows = await conn.QueryAsync<SeriesPoint>(new CommandDefinition(sql, new { deviceId, channelId, window }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Points of one channel between two instants, in ascending time. <see cref="SeriesResolution.Raw"/> returns the readings
    /// themselves (at most <see cref="MaxRawPoints"/>, the latest ones if there are more). The coarser resolutions return
    /// averages with the minimum and maximum of each bucket, read from the 1-minute aggregate.
    /// </summary>
    public async Task<IReadOnlyList<SeriesPoint>> GetChannelSeriesAsync(
        string deviceId, string channelId, DateTimeOffset from, DateTimeOffset to, SeriesResolution resolution, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var sql = resolution switch
        {
            SeriesResolution.Raw => """
                SELECT Timestamp, Avg, Min, Max FROM (
                    SELECT ts AS Timestamp, value AS Avg, value AS Min, value AS Max
                    FROM readings
                    WHERE device_id = @deviceId AND channel = @channelId AND ts >= @from AND ts < @to
                    ORDER BY ts DESC
                    LIMIT @maxRaw
                ) latest ORDER BY Timestamp
                """,
            SeriesResolution.OneMinute => """
                SELECT bucket AS Timestamp, avg_value AS Avg, min_value AS Min, max_value AS Max
                FROM readings_1m
                WHERE device_id = @deviceId AND channel = @channelId AND bucket >= @from AND bucket < @to
                ORDER BY bucket
                """,
            // An average of averages would be wrong when buckets hold different numbers of readings, so weight by count.
            SeriesResolution.OneHour => """
                SELECT time_bucket(INTERVAL '1 hour', bucket) AS Timestamp,
                       sum(avg_value * samples) / sum(samples) AS Avg,
                       min(min_value) AS Min,
                       max(max_value) AS Max
                FROM readings_1m
                WHERE device_id = @deviceId AND channel = @channelId AND bucket >= @from AND bucket < @to
                GROUP BY 1
                ORDER BY 1
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
        };

        var rows = await conn.QueryAsync<SeriesPoint>(new CommandDefinition(
            sql, new { deviceId, channelId, from = from.UtcDateTime, to = to.UtcDateTime, maxRaw = MaxRawPoints }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Alert>> GetAlertsAsync(int limit, string? ownerScope, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<AlertRow>(new CommandDefinition("""
            SELECT device_id AS DeviceId, channel AS Channel, metric AS Metric, rule AS Rule, value AS Value, ts AS Timestamp, state AS State, kind AS Kind
            FROM alerts
            WHERE @owner::text IS NULL
               OR EXISTS (SELECT 1 FROM devices d WHERE d.id = alerts.device_id AND d.owner_id = @owner)
            ORDER BY ts DESC LIMIT @limit
            """, new { limit, owner = ownerScope }, cancellationToken: ct));
        return rows.Select(r => new Alert(
            r.DeviceId, r.Metric, r.Rule, r.Value, r.Timestamp, Enum.Parse<AlertState>(r.State), Enum.Parse<AlertKind>(r.Kind), r.Channel)).ToList();
    }

    // Dapper binds these by property name; the public contracts stay immutable records.
    private sealed class AlertRow
    {
        public string DeviceId { get; set; } = "";
        public string Channel { get; set; } = "";
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
        public string Channel { get; set; } = "";
        public string Metric { get; set; } = "";
        public double SilentSeconds { get; set; }
    }

    private sealed class DeviceRow
    {
        public string DeviceId { get; set; } = "";
        public string Channel { get; set; } = "";
        public string Metric { get; set; } = "";
        public double? Value { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public bool Silent { get; set; }
    }

    private sealed class AlertKeyRow
    {
        public string DeviceId { get; set; } = "";
        public string Channel { get; set; } = "";
        public string Metric { get; set; } = "";
        public string Rule { get; set; } = "";
    }
}

public enum SeriesResolution
{
    Raw,
    OneMinute,
    OneHour,
}

/// <param name="Metric">What is measured. The channel says which sensor measured it.</param>
/// <param name="Value">Latest stored reading, or null if it has aged out of retention.</param>
/// <param name="Timestamp">Device time of that reading.</param>
/// <param name="LastSeenAt">When the server last received anything for this channel.</param>
/// <param name="Silent">True when nothing has arrived for longer than the no-data timeout.</param>
/// <param name="Channel">The sensor channel. For senders that name none, the same as the metric.</param>
public sealed record DeviceSummary(
    string DeviceId, string Metric, double? Value, DateTimeOffset? Timestamp, DateTimeOffset LastSeenAt, bool Silent, string? Channel = null);

public sealed record SeriesPoint(DateTimeOffset Timestamp, double Avg, double Min, double Max);
