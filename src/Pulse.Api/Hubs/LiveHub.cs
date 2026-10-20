using Microsoft.AspNetCore.SignalR;

namespace Pulse.Api.Hubs;

public sealed class LiveHub : Hub
{
    public const string ReadingsEvent = "readings";
    public const string AlertsEvent = "alerts";
}
