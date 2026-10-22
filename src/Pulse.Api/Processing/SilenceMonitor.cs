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
/// last-seen table on a timer instead. Every API instance runs it, and a Postgres advisory lock makes sure
/// only one of them evaluates in any given round.
/// </summary>
public sealed class SilenceMonitor(
    IOptions<SilenceOptions> options,
    PulseDb db,
    LiveFanout fanout,
    ILogger<SilenceMonitor> logger) : BackgroundService
{
    /// <summary>Arbitrary but fixed: every instance must use the same number for the lock to mean anything.</summary>
    public const long LeaderLockKey = 7_300_001;

    private readonly SilenceTracker _tracker = new(options.Value.Timeout);

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
                await CheckOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The state did not advance, so the next tick evaluates the same situation again.
                logger.LogError(ex, "No-data check failed");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>
    /// One check. Only the instance that holds the database lock does anything, so several API instances
    /// raise each alert once. Returns false when another instance is the leader for this round.
    /// </summary>
    public Task<bool> CheckOnceAsync(CancellationToken ct) =>
        db.TryRunExclusiveAsync(LeaderLockKey, () => EvaluateAsync(options.Value.Timeout, ct), ct);

    private async Task EvaluateAsync(TimeSpan timeout, CancellationToken ct)
    {
        // The leader can change between rounds, so what is already raised comes from storage every time,
        // not from whatever this instance remembers from an earlier round.
        _tracker.Restore(await db.GetActiveAlertKeysAsync(AlertKind.NoData, ct));

        var silent = await db.GetSilentSeriesAsync(timeout, ct);
        var result = _tracker.Evaluate(silent, DateTimeOffset.UtcNow);

        if (result.Alerts.Count > 0)
        {
            await db.InsertAlertsAsync(result.Alerts, ct);
            await fanout.SendAlertsAsync(result.Alerts, ct);
        }

        result.Commit();

        // The same round decides which devices went offline or came back, for the events. Only the leader does this, so it is reported once.
        await db.ReconcileDeviceStatusAsync(timeout, DateTimeOffset.UtcNow, ct);
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
