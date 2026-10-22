using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Pulse.Api.Auth;
using Pulse.Api.Observability;

namespace Pulse.Api.Hubs;

public sealed class LiveHub(IOptions<AuthOptions> auth) : Hub
{
    public const string ReadingsEvent = "readings";
    public const string AlertsEvent = "alerts";

    /// <summary>Service callers receive everything.</summary>
    public const string ServiceGroup = "service";

    /// <summary>Users receive what belongs to the devices they own.</summary>
    public static string OwnerGroup(string ownerId) => "owner:" + ownerId;

    public override async Task OnConnectedAsync()
    {
        PulseTelemetry.LiveConnections.Add(1);

        // With authentication off everyone receives everything, so no group is needed.
        var caller = Caller.From(Context.User ?? new System.Security.Claims.ClaimsPrincipal(), auth.Value);
        if (!caller.Unrestricted)
        {
            if (caller.IsService) await Groups.AddToGroupAsync(Context.ConnectionId, ServiceGroup);
            else if (caller.UserId is { } owner) await Groups.AddToGroupAsync(Context.ConnectionId, OwnerGroup(owner));
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        PulseTelemetry.LiveConnections.Add(-1);
        return base.OnDisconnectedAsync(exception);
    }
}
