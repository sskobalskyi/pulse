using System.Text.Json.Serialization;

namespace Pulse.Core;

/// <summary>A single measurement emitted by a device. Also the Kafka message payload.</summary>
/// <param name="Channel">
/// The sensor channel that produced it. A device can have several channels of one metric, for example one soil
/// moisture sensor per zone. Senders that do not know about channels leave it out.
/// </param>
public sealed record TelemetryReading(string DeviceId, string Metric, double Value, DateTimeOffset Timestamp, string? Channel = null)
{
    /// <summary>
    /// The series this reading belongs to, together with the device. A reading with no channel is on the channel named
    /// after its metric, so existing senders keep working and a device with one sensor per metric needs no channel ids.
    /// </summary>
    [JsonIgnore]
    public string ChannelId => string.IsNullOrWhiteSpace(Channel) ? Metric : Channel;
}
