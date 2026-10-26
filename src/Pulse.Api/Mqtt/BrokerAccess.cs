using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Pulse.Api.Registry;
using Pulse.Core;

namespace Pulse.Api.Mqtt;

/// <summary>
/// Who may connect to the broker and what they may do there. The broker asks Pulse over HTTP, because Pulse is the one
/// that holds each device's credential hash and knows whether the device is retired. A device connects with its id as the
/// user name and its plaintext credential as the password, and may then speak on its own topics and hear its own commands.
/// </summary>
public sealed class BrokerAccess(DeviceRegistry registry, IOptions<MqttOptions> options)
{
    /// <summary>Checking a password is deliberately slow. Only a few at a time, so a flood of attempts cannot starve the rest of the API.</summary>
    private static readonly SemaphoreSlim Gate = new(4);

    public async Task<BrokerVerdict> AuthenticateAsync(string? username, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return BrokerVerdict.Deny;

        // The bridge's own account, which may do what the bridge does without being asked topic by topic.
        var o = options.Value;
        if (!string.IsNullOrEmpty(o.UserName) && username == o.UserName)
            return Same(password, o.Password) ? BrokerVerdict.Superuser : BrokerVerdict.Deny;

        var stored = await registry.GetCredentialHashAsync(username, ct);

        await Gate.WaitAsync(ct);
        try
        {
            // An unknown or retired device costs the same as a wrong password, so the answer's timing says nothing about which devices exist.
            var ok = DeviceCredentials.Verify(password, stored ?? DeviceCredentials.Unknown);
            return ok && stored is not null ? BrokerVerdict.Allow : BrokerVerdict.Deny;
        }
        finally
        {
            Gate.Release();
        }
    }

    public bool Authorize(string? username, string? action, string? topic) =>
        !string.IsNullOrEmpty(username) && action is not null && topic is not null && DeviceAccess.Allows(username, action, topic);

    /// <summary>True when the request carries the secret the broker was configured to send. False too when none is configured.</summary>
    public bool IsBroker(string? authorizationHeader)
    {
        var secret = options.Value.HttpAuthSecret;
        return !string.IsNullOrEmpty(secret) && authorizationHeader is not null && Same(authorizationHeader, $"Bearer {secret}");
    }

    private static bool Same(string a, string? b) =>
        b is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}

public enum BrokerVerdict
{
    Deny,
    Allow,
    Superuser,
}
