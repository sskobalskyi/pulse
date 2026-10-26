using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Pulse.Api.Commands;
using Pulse.Api.Events;
using Pulse.Api.Processing;
using Pulse.Api.Registry;
using Pulse.Api.Storage;
using Pulse.Core;
using Testcontainers.PostgreSql;

namespace Pulse.Tests;

/// <summary>The command queue against a real database, with a clock the tests control and a device link they can break.</summary>
public sealed class CommandStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb:2.17.2-pg16")
        .WithDatabase("pulse")
        .WithUsername("pulse")
        .WithPassword("pulse")
        .Build();

    private readonly FakeTimeProvider _time = new(T0);
    private NpgsqlDataSource _dataSource = null!;
    private CommandStore _store = null!;
    private DeviceRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await TestSchema.ApplyAsync(_dataSource);

        var outbox = new EventOutbox(Options.Create(new EventsOptions { Enabled = true }));
        _store = new CommandStore(_dataSource, _time, outbox);
        _registry = new DeviceRegistry(_dataSource, _time, Options.Create(new SilenceOptions()), outbox);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task<string> Pot()
    {
        var id = $"pot-{Guid.NewGuid():N}"[..12];
        await _registry.RegisterAsync(new DeviceRegistration(
            id, "greenhaus-pot-v1",
            new Capabilities([new Channel("soil-1", "soil_moisture")], [new ActuatorCapability("pump-1", "pump", new HardLimits(20, 600, 120))]),
            "h", ClaimTokens.Hash("t"), T0.AddDays(30)), default);
        return id;
    }

    private CommandRequest Run(Guid? id = null, int seconds = 10, TimeSpan? validFor = null) =>
        new(id ?? Guid.NewGuid(), CommandTypes.RunActuator, "pump-1", seconds, null, _time.GetUtcNow() + (validFor ?? TimeSpan.FromMinutes(2)));

    private async Task<Command> Queued(string device, CommandRequest? request = null)
    {
        var result = await _store.EnqueueAsync(device, request ?? Run(), default);
        Assert.Equal(EnqueueOutcome.Created, result.Outcome);
        return result.Command!;
    }

    private async Task<JsonElement[]> CompletedEvents(Guid commandId)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT payload::text FROM outbox WHERE routing_key = 'command.completed.v1' ORDER BY id");
        await using var reader = await cmd.ExecuteReaderAsync();
        var events = new List<JsonElement>();
        while (await reader.ReadAsync())
        {
            var payload = JsonDocument.Parse(reader.GetString(0)).RootElement.Clone();
            if (payload.GetProperty("commandId").GetGuid() == commandId) events.Add(payload);
        }

        return [.. events];
    }

    private sealed class FakeLink : ICommandSender
    {
        public List<DeviceCommand> Delivered { get; } = [];
        public bool Broken { get; set; }
        public bool Available => true;

        public Task SendAsync(DeviceCommand command, CancellationToken ct)
        {
            if (Broken) throw new InvalidOperationException("link down");
            Delivered.Add(command);
            return Task.CompletedTask;
        }
    }

    // --- queueing ---

    [Fact]
    public async Task A_command_is_stored_queued_and_can_be_read_back()
    {
        var pot = await Pot();
        var request = Run();

        var command = await Queued(pot, request);

        Assert.Equal((request.CommandId, pot, "run_actuator", "pump-1", "queued", null, T0, null),
            (command.CommandId, command.DeviceId, command.Type, command.ActuatorId, command.Status, command.Reason, command.CreatedAt, command.FinishedAt));
        Assert.Equal(command, await _store.GetAsync(pot, request.CommandId, default));
        Assert.Equal([command], await _store.ListAsync(pot, 50, default));
    }

    [Fact]
    public async Task Sending_the_same_command_again_changes_nothing_and_returns_it_as_it_is_now()
    {
        var pot = await Pot();
        var request = Run();
        await Queued(pot, request);
        await _store.ApplyAckAsync(pot, request.CommandId, CommandStatus.Accepted, null, default);

        var again = await _store.EnqueueAsync(pot, request, default);

        Assert.Equal(EnqueueOutcome.Existing, again.Outcome);
        Assert.Equal("accepted", again.Command!.Status); // not reset to queued
        Assert.Single(await _store.ListAsync(pot, 50, default));
    }

    [Fact]
    public async Task The_same_id_for_a_different_command_is_refused_and_the_stored_one_stays()
    {
        var pot = await Pot();
        var request = Run(seconds: 10);
        await Queued(pot, request);

        var other = await _store.EnqueueAsync(pot, request with { DurationSeconds = 15 }, default);

        Assert.Equal(EnqueueOutcome.IdReused, other.Outcome);
        Assert.Null(other.Command);
        Assert.Single(await _store.ListAsync(pot, 50, default));
    }

    [Fact]
    public async Task The_same_id_for_another_device_is_refused_too()
    {
        var first = await Pot();
        var second = await Pot();
        var request = Run();
        await Queued(first, request);

        Assert.Equal(EnqueueOutcome.IdReused, (await _store.EnqueueAsync(second, request, default)).Outcome);
        Assert.Empty(await _store.ListAsync(second, 50, default));
    }

    [Fact]
    public async Task A_sender_that_never_saw_the_answer_can_retry_with_a_sub_millisecond_different_expiry()
    {
        // The database keeps microseconds and a client may serialise more digits.
        var pot = await Pot();
        var request = Run();
        await Queued(pot, request);

        var retry = await _store.EnqueueAsync(pot, request with { ExpiresAt = request.ExpiresAt.AddTicks(7) }, default);

        Assert.Equal(EnqueueOutcome.Existing, retry.Outcome);
    }

    [Fact]
    public async Task A_device_that_does_not_exist_or_is_retired_takes_no_commands()
    {
        var pot = await Pot();
        await _registry.RetireAsync(pot, default);

        Assert.Equal(EnqueueOutcome.DeviceMissing, (await _store.EnqueueAsync("nope", Run(), default)).Outcome);
        Assert.Equal(EnqueueOutcome.DeviceRetired, (await _store.EnqueueAsync(pot, Run(), default)).Outcome);
    }

    [Fact]
    public async Task The_list_is_newest_first_and_limited_and_a_command_is_only_found_under_its_own_device()
    {
        var pot = await Pot();
        var other = await Pot();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            ids.Add((await Queued(pot)).CommandId);
        }

        Assert.Equal([ids[2], ids[1], ids[0]], (await _store.ListAsync(pot, 50, default)).Select(c => c.CommandId));
        Assert.Equal([ids[2], ids[1]], (await _store.ListAsync(pot, 2, default)).Select(c => c.CommandId));
        Assert.Null(await _store.GetAsync(other, ids[0], default));
    }

    // --- delivery ---

    [Fact]
    public async Task Waiting_commands_are_handed_to_the_link_oldest_first_and_marked_sent_once()
    {
        var pot = await Pot();
        var first = await Queued(pot);
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = await Queued(pot);
        var link = new FakeLink();

        var sent = await _store.DeliverAsync(link, NullLogger.Instance, default);
        var again = await _store.DeliverAsync(link, NullLogger.Instance, default);

        Assert.Equal(2, sent);
        Assert.Equal(0, again);
        Assert.Equal([first.CommandId, second.CommandId], link.Delivered.Select(d => d.CommandId));
        Assert.All(await _store.ListAsync(pot, 50, default), c => Assert.Equal("sent", c.Status));
        Assert.Equal(("run_actuator", "pump-1", 10), (link.Delivered[0].Type, link.Delivered[0].ActuatorId, link.Delivered[0].DurationSeconds));
    }

    [Fact]
    public async Task A_link_that_fails_leaves_the_command_queued_and_nothing_overtakes_it()
    {
        var pot = await Pot();
        var first = await Queued(pot);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Queued(pot);
        var link = new FakeLink { Broken = true };

        Assert.Equal(0, await _store.DeliverAsync(link, NullLogger.Instance, default));
        Assert.All(await _store.ListAsync(pot, 50, default), c => Assert.Equal("queued", c.Status));

        link.Broken = false;
        Assert.Equal(2, await _store.DeliverAsync(link, NullLogger.Instance, default));
        Assert.Equal(first.CommandId, link.Delivered[0].CommandId);
    }

    [Fact]
    public async Task A_command_that_ran_out_of_time_is_not_handed_over_even_before_the_worker_expired_it()
    {
        var pot = await Pot();
        await Queued(pot, Run(validFor: TimeSpan.FromSeconds(30)));
        _time.Advance(TimeSpan.FromSeconds(31));
        var link = new FakeLink();

        Assert.Equal(0, await _store.DeliverAsync(link, NullLogger.Instance, default));
        Assert.Empty(link.Delivered);
    }

    [Fact]
    public async Task Two_instances_delivering_at_once_hand_each_command_over_once()
    {
        var pot = await Pot();
        for (var i = 0; i < 10; i++) await Queued(pot);
        var link = new FakeLink();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _store.DeliverAsync(link, NullLogger.Instance, default)));

        Assert.Equal(10, link.Delivered.Count);
        Assert.Equal(10, link.Delivered.Select(d => d.CommandId).Distinct().Count());
    }

    [Fact]
    public async Task Without_a_link_the_worker_expires_commands_but_delivers_nothing()
    {
        var pot = await Pot();
        var request = Run(validFor: TimeSpan.FromSeconds(30));
        await Queued(pot, request);
        var worker = new CommandWorker(_store, new NoDeviceLink(), Options.Create(new CommandsOptions()), NullLogger<CommandWorker>.Instance);

        Assert.Equal((0, 0), await worker.RunOnceAsync(default));
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal((0, 1), await worker.RunOnceAsync(default));

        Assert.Equal("expired", (await _store.GetAsync(pot, request.CommandId, default))!.Status);
    }

    // --- what the device reports ---

    [Fact]
    public async Task A_command_goes_through_accepted_to_completed_and_is_announced_once_when_it_ends()
    {
        var pot = await Pot();
        var command = await Queued(pot);

        var accepted = await _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Accepted, null, default);
        Assert.Equal(("accepted", null), (accepted!.Status, accepted.FinishedAt));
        Assert.Empty(await CompletedEvents(command.CommandId)); // not over yet

        _time.Advance(TimeSpan.FromSeconds(10));
        var completed = await _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Completed, null, default);

        Assert.Equal(("completed", T0.AddSeconds(10)), (completed!.Status, completed.FinishedAt));
        var announced = Assert.Single(await CompletedEvents(command.CommandId));
        Assert.Equal(("completed", pot, "pump-1"), (announced.GetProperty("status").GetString(), announced.GetProperty("deviceId").GetString(), announced.GetProperty("actuatorId").GetString()));
        Assert.False(announced.TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task A_refusal_by_the_device_ends_the_command_with_its_reason()
    {
        var pot = await Pot();
        var command = await Queued(pot);

        var rejected = await _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Rejected, "min_interval", default);

        Assert.Equal(("rejected", "min_interval"), (rejected!.Status, rejected.Reason));
        Assert.Equal("min_interval", Assert.Single(await CompletedEvents(command.CommandId)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_repeated_or_late_report_changes_nothing_and_announces_nothing_more()
    {
        var pot = await Pot();
        var command = await Queued(pot);
        await _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Completed, null, default);

        // The broker delivers at least once, and not in order.
        var repeat = await _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Completed, null, default);
        var late = await _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Accepted, null, default);
        var contradicting = await _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Failed, "fault", default);

        Assert.All([repeat, late, contradicting], c => Assert.Equal("completed", c!.Status));
        Assert.Null((await _store.GetAsync(pot, command.CommandId, default))!.Reason);
        Assert.Single(await CompletedEvents(command.CommandId));
    }

    [Fact]
    public async Task A_report_for_a_command_of_another_device_or_one_that_does_not_exist_is_ignored()
    {
        var pot = await Pot();
        var other = await Pot();
        var command = await Queued(pot);

        Assert.Null(await _store.ApplyAckAsync(other, command.CommandId, CommandStatus.Completed, null, default));
        Assert.Null(await _store.ApplyAckAsync(pot, Guid.NewGuid(), CommandStatus.Completed, null, default));
        Assert.Equal("queued", (await _store.GetAsync(pot, command.CommandId, default))!.Status);
    }

    [Fact]
    public async Task A_device_cannot_report_a_status_that_only_pulse_sets()
    {
        var pot = await Pot();
        var command = await Queued(pot);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Sent, null, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Queued, null, default));
    }

    // --- running out of time ---

    [Fact]
    public async Task A_command_nobody_took_in_time_expires_and_is_announced()
    {
        var pot = await Pot();
        var command = await Queued(pot, Run(validFor: TimeSpan.FromSeconds(30)));

        Assert.Equal(0, await _store.ExpireAsync(Grace, default)); // still has time
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await _store.ExpireAsync(Grace, default));
        Assert.Equal(0, await _store.ExpireAsync(Grace, default)); // and only once

        var expired = (await _store.GetAsync(pot, command.CommandId, default))!;
        Assert.Equal(("expired", "expired", T0.AddSeconds(31)), (expired.Status, expired.Reason, expired.FinishedAt));
        Assert.Equal("expired", Assert.Single(await CompletedEvents(command.CommandId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_sent_command_expires_like_a_queued_one()
    {
        var pot = await Pot();
        var command = await Queued(pot, Run(validFor: TimeSpan.FromSeconds(30)));
        await _store.DeliverAsync(new FakeLink(), NullLogger.Instance, default);
        _time.Advance(TimeSpan.FromSeconds(31));

        await _store.ExpireAsync(Grace, default);

        Assert.Equal("expired", (await _store.GetAsync(pot, command.CommandId, default))!.Status);
    }

    [Fact]
    public async Task A_command_the_device_accepted_is_left_to_run_and_fails_only_when_no_result_comes_after_it_should_have_ended()
    {
        var pot = await Pot();
        var command = await Queued(pot, Run(seconds: 10, validFor: TimeSpan.FromSeconds(30)));
        await _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Accepted, null, default);

        // Past its expiry, but it is running: it has until expiry + run time + grace to report.
        _time.Advance(TimeSpan.FromSeconds(30 + 10 + 59));
        Assert.Equal(0, await _store.ExpireAsync(Grace, default));
        Assert.Equal("accepted", (await _store.GetAsync(pot, command.CommandId, default))!.Status);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await _store.ExpireAsync(Grace, default));

        var failed = (await _store.GetAsync(pot, command.CommandId, default))!;
        Assert.Equal(("failed", "no_result"), (failed.Status, failed.Reason));
        Assert.Equal("failed", Assert.Single(await CompletedEvents(command.CommandId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_result_that_arrives_after_the_command_was_given_up_on_is_ignored()
    {
        var pot = await Pot();
        var command = await Queued(pot, Run(validFor: TimeSpan.FromSeconds(30)));
        _time.Advance(TimeSpan.FromSeconds(31));
        await _store.ExpireAsync(Grace, default);

        var late = await _store.ApplyAckAsync(pot, command.CommandId, CommandStatus.Completed, null, default);

        Assert.Equal("expired", late!.Status);
        Assert.Single(await CompletedEvents(command.CommandId));
    }

    // --- retiring ---

    [Fact]
    public async Task Retiring_a_device_ends_its_open_commands_and_leaves_the_finished_ones_alone()
    {
        var pot = await Pot();
        var waiting = await Queued(pot);
        var running = await Queued(pot);
        await _store.ApplyAckAsync(pot, running.CommandId, CommandStatus.Accepted, null, default);
        var done = await Queued(pot);
        await _store.ApplyAckAsync(pot, done.CommandId, CommandStatus.Completed, null, default);

        await _registry.RetireAsync(pot, default);
        await _registry.RetireAsync(pot, default); // a repeat does not end anything again

        foreach (var id in new[] { waiting.CommandId, running.CommandId })
        {
            var command = (await _store.GetAsync(pot, id, default))!;
            Assert.Equal(("failed", "device_retired"), (command.Status, command.Reason));
            Assert.Single(await CompletedEvents(id));
        }

        Assert.Equal("completed", (await _store.GetAsync(pot, done.CommandId, default))!.Status);
        Assert.Single(await CompletedEvents(done.CommandId));
    }

    [Fact]
    public async Task Retiring_and_queueing_at_the_same_time_never_leave_a_command_open_on_a_retired_device()
    {
        for (var round = 0; round < 10; round++)
        {
            var pot = await Pot();

            await Task.WhenAll(
                Task.Run(async () => await _registry.RetireAsync(pot, default)),
                Task.Run(async () => { for (var i = 0; i < 5; i++) await _store.EnqueueAsync(pot, Run(), default); }));

            Assert.All(await _store.ListAsync(pot, 50, default), c => Assert.Equal("failed", c.Status));
        }
    }

    [Fact]
    public async Task Without_events_switched_on_nothing_is_written_to_the_outbox()
    {
        var quiet = new CommandStore(_dataSource, _time, new EventOutbox(Options.Create(new EventsOptions { Enabled = false })));
        var pot = await Pot();
        var request = Run();
        await quiet.EnqueueAsync(pot, request, default);

        await quiet.ApplyAckAsync(pot, request.CommandId, CommandStatus.Completed, null, default);

        Assert.Empty(await CompletedEvents(request.CommandId));
    }
}
