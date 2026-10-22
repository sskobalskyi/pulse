using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Pulse.Api.Events;

/// <summary>
/// Writes integration events into the outbox table, inside the transaction of the change they describe. The relay
/// publishes them afterwards. Either the change and its event are both committed, or neither is.
/// </summary>
public sealed class EventOutbox(IOptions<EventsOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Enabled => options.Value.Enabled;

    /// <summary>Does nothing when events are switched off, so the standalone demo does not fill a table nobody empties.</summary>
    public async Task AddAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string routingKey, object payload, DateTimeOffset occurredAt, CancellationToken ct)
    {
        if (!Enabled) return;

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO outbox (message_id, routing_key, payload, traceparent, occurred_at)
            VALUES (@messageId, @routingKey, @payload, @traceparent, @occurredAt)
            """, connection, transaction);
        cmd.Parameters.AddWithValue("messageId", Guid.NewGuid());
        cmd.Parameters.AddWithValue("routingKey", routingKey);
        cmd.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(payload, Json) });
        // The trace of whatever caused the change, so a consumer's span can continue it.
        cmd.Parameters.AddWithValue("traceparent", (object?)Activity.Current?.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("occurredAt", occurredAt.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
