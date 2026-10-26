using Pulse.Core;

namespace Pulse.Tests;

public sealed class CommandDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly Capabilities Pot = new(
        [new Channel("soil-1", "soil_moisture")],
        [new ActuatorCapability("pump-1", "pump", new HardLimits(MaxRunSeconds: 20, MinIntervalSeconds: 600, DailyCapSeconds: 120))]);

    private static CommandRequest Run(
        int? seconds = 10, string? actuator = "pump-1", string? type = CommandTypes.RunActuator, DateTimeOffset? expires = null, bool? enabled = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), type, actuator, seconds, enabled, expires ?? Now.AddMinutes(2));

    [Fact]
    public void A_run_within_the_hard_limit_of_an_actuator_the_device_has_is_valid()
    {
        Assert.Empty(CommandValidator.Validate(Run(seconds: 20), Pot, Now));
    }

    [Fact]
    public void A_run_longer_than_the_hard_limit_is_refused_here_instead_of_by_the_device()
    {
        var problems = CommandValidator.Validate(Run(seconds: 21), Pot, Now);

        var problem = Assert.Single(problems);
        Assert.Equal("validation_failed", problem.Code);
        Assert.Contains("hard limit of 20", problem.Message);
    }

    [Fact]
    public void An_actuator_the_device_does_not_have_is_refused()
    {
        Assert.Contains("no actuator 'pump-9'", Assert.Single(CommandValidator.Validate(Run(actuator: "pump-9"), Pot, Now)).Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_run_needs_a_positive_duration(int? seconds)
    {
        Assert.Single(CommandValidator.Validate(Run(seconds: seconds), Pot, Now));
    }

    [Fact]
    public void Stopping_an_actuator_needs_no_duration_but_still_a_real_actuator()
    {
        Assert.Empty(CommandValidator.Validate(Run(seconds: null, type: CommandTypes.StopActuator), Pot, Now));
        Assert.Single(CommandValidator.Validate(Run(seconds: null, type: CommandTypes.StopActuator, actuator: "nope"), Pot, Now));
    }

    [Fact]
    public void The_kill_switch_needs_enabled_and_is_not_checked_against_the_actuators()
    {
        Assert.Empty(CommandValidator.Validate(Run(seconds: null, type: CommandTypes.SetKillSwitch, actuator: "kill-switch", enabled: true), Pot, Now));
        Assert.Single(CommandValidator.Validate(Run(seconds: null, type: CommandTypes.SetKillSwitch, actuator: "kill-switch", enabled: null), Pot, Now));
    }

    [Fact]
    public void A_command_that_is_already_out_of_time_has_its_own_code()
    {
        var problem = Assert.Single(CommandValidator.Validate(Run(expires: Now), Pot, Now));

        Assert.Equal("command_expired", problem.Code);
    }

    [Fact]
    public void A_missing_expiry_is_a_validation_error_not_an_expired_command()
    {
        var problem = Assert.Single(CommandValidator.Validate(Run(expires: default(DateTimeOffset)) with { ExpiresAt = default }, Pot, Now));

        Assert.Equal("validation_failed", problem.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("reboot")]
    public void An_unknown_type_is_refused(string? type)
    {
        Assert.Contains("type must be one of", Assert.Single(CommandValidator.Validate(Run(type: type), Pot, Now)).Message);
    }

    [Fact]
    public void A_missing_command_id_and_actuator_are_both_reported()
    {
        var problems = CommandValidator.Validate(Run(id: Guid.Empty, actuator: " "), Pot, Now);

        Assert.Equal(2, problems.Count);
    }

    [Theory]
    [InlineData(CommandStatus.Queued, CommandStatus.Sent, true)]
    [InlineData(CommandStatus.Queued, CommandStatus.Accepted, true)]
    [InlineData(CommandStatus.Sent, CommandStatus.Accepted, true)]
    [InlineData(CommandStatus.Accepted, CommandStatus.Completed, true)]
    [InlineData(CommandStatus.Sent, CommandStatus.Completed, true)] // the device was offline, nobody saw accepted
    [InlineData(CommandStatus.Queued, CommandStatus.Rejected, true)]
    [InlineData(CommandStatus.Accepted, CommandStatus.Failed, true)]
    [InlineData(CommandStatus.Sent, CommandStatus.Expired, true)]
    [InlineData(CommandStatus.Accepted, CommandStatus.Accepted, false)] // a repeat
    [InlineData(CommandStatus.Accepted, CommandStatus.Sent, false)] // an older fact
    [InlineData(CommandStatus.Completed, CommandStatus.Failed, false)] // an ending is for good
    [InlineData(CommandStatus.Expired, CommandStatus.Completed, false)]
    [InlineData(CommandStatus.Rejected, CommandStatus.Accepted, false)]
    public void A_command_only_moves_forward_and_an_ending_is_final(CommandStatus from, CommandStatus to, bool moves)
    {
        Assert.Equal(moves, CommandStatuses.CanMove(from, to));
    }

    [Theory]
    [InlineData("queued", true, CommandStatus.Queued)]
    [InlineData("completed", true, CommandStatus.Completed)]
    [InlineData("Completed", false, default(CommandStatus))] // the wire format is lower case
    [InlineData("done", false, default(CommandStatus))]
    [InlineData("3", false, default(CommandStatus))]
    [InlineData(null, false, default(CommandStatus))]
    public void Statuses_are_read_the_way_the_contract_writes_them(string? name, bool ok, CommandStatus expected)
    {
        Assert.Equal(ok, CommandStatuses.TryParse(name, out var status));
        if (ok) Assert.Equal(expected, status);
    }

    [Fact]
    public void Only_endings_are_final()
    {
        Assert.All([CommandStatus.Completed, CommandStatus.Rejected, CommandStatus.Failed, CommandStatus.Expired], s => Assert.True(CommandStatuses.IsFinal(s)));
        Assert.All([CommandStatus.Queued, CommandStatus.Sent, CommandStatus.Accepted], s => Assert.False(CommandStatuses.IsFinal(s)));
    }

    [Fact]
    public void The_completed_event_carries_the_ending_and_refuses_a_command_that_is_not_over()
    {
        var over = new Command(Guid.NewGuid(), "dev-1", CommandTypes.RunActuator, "pump-1", "rejected", "min_interval", Now, Now.AddSeconds(1));

        var payload = IntegrationEvents.ForCommandCompleted(over);

        Assert.Equal((over.CommandId, "dev-1", "pump-1", "rejected", "min_interval", Now.AddSeconds(1)),
            (payload.CommandId, payload.DeviceId, payload.ActuatorId, payload.Status, payload.Reason, payload.FinishedAt));
        Assert.Throws<ArgumentException>(() => IntegrationEvents.ForCommandCompleted(over with { Status = "sent", FinishedAt = null }));
    }
}
