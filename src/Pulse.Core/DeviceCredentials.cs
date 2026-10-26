using System.Security.Cryptography;
using System.Text;

namespace Pulse.Core;

/// <summary>
/// The broker password of a device, as the allocation stores it: <c>pbkdf2_sha256$iterations$salt$hash</c>, with salt and
/// hash in base64 and the hash PBKDF2-HMAC-SHA256 of the password's UTF-8 bytes. Pulse never sees the password itself,
/// only this, and checks what a connecting device presents against it.
/// </summary>
public static class DeviceCredentials
{
    private const string Scheme = "pbkdf2_sha256";

    /// <summary>The most work a stored hash may ask for, so a bad row cannot make a login take minutes.</summary>
    private const int MaxIterations = 1_000_000;

    /// <summary>A hash nobody has the password of, checked when the device is unknown so that an unknown device and a wrong password take the same time.</summary>
    public static readonly string Unknown = Create("no-one-has-this-password", 100_000, new byte[16]);

    public static string Create(string password, int iterations, byte[] salt) =>
        $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(Derive(password, salt, iterations, 32))}";

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || stored is null) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations is < 1 or > MaxIterations) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return expected.Length > 0 && CryptographicOperations.FixedTimeEquals(Derive(password, salt, iterations, expected.Length), expected);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, length);
}

/// <summary>What a device may do on the broker: speak on its own topics, and listen to its own commands, and nothing else.</summary>
public static class DeviceAccess
{
    public const string Publish = "publish";
    public const string Subscribe = "subscribe";

    public static bool Allows(string deviceId, string action, string topic) => action switch
    {
        // Everything a device says is under its own id. A topic with a wildcard or someone else's id parses as another device or as nothing.
        Publish => DeviceTopics.Parse(topic) is { } parsed && parsed.DeviceId == deviceId,
        // The commands are Pulse's to publish, and the device's to receive. It has no reason to listen to anything else.
        Subscribe => topic == DeviceTopics.Commands(deviceId),
        _ => false,
    };
}
