using Npgsql;
using Pulse.Core;

namespace Pulse.Api.Registry;

/// <summary>Where the alert rules of devices are kept. What a rule means is decided in <c>Pulse.Core</c>, this only stores it.</summary>
public sealed class DeviceRuleStore(NpgsqlDataSource dataSource, TimeProvider time)
{
    /// <summary>The rules of one device, in a stable order: by channel, then threshold before no-data.</summary>
    public async Task<IReadOnlyList<DeviceRule>> ListAsync(string deviceId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT channel_id, kind, min_value, max_value, margin, timeout_seconds
            FROM device_rules WHERE device_id = @deviceId
            ORDER BY channel_id, kind DESC
            """);
        cmd.Parameters.AddWithValue("deviceId", deviceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rules = new List<DeviceRule>();
        while (await reader.ReadAsync(ct)) rules.Add(Read(reader));
        return rules;
    }

    /// <summary>
    /// Replaces all the rules of a device with these, in one transaction, so a reader never sees half of the new ones.
    /// An empty list removes every rule of its own, which puts the device back on the platform defaults.
    /// </summary>
    public async Task ReplaceAsync(string deviceId, IReadOnlyList<DeviceRule> rules, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var delete = new NpgsqlCommand("DELETE FROM device_rules WHERE device_id = @deviceId", conn, tx))
        {
            delete.Parameters.AddWithValue("deviceId", deviceId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var rule in rules)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO device_rules (device_id, channel_id, kind, min_value, max_value, margin, timeout_seconds, updated_at)
                VALUES (@deviceId, @channelId, @kind, @min, @max, @margin, @timeout, @now)
                """, conn, tx);
            insert.Parameters.AddWithValue("deviceId", deviceId);
            insert.Parameters.AddWithValue("channelId", rule.ChannelId);
            insert.Parameters.AddWithValue("kind", rule.Kind);
            insert.Parameters.AddWithValue("min", (object?)rule.Min ?? DBNull.Value);
            insert.Parameters.AddWithValue("max", (object?)rule.Max ?? DBNull.Value);
            insert.Parameters.AddWithValue("margin", (object?)rule.Margin ?? DBNull.Value);
            insert.Parameters.AddWithValue("timeout", (object?)rule.TimeoutSeconds ?? DBNull.Value);
            insert.Parameters.AddWithValue("now", now);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>The threshold rules of these devices, by (device, channel). Devices without any are simply absent.</summary>
    public async Task<IReadOnlyDictionary<(string DeviceId, string ChannelId), DeviceRule>> ThresholdsForAsync(
        IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        var rules = new Dictionary<(string, string), DeviceRule>();
        if (deviceIds.Count == 0) return rules;

        await using var cmd = dataSource.CreateCommand("""
            SELECT channel_id, kind, min_value, max_value, margin, timeout_seconds, device_id
            FROM device_rules WHERE kind = 'threshold' AND device_id = ANY(@ids)
            """);
        cmd.Parameters.AddWithValue("ids", deviceIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rules[(reader.GetString(6), reader.GetString(0))] = Read(reader);
        return rules;
    }

    private static DeviceRule Read(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetDouble(2),
        reader.IsDBNull(3) ? null : reader.GetDouble(3),
        reader.IsDBNull(4) ? null : reader.GetDouble(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5));
}
