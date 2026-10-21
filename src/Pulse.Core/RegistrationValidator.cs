namespace Pulse.Core;

/// <summary>Checks a device registration before anything is stored. Pure, so it is easy to test exhaustively.</summary>
public static class RegistrationValidator
{
    public const int MaxIdLength = 64;

    /// <summary>Returns every problem found, not just the first, so a caller can fix them in one go. Empty means valid.</summary>
    public static IReadOnlyList<string> Validate(DeviceRegistration registration, DateTimeOffset now)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(registration.Id)) errors.Add("id is required");
        else if (registration.Id.Length > MaxIdLength) errors.Add($"id is longer than {MaxIdLength} characters");
        else if (!IsSafeId(registration.Id)) errors.Add("id may only contain letters, digits, '-', '_' and '.'");

        if (string.IsNullOrWhiteSpace(registration.Model)) errors.Add("model is required");
        if (string.IsNullOrWhiteSpace(registration.CredentialHash)) errors.Add("credentialHash is required");

        if (ClaimTokens.Normalise(registration.ClaimTokenHash) is null)
            errors.Add("claimTokenHash must be the SHA-256 of the claim token as 64 hex characters, optionally prefixed with 'sha256:'");

        if (registration.ClaimTokenExpiresAt <= now) errors.Add("claimTokenExpiresAt must be in the future");

        ValidateCapabilities(registration.Capabilities, errors);
        return errors;
    }

    private static void ValidateCapabilities(Capabilities? capabilities, List<string> errors)
    {
        if (capabilities?.Channels is null || capabilities.Actuators is null)
        {
            errors.Add("capabilities must contain channels and actuators");
            return;
        }

        var channelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in capabilities.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Id)) errors.Add("every channel needs an id");
            else if (!IsSafeId(channel.Id)) errors.Add($"channel id '{channel.Id}' may only contain letters, digits, '-', '_' and '.'");
            else if (!channelIds.Add(channel.Id)) errors.Add($"channel id '{channel.Id}' is used more than once");

            if (!MetricCatalog.IsKnown(channel.Metric))
                errors.Add($"channel '{channel.Id}' has an unknown metric '{channel.Metric}'");
        }

        var actuatorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actuator in capabilities.Actuators)
        {
            if (string.IsNullOrWhiteSpace(actuator.Id)) errors.Add("every actuator needs an id");
            else if (!actuatorIds.Add(actuator.Id)) errors.Add($"actuator id '{actuator.Id}' is used more than once");

            if (!ActuatorKinds.All.Contains(actuator.Kind))
                errors.Add($"actuator '{actuator.Id}' has an unknown kind '{actuator.Kind}'");

            var limits = actuator.HardLimits;
            if (limits is null)
            {
                errors.Add($"actuator '{actuator.Id}' needs hardLimits");
                continue;
            }

            if (limits.MaxRunSeconds < 1) errors.Add($"actuator '{actuator.Id}': maxRunSeconds must be at least 1");
            if (limits.MinIntervalSeconds < 0) errors.Add($"actuator '{actuator.Id}': minIntervalSeconds cannot be negative");
            if (limits.DailyCapSeconds < 1) errors.Add($"actuator '{actuator.Id}': dailyCapSeconds must be at least 1");
            if (limits.MaxRunSeconds > limits.DailyCapSeconds)
                errors.Add($"actuator '{actuator.Id}': maxRunSeconds cannot exceed dailyCapSeconds");
        }
    }

    // Ids end up in MQTT topics and URLs, so keep them to characters that need no escaping.
    private static bool IsSafeId(string id) =>
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
