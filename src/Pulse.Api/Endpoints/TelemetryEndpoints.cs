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

        // Reading needs a caller, and a user only gets what belongs to the devices they own, like in the registry.
        // A series of a device that was never registered belongs to nobody, so only service callers see it.
        var reads = api.MapGroup("").RequireAuthorization(Policies.Caller);
        reads.MapGet("/devices", async (HttpContext http, PulseDb db, IOptions<AuthOptions> auth, IOptions<SilenceOptions> silence, CancellationToken ct) =>
            Results.Ok(await db.GetDevicesAsync(silence.Value.Timeout, Caller.From(http.User, auth.Value).OwnerScope, ct)));
        reads.MapGet("/devices/{deviceId}/series", async (
            string deviceId, string metric, int? minutes, HttpContext http, PulseDb db, DeviceRegistry registry, IOptions<AuthOptions> auth, CancellationToken ct) =>
        {
            var scope = Caller.From(http.User, auth.Value).OwnerScope;
            if (scope is not null && await registry.GetAsync(deviceId, scope, ct) is null) return Results.NotFound();

            return Results.Ok(await db.GetSeriesAsync(deviceId, metric, TimeSpan.FromMinutes(Math.Clamp(minutes ?? 10, 1, 24 * 60)), ct));
        });
        reads.MapGet("/alerts", async (int? limit, HttpContext http, PulseDb db, IOptions<AuthOptions> auth, CancellationToken ct) =>
            Results.Ok(await db.GetAlertsAsync(Math.Clamp(limit ?? 50, 1, 500), Caller.From(http.User, auth.Value).OwnerScope, ct)));
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

        // A reading of a known metric has to be plausible. A pH of 70 or a soil moisture of 4000 is a faulty sensor or the
        // wrong unit, and storing it would poison charts and alerts. Metrics the platform does not know are not judged.
        var implausible = readings.Select(r => MetricCatalog.Validate(r.Metric, r.Value)).OfType<string>().Distinct().Take(10).ToList();
        if (implausible.Count > 0)
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "range"));
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "value_out_of_range", "A reading is outside what its metric can measure.", string.Join("; ", implausible));
        }

        // A retired device is out of service for good: its data is refused rather than quietly stored.
        var deviceIds = readings.Select(r => r.DeviceId).Distinct().ToArray();
        var retired = await registry.RetiredAmongAsync(deviceIds, ct);
        if (retired.Count > 0)
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "retired"));
            return ApiProblems.Problem(StatusCodes.Status409Conflict, "device_retired", "The batch contains a retired device.", string.Join(", ", retired.Order()));
        }

        // A registered device declared its channels, so its readings have to fit them, and get the channel they belong to.
        // A device nobody registered declared nothing: its readings are taken as they come.
        var (accepted, problems) = ChannelResolver.Resolve(readings, await registry.DeclaredChannelsAsync(deviceIds, ct));
        if (problems.Count > 0)
        {
            PulseTelemetry.BatchesRejected.Add(1, new KeyValuePair<string, object?>("reason", "channel"));
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, problems.Select(p => p.Code).Distinct().Count() == 1 ? problems[0].Code : "invalid_readings",
                "Some readings do not fit the channels their device declared.", string.Join(" ", problems.Take(10).Select(p => p.Message)));
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
        return Results.Accepted(value: new { accepted = accepted.Count });
    }
}
