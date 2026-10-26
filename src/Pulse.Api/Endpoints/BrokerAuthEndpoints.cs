using Pulse.Api.Mqtt;

namespace Pulse.Api.Endpoints;

/// <summary>
/// What the MQTT broker calls to decide who may connect and what they may do (EMQX's HTTP authentication and authorization).
/// Not part of the device API: only the broker calls it, and it proves it is the broker with the shared secret.
/// </summary>
public static class BrokerAuthEndpoints
{
    public sealed record AuthRequest(string? Username, string? Password);

    public sealed record AclRequest(string? Username, string? Topic, string? Action);

    public static void MapBrokerAuth(this IEndpointRouteBuilder app)
    {
        var broker = app.MapGroup("/mqtt").AllowAnonymous();

        broker.MapPost("/auth", async (AuthRequest? body, HttpContext http, BrokerAccess access, CancellationToken ct) =>
        {
            if (!access.IsBroker(http.Request.Headers.Authorization)) return Results.Unauthorized();

            var verdict = await access.AuthenticateAsync(body?.Username, body?.Password, ct);
            return Results.Json(new { result = verdict == BrokerVerdict.Deny ? "deny" : "allow", is_superuser = verdict == BrokerVerdict.Superuser });
        });

        broker.MapPost("/acl", (AclRequest? body, HttpContext http, BrokerAccess access) =>
            !access.IsBroker(http.Request.Headers.Authorization)
                ? Results.Unauthorized()
                : Results.Json(new { result = access.Authorize(body?.Username, body?.Action, body?.Topic) ? "allow" : "deny" }));
    }
}
