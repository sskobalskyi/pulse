using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Core;

public enum DeviceTopicKind
{
    Manifest,
    Telemetry,
    State,
    Status,
    CommandAck,
}

/// <param name="ChannelId">Only for telemetry: the channel the reading is of.</param>
public sealed record DeviceTopic(string DeviceId, DeviceTopicKind Kind, string? ChannelId = null);

/// <summary>The topics of <c>contracts/device-mqtt.yaml</c>. Everything a device says is under <c>greenhaus/v1/devices/{deviceId}/</c>.</summary>
public static class DeviceTopics
{
    public const string Prefix = "greenhaus/v1/devices";

    /// <summary>What the bridge listens to. <c>+</c> is one level, so a device id or a channel id never contains a slash.</summary>
    public static readonly IReadOnlyList<string> Filters =
    [
        $"{Prefix}/+/manifest",
        $"{Prefix}/+/telemetry/+",
        $"{Prefix}/+/state",
        $"{Prefix}/+/status",
        $"{Prefix}/+/commands/ack",
    ];

    public static string Commands(string deviceId) => $"{Prefix}/{deviceId}/commands";

    /// <summary>What a topic means, or null for a topic that is not one of the device's own (such as the commands the bridge sends).</summary>
    public static DeviceTopic? Parse(string topic)
    {
        var parts = topic.Split('/');
        if (parts.Length < 5 || $"{parts[0]}/{parts[1]}/{parts[2]}" != Prefix || parts[3].Length == 0) return null;

        var device = parts[3];
        return (parts.Length, parts[4]) switch
        {
            (5, "manifest") => new DeviceTopic(device, DeviceTopicKind.Manifest),
            (5, "state") => new DeviceTopic(device, DeviceTopicKind.State),
            (5, "status") => new DeviceTopic(device, DeviceTopicKind.Status),
            (6, "telemetry") when parts[5].Length > 0 => new DeviceTopic(device, DeviceTopicKind.Telemetry, parts[5]),
            (6, "commands") when parts[5] == "ack" => new DeviceTopic(device, DeviceTopicKind.CommandAck),
            _ => null,
        };
    }
}

/// <summary>A reading as a device publishes it. The device and the channel are in the topic.</summary>
public sealed record MqttReading(DateTimeOffset? Ts, double? Value, double? Raw, long? Seq);

/// <summary>Online, which the device publishes when it connects, or offline, which the broker publishes as the device's last will.</summary>
public sealed record MqttStatus(string? Status, DateTimeOffset? Ts);

public sealed record MqttAck(Guid? CommandId, string? Status, string? Reason, DateTimeOffset? Ts);

public sealed record MqttManifest(string? Model, string? Firmware);

/// <summary>Reads what devices publish. A payload that is not what the contract describes is null, never an exception: a faulty device must not hurt the bridge.</summary>
public static class DeviceMessages
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions Out = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static T? Read<T>(ReadOnlySpan<byte> payload) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The reading, once the metric is known from the channel the device declared. Null when it lacks a time or a finite value.</summary>
    public static TelemetryReading? ToReading(string deviceId, string channelId, string metric, MqttReading payload) =>
        payload is { Ts: { } ts, Value: { } value } && double.IsFinite(value)
            ? new TelemetryReading(deviceId, metric, value, ts, channelId)
            : null;

    /// <summary>True for online, false for offline, null for anything else.</summary>
    public static bool? IsOnline(MqttStatus status) => status.Status switch
    {
        "online" => true,
        "offline" => false,
        _ => null,
    };

    /// <summary>What a device may report about a command: accepted, or one of the endings. Pulse alone says queued and sent.</summary>
    public static bool TryAck(MqttAck ack, out Guid commandId, out CommandStatus status)
    {
        commandId = ack.CommandId ?? Guid.Empty;
        status = default;
        return commandId != Guid.Empty && CommandStatuses.TryParse(ack.Status, out status) && status >= CommandStatus.Accepted;
    }

    /// <summary>The command as the device receives it.</summary>
    public static byte[] CommandPayload(DeviceCommand command) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                commandId = command.CommandId,
                type = command.Type,
                actuatorId = command.ActuatorId,
                durationSeconds = command.DurationSeconds,
                enabled = command.Enabled,
                expiresAt = command.ExpiresAt,
            },
            Out);
}
