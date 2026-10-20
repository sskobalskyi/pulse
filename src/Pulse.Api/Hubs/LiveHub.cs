using Microsoft.AspNetCore.SignalR;
using Pulse.Api.Observability;

namespace Pulse.Api.Hubs;

public sealed class LiveHub : Hub
{
    public const string ReadingsEvent = "readings";
    public const string AlertsEvent = "alerts";

    public override Task OnConnectedAsync()
    {
        PulseTelemetry.LiveConnections.Add(1);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        PulseTelemetry.LiveConnections.Add(-1);
        return base.OnDisconnectedAsync(exception);
    }
}
