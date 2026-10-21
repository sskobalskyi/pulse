using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Pulse.Core;

namespace Pulse.Tests;

/// <summary>
/// What the tests that run the API with authentication on have in common: a host that trusts a key the test holds,
/// and tokens shaped like the ones Keycloak issues, so claims handling and policies run without a Keycloak.
/// </summary>
public static class AuthTestKit
{
    public const string Issuer = "https://issuer.test/realms/greenhaus";

    public static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(32));

    /// <summary>A host on the shared database with authentication on, validating tokens against <see cref="SigningKey"/>.</summary>
    public static WebApplicationFactory<Program> CreateHost(PipelineFixture stack, params (string, string)[] extra) =>
        stack.CreateHttpOnlyHost(
            services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
            {
                // Validate against the test key instead of fetching the issuer's discovery document.
                jwt.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                jwt.TokenValidationParameters.IssuerSigningKey = SigningKey;
                jwt.TokenValidationParameters.ValidIssuer = Issuer;
            }),
            [("Auth:Enabled", "true"), ("Auth:Authority", Issuer), .. extra]);

    public static string Token(
        string subject,
        bool service = false,
        TimeSpan? lifetime = null,
        string issuer = Issuer,
        SecurityKey? key = null,
        string? audience = null,
        bool clientRole = false)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["preferred_username"] = subject };
        if (service && !clientRole) claims["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "offline_access", "pulse-service" } };
        if (service && clientRole) claims["resource_access"] = new Dictionary<string, object> { ["greenhaus-api"] = new Dictionary<string, object> { ["roles"] = new[] { "pulse-service" } } };

        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = now.AddMinutes(-10),
            Expires = now + (lifetime ?? TimeSpan.FromHours(1)),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }

    public static string ServiceToken() => Token("greenhaus-api", service: true);

    public static string NewId() => $"auth-{Guid.NewGuid():N}"[..14];

    public static object Registration(string id) => new
    {
        id,
        model = "greenhaus-pot-v1",
        capabilities = new { channels = new[] { new { id = "soil-1", metric = "soil_moisture" } }, actuators = Array.Empty<object>() },
        credentialHash = "h",
        claimTokenHash = ClaimTokens.Hash("token"),
        claimTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };
}
