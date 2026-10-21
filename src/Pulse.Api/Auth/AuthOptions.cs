namespace Pulse.Api.Auth;

/// <summary>
/// Bound from the <c>Auth</c> section. Authentication is off by default, so the standalone demo runs without
/// an identity provider: every caller is then treated as unrestricted.
/// </summary>
public sealed class AuthOptions
{
    public bool Enabled { get; set; }

    /// <summary>The issuer of the tokens, for example <c>http://localhost:8180/realms/greenhaus</c>. Its discovery document supplies the signing keys.</summary>
    public string? Authority { get; set; }

    /// <summary>
    /// The issuer that tokens carry, when it differs from <see cref="Authority"/>. In Docker the API reaches Keycloak by a
    /// service name (<c>http://keycloak:8080/...</c>) while tokens are issued for the public address (<c>http://localhost:8180/...</c>).
    /// Empty means the issuer from the discovery document.
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>The expected audience. Empty skips the audience check, which suits Keycloak setups without an audience mapper.</summary>
    public string? Audience { get; set; }

    /// <summary>Keycloak serves its discovery document over plain http locally. Keep this true anywhere else.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// The realm or client role that marks a service caller (a client-credentials token), such as the Greenhaus API.
    /// Service callers manage the registry and see every device. Everyone else is a user and sees only their own.
    /// </summary>
    public string ServiceRole { get; set; } = "pulse-service";
}
