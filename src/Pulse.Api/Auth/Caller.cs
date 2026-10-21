using System.Security.Claims;

namespace Pulse.Api.Auth;

/// <summary>Who is calling and how much of the registry they may see.</summary>
/// <param name="IsService">A client-credentials caller such as the Greenhaus API.</param>
/// <param name="UserId">The subject of a user token.</param>
/// <param name="Unrestricted">Authentication is off, so there is nobody to scope to.</param>
public sealed record Caller(bool IsService, string? UserId, bool Unrestricted)
{
    /// <summary>
    /// The owner whose devices this caller may see, or null for no limit. A user without a subject gets an owner that
    /// no device has, so the result is empty rather than everything.
    /// </summary>
    public string? OwnerScope => Unrestricted || IsService ? null : UserId ?? "\0no-subject";

    public static Caller From(ClaimsPrincipal user, AuthOptions options)
    {
        if (!options.Enabled) return new Caller(IsService: true, UserId: null, Unrestricted: true);

        var subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return new Caller(user.IsInRole(options.ServiceRole), subject, Unrestricted: false);
    }
}
