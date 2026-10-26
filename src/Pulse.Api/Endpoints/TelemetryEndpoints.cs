using Microsoft.Extensions.Options;
using Pulse.Api.Auth;
using Pulse.Api.Processing;
using Pulse.Api.Registry;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Api.Endpoints;

public static class TelemetryEndpoints
{
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

    private static async Task<IResult> IngestAsync(TelemetryReading[] readings, IngestService ingest, CancellationToken ct)
    {
        var result = await ingest.IngestAsync(readings, ct);
        if (result.Refusal is { } refusal)
        {
            return refusal.Code is null
                ? Results.Problem(refusal.Title, statusCode: refusal.Status)
                : ApiProblems.Problem(refusal.Status, refusal.Code, refusal.Title, refusal.Detail);
        }

        return Results.Accepted(value: new { accepted = result.Accepted });
    }
}
