using Microsoft.Extensions.Options;
using Pulse.Api.Messaging;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Api.Processing;

/// <summary>Persists raw readings into the TimescaleDB hypertable and records that their series are alive.</summary>
public sealed class TimescaleWriter(IOptions<KafkaOptions> options, PulseDb db, ILogger<TimescaleWriter> logger)
    : ReadingsConsumer(options, logger)
{
    protected override string GroupId => "pulse-writer";

    protected override async Task HandleAsync(IReadOnlyList<TelemetryReading> batch, CancellationToken ct)
    {
        await db.InsertReadingsAsync(batch, ct);

        // If this fails the batch is redelivered, and both statements are safe to repeat.
        await db.TouchLastSeenAsync(batch, ct);
    }
}
