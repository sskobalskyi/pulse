using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Pulse.Api.Auth;
using Pulse.Api.Registry;
using Pulse.Core;

namespace Pulse.Api.Hubs;

/// <summary>
/// Sends live readings and alerts to the browsers that may see them. With authentication off that is everybody.
/// With it on, service callers get everything and a user gets only what belongs to the devices they own, so a push
/// is split by owner. A device nobody has claimed reaches service callers only.
/// </summary>
/// <remarks>
/// The owners are looked up for every batch rather than cached: a claim takes effect on the very next push, and
/// the lookup is one indexed query per batch, a few times a second.
/// </remarks>
public sealed class LiveFanout(IHubContext<LiveHub> hub, DeviceRegistry registry, IOptions<AuthOptions> auth)
{
    public Task SendReadingsAsync(IReadOnlyList<TelemetryReading> readings, CancellationToken ct) =>
        SendAsync(LiveHub.ReadingsEvent, readings, r => r.DeviceId, ct);

    public Task SendAlertsAsync(IReadOnlyList<Alert> alerts, CancellationToken ct) =>
        SendAsync(LiveHub.AlertsEvent, alerts, a => a.DeviceId, ct);

    private async Task SendAsync<T>(string method, IReadOnlyList<T> items, Func<T, string> deviceOf, CancellationToken ct)
    {
        if (items.Count == 0) return;

        if (!auth.Value.Enabled)
        {
            await hub.Clients.All.SendAsync(method, items, ct);
            return;
        }

        var owners = await registry.OwnersAsync(items.Select(deviceOf).Distinct().ToArray(), ct);

        await hub.Clients.Group(LiveHub.ServiceGroup).SendAsync(method, items, ct);
        foreach (var byOwner in items.Where(i => owners.ContainsKey(deviceOf(i))).GroupBy(i => owners[deviceOf(i)]))
            await hub.Clients.Group(LiveHub.OwnerGroup(byOwner.Key)).SendAsync(method, byOwner.ToList(), ct);
    }
}
