using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Pulse.Api.Auth;
using Pulse.Api.Registry;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Api.Endpoints;

/// <summary>
/// The device registry surface of the Pulse Device API contract (<c>contracts/pulse-device-api.yaml</c>).
/// Registering, shipping, claiming and retiring are for service callers. Reading is for everyone, scoped to the
/// devices the caller owns.
/// </summary>
public static class DeviceRegistryEndpoints
{
    public const string BasePath = "/api/pulse/v2";

    public static void MapDeviceRegistry(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(BasePath).RequireAuthorization(Policies.Caller);

        api.MapGet("/devices", ListAsync);
        api.MapGet("/devices/{deviceId}", GetAsync);
        api.MapPut("/devices/{deviceId}/channels/{channelId}/calibration", CalibrateAsync);
        api.MapGet("/devices/{deviceId}/channels/{channelId}/series", SeriesAsync);
        api.MapGet("/devices/{deviceId}/rules", GetRulesAsync);

        var service = api.MapGroup("").RequireAuthorization(Policies.Service);
        service.MapPost("/devices", RegisterAsync);
        service.MapPost("/devices/{deviceId}/ship", ShipAsync);
        service.MapPost("/devices/{deviceId}/claim", ClaimAsync).WithMetadata(new ClaimRateLimited());
        service.MapPost("/devices/{deviceId}/retire", RetireAsync);
        service.MapPut("/devices/{deviceId}/rules", SetRulesAsync);
    }

    private static async Task<IResult> ListAsync(
        string? state, string? ownerId, HttpContext http, DeviceRegistry registry, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        Lifecycle? filter = null;
        if (state is not null)
        {
            if (!DeviceLifecycle.TryParse(state, out var parsed))
                return ApiProblems.Problem(StatusCodes.Status400BadRequest, "invalid_state_filter", "Unknown state.", $"'{state}' is not one of allocated, shipped, claimed, active, retired.");
            filter = parsed;
        }

        var caller = Caller.From(http.User, auth.Value);

        // A user is always limited to their own devices. Only a service caller may ask for another owner's.
        var scope = caller.OwnerScope ?? (caller.IsService ? ownerId : null);
        return Results.Ok(await registry.ListAsync(filter, scope, ct));
    }

    private static async Task<IResult> GetAsync(string deviceId, HttpContext http, DeviceRegistry registry, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        var device = await registry.GetAsync(deviceId, Caller.From(http.User, auth.Value).OwnerScope, ct);
        return device is null ? ApiProblems.NotFound() : Results.Ok(device);
    }

    private static async Task<IResult> RegisterAsync(DeviceRegistration? registration, DeviceRegistry registry, TimeProvider time, CancellationToken ct)
    {
        if (registration is null) return ApiProblems.Invalid(["a request body is required"]);

        var errors = RegistrationValidator.Validate(registration, time.GetUtcNow());
        if (errors.Count > 0) return ApiProblems.Invalid(errors);

        var (device, created) = await registry.RegisterAsync(registration, ct);
        return created ? Results.Created($"{BasePath}/devices/{device.Id}", device) : Results.Ok(device);
    }

    private static async Task<IResult> ShipAsync(string deviceId, DeviceRegistry registry, CancellationToken ct) =>
        ToResult(await registry.ShipAsync(deviceId, ct));

    private static async Task<IResult> RetireAsync(string deviceId, DeviceRegistry registry, CancellationToken ct) =>
        ToResult(await registry.RetireAsync(deviceId, ct));

    private static async Task<IResult> ClaimAsync(string deviceId, ClaimRequest? request, DeviceRegistry registry, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.OwnerId))
            return ApiProblems.Invalid(["token and ownerId are required"]);

        return ToResult(await registry.ClaimAsync(deviceId, request.Token, request.OwnerId, ct));
    }

    private static async Task<IResult> CalibrateAsync(
        string deviceId, string channelId, CalibrationRequest? request, HttpContext http, DeviceRegistry registry, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (request is not { DryRaw: { } dry, WetRaw: { } wet } || !double.IsFinite(dry) || !double.IsFinite(wet))
            return ApiProblems.Invalid(["dryRaw and wetRaw are required numbers"]);
        if (dry == wet)
            return ApiProblems.Invalid(["dryRaw and wetRaw must differ, otherwise readings cannot be scaled"]);

        var result = await registry.CalibrateAsync(deviceId, channelId, dry, wet, Caller.From(http.User, auth.Value).OwnerScope, ct);
        return result.IsOk ? Results.Ok(result.Value) : ApiProblems.NotFound("device or channel");
    }

    private static async Task<IResult> GetRulesAsync(
        string deviceId, HttpContext http, DeviceRegistry registry, DeviceRuleStore rules, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        var device = await registry.GetAsync(deviceId, Caller.From(http.User, auth.Value).OwnerScope, ct);
        return device is null ? ApiProblems.NotFound() : Results.Ok(await rules.ListAsync(deviceId, ct));
    }

    /// <summary>
    /// Replaces the rules of a device. Only a service caller sets them: Greenhaus turns a pot's requirements into rules, and
    /// letting anyone write rules straight to Pulse would bypass what Greenhaus allows a tier to have.
    /// </summary>
    private static async Task<IResult> SetRulesAsync(
        string deviceId, DeviceRule[]? body, DeviceRegistry registry, DeviceRuleStore rules, CancellationToken ct)
    {
        var device = await registry.GetAsync(deviceId, null, ct);
        if (device is null) return ApiProblems.NotFound();
        if (device.State == Lifecycle.Retired.ToWire())
            return ApiProblems.Problem(StatusCodes.Status409Conflict, "invalid_state", "A retired device has no rules to set.");

        var problems = RuleValidator.Validate(body, [.. device.Capabilities.Channels.Select(c => new ChannelRef(c.Id, c.Metric))]);
        if (problems.Count > 0)
        {
            var code = problems.All(p => p.Code == "unknown_channel") ? "unknown_channel" : "validation_failed";
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, code, "The rules are invalid.", string.Join(" ", problems.Select(p => p.Message)));
        }

        await rules.ReplaceAsync(deviceId, body!, ct);
        return Results.Ok(await rules.ListAsync(deviceId, ct));
    }

    // The longest window each resolution answers, which keeps one request from asking for millions of points.
    private static readonly Dictionary<SeriesResolution, TimeSpan> MaxWindow = new()
    {
        [SeriesResolution.Raw] = TimeSpan.FromHours(6),
        [SeriesResolution.OneMinute] = TimeSpan.FromDays(7),
        [SeriesResolution.OneHour] = TimeSpan.FromDays(365),
    };

    private static async Task<IResult> SeriesAsync(
        string deviceId, string channelId, string? from, string? to, string? resolution,
        HttpContext http, DeviceRegistry registry, PulseDb db, IOptions<AuthOptions> auth, TimeProvider time, CancellationToken ct)
    {
        if (!TryParseInstant(from, out var start))
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "invalid_range", "from is required and must be a date and time, such as 2026-10-21T12:00:00Z.");

        var end = time.GetUtcNow();
        if (to is not null && !TryParseInstant(to, out end))
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "invalid_range", "to must be a date and time, such as 2026-10-21T12:00:00Z.");
        if (start >= end)
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "invalid_range", "from must be before to.");

        var chosen = resolution switch
        {
            null or "raw" => SeriesResolution.Raw,
            "1m" => SeriesResolution.OneMinute,
            "1h" => SeriesResolution.OneHour,
            _ => (SeriesResolution?)null,
        };
        if (chosen is not { } granularity)
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "invalid_resolution", "resolution must be raw, 1m or 1h.");
        if (end - start > MaxWindow[granularity])
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "window_too_large", "The window is too long for this resolution.",
                $"At most {Describe(MaxWindow[granularity])} for this resolution. Ask for a coarser one to see further back.");
        }

        // A user sees the series of their own devices. A device nobody registered is for service callers, like in the registry.
        var scope = Caller.From(http.User, auth.Value).OwnerScope;
        var device = await registry.GetAsync(deviceId, scope, ct);
        if (device is null && scope is not null) return ApiProblems.NotFound();
        if (device is not null && device.Capabilities.Channels.All(c => c.Id != channelId)) return ApiProblems.NotFound("channel");

        var points = await db.GetChannelSeriesAsync(deviceId, channelId, start, end, granularity, ct);
        if (granularity == SeriesResolution.Raw && points.Count >= PulseDb.MaxRawPoints) http.Response.Headers["X-Pulse-Truncated"] = "true";

        // A raw point is one reading, so a minimum and maximum would only repeat it.
        var raw = granularity == SeriesResolution.Raw;
        return Results.Ok(points.Select(p => new SeriesPointDto(p.Timestamp, p.Avg, raw ? null : p.Min, raw ? null : p.Max)));
    }

    private static string Describe(TimeSpan window) =>
        window.TotalHours <= 48 ? $"{window.TotalHours:0} hours" : $"{window.TotalDays:0} days";

    private static bool TryParseInstant(string? text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant);

    private static IResult ToResult(RegistryResult<Device> result) => result.Error switch
    {
        RegistryError.None => Results.Ok(result.Value),
        RegistryError.NotFound => ApiProblems.NotFound(),
        RegistryError.InvalidState => ApiProblems.Problem(StatusCodes.Status409Conflict, "invalid_state", "The device is not in a state that allows this."),
        RegistryError.TokenInvalid => ApiProblems.Problem(StatusCodes.Status409Conflict, "token_invalid", "The claim token is not valid for this device."),
        RegistryError.TokenExpired => ApiProblems.Problem(StatusCodes.Status409Conflict, "token_expired", "The claim token has expired."),
        _ => throw new InvalidOperationException($"Unhandled registry error {result.Error}"),
    };
}

/// <param name="Token">The one-time token printed on the device.</param>
/// <param name="OwnerId">Keycloak subject of the new owner.</param>
public sealed record ClaimRequest(string? Token, string? OwnerId);

public sealed record CalibrationRequest(double? DryRaw, double? WetRaw);

public sealed record SeriesPointDto(
    [property: JsonPropertyName("ts")] DateTimeOffset Ts,
    double Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Min,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Max);
