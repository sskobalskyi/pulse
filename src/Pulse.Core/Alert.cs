namespace Pulse.Core;

public sealed record Alert(string DeviceId, string Metric, string Rule, double Value, DateTimeOffset Timestamp);
