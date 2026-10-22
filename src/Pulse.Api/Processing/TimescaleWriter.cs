using Microsoft.Extensions.Options;
using Pulse.Api.Messaging;
using Pulse.Api.Registry;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Api.Processing;

/// <summary>
/// Persists raw readings into the TimescaleDB hypertable, records that their series are alive, and activates
/// claimed devices that have just reported for the first time.
/// </summary>
public sealed class TimescaleWriter(
    IOptions<KafkaOptions> options,
    PulseDb db,
    DeviceRegistry registry,
    ILogger<TimescaleWriter> logger) : ReadingsConsumer(options, logger)
{
    protected override string GroupId => "pulse-writer";

    protected override async Task HandleAsync(IReadOnlyList<TelemetryReading> batch, CancellationToken ct)
    {
        await db.InsertReadingsAsync(batch, ct);

        // If anything below fails the batch is redelivered, and every statement is safe to repeat.
        await db.TouchLastSeenAsync(batch, ct);
        await registry.ActivateAsync(batch.Select(r => r.DeviceId).Distinct().ToArray(), ct);
    }
}
