using Microsoft.Extensions.Options;
using Pulse.Api.Messaging;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Api.Processing;

/// <summary>Persists raw readings into the TimescaleDB hypertable using binary COPY.</summary>
public sealed class TimescaleWriter(IOptions<KafkaOptions> options, PulseDb db, ILogger<TimescaleWriter> logger)
    : ReadingsConsumer(options, logger)
{
    protected override string GroupId => "pulse-writer";

    protected override Task HandleAsync(IReadOnlyList<TelemetryReading> batch, CancellationToken ct) =>
        db.InsertReadingsAsync(batch, ct);
}
