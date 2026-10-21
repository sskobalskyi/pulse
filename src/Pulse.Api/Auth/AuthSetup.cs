using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Pulse.Api.Auth;

public static class Policies
{
    /// <summary>Any authenticated caller, user or service.</summary>
    public const string Caller = "caller";

    /// <summary>A service caller only.</summary>
    public const string Service = "service";
}

/// <summary>
/// One requirement, checked at request time against <see cref="AuthOptions"/>. With authentication off it always
/// succeeds, so the same endpoints serve the standalone demo and the secured deployment.
/// </summary>
public sealed class CallerRequirement(bool serviceOnly) : IAuthorizationRequirement
{
    public bool ServiceOnly { get; } = serviceOnly;
}

public sealed class CallerHandler(IOptions<AuthOptions> options) : AuthorizationHandler<CallerRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CallerRequirement requirement)
    {
        var auth = options.Value;
        if (!auth.Enabled)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated == true && (!requirement.ServiceOnly || context.User.IsInRole(auth.ServiceRole)))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public static class AuthSetup
{
    public static IServiceCollection AddPulseAuth(this IServiceCollection services)
    {
        services.AddOptions<AuthOptions>()
            .BindConfiguration("Auth")
            .Validate(o => !o.Enabled || !string.IsNullOrWhiteSpace(o.Authority), "Auth:Authority is required when Auth:Enabled is true.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured when first used rather than at registration, so the settings come from the final configuration.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.Authority = auth.Value.Authority;
                jwt.RequireHttpsMetadata = auth.Value.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;
                if (!string.IsNullOrWhiteSpace(auth.Value.Issuer)) jwt.TokenValidationParameters.ValidIssuer = auth.Value.Issuer;
                jwt.TokenValidationParameters.ValidateAudience = !string.IsNullOrWhiteSpace(auth.Value.Audience);
                jwt.TokenValidationParameters.ValidAudience = auth.Value.Audience;
                jwt.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                jwt.TokenValidationParameters.NameClaimType = "preferred_username";
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ctx =>
                    {
                        AddKeycloakRoles(ctx.Principal);
                        return Task.CompletedTask;
                    },

                    // Browsers cannot set headers on a WebSocket, so SignalR sends the token in the query string.
                    OnMessageReceived = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query["access_token"] is { Count: > 0 } token)
                            ctx.Token = token;
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddSingleton<IAuthorizationHandler, CallerHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Caller, p => p.AddRequirements(new CallerRequirement(serviceOnly: false)))
            .AddPolicy(Policies.Service, p => p.AddRequirements(new CallerRequirement(serviceOnly: true)));

        return services;
    }

    /// <summary>
    /// Keycloak keeps roles inside the token, not as role claims: <c>realm_access.roles</c> for realm roles and
    /// <c>resource_access.{client}.roles</c> for client roles. Copy them to role claims so <c>IsInRole</c> works.
    /// </summary>
    internal static void AddKeycloakRoles(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity identity) return;

        var roles = new HashSet<string>(StringComparer.Ordinal);
        CollectRoles(identity.FindFirst("realm_access")?.Value, roles);

        if (identity.FindFirst("resource_access")?.Value is { } resources)
        {
            try
            {
                using var document = JsonDocument.Parse(resources);
                foreach (var client in document.RootElement.EnumerateObject())
                    CollectRoles(client.Value.GetRawText(), roles);
            }
            catch (JsonException)
            {
                // A claim that is not valid JSON carries no roles.
            }
        }

        foreach (var role in roles)
        {
            if (!identity.HasClaim(ClaimTypes.Role, role)) identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
    }

    private static void CollectRoles(string? json, HashSet<string> roles)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("roles", out var list) || list.ValueKind != JsonValueKind.Array)
                return;

            foreach (var role in list.EnumerateArray())
            {
                if (role.ValueKind == JsonValueKind.String && role.GetString() is { Length: > 0 } name) roles.Add(name);
            }
        }
        catch (JsonException)
        {
            // A claim that is not valid JSON carries no roles.
        }
    }
}
