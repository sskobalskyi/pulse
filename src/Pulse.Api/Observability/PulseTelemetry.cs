using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pulse.Api.Observability;

/// <summary>The application's own traces and metrics. Everything else (HTTP, Kestrel, Npgsql) comes from library instrumentation.</summary>
public static class PulseTelemetry
{
    public const string Name = "Pulse";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    private static readonly ConcurrentDictionary<(string Group, int Partition), long> Lag = new();

    public static readonly Counter<long> ReadingsIngested =
        Meter.CreateCounter<long>("pulse.readings.ingested", "{reading}", "Readings accepted by the ingestion API and published to Kafka.");

    public static readonly Counter<long> BatchesRejected =
        Meter.CreateCounter<long>("pulse.ingest.rejected", "{batch}", "Ingestion batches rejected as invalid.");

    public static readonly Histogram<int> ConsumerBatchSize =
        Meter.CreateHistogram<int>("pulse.consumer.batch.size", "{reading}", "Readings handled per consumer batch.");

    public static readonly Histogram<double> ConsumerBatchDuration =
        Meter.CreateHistogram<double>("pulse.consumer.batch.duration", "s", "Time to handle one consumer batch, per consumer group and outcome.");

    public static readonly Counter<long> ConsumerFailures =
        Meter.CreateCounter<long>("pulse.consumer.failures", "{batch}", "Batches that failed and will be redelivered.");

    public static readonly Counter<long> AlertsEmitted =
        Meter.CreateCounter<long>("pulse.alerts", "{alert}", "Alert state changes, by state and metric.");

    public static readonly UpDownCounter<long> LiveConnections =
        Meter.CreateUpDownCounter<long>("pulse.signalr.connections", "{connection}", "Browsers currently connected to the live hub.");

    static PulseTelemetry() =>
        Meter.CreateObservableGauge(
            "pulse.consumer.lag",
            () => Lag.Select(l => new Measurement<long>(
                l.Value,
                new KeyValuePair<string, object?>("group", l.Key.Group),
                new KeyValuePair<string, object?>("partition", l.Key.Partition))),
            "{message}",
            "Messages a consumer group is behind the end of each partition it owns.");

    /// <summary>Replaces the lag of one consumer group; partitions it no longer owns disappear from the gauge.</summary>
    public static void SetLag(string group, IReadOnlyDictionary<int, long> lagByPartition)
    {
        foreach (var key in Lag.Keys.Where(k => k.Group == group && !lagByPartition.ContainsKey(k.Partition)))
            Lag.TryRemove(key, out _);

        foreach (var (partition, lag) in lagByPartition)
            Lag[(group, partition)] = lag;
    }
}
