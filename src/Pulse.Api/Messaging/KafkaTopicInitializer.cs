using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Options;
using Pulse.Core;

namespace Pulse.Api.Messaging;

/// <summary>Creates the topics on startup so the API works against a fresh broker.</summary>
public sealed class KafkaTopicInitializer(IOptions<KafkaOptions> options, ILogger<KafkaTopicInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var kafka = options.Value;
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();

        var specs = new[] { Topics.Readings, Topics.Alerts }
            .Select(t => new TopicSpecification { Name = t, NumPartitions = kafka.Partitions, ReplicationFactor = 1 });

        try
        {
            await admin.CreateTopicsAsync(specs);
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.TopicAlreadyExists or ErrorCode.NoError))
        {
            logger.LogDebug("Kafka topics already exist");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
