using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Pulse.Api.Messaging;
using Pulse.Api.Observability;
using Pulse.Core;

namespace Pulse.Api.Processing;

/// <summary>
/// Base class for stream processors. Every subclass is its own consumer group, so each one
/// independently receives every reading, while partitions are balanced across instances of the same group.
/// Offsets are committed only after a batch has been handled (at-least-once delivery).
/// Each batch is one span linked to the producer spans of its messages, and reports size, duration and lag.
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
            AutoOffsetReset = options.Value.AutoOffsetReset,
            EnableAutoCommit = false,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topics.Readings);
        logger.LogInformation("Consumer group {GroupId} subscribed to {Topic}", GroupId, Topics.Readings);

        var batch = new List<TelemetryReading>(MaxBatchSize);
        var links = new BatchLinks();
        var groupTag = new KeyValuePair<string, object?>("group", GroupId);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                batch.Clear();
                links.Clear();
                var deadline = DateTime.UtcNow + BatchWindow;
                while (batch.Count < MaxBatchSize && DateTime.UtcNow < deadline)
                {
                    var result = consumer.Consume(TimeSpan.FromMilliseconds(50));
                    if (result is null) continue;

                    var reading = JsonSerializer.Deserialize<TelemetryReading>(result.Message.Value);
                    if (reading is null) continue;

                    batch.Add(reading);
                    links.Add(result.Message.Headers);
                }

                if (batch.Count == 0) continue;

                // A new root span per batch: it has no single parent, only links to the requests that produced it.
                // A default parent context falls back to Activity.Current, so make sure nothing is current.
                Activity.Current = null;
                using var activity = PulseTelemetry.Source.StartActivity(
                    $"{GroupId} process", ActivityKind.Consumer, parentContext: default, links: links.ToLinks());
                activity?.SetTag("messaging.system", "kafka");
                activity?.SetTag("messaging.destination.name", Topics.Readings);
                activity?.SetTag("messaging.consumer.group.name", GroupId);
                activity?.SetTag("messaging.batch.message_count", batch.Count);

                var started = Stopwatch.GetTimestamp();
                var outcome = "ok";
                try
                {
                    await HandleAsync(batch, ct);
                    consumer.Commit();
                    ReportLag(consumer);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not committed: the batch is redelivered after a rebalance or restart.
                    outcome = "error";
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.AddException(ex);
                    PulseTelemetry.ConsumerFailures.Add(1, groupTag);
                    logger.LogError(ex, "Group {GroupId} failed to handle a batch of {Count}", GroupId, batch.Count);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                finally
                {
                    PulseTelemetry.ConsumerBatchSize.Record(batch.Count, groupTag);
                    PulseTelemetry.ConsumerBatchDuration.Record(
                        Stopwatch.GetElapsedTime(started).TotalSeconds, groupTag, new KeyValuePair<string, object?>("outcome", outcome));
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

    private void ReportLag(IConsumer<string, string> consumer)
    {
        var lag = new Dictionary<int, long>();
        foreach (var partition in consumer.Assignment)
        {
            var position = consumer.Position(partition);
            if (position == Offset.Unset) continue;

            lag[partition.Partition.Value] = Math.Max(0, consumer.GetWatermarkOffsets(partition).High.Value - position.Value);
        }

        PulseTelemetry.SetLag(GroupId, lag);
    }
}
