using Npgsql;
using Pulse.Api.Events;
using Pulse.Api.Observability;
using Pulse.Core;

namespace Pulse.Api.Commands;

public enum EnqueueOutcome
{
    /// <summary>Stored now, waiting to be delivered.</summary>
    Created,

    /// <summary>The same command was stored before. Nothing changed, and the caller gets it as it is now.</summary>
    Existing,

    /// <summary>The id belongs to a different command, so one of the two senders is wrong.</summary>
    IdReused,

    DeviceMissing,
    DeviceRetired,
}

public sealed record EnqueueResult(EnqueueOutcome Outcome, Command? Command = null);

/// <summary>
/// Commands and where they are. Every change of state is one transaction, and the end of a command is announced in
/// the same transaction through the outbox, so a command is never over without its event or announced without being over.
/// </summary>
public sealed class CommandStore(NpgsqlDataSource dataSource, TimeProvider time, EventOutbox? outbox = null)
{
    private const string Columns = "command_id, device_id, type, actuator_id, status, reason, created_at, finished_at";

    /// <summary>How many commands one delivery round takes, oldest first.</summary>
    private const int DeliveryBatch = 50;

    public async Task<EnqueueResult> EnqueueAsync(string deviceId, CommandRequest request, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Shared lock on the device: retiring it needs the exclusive one, so a command either goes in before the device is
        // retired (and is cancelled with it) or sees that it is retired. It cannot slip in between and be delivered later.
        string? state;
        await using (var lookup = new NpgsqlCommand("SELECT state FROM devices WHERE id = @id FOR SHARE", conn, tx))
        {
            lookup.Parameters.AddWithValue("id", deviceId);
            state = (string?)await lookup.ExecuteScalarAsync(ct);
        }

        if (state is null) return new EnqueueResult(EnqueueOutcome.DeviceMissing);
        if (state == "retired") return new EnqueueResult(EnqueueOutcome.DeviceRetired);

        await using var insert = new NpgsqlCommand($"""
            INSERT INTO commands (command_id, device_id, type, actuator_id, duration_seconds, enabled, expires_at, created_at)
            VALUES (@commandId, @deviceId, @type, @actuatorId, @duration, @enabled, @expiresAt, @now)
            ON CONFLICT (command_id) DO NOTHING
            RETURNING {Columns}
            """, conn, tx);
        insert.Parameters.AddWithValue("commandId", request.CommandId);
        insert.Parameters.AddWithValue("deviceId", deviceId);
        insert.Parameters.AddWithValue("type", request.Type!);
        insert.Parameters.AddWithValue("actuatorId", request.ActuatorId!);
        insert.Parameters.AddWithValue("duration", (object?)request.DurationSeconds ?? DBNull.Value);
        insert.Parameters.AddWithValue("enabled", (object?)request.Enabled ?? DBNull.Value);
        insert.Parameters.AddWithValue("expiresAt", request.ExpiresAt.UtcDateTime);
        insert.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime);

        Command? created = null;
        await using (var reader = await insert.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct)) created = Read(reader);
        }

        if (created is not null)
        {
            await tx.CommitAsync(ct);
            PulseTelemetry.CommandsQueued.Add(1, new KeyValuePair<string, object?>("type", request.Type));
            return new EnqueueResult(EnqueueOutcome.Created, created);
        }

        // The id is taken. Sending the same command again is how a sender retries after a lost answer, so that is fine and
        // returns what is stored. The same id for something else is a mistake worth refusing out loud.
        await using var existing = new NpgsqlCommand($"""
            SELECT {Columns}, duration_seconds, enabled, expires_at FROM commands WHERE command_id = @commandId
            """, conn, tx);
        existing.Parameters.AddWithValue("commandId", request.CommandId);
        await using var row = await existing.ExecuteReaderAsync(ct);
        await row.ReadAsync(ct);

        var stored = Read(row);
        var sameCommand = stored.DeviceId == deviceId
            && stored.Type == request.Type
            && stored.ActuatorId == request.ActuatorId
            && (row.IsDBNull(8) ? null : row.GetInt32(8)) == request.DurationSeconds
            && (row.IsDBNull(9) ? null : row.GetBoolean(9)) == request.Enabled
            // The database keeps microseconds, a client may send more.
            && Math.Abs((new DateTimeOffset(DateTime.SpecifyKind(row.GetDateTime(10), DateTimeKind.Utc)) - request.ExpiresAt).TotalMilliseconds) < 1;

        return new EnqueueResult(sameCommand ? EnqueueOutcome.Existing : EnqueueOutcome.IdReused, sameCommand ? stored : null);
    }

    public async Task<IReadOnlyList<Command>> ListAsync(string deviceId, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {Columns} FROM commands WHERE device_id = @deviceId ORDER BY created_at DESC, command_id LIMIT @limit");
        cmd.Parameters.AddWithValue("deviceId", deviceId);
        cmd.Parameters.AddWithValue("limit", limit);

        var commands = new List<Command>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) commands.Add(Read(reader));
        return commands;
    }

    public async Task<Command?> GetAsync(string deviceId, Guid commandId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {Columns} FROM commands WHERE device_id = @deviceId AND command_id = @commandId");
        cmd.Parameters.AddWithValue("deviceId", deviceId);
        cmd.Parameters.AddWithValue("commandId", commandId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>
    /// What the device said about a command. Returns the command as it is afterwards, or null when this device has no such
    /// command. A report that does not move the command forward (a repeat, or something older than what is known) changes
    /// nothing, because the broker delivers at least once and not in order.
    /// </summary>
    public async Task<Command?> ApplyAckAsync(string deviceId, Guid commandId, CommandStatus reported, string? reason, CancellationToken ct)
    {
        if (reported <= CommandStatus.Sent) throw new ArgumentOutOfRangeException(nameof(reported), "A device reports accepted or an ending.");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var select = new NpgsqlCommand($"SELECT {Columns} FROM commands WHERE command_id = @commandId AND device_id = @deviceId FOR UPDATE", conn, tx);
        select.Parameters.AddWithValue("commandId", commandId);
        select.Parameters.AddWithValue("deviceId", deviceId);

        Command current;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            current = Read(reader);
        }

        if (!CommandStatuses.CanMove(current.State, reported)) return current;

        var final = CommandStatuses.IsFinal(reported);
        var now = time.GetUtcNow();
        await using var update = new NpgsqlCommand($"""
            UPDATE commands SET status = @status, reason = @reason, finished_at = @finishedAt
            WHERE command_id = @commandId
            RETURNING {Columns}
            """, conn, tx);
        update.Parameters.AddWithValue("status", CommandStatuses.Name(reported));
        update.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        update.Parameters.AddWithValue("finishedAt", final ? now.UtcDateTime : DBNull.Value);
        update.Parameters.AddWithValue("commandId", commandId);

        Command updated;
        await using (var reader = await update.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            updated = Read(reader);
        }

        if (final) await FinishedAsync(conn, tx, updated, now, ct);
        await tx.CommitAsync(ct);
        return updated;
    }

    /// <summary>
    /// Hands commands that are waiting to the device link, oldest first, and marks each one sent. Stops at the first one
    /// the link refuses and leaves it queued, so it is tried again next round and nothing overtakes it.
    /// </summary>
    public async Task<int> DeliverAsync(ICommandSender sender, ILogger logger, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var waiting = new List<DeviceCommand>();
        await using (var select = new NpgsqlCommand("""
            SELECT command_id, device_id, type, actuator_id, duration_seconds, enabled, expires_at
            FROM commands
            WHERE status = 'queued' AND expires_at > @now
            ORDER BY created_at, command_id
            LIMIT @limit
            FOR UPDATE SKIP LOCKED
            """, conn, tx))
        {
            select.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime);
            select.Parameters.AddWithValue("limit", DeliveryBatch);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                waiting.Add(new DeviceCommand(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetBoolean(5),
                    new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc))));
            }
        }

        var sent = 0;
        foreach (var command in waiting)
        {
            try
            {
                await sender.SendAsync(command, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not hand command {CommandId} to the device link, it stays queued", command.CommandId);
                break;
            }

            await using var mark = new NpgsqlCommand("UPDATE commands SET status = 'sent' WHERE command_id = @id", conn, tx);
            mark.Parameters.AddWithValue("id", command.CommandId);
            await mark.ExecuteNonQueryAsync(ct);
            sent++;
        }

        await tx.CommitAsync(ct);
        return sent;
    }

    /// <summary>
    /// Ends the commands that ran out of time. One that was never accepted expires: the device must not run it any more.
    /// One the device accepted but never reported on fails once it should long have finished, which is its expiry, the
    /// time it was told to run and a grace period for the report to arrive.
    /// </summary>
    public async Task<int> ExpireAsync(TimeSpan acceptedGrace, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var now = time.GetUtcNow();
        await using var update = new NpgsqlCommand($"""
            UPDATE commands
            SET status = CASE WHEN status = 'accepted' THEN 'failed' ELSE 'expired' END,
                reason = CASE WHEN status = 'accepted' THEN 'no_result' ELSE 'expired' END,
                finished_at = @now
            WHERE command_id IN (
                SELECT command_id FROM commands
                WHERE finished_at IS NULL
                  AND ((status IN ('queued', 'sent') AND expires_at < @now)
                    OR (status = 'accepted' AND expires_at + make_interval(secs => COALESCE(duration_seconds, 0) + @grace) < @now))
                FOR UPDATE SKIP LOCKED)
            RETURNING {Columns}
            """, conn, tx);
        update.Parameters.AddWithValue("now", now.UtcDateTime);
        update.Parameters.AddWithValue("grace", acceptedGrace.TotalSeconds);

        var ended = await ReadAllAsync(update, ct);
        foreach (var command in ended) await FinishedAsync(conn, tx, command, now, ct);

        await tx.CommitAsync(ct);
        return ended.Count;
    }

    /// <summary>
    /// Ends every command of a device that is being retired, inside the transaction that retires it. Nobody is left
    /// waiting for a result that can no longer come.
    /// </summary>
    public static async Task CancelOpenAsync(NpgsqlConnection conn, NpgsqlTransaction tx, EventOutbox? outbox, string deviceId, DateTimeOffset now, CancellationToken ct)
    {
        await using var update = new NpgsqlCommand($"""
            UPDATE commands SET status = 'failed', reason = 'device_retired', finished_at = @now
            WHERE device_id = @deviceId AND finished_at IS NULL
            RETURNING {Columns}
            """, conn, tx);
        update.Parameters.AddWithValue("deviceId", deviceId);
        update.Parameters.AddWithValue("now", now.UtcDateTime);

        foreach (var command in await ReadAllAsync(update, ct)) await FinishedAsync(conn, tx, command, now, ct, outbox);
    }

    private Task FinishedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Command command, DateTimeOffset now, CancellationToken ct) =>
        FinishedAsync(conn, tx, command, now, ct, outbox);

    private static async Task FinishedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Command command, DateTimeOffset now, CancellationToken ct, EventOutbox? outbox)
    {
        PulseTelemetry.CommandsFinished.Add(1, new KeyValuePair<string, object?>("status", command.Status));
        if (outbox is { Enabled: true })
            await outbox.AddAsync(conn, tx, EventNames.CommandCompleted, IntegrationEvents.ForCommandCompleted(command), now, ct);
    }

    private static async Task<List<Command>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var commands = new List<Command>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) commands.Add(Read(reader));
        return commands;
    }

    private static Command Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc)),
        r.IsDBNull(7) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc)));
}
