using System.Text.Json.Serialization;

namespace Pulse.Core;

/// <summary>Where Pulse's integration events go, as <c>contracts/integration-events.yaml</c> defines them.</summary>
public static class EventNames
{
    /// <summary>The topic exchange every integration event is published to, with the event name as the routing key.</summary>
    public const string Exchange = "integration.events";

    public const string AlertRaised = "alert.raised.v1";
    public const string AlertResolved = "alert.resolved.v1";
    public const string DeviceStatus = "device.status.v1";
    public const string DeviceRetired = "device.retired.v1";
    public const string DeviceAllocated = "device.allocated.v1";
    public const string CommandCompleted = "command.completed.v1";

    /// <summary>The <c>schemaVersion</c> header of every event this version of Pulse publishes.</summary>
    public const string SchemaVersion = "1";
}

/// <summary>The payload of <c>alert.raised.v1</c> and <c>alert.resolved.v1</c>.</summary>
public sealed record AlertEvent(
    string AlertId,
    string DeviceId,
    string ChannelId,
    string Metric,
    string Rule,
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Direction,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Value,
    DateTimeOffset At);

/// <summary>The end of a command: <paramref name="Status"/> is completed, rejected, failed or expired.</summary>
public sealed record CommandCompletedEvent(
    Guid CommandId,
    string DeviceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActuatorId,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    DateTimeOffset FinishedAt);

public sealed record DeviceRetiredEvent(string DeviceId, DateTimeOffset RetiredAt);

/// <param name="Status">online or offline.</param>
/// <param name="Cause">last_will, no_data or reconnect. Pulse reports no_data and reconnect until the MQTT bridge adds last_will.</param>
public sealed record DeviceStatusEvent(string DeviceId, string Status, DateTimeOffset At, string Cause);

/// <summary>
/// The payload of <c>device.allocated.v1</c> that Pulse consumes: a device packed for an order, with everything needed to register it.
/// </summary>
public sealed record DeviceAllocatedMessage(
    string DeviceId,
    Guid OrderId,
    string CustomerId,
    string Sku,
    string Model,
    string ClaimTokenHash,
    DateTimeOffset ClaimTokenExpiresAt,
    string CredentialHash,
    Capabilities Capabilities)
{
    public DeviceRegistration ToRegistration() =>
        new(DeviceId, Model, Capabilities, CredentialHash, ClaimTokenHash, ClaimTokenExpiresAt);
}

/// <summary>Builds the events from what happened. Pure, so every event can be checked against its schema without a broker.</summary>
public static class IntegrationEvents
{
    /// <summary>The event for an alert state change. <paramref name="alertId"/> is shared by the raised and resolved events of one alert.</summary>
    public static (string RoutingKey, AlertEvent Payload) ForAlert(Alert alert, Guid alertId)
    {
        var raised = alert.State == AlertState.Raised;
        var isThreshold = alert.Kind == AlertKind.Threshold;

        return (
            raised ? EventNames.AlertRaised : EventNames.AlertResolved,
            new AlertEvent(
                alertId.ToString(),
                alert.DeviceId,
                alert.ChannelId,
                alert.Metric,
                isThreshold ? "threshold" : "no_data",
                raised ? "raised" : "resolved",
                // Which way a value went is only known when the alert is raised.
                raised && isThreshold ? alert.Direction : null,
                // A no-data alert carries the seconds of silence, which is not a reading, so the event says nothing.
                isThreshold ? alert.Value : null,
                alert.Timestamp));
    }

    public static CommandCompletedEvent ForCommandCompleted(Command command) =>
        new(command.CommandId, command.DeviceId, command.ActuatorId, command.Status, command.Reason, command.FinishedAt ?? throw new ArgumentException("The command is not over.", nameof(command)));

    public static DeviceRetiredEvent ForRetired(string deviceId, DateTimeOffset at) => new(deviceId, at);

    public static DeviceStatusEvent ForStatus(string deviceId, bool online, DateTimeOffset at, string cause) =>
        new(deviceId, online ? "online" : "offline", at, cause);
}
