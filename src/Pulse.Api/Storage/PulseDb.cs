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
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY readings (device_id, metric, value, ts) FROM STDIN (FORMAT BINARY)", ct);

        foreach (var r in readings)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(r.DeviceId, ct);
            await writer.WriteAsync(r.Metric, ct);
            await writer.WriteAsync(r.Value, ct);
            await writer.WriteAsync(r.Timestamp.UtcDateTime, NpgsqlDbType.TimestampTz, ct);
        }

        await writer.CompleteAsync(ct);
    }

    public async Task InsertAlertsAsync(IReadOnlyList<Alert> alerts, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO alerts (device_id, metric, rule, value, ts) VALUES (@DeviceId, @Metric, @Rule, @Value, @Timestamp)",
            alerts, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DeviceSummary>> GetDevicesAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DeviceSummary>(new CommandDefinition("""
            SELECT DISTINCT ON (device_id, metric)
                   device_id AS DeviceId, metric AS Metric, value AS Value, ts AS Timestamp
            FROM readings
            WHERE ts > now() - INTERVAL '10 minutes'
            ORDER BY device_id, metric, ts DESC
            """, cancellationToken: ct));
        return rows.ToList();
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
        var rows = await conn.QueryAsync<Alert>(new CommandDefinition("""
            SELECT device_id AS DeviceId, metric AS Metric, rule AS Rule, value AS Value, ts AS Timestamp
            FROM alerts ORDER BY ts DESC LIMIT @limit
            """, new { limit }, cancellationToken: ct));
        return rows.ToList();
    }
}

public sealed record DeviceSummary(string DeviceId, string Metric, double Value, DateTimeOffset Timestamp);

public sealed record SeriesPoint(DateTimeOffset Timestamp, double Avg, double Min, double Max);
