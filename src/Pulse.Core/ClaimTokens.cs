using System.Security.Cryptography;
using System.Text;

namespace Pulse.Core;

/// <summary>
/// The claim token is a long random string printed on the device (in the QR code). Only its hash is stored,
/// so the registry cannot reveal it. The format of the hash is lower case hex of the SHA-256 of the UTF-8
/// token, optionally written as <c>sha256:&lt;hex&gt;</c>. A plain fast hash is enough because the token has
/// plenty of entropy; a password-style slow hash would only protect guessable secrets.
/// </summary>
public static class ClaimTokens
{
    private const string Prefix = "sha256:";

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Normalises a stored or supplied hash to bare lower case hex, or null if it is not a SHA-256 hex string.</summary>
    public static string? Normalise(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;

        var hex = hash.Trim();
        if (hex.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) hex = hex[Prefix.Length..];
        hex = hex.ToLowerInvariant();

        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>Compares in constant time, so response timing does not reveal how much of the hash matched.</summary>
    public static bool Matches(string token, string storedHash)
    {
        var expected = Normalise(storedHash);
        if (expected is null || string.IsNullOrEmpty(token)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(token)),
            Encoding.ASCII.GetBytes(expected));
    }
}
