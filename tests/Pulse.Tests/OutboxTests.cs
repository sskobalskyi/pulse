using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Pulse.Api.Events;
using Pulse.Api.Processing;
using Pulse.Api.Registry;
using Pulse.Api.Storage;
using Pulse.Core;
using Testcontainers.PostgreSql;

namespace Pulse.Tests;

/// <summary>The outbox on a real database: events are written with the change they describe, or not at all, and the relay hands them on in order.</summary>
public sealed class OutboxTests : IAsyncLifetime
{
    private static readonly AsyncApiContract Contract = new(ContractPath());
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb:2.17.2-pg16")
        .WithDatabase("pulse")
        .WithUsername("pulse")
        .WithPassword("pulse")
        .Build();

    private readonly FakeTimeProvider _time = new(T0);
    private NpgsqlDataSource _dataSource = null!;
    private PulseDb _db = null!;
    private DeviceRegistry _registry = null!;
    private EventOutbox _outbox = null!;

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pulse.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "contracts", "integration-events.yaml");
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await TestSchema.ApplyAsync(_dataSource);
        _outbox = new EventOutbox(Options.Create(new EventsOptions { Enabled = true }));
        _db = new PulseDb(_dataSource, _outbox);
        _registry = new DeviceRegistry(_dataSource, _time, Options.Create(new SilenceOptions()), _outbox);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task<long> Count(string sql)
    {
        await using var cmd = _dataSource.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<List<(string RoutingKey, JsonElement Payload, DateTimeOffset OccurredAt)>> Events(string? routingKey = null)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT routing_key, payload::text, occurred_at FROM outbox WHERE (@key::text IS NULL OR routing_key = @key) ORDER BY id");
        cmd.Parameters.Add(new NpgsqlParameter("key", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)routingKey ?? DBNull.Value });
        await using var reader = await cmd.ExecuteReaderAsync();
        var events = new List<(string, JsonElement, DateTimeOffset)>();
        while (await reader.ReadAsync())
        {
            events.Add((reader.GetString(0), JsonDocument.Parse(reader.GetString(1)).RootElement.Clone(),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc))));
        }

        return events;
    }

    private static Alert Raised(string device = "pot", string channel = "soil-1", int minute = 0, double value = 31) =>
        new(device, "soil_moisture", "soil_moisture outside [40, 70]", value, T0.AddMinutes(minute), AlertState.Raised, AlertKind.Threshold, channel, "below");

    private static Alert Resolved(string device = "pot", string channel = "soil-1", int minute = 5) =>
        new(device, "soil_moisture", "soil_moisture outside [40, 70]", 55, T0.AddMinutes(minute), AlertState.Resolved, AlertKind.Threshold, channel);

    private Task RegisterInService(string id, params string[] channels) => RegisterAndClaim(id, channels.Length == 0 ? ["soil-1"] : channels);

    private async Task RegisterAndClaim(string id, string[] channels)
    {
        await _registry.RegisterAsync(new DeviceRegistration(
            id, "greenhaus-pot-v1", new Capabilities([.. channels.Select(c => new Channel(c, "soil_moisture"))], []), "h", ClaimTokens.Hash("t"), T0.AddDays(1)), default);
        await _registry.ShipAsync(id, default);
        await _registry.ClaimAsync(id, "t", "owner", default);
    }

    private async Task Seen(string device, string channel, string? ago = null)
    {
        await using var cmd = _dataSource.CreateCommand($"""
            INSERT INTO device_last_seen (device_id, channel, metric, last_seen_at) VALUES (@d, @c, 'soil_moisture', now() - interval '{ago ?? "0 seconds"}')
            ON CONFLICT (device_id, channel) DO UPDATE SET last_seen_at = EXCLUDED.last_seen_at
            """);
        cmd.Parameters.AddWithValue("d", device);
        cmd.Parameters.AddWithValue("c", channel);
        await cmd.ExecuteNonQueryAsync();
    }

    // --- events are written with the change ---

    [Fact]
    public async Task An_alert_and_its_event_are_stored_together_and_the_event_fits_the_contract()
    {
        await _db.InsertAlertsAsync([Raised()], default);

        Assert.Equal(1, await Count("SELECT count(*) FROM alerts"));
        var (key, payload, at) = Assert.Single(await Events());
        Assert.Equal("alert.raised.v1", key);
        Assert.Equal(T0, at);
        Assert.Equal(("pot", "soil-1", "below"), (payload.GetProperty("deviceId").GetString(), payload.GetProperty("channelId").GetString(), payload.GetProperty("direction").GetString()));
        Assert.Empty(Contract.ValidatePayload(key, payload));
    }

    [Fact]
    public async Task A_resolved_alert_carries_the_id_of_the_alert_it_ends()
    {
        await _db.InsertAlertsAsync([Raised(minute: 0)], default);
        await _db.InsertAlertsAsync([Resolved(minute: 5)], default);
        await _db.InsertAlertsAsync([Raised(minute: 10)], default); // a later breach is a new alert

        var events = await Events();

        Assert.Equal(["alert.raised.v1", "alert.resolved.v1", "alert.raised.v1"], events.Select(e => e.RoutingKey));
        var ids = events.Select(e => e.Payload.GetProperty("alertId").GetString()).ToArray();
        Assert.Equal(ids[0], ids[1]);
        Assert.NotEqual(ids[0], ids[2]);
        Assert.All(events, e => Assert.Empty(Contract.ValidatePayload(e.RoutingKey, e.Payload)));
    }

    [Fact]
    public async Task Two_channels_of_a_device_are_two_alerts_with_two_ids()
    {
        await _db.InsertAlertsAsync([Raised(channel: "soil-1"), Raised(channel: "soil-2")], default);
        await _db.InsertAlertsAsync([Resolved(channel: "soil-2")], default);

        var events = await Events();

        var raisedIds = events.Where(e => e.RoutingKey == "alert.raised.v1").ToDictionary(e => e.Payload.GetProperty("channelId").GetString()!, e => e.Payload.GetProperty("alertId").GetString());
        Assert.NotEqual(raisedIds["soil-1"], raisedIds["soil-2"]);
        Assert.Equal(raisedIds["soil-2"], events.Single(e => e.RoutingKey == "alert.resolved.v1").Payload.GetProperty("alertId").GetString());
    }

    [Fact]
    public async Task The_same_alert_stored_again_is_not_announced_again()
    {
        await _db.InsertAlertsAsync([Raised()], default);
        await _db.InsertAlertsAsync([Raised()], default); // what a redelivered batch does

        Assert.Equal(1, await Count("SELECT count(*) FROM alerts"));
        Assert.Equal(1, await Count("SELECT count(*) FROM outbox"));
    }

    [Fact]
    public async Task A_batch_that_fails_leaves_neither_alerts_nor_events()
    {
        var broken = Raised(channel: "soil-2") with { State = (AlertState)7 }; // a state the table refuses, after the first alert went in

        await Assert.ThrowsAsync<PostgresException>(() => _db.InsertAlertsAsync([Raised(), broken], default));

        Assert.Equal(0, await Count("SELECT count(*) FROM alerts"));
        Assert.Equal(0, await Count("SELECT count(*) FROM outbox")); // the first alert's event went with it
    }

    [Fact]
    public async Task A_no_data_alert_is_announced_without_a_value()
    {
        await _db.InsertAlertsAsync([new Alert("pot", "soil_moisture", "no data for 60s", 75.5, T0, AlertState.Raised, AlertKind.NoData, "soil-1")], default);

        var (key, payload, _) = Assert.Single(await Events());

        Assert.Equal("no_data", payload.GetProperty("rule").GetString());
        Assert.False(payload.TryGetProperty("value", out _));
        Assert.Empty(Contract.ValidatePayload(key, payload));
    }

    [Fact]
    public async Task With_events_off_alerts_are_stored_and_nothing_is_written_to_the_outbox()
    {
        var off = new PulseDb(_dataSource, new EventOutbox(Options.Create(new EventsOptions { Enabled = false })));
        var without = new PulseDb(_dataSource); // as in the standalone demo and in most tests

        await off.InsertAlertsAsync([Raised(channel: "soil-1")], default);
        await without.InsertAlertsAsync([Raised(channel: "soil-2")], default);

        Assert.Equal(2, await Count("SELECT count(*) FROM alerts"));
        Assert.Equal(0, await Count("SELECT count(*) FROM outbox"));
    }

    [Fact]
    public async Task The_trace_of_the_change_is_kept_with_the_event()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("Pulse.Tests.Outbox");
        using var activity = source.StartActivity("change")!;

        await _db.InsertAlertsAsync([Raised()], default);

        await using var cmd = _dataSource.CreateCommand("SELECT traceparent FROM outbox");
        Assert.Equal(activity.Id, await cmd.ExecuteScalarAsync());
    }

    // --- devices ---

    [Fact]
    public async Task Retiring_a_device_announces_it_once_in_the_same_transaction()
    {
        await RegisterInService("pot");

        await _registry.RetireAsync("pot", default);
        await _registry.RetireAsync("pot", default); // repeating it changes nothing, so announces nothing
        await _registry.RetireAsync("never-registered", default);

        var (key, payload, _) = Assert.Single(await Events());
        Assert.Equal("device.retired.v1", key);
        Assert.Equal("pot", payload.GetProperty("deviceId").GetString());
        Assert.Empty(Contract.ValidatePayload(key, payload));
    }

    [Fact]
    public async Task A_device_that_reports_is_announced_online_once_and_offline_once_when_all_its_channels_go_quiet()
    {
        await RegisterInService("pot", "soil-1", "soil-2");
        await Seen("pot", "soil-1");
        await Seen("pot", "soil-2");

        var first = await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0, default);
        var again = await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0, default);
        await Seen("pot", "soil-1", "5 minutes"); // one channel quiet, the other still reports: the device is online
        var oneQuiet = await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0, default);
        await Seen("pot", "soil-2", "5 minutes");
        var allQuiet = await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0.AddMinutes(1), default);
        var stillQuiet = await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0.AddMinutes(2), default);

        Assert.Equal((1, 0, 0, 1, 0), (first, again, oneQuiet, allQuiet, stillQuiet));
        var events = await Events("device.status.v1");
        Assert.Equal(["online", "offline"], events.Select(e => e.Payload.GetProperty("status").GetString()));
        Assert.Equal(["reconnect", "no_data"], events.Select(e => e.Payload.GetProperty("cause").GetString()));
        Assert.All(events, e => Assert.Empty(Contract.ValidatePayload("device.status.v1", e.Payload)));
    }

    [Fact]
    public async Task A_device_that_comes_back_is_announced_online_again()
    {
        await RegisterInService("pot");
        await Seen("pot", "soil-1");
        await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0, default);
        await Seen("pot", "soil-1", "5 minutes");
        await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0, default);

        await Seen("pot", "soil-1");
        await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0.AddMinutes(10), default);

        Assert.Equal(["online", "offline", "online"], (await Events("device.status.v1")).Select(e => e.Payload.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Status_follows_the_no_data_timeout_of_the_channels_own_rule()
    {
        await RegisterInService("pot");
        await Seen("pot", "soil-1", "20 seconds");
        await new DeviceRuleStore(_dataSource, _time).ReplaceAsync("pot", [new DeviceRule("soil-1", RuleKinds.NoData, TimeoutSeconds: 600)], default);

        await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(5), T0, default); // 20 seconds is long for the platform's 5, fine for its own 600

        Assert.Equal("online", (await Events("device.status.v1")).Single().Payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Devices_that_are_not_in_service_or_not_registered_are_not_announced()
    {
        await _registry.RegisterAsync(new DeviceRegistration("only-allocated", "m", new Capabilities([new Channel("soil-1", "soil_moisture")], []), "h", ClaimTokens.Hash("t"), T0.AddDays(1)), default);
        await Seen("only-allocated", "soil-1");
        await Seen("never-registered", "temperature");
        await RegisterInService("retired-one");
        await Seen("retired-one", "soil-1");
        await _registry.RetireAsync("retired-one", default);

        var reported = await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0, default);

        Assert.Equal(0, reported);
        Assert.Empty(await Events("device.status.v1"));
    }

    [Fact]
    public async Task A_restart_does_not_announce_the_same_status_again()
    {
        await RegisterInService("pot");
        await Seen("pot", "soil-1");
        await _db.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0, default);

        var afterRestart = new PulseDb(_dataSource, _outbox); // a new process knows only what the database says
        await afterRestart.ReconcileDeviceStatusAsync(TimeSpan.FromSeconds(30), T0.AddMinutes(1), default);

        Assert.Single(await Events("device.status.v1"));
    }

    // --- the relay ---

    private sealed class RecordingBus(Func<OutboxMessage, bool>? fails = null, TimeSpan? delay = null) : IEventBus
    {
        private readonly List<OutboxMessage> _published = [];
        public IReadOnlyList<OutboxMessage> Published { get { lock (_published) return [.. _published]; } }

        public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
        {
            if (delay is { } d) await Task.Delay(d, ct);
            if (fails?.Invoke(message) == true) throw new InvalidOperationException("the broker is away");
            lock (_published) _published.Add(message);
        }
    }

    private OutboxRelay Relay(IEventBus bus) => new(_dataSource, bus, _time, NullLogger<OutboxRelay>.Instance);

    private async Task Fill(int count)
    {
        for (var i = 0; i < count; i++) await _db.InsertAlertsAsync([Raised(device: $"d{i:000}")], default);
    }

    [Fact]
    public async Task The_relay_publishes_in_order_and_marks_what_it_published()
    {
        await Fill(5);
        var bus = new RecordingBus();

        var published = await Relay(bus).RelayOnceAsync(default);

        Assert.Equal(5, published);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"d{i:000}"), bus.Published.Select(m => JsonDocument.Parse(m.Payload).RootElement.GetProperty("deviceId").GetString()));
        Assert.Equal(0, await Relay(bus).PendingAsync(default));
        Assert.Equal(0, await Relay(bus).RelayOnceAsync(default)); // nothing is published twice
        Assert.Equal(5, bus.Published.Count);
    }

    [Fact]
    public async Task A_broker_that_cannot_be_reached_does_not_stop_pulse_and_the_events_wait()
    {
        await Fill(2);
        // The real bus, pointed at a port nothing listens on. Pulse must go on working and keep the events.
        await using var bus = new RabbitMqEventBus(Options.Create(new EventsOptions { Enabled = true, RabbitMq = { HostName = "127.0.0.1", Port = 1 } }));

        var published = await Relay(bus).RelayOnceAsync(default);

        Assert.Equal(0, published);
        Assert.Equal(2, await Relay(bus).PendingAsync(default));
        Assert.Equal(2, await Count("SELECT count(*) FROM alerts")); // the alerts themselves were stored without the broker
    }

    [Fact]
    public async Task A_failure_keeps_the_event_and_everything_after_it_so_order_is_never_broken()
    {
        await Fill(4);
        var third = (await Events())[2].Payload.GetProperty("deviceId").GetString();
        var bus = new RecordingBus(fails: m => m.Payload.Contains(third!));

        var published = await Relay(bus).RelayOnceAsync(default);

        Assert.Equal(2, published); // the first two went, the third failed, the fourth was not tried
        Assert.Equal(2, await Relay(bus).PendingAsync(default));
        await using var cmd = _dataSource.CreateCommand("SELECT attempts FROM outbox WHERE published_at IS NULL ORDER BY id LIMIT 1");
        Assert.Equal(1, await cmd.ExecuteScalarAsync()); // the failure is counted
    }

    [Fact]
    public async Task Events_that_could_not_go_out_are_published_when_the_broker_is_back()
    {
        await Fill(3);
        var down = true;
        var bus = new RecordingBus(fails: _ => down);

        await Relay(bus).RelayOnceAsync(default);
        Assert.Empty(bus.Published);
        Assert.Equal(3, await Relay(bus).PendingAsync(default));

        down = false;
        await Relay(bus).RelayOnceAsync(default);

        Assert.Equal(3, bus.Published.Count);
        Assert.Equal(0, await Relay(bus).PendingAsync(default));
    }

    [Fact]
    public async Task Two_relays_at_once_never_publish_the_same_event_twice()
    {
        await Fill(60);
        var bus = new RecordingBus(delay: TimeSpan.FromMilliseconds(5));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (await Relay(bus).RelayOnceAsync(default) > 0) { }
        })));

        Assert.Equal(60, bus.Published.Count);
        Assert.Equal(60, bus.Published.Select(m => m.MessageId).Distinct().Count());
        Assert.Equal(0, await Relay(bus).PendingAsync(default));
    }

    [Fact]
    public async Task A_batch_is_at_most_a_hundred_so_one_round_cannot_hold_locks_for_ever()
    {
        await Fill(130);
        var bus = new RecordingBus();

        Assert.Equal(100, await Relay(bus).RelayOnceAsync(default));
        Assert.Equal(30, await Relay(bus).RelayOnceAsync(default));
    }

    [Fact]
    public async Task Cleaning_up_removes_old_published_events_only()
    {
        await Fill(3);
        await Relay(new RecordingBus(fails: m => m.Payload.Contains("d002"))).RelayOnceAsync(default);
        await using (var age = _dataSource.CreateCommand("UPDATE outbox SET published_at = now() - interval '10 days' WHERE published_at IS NOT NULL AND id = (SELECT min(id) FROM outbox)"))
            await age.ExecuteNonQueryAsync();
        var relay = new OutboxRelay(_dataSource, new RecordingBus(), TimeProvider.System, NullLogger<OutboxRelay>.Instance);

        var removed = await relay.CleanUpAsync(TimeSpan.FromDays(7), default);

        Assert.Equal(1, removed); // the one published ten days ago. The one published just now and the one still waiting stay.
        Assert.Equal(2, await Count("SELECT count(*) FROM outbox"));
        Assert.Equal(1, await relay.PendingAsync(default));
    }
}
