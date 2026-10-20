using System.Text.Json;
using Confluent.Kafka;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Api.Endpoints;

public static class TelemetryEndpoints
{
    public const int MaxBatch = 1000;

    public static void MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/telemetry", IngestAsync);
        api.MapGet("/devices", async (PulseDb db, CancellationToken ct) => Results.Ok(await db.GetDevicesAsync(ct)));
        api.MapGet("/devices/{deviceId}/series", async (string deviceId, string metric, int? minutes, PulseDb db, CancellationToken ct) =>
            Results.Ok(await db.GetSeriesAsync(deviceId, metric, TimeSpan.FromMinutes(Math.Clamp(minutes ?? 10, 1, 24 * 60)), ct)));
        api.MapGet("/alerts", async (int? limit, PulseDb db, CancellationToken ct) =>
            Results.Ok(await db.GetAlertsAsync(Math.Clamp(limit ?? 50, 1, 500), ct)));
    }

    private static async Task<IResult> IngestAsync(TelemetryReading[] readings, IProducer<string, string> producer, CancellationToken ct)
    {
        if (readings.Length is 0 or > MaxBatch)
            return Results.Problem($"Batch must contain between 1 and {MaxBatch} readings.", statusCode: StatusCodes.Status400BadRequest);

        if (readings.Any(r => string.IsNullOrWhiteSpace(r.DeviceId) || string.IsNullOrWhiteSpace(r.Metric) || !double.IsFinite(r.Value)))
            return Results.Problem("Every reading needs a device id, a metric and a finite value.", statusCode: StatusCodes.Status400BadRequest);

        // Keyed by device id: Kafka hashes the key, so readings of one device stay ordered on one partition.
        foreach (var r in readings)
            producer.Produce(Topics.Readings, new Message<string, string> { Key = r.DeviceId, Value = JsonSerializer.Serialize(r) });

        await Task.Run(() => producer.Flush(TimeSpan.FromSeconds(5)), ct);
        return Results.Accepted(value: new { accepted = readings.Length });
    }
}
