using Pulse.Api.Commands;
using Pulse.Api.Observability;
using Pulse.Api.Processing;
using Pulse.Api.Registry;
using Pulse.Core;

namespace Pulse.Api.Mqtt;

/// <summary>
/// What Pulse does with what a device says. Separate from the connection to the broker so it can be exercised with just
/// a topic and a payload. A message that is not what the contract describes is counted and dropped: a faulty device
/// must not be able to hurt the bridge, and there is nobody to send an error to.
/// </summary>
public sealed class DeviceMessageHandler(
    IngestService ingest, DeviceRegistry registry, CommandStore commands, ILogger<DeviceMessageHandler> logger)
{
    public async Task HandleAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var parsed = DeviceTopics.Parse(topic);
        if (parsed is null)
        {
            Count("unknown", "unknown_topic");
            return;
        }

        var kind = parsed.Kind.ToString().ToLowerInvariant();
        var outcome = parsed.Kind switch
        {
            DeviceTopicKind.Telemetry => await TelemetryAsync(parsed, payload, ct),
            DeviceTopicKind.Status => await StatusAsync(parsed, payload, ct),
            DeviceTopicKind.CommandAck => await AckAsync(parsed, payload, ct),
            DeviceTopicKind.Manifest => await ManifestAsync(parsed, payload, ct),
            // Kept for later: Pulse does not store a device's actuator state yet, the answer to a command says what it needs.
            _ => "ignored",
        };
        Count(kind, outcome);
    }

    private async Task<string> TelemetryAsync(DeviceTopic topic, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (DeviceMessages.Read<MqttReading>(payload.Span) is not { } message) return "invalid_payload";

        // The device named a channel, and the metric is whatever it declared that channel to measure.
        var declared = (await registry.DeclaredChannelsAsync([topic.DeviceId], ct)).GetValueOrDefault(topic.DeviceId);
        var metric = declared?.FirstOrDefault(c => c.Id == topic.ChannelId)?.Metric;
        if (metric is null) return declared is null ? "unknown_device" : "unknown_channel";

        if (DeviceMessages.ToReading(topic.DeviceId, topic.ChannelId!, metric, message) is not { } reading) return "invalid_payload";

        var result = await ingest.IngestAsync([reading], ct);
        return result.Refusal is { } refusal ? refusal.Code ?? "refused" : "handled";
    }

    private async Task<string> StatusAsync(DeviceTopic topic, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (DeviceMessages.Read<MqttStatus>(payload.Span) is not { } message || DeviceMessages.IsOnline(message) is not { } online) return "invalid_payload";

        await registry.ReportConnectionAsync(topic.DeviceId, online, ct);
        return "handled";
    }

    private async Task<string> AckAsync(DeviceTopic topic, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (DeviceMessages.Read<MqttAck>(payload.Span) is not { } message || !DeviceMessages.TryAck(message, out var commandId, out var status))
            return "invalid_payload";

        var command = await commands.ApplyAckAsync(topic.DeviceId, commandId, status, message.Reason, ct);
        if (command is null) logger.LogWarning("Device {DeviceId} answered a command it was never sent: {CommandId}", topic.DeviceId, commandId);
        return command is null ? "unknown_command" : "handled";
    }

    private async Task<string> ManifestAsync(DeviceTopic topic, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (DeviceMessages.Read<MqttManifest>(payload.Span) is not { } message) return "invalid_payload";

        // Pulse's registry is where capabilities come from, as the device was registered with them. The manifest says which firmware runs.
        if (!string.IsNullOrWhiteSpace(message.Firmware)) await registry.ReportFirmwareAsync(topic.DeviceId, message.Firmware, ct);
        return "handled";
    }

    private static void Count(string kind, string outcome) =>
        PulseTelemetry.MqttMessages.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("outcome", outcome));
}
