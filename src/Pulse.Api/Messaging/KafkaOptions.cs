using Confluent.Kafka;

namespace Pulse.Api.Messaging;

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:19092";
    public int Partitions { get; set; } = 3;

    /// <summary>
    /// Where a consumer group starts when it has no committed offset. Latest skips history on a first start,
    /// which is what a live dashboard wants. Tests use Earliest so messages sent before the group has joined are not missed.
    /// </summary>
    public AutoOffsetReset AutoOffsetReset { get; set; } = AutoOffsetReset.Latest;
}
