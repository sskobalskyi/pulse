using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Core;

namespace Pulse.Tests;

/// <summary>
/// The bridge against a real EMQX, with a device that talks MQTT the way the contract says: readings in, status in,
/// commands out, answers in.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class MqttBridgeTests(PipelineFixture stack)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly AsyncApiContract Contract = new(ContractPath());
    private const string V2 = "/api/pulse/v2";

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pulse.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "contracts", "device-mqtt.yaml");
    }

    private static string NewId() => $"mq-{Guid.NewGuid():N}"[..14];

    private async Task<string> Pot()
    {
        var id = NewId();
        using var response = await stack.Client.PostAsJsonAsync($"{V2}/devices", new
        {
            id,
            model = "greenhaus-pot-v1",
            capabilities = new
            {
                channels = new[] { new { id = "soil-1", metric = "soil_moisture" }, new { id = "air-t", metric = "temperature" } },
                actuators = new[] { new { id = "pump-1", kind = "pump", hardLimits = new { maxRunSeconds = 20, minIntervalSeconds = 600, dailyCapSeconds = 120 } } },
            },
            credentialHash = "h",
            claimTokenHash = ClaimTokens.Hash("t"),
            claimTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    private async Task<string> ClaimedPot()
    {
        var id = await Pot();
        using var shipped = await stack.Client.PostAsJsonAsync($"{V2}/devices/{id}/ship", new { });
        using var claimed = await stack.Client.PostAsJsonAsync($"{V2}/devices/{id}/claim", new { token = "t", ownerId = "owner" });
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        return id;
    }

    private async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        T value;
        while (!done(value = await read()) && DateTime.UtcNow < deadline) await Task.Delay(100);
        return value;
    }

    private async Task<JsonElement> Command(string device, Guid id) =>
        await stack.Client.GetFromJsonAsync<JsonElement>($"{V2}/devices/{device}/commands/{id}", Web);

    private async Task<string?> Status(string device, Guid id) => (await Command(device, id)).GetProperty("status").GetString();

    private async Task<Guid> Send(string device, int seconds = 10, TimeSpan? validFor = null)
    {
        var id = Guid.NewGuid();
        using var response = await stack.Client.PostAsJsonAsync($"{V2}/devices/{device}/commands", new
        {
            commandId = id,
            type = "run_actuator",
            actuatorId = "pump-1",
            durationSeconds = seconds,
            expiresAt = DateTimeOffset.UtcNow + (validFor ?? TimeSpan.FromMinutes(2)),
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return id;
    }

    private async Task<string> Connection(string device) =>
        (await stack.Client.GetFromJsonAsync<JsonElement>($"{V2}/devices/{device}", Web)).GetProperty("connection").GetString()!;

    // --- what devices say ---

    [Fact]
    public async Task A_reading_a_device_publishes_is_stored_on_its_channel()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync();

        await device.Reading("soil-1", 41.5, DateTimeOffset.UtcNow.AddSeconds(-5));

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o"));
        var points = await Eventually(
            async () => await stack.Client.GetFromJsonAsync<JsonElement[]>($"{V2}/devices/{pot}/channels/soil-1/series?from={from}", Web) ?? [],
            p => p.Length > 0);
        Assert.Equal(41.5, Assert.Single(points).GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Readings_that_cannot_be_right_are_dropped_and_the_next_good_one_still_gets_through()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync();

        await device.Reading("soil-9", 40); // a channel the device never declared
        await device.Reading("soil-1", 4000); // not a possible soil moisture
        await device.PublishRawAsync("telemetry/soil-1", "this is not json"u8.ToArray());
        await device.PublishAsync("telemetry/soil-1", new { value = 40 }); // no time
        await device.Reading("soil-1", 33, DateTimeOffset.UtcNow.AddSeconds(-3));

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o"));
        var points = await Eventually(
            async () => await stack.Client.GetFromJsonAsync<JsonElement[]>($"{V2}/devices/{pot}/channels/soil-1/series?from={from}", Web) ?? [],
            p => p.Length > 0);
        Assert.Equal(33, Assert.Single(points).GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task A_device_nobody_registered_gets_no_readings_stored()
    {
        var stranger = NewId();
        await using var device = new FakeDevice(stranger, stack);
        await device.ConnectAsync();

        await device.Reading("soil-1", 40, DateTimeOffset.UtcNow.AddSeconds(-2));
        await Task.Delay(2000);

        var devices = await stack.Client.GetFromJsonAsync<JsonElement[]>("/api/devices", Web);
        Assert.DoesNotContain(devices!, d => d.GetProperty("deviceId").GetString() == stranger);
    }

    [Fact]
    public async Task The_status_a_device_publishes_and_the_will_the_broker_publishes_decide_whether_it_is_online()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync(withWill: true);

        await device.Online();
        Assert.Equal("online", await Eventually(() => Connection(pot), c => c == "online"));

        // The connection drops without a goodbye: the broker says so on the device's behalf.
        await device.LoseConnectionAsync();
        Assert.Equal("offline", await Eventually(() => Connection(pot), c => c == "offline"));
    }

    [Fact]
    public async Task A_device_that_reports_its_own_connection_is_not_called_offline_because_it_is_quiet()
    {
        // A claimed device that has reported is one the silence monitor watches, which is what this has to stay clear of.
        var pot = await ClaimedPot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync();
        await device.Online();
        await device.Reading("soil-1", 40, DateTimeOffset.UtcNow);
        await Eventually(() => Connection(pot), c => c == "online");

        // The shared stack's no-data timeout is three seconds. Connected is connected, and the quiet sensor has its own no-data alert.
        await Task.Delay(6000);

        Assert.Equal("online", await Connection(pot));
    }

    [Fact]
    public async Task The_firmware_in_the_manifest_is_recorded()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync();

        await device.PublishAsync("manifest", new { model = "greenhaus-pot-v1", firmware = "1.4.2", channels = Array.Empty<object>(), actuators = Array.Empty<object>() });

        var firmware = await Eventually(
            async () => (await stack.Client.GetFromJsonAsync<JsonElement>($"{V2}/devices/{pot}", Web)).TryGetProperty("firmware", out var f) ? f.GetString() : null,
            f => f == "1.4.2");
        Assert.Equal("1.4.2", firmware);
    }

    [Fact]
    public async Task Messages_that_mean_nothing_do_not_stop_the_bridge()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync();

        await device.PublishRawAsync("status", "{"u8.ToArray());
        await device.PublishAsync("status", new { status = "away" });
        await device.PublishAsync("commands/ack", new { commandId = Guid.NewGuid(), status = "completed" }); // a command that was never sent
        await device.PublishAsync("commands/ack", new { status = "completed" });
        await device.PublishAsync("state", new { ts = DateTimeOffset.UtcNow, killSwitch = false, actuators = Array.Empty<object>() });
        await device.Online();

        Assert.Equal("online", await Eventually(() => Connection(pot), c => c == "online"));
    }

    // --- commands ---

    [Fact]
    public async Task A_command_reaches_the_device_on_its_topic_in_the_contracts_format_and_is_marked_sent()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync();

        var id = await Send(pot, seconds: 12);

        var received = await Eventually(() => Task.FromResult(device.Commands), c => c.Count > 0);
        var command = Assert.Single(received);
        Assert.Empty(Contract.ValidatePayload("command", command));
        Assert.Equal((id, "run_actuator", "pump-1", 12), (command.GetProperty("commandId").GetGuid(), command.GetProperty("type").GetString(), command.GetProperty("actuatorId").GetString(), command.GetProperty("durationSeconds").GetInt32()));
        Assert.Equal("sent", await Eventually(() => Status(pot, id), s => s == "sent"));
    }

    [Fact]
    public async Task The_answers_of_the_device_take_the_command_to_its_end_and_a_repeated_answer_changes_nothing()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync();
        var id = await Send(pot);
        await Eventually(() => Task.FromResult(device.Commands), c => c.Count > 0);

        await device.Ack(id, "accepted");
        Assert.Equal("accepted", await Eventually(() => Status(pot, id), s => s == "accepted"));
        await device.Ack(id, "completed");
        await device.Ack(id, "completed"); // QoS 1 may deliver twice
        await device.Ack(id, "failed", "fault"); // and a contradicting one must not rewrite history
        var done = await Eventually(() => Command(pot, id), c => c.GetProperty("status").GetString() == "completed");
        await Task.Delay(500);

        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Equal("completed", await Status(pot, id));
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("finishedAt").ValueKind);
    }

    [Fact]
    public async Task A_refusal_by_the_device_arrives_with_its_reason()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync();
        var id = await Send(pot);
        await Eventually(() => Task.FromResult(device.Commands), c => c.Count > 0);

        await device.Ack(id, "rejected", "min_interval");

        var command = await Eventually(() => Command(pot, id), c => c.GetProperty("status").GetString() == "rejected");
        Assert.Equal("min_interval", command.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_command_sent_while_the_device_is_away_waits_for_it_on_the_broker()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync(persistent: true);
        await device.DisconnectAsync();

        var id = await Send(pot, validFor: TimeSpan.FromMinutes(5));
        Assert.Equal("sent", await Eventually(() => Status(pot, id), s => s == "sent")); // handed to the broker, the device is not there yet
        Assert.Empty(device.Commands);

        await device.ConnectAsync(persistent: true);

        var received = await Eventually(() => Task.FromResult(device.Commands), c => c.Count > 0);
        Assert.Equal(id, Assert.Single(received).GetProperty("commandId").GetGuid());
    }

    [Fact]
    public async Task A_command_that_runs_out_of_time_while_the_device_is_away_is_never_delivered_and_ends_expired()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync(persistent: true);
        await device.DisconnectAsync();

        var id = await Send(pot, validFor: TimeSpan.FromSeconds(3));
        await Eventually(() => Status(pot, id), s => s != "queued");
        await Task.Delay(4500); // past the expiry, which the broker was told about

        await device.ConnectAsync(persistent: true);
        await Task.Delay(1500);

        Assert.Empty(device.Commands);
        var ended = await Eventually(() => Command(pot, id), c => c.GetProperty("status").GetString() == "expired");
        Assert.Equal("expired", ended.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_retired_device_gets_no_more_commands_and_what_it_still_says_is_not_stored()
    {
        var pot = await Pot();
        await using var device = new FakeDevice(pot, stack);
        await device.ConnectAsync();
        using var retire = await stack.Client.PostAsJsonAsync($"{V2}/devices/{pot}/retire", new { });

        using var refused = await stack.Client.PostAsJsonAsync($"{V2}/devices/{pot}/commands", new
        {
            commandId = Guid.NewGuid(),
            type = "stop_actuator",
            actuatorId = "pump-1",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        });
        await device.Reading("soil-1", 40, DateTimeOffset.UtcNow.AddSeconds(-1));
        await Task.Delay(1500);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty(device.Commands);
        var devices = await stack.Client.GetFromJsonAsync<JsonElement[]>("/api/devices", Web);
        Assert.DoesNotContain(devices!, d => d.GetProperty("deviceId").GetString() == pot);
    }
}
