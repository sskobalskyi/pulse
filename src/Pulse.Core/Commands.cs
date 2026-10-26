using System.Text.Json.Serialization;

namespace Pulse.Core;

public static class CommandTypes
{
    public const string RunActuator = "run_actuator";
    public const string StopActuator = "stop_actuator";
    public const string SetKillSwitch = "set_kill_switch";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { RunActuator, StopActuator, SetKillSwitch };
}

/// <summary>
/// Where a command is on its way. Queued: stored, not handed to the device link yet. Sent: handed over. Accepted: the
/// device took it. Then one of the endings: completed, rejected (the device refused it, for a safety reason or because
/// it was a repeat), failed, or expired (it ran out of time before it could run).
/// </summary>
public enum CommandStatus
{
    Queued,
    Sent,
    Accepted,
    Completed,
    Rejected,
    Failed,
    Expired,
}

public static class CommandStatuses
{
    public static string Name(CommandStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>Reads a status by the exact name the contract uses. A number or a different case is not a status.</summary>
    public static bool TryParse(string? name, out CommandStatus status)
    {
        foreach (var candidate in Enum.GetValues<CommandStatus>())
        {
            if (Name(candidate) != name) continue;
            status = candidate;
            return true;
        }

        status = default;
        return false;
    }

    public static bool IsFinal(CommandStatus status) => status >= CommandStatus.Completed;

    /// <summary>
    /// Whether a report of <paramref name="to"/> changes a command that is <paramref name="from"/>. A command only moves
    /// forward and an ending is for good, so a late or repeated message (the broker delivers at least once) changes nothing.
    /// It may skip a step: a device that was offline can report completed without anyone having seen accepted.
    /// </summary>
    public static bool CanMove(CommandStatus from, CommandStatus to) => !IsFinal(from) && to > from;
}

/// <summary>What a caller asks for, as the contract's CommandRequest names it.</summary>
public sealed record CommandRequest(
    Guid CommandId,
    string? Type,
    string? ActuatorId,
    int? DurationSeconds,
    bool? Enabled,
    DateTimeOffset ExpiresAt);

/// <param name="Code">validation_failed, or command_expired for a command that is already out of time.</param>
public sealed record CommandProblem(string Code, string Message);

/// <summary>Checks a command against what the device declared it can do, before anything is stored.</summary>
public static class CommandValidator
{
    public static IReadOnlyList<CommandProblem> Validate(CommandRequest request, Capabilities capabilities, DateTimeOffset now)
    {
        var problems = new List<CommandProblem>();

        if (request.CommandId == Guid.Empty)
            problems.Add(new CommandProblem("validation_failed", "commandId is required."));

        if (request.Type is null || !CommandTypes.All.Contains(request.Type))
        {
            problems.Add(new CommandProblem("validation_failed", "type must be one of run_actuator, stop_actuator, set_kill_switch."));
            return problems;
        }

        if (string.IsNullOrWhiteSpace(request.ActuatorId))
            problems.Add(new CommandProblem("validation_failed", "actuatorId is required."));

        if (request.ExpiresAt == default)
            problems.Add(new CommandProblem("validation_failed", "expiresAt is required."));
        else if (request.ExpiresAt <= now)
            problems.Add(new CommandProblem("command_expired", "expiresAt is already in the past, so the command could never run."));

        switch (request.Type)
        {
            case CommandTypes.RunActuator:
                if (request.DurationSeconds is null or < 1)
                    problems.Add(new CommandProblem("validation_failed", "durationSeconds of at least 1 is required to run an actuator."));
                break;
            case CommandTypes.SetKillSwitch:
                if (request.Enabled is null)
                    problems.Add(new CommandProblem("validation_failed", "enabled is required to set the kill switch."));
                break;
        }

        // The kill switch is the device's own, not one actuator's. An actuator command has to name one the device has,
        // and a run may not be longer than the device allows: it would refuse it anyway, and saying so here is quicker.
        if (request.Type != CommandTypes.SetKillSwitch && !string.IsNullOrWhiteSpace(request.ActuatorId))
        {
            var actuator = capabilities.Actuators.FirstOrDefault(a => a.Id == request.ActuatorId);
            if (actuator is null)
                problems.Add(new CommandProblem("validation_failed", $"The device has no actuator '{request.ActuatorId}'."));
            else if (request.Type == CommandTypes.RunActuator && request.DurationSeconds > actuator.HardLimits.MaxRunSeconds)
                problems.Add(new CommandProblem("validation_failed", $"A run of {request.DurationSeconds} s exceeds the hard limit of {actuator.HardLimits.MaxRunSeconds} s of '{actuator.Id}'."));
        }

        return problems;
    }
}

/// <summary>A command as the API returns it.</summary>
public sealed record Command(
    Guid CommandId,
    string DeviceId,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActuatorId,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt)
{
    [JsonIgnore]
    public CommandStatus State => Enum.Parse<CommandStatus>(Status, ignoreCase: true);
}

/// <summary>What the device link needs to deliver a command, in the shape of the device topic's payload.</summary>
public sealed record DeviceCommand(
    Guid CommandId,
    string DeviceId,
    string Type,
    string ActuatorId,
    int? DurationSeconds,
    bool? Enabled,
    DateTimeOffset ExpiresAt);
