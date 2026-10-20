namespace Pulse.Core;

/// <summary>A single measurement emitted by a device. Also the Kafka message payload.</summary>
public sealed record TelemetryReading(string DeviceId, string Metric, double Value, DateTimeOffset Timestamp);
