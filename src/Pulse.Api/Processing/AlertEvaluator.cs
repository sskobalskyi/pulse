using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Pulse.Api.Hubs;
using Pulse.Api.Messaging;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Api.Processing;

/// <summary>Evaluates threshold rules, stores breaches and pushes them to the alert feed.</summary>
public sealed class AlertEvaluator(
    IOptions<KafkaOptions> options,
    IOptions<AlertRulesOptions> rules,
    PulseDb db,
    IHubContext<LiveHub> hub,
    ILogger<AlertEvaluator> logger) : ReadingsConsumer(options, logger)
{
    private readonly AlertDebouncer _debouncer = new(rules.Value.Cooldown);

    protected override string GroupId => "pulse-alerts";

    protected override async Task HandleAsync(IReadOnlyList<TelemetryReading> batch, CancellationToken ct)
    {
        var alerts = batch
            .SelectMany(r => rules.Value.Rules.Select(rule => rule.Evaluate(r)))
            .OfType<Alert>()
            .Where(_debouncer.ShouldRaise)
            .ToList();

        if (alerts.Count == 0) return;

        try
        {
            await db.InsertAlertsAsync(alerts, ct);
        }
        catch
        {
            // The batch is redelivered; without a reset the debouncer would swallow these alerts the second time.
            // Alerts that were stored in the meantime are skipped by the unique index.
            _debouncer.Reset();
            throw;
        }

        await hub.Clients.All.SendAsync(LiveHub.AlertsEvent, alerts, ct);
    }
}

public sealed class AlertRulesOptions
{
    /// <summary>Minimum event-time gap between two alerts for the same device, metric and rule.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(1);

    public List<ThresholdRule> Rules { get; set; } =
    [
        new("temperature", -10, 80),
        new("humidity", 5, 95),
        new("battery", 10, null),
    ];
}
