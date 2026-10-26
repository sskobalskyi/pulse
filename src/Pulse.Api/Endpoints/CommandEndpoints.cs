using Microsoft.Extensions.Options;
using Pulse.Api.Auth;
using Pulse.Api.Commands;
using Pulse.Api.Registry;
using Pulse.Core;

namespace Pulse.Api.Endpoints;

/// <summary>
/// The command surface of the Pulse Device API contract. Greenhaus decides what a device should do and sends it here
/// as a service caller. Pulse queues it, delivers it and reports how it ended. Users can follow the commands of their own devices.
/// </summary>
public static class CommandEndpoints
{
    public static void MapCommands(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(DeviceRegistryEndpoints.BasePath);

        api.MapGet("/devices/{deviceId}/commands", ListAsync).RequireAuthorization(Policies.Caller);
        api.MapGet("/devices/{deviceId}/commands/{commandId}", GetAsync).RequireAuthorization(Policies.Caller);
        // Making a device do something, a pump above all, is for the platform. A user asks Greenhaus, which checks what their tier allows.
        api.MapPost("/devices/{deviceId}/commands", SendAsync).RequireAuthorization(Policies.Service);
    }

    private static async Task<IResult> SendAsync(
        string deviceId, CommandRequest? body, DeviceRegistry registry, CommandStore store, TimeProvider time, CancellationToken ct)
    {
        if (body is null) return ApiProblems.Problem(StatusCodes.Status400BadRequest, "validation_failed", "A command is required.");

        var device = await registry.GetAsync(deviceId, null, ct);
        if (device is null) return ApiProblems.NotFound();
        if (device.State == "retired")
            return ApiProblems.Problem(StatusCodes.Status409Conflict, "invalid_state", "The device is retired and takes no commands.");

        var problems = CommandValidator.Validate(body, device.Capabilities, time.GetUtcNow());
        if (problems.Count > 0)
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, problems[0].Code, "The command is invalid.", string.Join("; ", problems.Select(p => p.Message)));

        var result = await store.EnqueueAsync(deviceId, body, ct);
        return result.Outcome switch
        {
            EnqueueOutcome.Created or EnqueueOutcome.Existing => Results.Json(result.Command, statusCode: StatusCodes.Status202Accepted),
            EnqueueOutcome.IdReused => ApiProblems.Problem(
                StatusCodes.Status409Conflict, "command_id_reused", "This commandId belongs to a different command.",
                "Send the same body to retry a command, or a new commandId for a new one."),
            EnqueueOutcome.DeviceRetired => ApiProblems.Problem(StatusCodes.Status409Conflict, "invalid_state", "The device is retired and takes no commands."),
            _ => ApiProblems.NotFound(),
        };
    }

    private static async Task<IResult> ListAsync(
        string deviceId, int? limit, HttpContext http, DeviceRegistry registry, CommandStore store, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await registry.GetAsync(deviceId, Caller.From(http.User, auth.Value).OwnerScope, ct) is null) return ApiProblems.NotFound();

        return Results.Ok(await store.ListAsync(deviceId, Math.Clamp(limit ?? 50, 1, 200), ct));
    }

    private static async Task<IResult> GetAsync(
        string deviceId, string commandId, HttpContext http, DeviceRegistry registry, CommandStore store, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        // An id that is not a uuid cannot belong to a command, which is the same as not finding it.
        if (!Guid.TryParse(commandId, out var id)) return ApiProblems.NotFound("command");

        if (await registry.GetAsync(deviceId, Caller.From(http.User, auth.Value).OwnerScope, ct) is null) return ApiProblems.NotFound();

        var command = await store.GetAsync(deviceId, id, ct);
        return command is null ? ApiProblems.NotFound("command") : Results.Ok(command);
    }
}
