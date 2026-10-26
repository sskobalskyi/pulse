using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Pulse.Api.Observability;
using Pulse.Api.Registry;
using Pulse.Core;

namespace Pulse.Api.Processing;

/// <param name="Code">Null for the plain 400s that never had a code.</param>
public sealed record IngestRefusal(int Status, string? Code, string Title, string? Detail = null);

public sealed record IngestResult(int Accepted, IngestRefusal? Refusal)
{
    public bool IsAccepted => Refusal is null;
}

/// <summary>
/// What every reading goes through on its way into Pulse, whether it came over HTTP or from a device on MQTT: it has to be
/// plausible, its device must not be retired, and it must fit the channels the device declared. What passes is published
/// to Kafka, where the stream processors take over.
/// </summary>
public sealed class IngestService(IProducer<string, string> producer, DeviceRegistry registry)
{
    public const int MaxBatch = 1000;

    public async Task<IngestResult> IngestAsync(TelemetryReading[] readings, CancellationToken ct)
    {
        if (readings.Length is 0 or > MaxBatch)
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "size"));
            return Refused(new IngestRefusal(StatusCodes.Status400BadRequest, null, $"Batch must contain between 1 and {MaxBatch} readings."));
        }

        if (readings.Any(r => string.IsNullOrWhiteSpace(r.DeviceId) || string.IsNullOrWhiteSpace(r.Metric) || !double.IsFinite(r.Value)))
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "invalid"));
            return Refused(new IngestRefusal(StatusCodes.Status400BadRequest, null, "Every reading needs a device id, a metric and a finite value."));
        }

        // A reading of a known metric has to be plausible. A pH of 70 or a soil moisture of 4000 is a faulty sensor or the
        // wrong unit, and storing it would poison charts and alerts. Metrics the platform does not know are not judged.
        var implausible = readings.Select(r => MetricCatalog.Validate(r.Metric, r.Value)).OfType<string>().Distinct().Take(10).ToList();
        if (implausible.Count > 0)
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "range"));
            return Refused(new IngestRefusal(StatusCodes.Status400BadRequest, "value_out_of_range", "A reading is outside what its metric can measure.", string.Join("; ", implausible)));
        }

        // A retired device is out of service for good: its data is refused rather than quietly stored.
        var deviceIds = readings.Select(r => r.DeviceId).Distinct().ToArray();
        var retired = await registry.RetiredAmongAsync(deviceIds, ct);
        if (retired.Count > 0)
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "retired"));
            return Refused(new IngestRefusal(StatusCodes.Status409Conflict, "device_retired", "The batch contains a retired device.", string.Join(", ", retired.Order())));
        }

        // A registered device declared its channels, so its readings have to fit them, and get the channel they belong to.
        // A device nobody registered declared nothing: its readings are taken as they come.
        var (accepted, problems) = ChannelResolver.Resolve(readings, await registry.DeclaredChannelsAsync(deviceIds, ct));
        if (problems.Count > 0)
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "channel"));
            return Refused(new IngestRefusal(
                StatusCodes.Status400BadRequest,
                problems.Select(p => p.Code).Distinct().Count() == 1 ? problems[0].Code : "invalid_readings",
                "Some readings do not fit the channels their device declared.",
                string.Join(" ", problems.Take(10).Select(p => p.Message))));
        }

        using var activity = PulseTelemetry.Source.StartActivity($"{Topics.Readings} publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", Topics.Readings);
        activity?.SetTag("messaging.batch.message_count", readings.Length);

        // Keyed by device id: Kafka hashes the key, so readings of one device stay ordered on one partition.
        foreach (var r in accepted)
        {
            var message = new Message<string, string> { Key = r.DeviceId, Value = JsonSerializer.Serialize(r), Headers = new Headers() };
            KafkaTraceContext.Inject(message.Headers, activity);
            producer.Produce(Topics.Readings, message);
        }

        await Task.Run(() => producer.Flush(TimeSpan.FromSeconds(5)), ct);
        PulseTelemetry.ReadingsIngested.Add(accepted.Count);
        return new IngestResult(accepted.Count, null);
    }

    private static IngestResult Refused(IngestRefusal refusal) => new(0, refusal);
}
