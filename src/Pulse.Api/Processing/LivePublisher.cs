using Microsoft.Extensions.Options;
using Pulse.Api.Hubs;
using Pulse.Api.Messaging;
using Pulse.Core;

namespace Pulse.Api.Processing;

/// <summary>Pushes readings to the connected browsers that may see them, over SignalR.</summary>
public sealed class LivePublisher(IOptions<KafkaOptions> options, LiveFanout fanout, ILogger<LivePublisher> logger)
    : ReadingsConsumer(options, logger)
{
    protected override string GroupId => "pulse-live";

    protected override Task HandleAsync(IReadOnlyList<TelemetryReading> batch, CancellationToken ct) =>
        fanout.SendReadingsAsync(batch, ct);
}
