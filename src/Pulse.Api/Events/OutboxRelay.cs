using Microsoft.Extensions.Options;
using Npgsql;
using Pulse.Api.Observability;

namespace Pulse.Api.Events;

/// <summary>
/// Takes events out of the outbox and hands them to the bus. Delivery is at least once: if the process dies after the
/// broker took a message but before the row was marked, the message goes out again, which is why consumers have to
/// be idempotent on <c>messageId</c>, as the contract says.
/// </summary>
public sealed class OutboxRelay(
    NpgsqlDataSource dataSource,
    IEventBus bus,
    TimeProvider time,
    ILogger<OutboxRelay> logger)
{
    private const int BatchSize = 100;

    /// <summary>
    /// Publishes what is waiting, oldest first, and returns how many events went out. Stops at the first failure, so an
    /// event never overtakes an older one. The rows are locked while they are published, and rows another instance has
    /// locked are skipped, so several instances can run the relay without sending anything twice.
    /// </summary>
    public async Task<int> RelayOnceAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var waiting = new List<OutboxMessage>();
        await using (var select = new NpgsqlCommand("""
            SELECT id, message_id, routing_key, payload::text, traceparent, occurred_at
            FROM outbox
            WHERE published_at IS NULL
            ORDER BY id
            LIMIT @limit
            FOR UPDATE SKIP LOCKED
            """, conn, tx))
        {
            select.Parameters.AddWithValue("limit", BatchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                waiting.Add(new OutboxMessage(
                    reader.GetInt64(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc))));
            }
        }

        var published = 0;
        foreach (var message in waiting)
        {
            try
            {
                await bus.PublishAsync(message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not publish {RoutingKey} {MessageId}, it stays in the outbox", message.RoutingKey, message.MessageId);
                PulseTelemetry.OutboxFailures.Add(1);
                await MarkAsync(conn, tx, message.Id, published: false, ct);
                break;
            }

            await MarkAsync(conn, tx, message.Id, published: true, ct);
            PulseTelemetry.OutboxPublished.Add(1, new KeyValuePair<string, object?>("event", message.RoutingKey));
            published++;
        }

        await tx.CommitAsync(ct);
        return published;
    }

    /// <summary>How many events are waiting, which is what to watch to know the broker is keeping up.</summary>
    public async Task<long> PendingAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM outbox WHERE published_at IS NULL");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Removes published events older than the retention, so the table does not grow for ever.</summary>
    public async Task<int> CleanUpAsync(TimeSpan retention, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM outbox WHERE published_at IS NOT NULL AND published_at < @before");
        cmd.Parameters.AddWithValue("before", (time.GetUtcNow() - retention).UtcDateTime);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task MarkAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long id, bool published, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            published
                ? "UPDATE outbox SET published_at = @now, attempts = attempts + 1 WHERE id = @id"
                : "UPDATE outbox SET attempts = attempts + 1 WHERE id = @id",
            conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Runs the relay on a timer while events are switched on.</summary>
public sealed class OutboxPublisher(
    OutboxRelay relay,
    IOptions<EventsOptions> options,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan CleanUpEvery = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            logger.LogInformation("Integration events are disabled, nothing is published");
            return;
        }

        using var timer = new PeriodicTimer(o.PollInterval);
        var lastCleanUp = DateTimeOffset.MinValue;
        do
        {
            try
            {
                // Go on straight away while there is more, instead of waiting a poll interval between full batches.
                while (await relay.RelayOnceAsync(stoppingToken) > 0) { }

                PulseTelemetry.OutboxPending = await relay.PendingAsync(stoppingToken);
                if (DateTimeOffset.UtcNow - lastCleanUp > CleanUpEvery)
                {
                    await relay.CleanUpAsync(o.Retention, stoppingToken);
                    lastCleanUp = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The outbox relay failed, trying again");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
