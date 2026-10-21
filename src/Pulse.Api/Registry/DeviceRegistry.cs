using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pulse.Api.Processing;
using Pulse.Core;

namespace Pulse.Api.Registry;

public enum RegistryError
{
    None,
    NotFound,

    /// <summary>The device is not in a state this action is allowed in.</summary>
    InvalidState,
    TokenInvalid,
    TokenExpired,
}

public readonly record struct RegistryResult<T>(T? Value, RegistryError Error)
{
    public bool IsOk => Error == RegistryError.None;

    public static RegistryResult<T> Ok(T value) => new(value, RegistryError.None);

    public static RegistryResult<T> Fail(RegistryError error) => new(default, error);
}

/// <summary>
/// The device registry: identity, lifecycle, ownership and capabilities. Every state change is one conditional
/// statement or one transaction, so two callers racing (a double claim, a ship during a retire) cannot both win.
/// </summary>
/// <remarks>
/// <c>ownerScope</c> on the read methods limits what a caller can see to one owner. Null means no limit, which is
/// what service callers and the unauthenticated demo mode get. A device outside the scope behaves as if it did
/// not exist, so its existence is not revealed.
/// </remarks>
public sealed class DeviceRegistry(
    NpgsqlDataSource dataSource,
    TimeProvider time,
    IOptions<SilenceOptions> silence)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(Device Device, bool Created)> RegisterAsync(DeviceRegistration registration, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Idempotent on the device id: a second registration changes nothing and says so.
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO devices (id, model, state, credential_hash, claim_token_hash, claim_token_expires_at, actuators, registered_at)
            VALUES (@id, @model, 'allocated', @credentialHash, @claimTokenHash, @expires, @actuators, @now)
            ON CONFLICT (id) DO NOTHING
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", registration.Id);
            insert.Parameters.AddWithValue("model", registration.Model);
            insert.Parameters.AddWithValue("credentialHash", registration.CredentialHash);
            insert.Parameters.AddWithValue("claimTokenHash", ClaimTokens.Normalise(registration.ClaimTokenHash)!);
            insert.Parameters.AddWithValue("expires", registration.ClaimTokenExpiresAt.UtcDateTime);
            insert.Parameters.Add(new NpgsqlParameter("actuators", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(registration.Capabilities.Actuators, Json) });
            insert.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime);
            var created = await insert.ExecuteNonQueryAsync(ct) == 1;

            if (created)
            {
                var position = 0;
                foreach (var channel in registration.Capabilities.Channels)
                {
                    await using var insertChannel = new NpgsqlCommand("""
                        INSERT INTO device_channels (device_id, channel_id, position, metric, label, unit)
                        VALUES (@deviceId, @channelId, @position, @metric, @label, @unit)
                        """, conn, tx);
                    insertChannel.Parameters.AddWithValue("deviceId", registration.Id);
                    insertChannel.Parameters.AddWithValue("channelId", channel.Id);
                    insertChannel.Parameters.AddWithValue("position", position++);
                    insertChannel.Parameters.AddWithValue("metric", channel.Metric);
                    insertChannel.Parameters.AddWithValue("label", (object?)channel.Label ?? DBNull.Value);
                    insertChannel.Parameters.AddWithValue("unit", (object?)channel.Unit ?? DBNull.Value);
                    await insertChannel.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
            var device = (await QueryAsync(conn, registration.Id, null, null, ct)).Single();
            return (device, created);
        }
    }

    public async Task<Device?> GetAsync(string id, string? ownerScope, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return (await QueryAsync(conn, id, null, ownerScope, ct)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<Device>> ListAsync(Lifecycle? state, string? ownerScope, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await QueryAsync(conn, null, state, ownerScope, ct);
    }

    /// <summary>Allocated to shipped. Shipping a device that is already shipped is accepted again, so a retried call is harmless.</summary>
    public async Task<RegistryResult<Device>> ShipAsync(string id, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await using (var update = new NpgsqlCommand(
            "UPDATE devices SET state = 'shipped', shipped_at = @now WHERE id = @id AND state = 'allocated'", conn))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime);
            if (await update.ExecuteNonQueryAsync(ct) == 1)
                return RegistryResult<Device>.Ok((await QueryAsync(conn, id, null, null, ct)).Single());
        }

        var current = (await QueryAsync(conn, id, null, null, ct)).SingleOrDefault();
        if (current is null) return RegistryResult<Device>.Fail(RegistryError.NotFound);

        return current.State == Lifecycle.Shipped.ToWire()
            ? RegistryResult<Device>.Ok(current)
            : RegistryResult<Device>.Fail(RegistryError.InvalidState);
    }

    /// <summary>
    /// Shipped to claimed, for one owner. The row is locked while the token is checked, so two claims cannot both succeed,
    /// and the token hash is erased afterwards, so a token works once.
    /// </summary>
    public async Task<RegistryResult<Device>> ClaimAsync(string id, string token, string ownerId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string stateWire;
        string? tokenHash;
        DateTime? expiresAt;
        await using (var select = new NpgsqlCommand(
            "SELECT state, claim_token_hash, claim_token_expires_at FROM devices WHERE id = @id FOR UPDATE", conn, tx))
        {
            select.Parameters.AddWithValue("id", id);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return RegistryResult<Device>.Fail(RegistryError.NotFound);

            stateWire = reader.GetString(0);
            tokenHash = reader.IsDBNull(1) ? null : reader.GetString(1);
            expiresAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
        }

        if (!DeviceLifecycle.CanClaim(DeviceLifecycle.Parse(stateWire)))
            return RegistryResult<Device>.Fail(RegistryError.InvalidState);

        // A wrong token is reported before an expired one, so an expired answer is only ever given to the holder of the real token.
        if (tokenHash is null || !ClaimTokens.Matches(token, tokenHash))
            return RegistryResult<Device>.Fail(RegistryError.TokenInvalid);

        if (expiresAt is null || new DateTimeOffset(DateTime.SpecifyKind(expiresAt.Value, DateTimeKind.Utc)) <= time.GetUtcNow())
            return RegistryResult<Device>.Fail(RegistryError.TokenExpired);

        await using (var update = new NpgsqlCommand("""
            UPDATE devices
            SET state = 'claimed', owner_id = @owner, claimed_at = @now, claim_token_hash = NULL, claim_token_expires_at = NULL
            WHERE id = @id
            """, conn, tx))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("owner", ownerId);
            update.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime);
            await update.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return RegistryResult<Device>.Ok((await QueryAsync(conn, id, null, null, ct)).Single());
    }

    /// <summary>
    /// Takes a device out of service for good. Its last-seen rows go, so the no-data monitor stops caring about it and
    /// resolves any no-data alert that was open, and an unused claim token stops working. Retiring twice is accepted.
    /// </summary>
    public async Task<RegistryResult<Device>> RetireAsync(string id, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var update = new NpgsqlCommand("""
            UPDATE devices
            SET state = 'retired', retired_at = @now, claim_token_hash = NULL, claim_token_expires_at = NULL
            WHERE id = @id AND state <> 'retired'
            """, conn, tx))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime);
            if (await update.ExecuteNonQueryAsync(ct) == 1)
            {
                await using var forget = new NpgsqlCommand("DELETE FROM device_last_seen WHERE device_id = @id", conn, tx);
                forget.Parameters.AddWithValue("id", id);
                await forget.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);

        var device = (await QueryAsync(conn, id, null, null, ct)).SingleOrDefault();
        return device is null ? RegistryResult<Device>.Fail(RegistryError.NotFound) : RegistryResult<Device>.Ok(device);
    }

    public async Task<RegistryResult<Channel>> CalibrateAsync(
        string deviceId, string channelId, double dryRaw, double wetRaw, string? ownerScope, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await using var update = new NpgsqlCommand("""
            UPDATE device_channels c
            SET dry_raw = @dry, wet_raw = @wet, calibrated_at = @now
            FROM devices d
            WHERE c.device_id = d.id AND c.device_id = @deviceId AND c.channel_id = @channelId
              AND (@owner::text IS NULL OR d.owner_id = @owner)
            """, conn);
        update.Parameters.AddWithValue("deviceId", deviceId);
        update.Parameters.AddWithValue("channelId", channelId);
        update.Parameters.AddWithValue("dry", dryRaw);
        update.Parameters.AddWithValue("wet", wetRaw);
        update.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime);
        update.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Text) { Value = (object?)ownerScope ?? DBNull.Value });
        if (await update.ExecuteNonQueryAsync(ct) == 0) return RegistryResult<Channel>.Fail(RegistryError.NotFound);

        var device = (await QueryAsync(conn, deviceId, null, null, ct)).Single();
        return RegistryResult<Channel>.Ok(device.Capabilities.Channels.Single(c => c.Id == channelId));
    }

    /// <summary>The first reading of a claimed device makes it active. Returns how many devices changed.</summary>
    public async Task<int> ActivateAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        if (deviceIds.Count == 0) return 0;

        await using var cmd = dataSource.CreateCommand("""
            UPDATE devices SET state = 'active', activated_at = @now
            WHERE id = ANY(@ids) AND state = 'claimed'
            """);
        cmd.Parameters.AddWithValue("ids", deviceIds.ToArray());
        cmd.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Which of these devices are retired. Used to refuse their data.</summary>
    public async Task<IReadOnlySet<string>> RetiredAmongAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        if (deviceIds.Count == 0) return new HashSet<string>();

        await using var cmd = dataSource.CreateCommand("SELECT id FROM devices WHERE state = 'retired' AND id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", deviceIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var retired = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct)) retired.Add(reader.GetString(0));
        return retired;
    }

    private async Task<IReadOnlyList<Device>> QueryAsync(
        NpgsqlConnection conn, string? id, Lifecycle? state, string? ownerScope, CancellationToken ct)
    {
        var rows = new List<DeviceRow>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT d.id, d.model, d.state, d.owner_id, d.firmware, d.actuators,
                   seen.at AS last_seen_at,
                   -- Same clock as last_seen_at and the no-data check, so the three can never disagree.
                   CASE WHEN seen.at IS NULL THEN 'unknown' WHEN seen.at > now() - @onlineWithin THEN 'online' ELSE 'offline' END AS connection
            FROM devices d
            LEFT JOIN LATERAL (SELECT max(s.last_seen_at) AS at FROM device_last_seen s WHERE s.device_id = d.id) seen ON true
            WHERE (@id::text IS NULL OR d.id = @id)
              AND (@state::text IS NULL OR d.state = @state)
              AND (@owner::text IS NULL OR d.owner_id = @owner)
            ORDER BY d.id
            """, conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Text) { Value = (object?)id ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Text) { Value = state is { } s ? s.ToWire() : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Text) { Value = (object?)ownerScope ?? DBNull.Value });
            cmd.Parameters.AddWithValue("onlineWithin", silence.Value.Timeout);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new DeviceRow(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
                    reader.GetString(7)));
            }
        }

        var channels = await ChannelsAsync(conn, rows.Select(r => r.Id).ToArray(), ct);
        return rows.Select(r => new Device(
            r.Id,
            r.Model,
            r.State,
            r.OwnerId,
            r.Connection,
            r.LastSeenAt,
            r.Firmware,
            new Capabilities(
                channels.GetValueOrDefault(r.Id) ?? [],
                JsonSerializer.Deserialize<List<ActuatorCapability>>(r.ActuatorsJson, Json) ?? []))).ToList();
    }

    private static async Task<Dictionary<string, List<Channel>>> ChannelsAsync(NpgsqlConnection conn, string[] deviceIds, CancellationToken ct)
    {
        var byDevice = new Dictionary<string, List<Channel>>(StringComparer.Ordinal);
        if (deviceIds.Length == 0) return byDevice;

        await using var cmd = new NpgsqlCommand("""
            SELECT device_id, channel_id, metric, label, unit, dry_raw, wet_raw, calibrated_at
            FROM device_channels WHERE device_id = ANY(@ids) ORDER BY device_id, position
            """, conn);
        cmd.Parameters.AddWithValue("ids", deviceIds);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            Calibration? calibration = reader.IsDBNull(5) || reader.IsDBNull(6) || reader.IsDBNull(7)
                ? null
                : new Calibration(reader.GetDouble(5), reader.GetDouble(6), new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)));

            var channel = new Channel(
                reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                calibration);

            var deviceId = reader.GetString(0);
            if (!byDevice.TryGetValue(deviceId, out var list)) byDevice[deviceId] = list = [];
            list.Add(channel);
        }

        return byDevice;
    }

    private sealed record DeviceRow(
        string Id, string Model, string State, string? OwnerId, string? Firmware, string ActuatorsJson, DateTimeOffset? LastSeenAt, string Connection);
}
