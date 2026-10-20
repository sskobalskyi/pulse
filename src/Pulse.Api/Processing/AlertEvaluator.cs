using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Pulse.Api.Hubs;
using Pulse.Api.Messaging;
using Pulse.Api.Observability;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Api.Processing;

/// <summary>
/// Evaluates threshold rules and records state changes: one alert when a rule is breached, one when it
/// resolves. State lives in an <see cref="AlertTracker"/> that is rebuilt from storage on the first batch,
/// and only advances once a batch's alerts have been stored.
/// </summary>
public sealed class AlertEvaluator(
    IOptions<KafkaOptions> options,
    IOptions<AlertRulesOptions> rules,
    PulseDb db,
    IHubContext<LiveHub> hub,
    ILogger<AlertEvaluator> logger) : ReadingsConsumer(options, logger)
{
    private readonly AlertTracker _tracker = new();
    private bool _restored;

    protected override string GroupId => "pulse-alerts";

    protected override async Task HandleAsync(IReadOnlyList<TelemetryReading> batch, CancellationToken ct)
    {
        if (!_restored)
        {
            _tracker.Restore(await db.GetActiveAlertKeysAsync(ct));
            _restored = true;
        }

        var result = _tracker.Evaluate(batch, rules.Value.Rules);
        if (result.Alerts.Count > 0)
        {
            await db.InsertAlertsAsync(result.Alerts, ct);
            await hub.Clients.All.SendAsync(LiveHub.AlertsEvent, result.Alerts, ct);

            foreach (var alert in result.Alerts)
            {
                PulseTelemetry.AlertsEmitted.Add(1,
                    new KeyValuePair<string, object?>("state", alert.State.ToString()),
                    new KeyValuePair<string, object?>("metric", alert.Metric));
            }
        }

        // Not reached when storing fails: the batch is redelivered and evaluated from the same state.
        result.Commit();
    }
}

public sealed class AlertRulesOptions
{
    public List<ThresholdRule> Rules { get; set; } =
    [
        new("temperature", -10, 80, Hysteresis: 2),
        new("humidity", 5, 95, Hysteresis: 3),
        new("battery", 10, null, Hysteresis: 5),
    ];
}
