using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Pulse.Api.Auth;
using Pulse.Api.Observability;
using Pulse.Api.Processing;
using Pulse.Api.Registry;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Api.Endpoints;

public static class TelemetryEndpoints
{
    public const int MaxBatch = 1000;

    public static void MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // Sending data is for service callers (the simulator, the MQTT bridge). Everything stays open while authentication is off.
        api.MapPost("/telemetry", IngestAsync).RequireAuthorization(Policies.Service);

        // Reading needs a caller. These endpoints do not yet limit a user to their own devices, which is the next change.
        var reads = api.MapGroup("").RequireAuthorization(Policies.Caller);
        reads.MapGet("/devices", async (PulseDb db, IOptions<SilenceOptions> silence, CancellationToken ct) =>
            Results.Ok(await db.GetDevicesAsync(silence.Value.Timeout, ct)));
        reads.MapGet("/devices/{deviceId}/series", async (string deviceId, string metric, int? minutes, PulseDb db, CancellationToken ct) =>
            Results.Ok(await db.GetSeriesAsync(deviceId, metric, TimeSpan.FromMinutes(Math.Clamp(minutes ?? 10, 1, 24 * 60)), ct)));
        reads.MapGet("/alerts", async (int? limit, PulseDb db, CancellationToken ct) =>
            Results.Ok(await db.GetAlertsAsync(Math.Clamp(limit ?? 50, 1, 500), ct)));
    }

    private static async Task<IResult> IngestAsync(
        TelemetryReading[] readings, IProducer<string, string> producer, DeviceRegistry registry, CancellationToken ct)
    {
        if (readings.Length is 0 or > MaxBatch)
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "size"));
            return Results.Problem($"Batch must contain between 1 and {MaxBatch} readings.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (readings.Any(r => string.IsNullOrWhiteSpace(r.DeviceId) || string.IsNullOrWhiteSpace(r.Metric) || !double.IsFinite(r.Value)))
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "invalid"));
            return Results.Problem("Every reading needs a device id, a metric and a finite value.", statusCode: StatusCodes.Status400BadRequest);
        }

        // A retired device is out of service for good: its data is refused rather than quietly stored.
        var retired = await registry.RetiredAmongAsync(readings.Select(r => r.DeviceId).Distinct().ToArray(), ct);
        if (retired.Count > 0)
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "retired"));
            return ApiProblems.Problem(StatusCodes.Status409Conflict, "device_retired", "The batch contains a retired device.", string.Join(", ", retired.Order()));
        }

        using var activity = PulseTelemetry.Source.StartActivity($"{Topics.Readings} publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", Topics.Readings);
        activity?.SetTag("messaging.batch.message_count", readings.Length);

        // Keyed by device id: Kafka hashes the key, so readings of one device stay ordered on one partition.
        foreach (var r in readings)
        {
            var message = new Message<string, string> { Key = r.DeviceId, Value = JsonSerializer.Serialize(r), Headers = new Headers() };
            KafkaTraceContext.Inject(message.Headers, activity);
            producer.Produce(Topics.Readings, message);
        }

        await Task.Run(() => producer.Flush(TimeSpan.FromSeconds(5)), ct);
        PulseTelemetry.ReadingsIngested.Add(readings.Length);
        return Results.Accepted(value: new { accepted = readings.Length });
    }
}
