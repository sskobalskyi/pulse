using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Pulse.Api.Hubs;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Api.Processing;

public sealed class SilenceOptions
{
    /// <summary>A series that has not reported for this long is considered silent.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the monitor looks. Detection is late by up to this much.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(10);

    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Raises a "no data" alert when a device stops reporting a metric, and resolves it when data resumes.
/// A silent device sends nothing, so this cannot be driven by the Kafka consumers: it polls the
/// last-seen table on a timer instead. Many instances can run it, see the README for the caveat.
/// </summary>
public sealed class SilenceMonitor(
    IOptions<SilenceOptions> options,
    PulseDb db,
    IHubContext<LiveHub> hub,
    ILogger<SilenceMonitor> logger) : BackgroundService
{
    private readonly SilenceTracker _tracker = new(options.Value.Timeout);
    private bool _restored;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            logger.LogInformation("No-data monitoring is disabled");
            return;
        }

        using var timer = new PeriodicTimer(o.CheckInterval);
        do
        {
            try
            {
                await CheckAsync(o.Timeout, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The state did not advance, so the next tick evaluates the same situation again.
                logger.LogError(ex, "No-data check failed");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private async Task CheckAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!_restored)
        {
            _tracker.Restore(await db.GetActiveAlertKeysAsync(AlertKind.NoData, ct));
            _restored = true;
        }

        var silent = await db.GetSilentSeriesAsync(timeout, ct);
        var result = _tracker.Evaluate(silent, DateTimeOffset.UtcNow);

        if (result.Alerts.Count > 0)
        {
            await db.InsertAlertsAsync(result.Alerts, ct);
            await hub.Clients.All.SendAsync(LiveHub.AlertsEvent, result.Alerts, ct);
        }

        result.Commit();
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
