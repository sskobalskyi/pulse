using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Pulse.Api.Hubs;
using Pulse.Api.Messaging;
using Pulse.Core;

namespace Pulse.Api.Processing;

/// <summary>Pushes readings to connected browsers over SignalR.</summary>
public sealed class LivePublisher(IOptions<KafkaOptions> options, IHubContext<LiveHub> hub, ILogger<LivePublisher> logger)
    : ReadingsConsumer(options, logger)
{
    protected override string GroupId => "pulse-live";

    protected override Task HandleAsync(IReadOnlyList<TelemetryReading> batch, CancellationToken ct) =>
        hub.Clients.All.SendAsync(LiveHub.ReadingsEvent, batch, ct);
}
