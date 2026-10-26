namespace Pulse.Api.Commands;

public sealed class CommandsOptions
{
    public const string Section = "Commands";

    /// <summary>How often the worker delivers waiting commands and ends the ones that ran out of time.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long past the time it was told to run a command the device accepted may stay without a result before it counts
    /// as failed. Covers the report travelling back, and a device that was offline when it finished.
    /// </summary>
    public TimeSpan AcceptedGrace { get; set; } = TimeSpan.FromSeconds(60);
}
