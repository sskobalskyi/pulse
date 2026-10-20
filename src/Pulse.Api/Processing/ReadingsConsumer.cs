using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Pulse.Api.Messaging;
using Pulse.Core;

namespace Pulse.Api.Processing;

/// <summary>
/// Base class for stream processors. Every subclass is its own consumer group, so each one
/// independently receives every reading, while partitions are balanced across instances of the same group.
/// Offsets are committed only after a batch has been handled (at-least-once delivery).
/// </summary>
public abstract class ReadingsConsumer(IOptions<KafkaOptions> options, ILogger logger) : BackgroundService
{
    private const int MaxBatchSize = 500;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(250);

    protected abstract string GroupId { get; }

    protected abstract Task HandleAsync(IReadOnlyList<TelemetryReading> batch, CancellationToken ct);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = GroupId,
            AutoOffsetReset = AutoOffsetReset.Latest,
            EnableAutoCommit = false,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.Readings);
        logger.LogInformation("Consumer group {GroupId} subscribed to {Topic}", GroupId, Topics.Readings);

        var batch = new List<TelemetryReading>(MaxBatchSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                batch.Clear();
                var deadline = DateTime.UtcNow + BatchWindow;
                while (batch.Count < MaxBatchSize && DateTime.UtcNow < deadline)
                {
                    var result = consumer.Consume(TimeSpan.FromMilliseconds(50));
                    if (result is null) continue;

                    var reading = JsonSerializer.Deserialize<TelemetryReading>(result.Message.Value);
                    if (reading is not null) batch.Add(reading);
                }

                if (batch.Count == 0) continue;

                try
                {
                    await HandleAsync(batch, ct);
                    consumer.Commit();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not committed: the batch is redelivered after a rebalance or restart.
                    logger.LogError(ex, "Group {GroupId} failed to handle a batch of {Count}", GroupId, batch.Count);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            consumer.Close();
        }
    }
}
