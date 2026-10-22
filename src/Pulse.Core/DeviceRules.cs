using System.Text.Json.Serialization;

namespace Pulse.Core;

public static class RuleKinds
{
    public const string Threshold = "threshold";
    public const string NoData = "no_data";
}

/// <summary>
/// An alert rule of one channel of one device. A threshold rule says what range is fine and how far back inside it a
/// value has to come before the alert clears. A no-data rule says how long the channel may stay quiet.
/// </summary>
public sealed record DeviceRule(
    string ChannelId,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Min = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Max = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Margin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TimeoutSeconds = null);

/// <param name="Code">validation_failed, or unknown_channel for a rule about a channel the device does not have.</param>
public sealed record RuleProblem(string Code, string Message);

/// <summary>Checks the rules a caller wants to set on a device, as a whole, before anything is stored.</summary>
public static class RuleValidator
{
    public const int MinTimeoutSeconds = 10;

    /// <summary>The longest quiet period a rule can allow. Beyond a week a no-data alert says nothing a person would not have noticed.</summary>
    public const int MaxTimeoutSeconds = 7 * 24 * 3600;

    public static IReadOnlyList<RuleProblem> Validate(IReadOnlyList<DeviceRule>? rules, IReadOnlyCollection<ChannelRef> declared)
    {
        if (rules is null) return [new RuleProblem("validation_failed", "A list of rules is required.")];

        var problems = new List<RuleProblem>();
        var seen = new HashSet<(string, string)>();

        foreach (var rule in rules)
        {
            if (rule is null)
            {
                problems.Add(new RuleProblem("validation_failed", "A rule is empty."));
                continue;
            }

            var label = $"Rule for '{rule.ChannelId}' ({rule.Kind})";
            if (string.IsNullOrWhiteSpace(rule.ChannelId))
            {
                problems.Add(new RuleProblem("validation_failed", "Every rule needs a channelId."));
                continue;
            }

            if (declared.All(c => c.Id != rule.ChannelId))
                problems.Add(new RuleProblem("unknown_channel", $"The device has no channel '{rule.ChannelId}'."));

            if (rule.Kind is not (RuleKinds.Threshold or RuleKinds.NoData))
            {
                problems.Add(new RuleProblem("validation_failed", $"{label}: kind must be threshold or no_data."));
                continue;
            }

            if (!seen.Add((rule.ChannelId, rule.Kind)))
                problems.Add(new RuleProblem("validation_failed", $"{label}: a channel can have only one rule of each kind."));

            if (rule.Kind == RuleKinds.Threshold) ValidateThreshold(rule, label, problems);
            else ValidateNoData(rule, label, problems);
        }

        return problems;
    }

    private static void ValidateThreshold(DeviceRule rule, string label, List<RuleProblem> problems)
    {
        void Problem(string message) => problems.Add(new RuleProblem("validation_failed", $"{label}: {message}"));

        if (rule.TimeoutSeconds is not null) Problem("timeoutSeconds belongs to no_data rules.");
        if (rule.Min is null && rule.Max is null) Problem("at least one of min and max is required.");
        if (rule.Min is { } min && !double.IsFinite(min)) Problem("min must be a finite number.");
        if (rule.Max is { } max && !double.IsFinite(max)) Problem("max must be a finite number.");

        if (rule.Min is { } lo && rule.Max is { } hi && double.IsFinite(lo) && double.IsFinite(hi) && lo >= hi)
            Problem("min must be below max.");

        if (rule.Margin is { } margin)
        {
            if (!double.IsFinite(margin) || margin < 0) Problem("margin must be zero or more.");
            // With both bounds, the margin is taken off each side. If the two margins meet, no value could ever clear the alert.
            else if (rule.Min is { } a && rule.Max is { } b && a < b && margin * 2 >= b - a)
                Problem("margin is too large: it must be less than half the distance between min and max, or the alert could never clear.");
        }
    }

    private static void ValidateNoData(DeviceRule rule, string label, List<RuleProblem> problems)
    {
        void Problem(string message) => problems.Add(new RuleProblem("validation_failed", $"{label}: {message}"));

        if (rule.TimeoutSeconds is not { } timeout) Problem("timeoutSeconds is required.");
        else if (timeout < MinTimeoutSeconds) Problem($"timeoutSeconds must be at least {MinTimeoutSeconds}.");
        else if (timeout > MaxTimeoutSeconds) Problem($"timeoutSeconds must be at most {MaxTimeoutSeconds} (a week).");

        if (rule.Min is not null || rule.Max is not null || rule.Margin is not null)
            Problem("min, max and margin belong to threshold rules.");
    }
}

/// <summary>
/// The threshold rules in force. A channel with a rule of its own uses that. A channel without one falls back to the
/// platform's rules for its metric, so a device that never had rules set behaves as before.
/// </summary>
public sealed class EffectiveRules(
    IReadOnlyList<ThresholdRule> defaults,
    IReadOnlyDictionary<(string DeviceId, string ChannelId), DeviceRule> custom)
{
    public static EffectiveRules DefaultsOnly(IReadOnlyList<ThresholdRule> defaults) =>
        new(defaults, new Dictionary<(string, string), DeviceRule>());

    public IReadOnlyList<ThresholdRule> For(TelemetryReading reading) =>
        custom.TryGetValue((reading.DeviceId, reading.ChannelId), out var rule)
            ? [new ThresholdRule(reading.Metric, rule.Min, rule.Max, rule.Margin ?? 0)]
            : defaults;
}
