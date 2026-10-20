namespace Pulse.Api.Messaging;

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:19092";
    public int Partitions { get; set; } = 3;
}
