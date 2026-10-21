using Microsoft.Extensions.Options;
using Pulse.Api.Auth;
using Pulse.Api.Registry;
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

        var service = api.MapGroup("").RequireAuthorization(Policies.Service);
        service.MapPost("/devices", RegisterAsync);
        service.MapPost("/devices/{deviceId}/ship", ShipAsync);
        service.MapPost("/devices/{deviceId}/claim", ClaimAsync).WithMetadata(new ClaimRateLimited());
        service.MapPost("/devices/{deviceId}/retire", RetireAsync);
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
