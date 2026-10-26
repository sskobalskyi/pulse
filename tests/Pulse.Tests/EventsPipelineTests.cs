using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Core;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Pulse.Tests;

/// <summary>
/// Pulse's integration events against a real RabbitMQ: what it publishes when things happen, in the format the contract
/// promises, and what it does with the events it consumes.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class EventsPipelineTests(PipelineFixture stack)
{
    private static readonly AsyncApiContract Contract = new(ContractPath());
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string V2 = "/api/pulse/v2";

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pulse.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "contracts", "integration-events.yaml");
    }

    private static string NewId() => $"ev-{Guid.NewGuid():N}"[..14];

    private sealed record Message(string RoutingKey, IReadOnlyDictionary<string, object?> Headers, JsonElement Payload, string? MessageId)
    {
        public string? Device => Payload.TryGetProperty("deviceId", out var d) ? d.GetString() : null;
    }

    /// <remarks>In a topic exchange `*` matches exactly one word and `#` any number, and an event name has three (`alert.raised.v1`), so a pattern for a family is `alert.#`.</remarks>
    /// <summary>A queue bound to the exchange, collecting what is published to it. It is bound before anything happens, so nothing is missed.</summary>
    private sealed class Listener : IAsyncDisposable
    {
        private readonly List<Message> _messages = [];
        private IConnection _connection = null!;
        private IChannel _channel = null!;

        public static async Task<Listener> StartAsync(ConnectionFactory broker, params string[] routingKeys)
        {
            var listener = new Listener();
            listener._connection = await broker.CreateConnectionAsync();
            listener._channel = await listener._connection.CreateChannelAsync();
            await listener._channel.ExchangeDeclareAsync(EventNames.Exchange, ExchangeType.Topic, durable: true);
            var queue = (await listener._channel.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true)).QueueName;
            foreach (var key in routingKeys) await listener._channel.QueueBindAsync(queue, EventNames.Exchange, key);

            var consumer = new AsyncEventingBasicConsumer(listener._channel);
            consumer.ReceivedAsync += (_, delivery) =>
            {
                var headers = (delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
                    .ToDictionary(h => h.Key, h => h.Value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : h.Value);
                var message = new Message(delivery.RoutingKey, headers, JsonDocument.Parse(delivery.Body.ToArray()).RootElement.Clone(), delivery.BasicProperties.MessageId);
                lock (listener._messages) listener._messages.Add(message);
                return Task.CompletedTask;
            };
            await listener._channel.BasicConsumeAsync(queue, autoAck: true, consumer);
            return listener;
        }

        public IReadOnlyList<Message> For(string device) { lock (_messages) return [.. _messages.Where(m => m.Device == device)]; }

        public async Task<Message> Next(string device, string routingKey, Func<Message, bool>? where = null, int skip = 0, int seconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                var found = For(device).Where(m => m.RoutingKey == routingKey && (where?.Invoke(m) ?? true)).Skip(skip).FirstOrDefault();
                if (found is not null) return found;
                await Task.Delay(100);
            }

            throw new TimeoutException($"No {routingKey} for {device} in {seconds} s. Got: {string.Join(", ", For(device).Select(m => m.RoutingKey))}");
        }

        public async ValueTask DisposeAsync()
        {
            await _channel.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private static void AssertFitsContract(Message message)
    {
        var payload = Contract.ValidatePayload(message.RoutingKey, message.Payload);
        Assert.True(payload.Count == 0, $"{message.RoutingKey} payload does not fit the contract: {string.Join("; ", payload)}\n{message.Payload}");

        var headers = Contract.ValidateHeaders(message.RoutingKey, message.Headers);
        Assert.True(headers.Count == 0, $"{message.RoutingKey} headers do not fit the contract: {string.Join("; ", headers)}");
        Assert.Equal("1", message.Headers["schemaVersion"]);
        Assert.Equal(message.MessageId, message.Headers["messageId"]); // the same id in the properties and the headers
    }

    private async Task<string> ClaimedPot()
    {
        var id = NewId();
        var token = "token-" + id;
        using var register = await stack.Client.PostAsJsonAsync($"{V2}/devices", new
        {
            id,
            model = "greenhaus-pot-v1",
            capabilities = new { channels = new[] { new { id = "air-t", metric = "temperature" } }, actuators = Array.Empty<object>() },
            credentialHash = "h",
            claimTokenHash = ClaimTokens.Hash(token),
            claimTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        using var ship = await stack.Client.PostAsJsonAsync($"{V2}/devices/{id}/ship", new { });
        using var claim = await stack.Client.PostAsJsonAsync($"{V2}/devices/{id}/claim", new { token, ownerId = "owner" });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        return id;
    }

    private async Task Send(string device, double value)
    {
        using var response = await stack.Client.PostAsJsonAsync("/api/telemetry", new[] { new TelemetryReading(device, "temperature", value, DateTimeOffset.UtcNow, "air-t") });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public void The_schema_is_migrated_before_anything_that_uses_the_database_starts()
    {
        // Hosted services start in the order they were registered. The relay reads the outbox table and the consumer
        // registers devices, so both have to come after the migration, or they fail on a database that is not up to date yet.
        var services = stack.HostedServices;
        var migration = services.IndexOf(typeof(Pulse.Api.Storage.MigrationService));
        Assert.True(migration >= 0, "the migration is not a hosted service");

        foreach (var user in new[]
        {
            typeof(Pulse.Api.Processing.TimescaleWriter), typeof(Pulse.Api.Processing.AlertEvaluator), typeof(Pulse.Api.Processing.SilenceMonitor),
            typeof(Pulse.Api.Events.OutboxPublisher), typeof(Pulse.Api.Events.DeviceAllocatedConsumer),
        })
        {
            var position = services.IndexOf(user);
            Assert.True(position > migration, $"{user.Name} starts at {position}, the migration at {migration}");
        }
    }

    // --- what Pulse publishes ---

    [Fact]
    public async Task A_breach_and_its_recovery_are_published_with_one_alert_id_in_the_contracts_format()
    {
        await using var listener = await Listener.StartAsync(stack.BrokerFactory, "alert.#");
        var pot = await ClaimedPot();

        await Send(pot, 95); // above 80
        var raised = await listener.Next(pot, "alert.raised.v1", m => m.Payload.GetProperty("rule").GetString() == "threshold");
        await Send(pot, 50);
        var resolved = await listener.Next(pot, "alert.resolved.v1", m => m.Payload.GetProperty("rule").GetString() == "threshold");

        AssertFitsContract(raised);
        AssertFitsContract(resolved);
        Assert.Equal(("air-t", "temperature", "above", 95.0), (raised.Payload.GetProperty("channelId").GetString(), raised.Payload.GetProperty("metric").GetString(), raised.Payload.GetProperty("direction").GetString(), raised.Payload.GetProperty("value").GetDouble()));
        Assert.Equal(raised.Payload.GetProperty("alertId").GetString(), resolved.Payload.GetProperty("alertId").GetString());
        Assert.NotEqual(raised.MessageId, resolved.MessageId); // two events, two message ids
    }

    [Fact]
    public async Task Every_event_gets_its_own_message_id_and_a_trace_header()
    {
        await using var listener = await Listener.StartAsync(stack.BrokerFactory, "device.#");
        var pot = await ClaimedPot();
        await Send(pot, 20);
        await listener.Next(pot, "device.status.v1");

        using var retire = await stack.Client.PostAsJsonAsync($"{V2}/devices/{pot}/retire", new { });
        await listener.Next(pot, "device.retired.v1");

        var all = listener.For(pot);
        Assert.Equal(all.Count, all.Select(m => m.MessageId).Distinct().Count());
        Assert.All(all, m => Assert.True(Guid.TryParse(m.MessageId, out _)));
        Assert.Contains(all, m => m.Headers.ContainsKey("traceparent")); // the retire call was made inside a request, so it has a trace
    }

    [Fact]
    public async Task A_device_that_goes_quiet_is_published_offline_with_a_no_data_alert_and_online_again_when_it_returns()
    {
        await using var listener = await Listener.StartAsync(stack.BrokerFactory, "device.status.v1", "alert.#");
        var pot = await ClaimedPot();

        await Send(pot, 20);
        var online = await listener.Next(pot, "device.status.v1", m => m.Payload.GetProperty("status").GetString() == "online");
        // The platform's no-data timeout in these tests is 3 seconds.
        var offline = await listener.Next(pot, "device.status.v1", m => m.Payload.GetProperty("status").GetString() == "offline");
        var noData = await listener.Next(pot, "alert.raised.v1", m => m.Payload.GetProperty("rule").GetString() == "no_data");
        await Send(pot, 21);
        var back = await listener.Next(pot, "device.status.v1", m => m.Payload.GetProperty("status").GetString() == "online", skip: 1);
        var noDataResolved = await listener.Next(pot, "alert.resolved.v1", m => m.Payload.GetProperty("rule").GetString() == "no_data");

        foreach (var message in new[] { online, offline, noData, back, noDataResolved }) AssertFitsContract(message);
        Assert.Equal("reconnect", online.Payload.GetProperty("cause").GetString());
        Assert.Equal("no_data", offline.Payload.GetProperty("cause").GetString());
        Assert.Equal("reconnect", back.Payload.GetProperty("cause").GetString());
        Assert.False(noData.Payload.TryGetProperty("value", out _)); // seconds of silence are not a reading
        Assert.Equal(noData.Payload.GetProperty("alertId").GetString(), noDataResolved.Payload.GetProperty("alertId").GetString());
    }

    [Fact]
    public async Task The_end_of_a_command_is_published_once_in_the_contracts_format()
    {
        await using var listener = await Listener.StartAsync(stack.BrokerFactory, "command.completed.v1");
        var pot = NewId();
        using var register = await stack.Client.PostAsJsonAsync($"{V2}/devices", new
        {
            id = pot,
            model = "greenhaus-pot-v1",
            capabilities = new
            {
                channels = new[] { new { id = "soil-1", metric = "soil_moisture" } },
                actuators = new[] { new { id = "pump-1", kind = "pump", hardLimits = new { maxRunSeconds = 20, minIntervalSeconds = 600, dailyCapSeconds = 120 } } },
            },
            credentialHash = "h",
            claimTokenHash = ClaimTokens.Hash("t"),
            claimTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        var commandId = Guid.NewGuid();
        using var queued = await stack.Client.PostAsJsonAsync($"{V2}/devices/{pot}/commands", new
        {
            commandId,
            type = "run_actuator",
            actuatorId = "pump-1",
            durationSeconds = 10,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
        });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var store = stack.Services.GetRequiredService<Pulse.Api.Commands.CommandStore>();

        // What the device link does when the device answers: first it took the command, then it refused it.
        await store.ApplyAckAsync(pot, commandId, CommandStatus.Accepted, null, default);
        await store.ApplyAckAsync(pot, commandId, CommandStatus.Rejected, "min_interval", default);
        await store.ApplyAckAsync(pot, commandId, CommandStatus.Rejected, "min_interval", default); // the broker delivered it twice
        var message = await listener.Next(pot, "command.completed.v1");
        await Task.Delay(1500);

        AssertFitsContract(message);
        Assert.Equal((commandId, "pump-1", "rejected", "min_interval"),
            (message.Payload.GetProperty("commandId").GetGuid(), message.Payload.GetProperty("actuatorId").GetString(),
             message.Payload.GetProperty("status").GetString(), message.Payload.GetProperty("reason").GetString()));
        Assert.Single(listener.For(pot), m => m.RoutingKey == "command.completed.v1");
    }

    [Fact]
    public async Task Retiring_a_device_is_published_once()
    {
        await using var listener = await Listener.StartAsync(stack.BrokerFactory, "device.retired.v1");
        var pot = await ClaimedPot();

        using var first = await stack.Client.PostAsJsonAsync($"{V2}/devices/{pot}/retire", new { });
        using var second = await stack.Client.PostAsJsonAsync($"{V2}/devices/{pot}/retire", new { });
        var retired = await listener.Next(pot, "device.retired.v1");
        await Task.Delay(1500);

        AssertFitsContract(retired);
        Assert.Single(listener.For(pot), m => m.RoutingKey == "device.retired.v1");
    }

    [Fact]
    public async Task A_device_that_never_reports_is_never_announced()
    {
        await using var listener = await Listener.StartAsync(stack.BrokerFactory, "device.status.v1");
        var pot = await ClaimedPot();

        await Task.Delay(TimeSpan.FromSeconds(5)); // longer than the no-data timeout and a number of checks

        Assert.Empty(listener.For(pot));
    }

    // --- what Pulse consumes ---

    private static object Allocated(string device, object? actuators = null, string? claimTokenHash = null) => new
    {
        deviceId = device,
        orderId = Guid.NewGuid(),
        customerId = "user-1",
        sku = "pot-v1",
        model = "greenhaus-pot-v1",
        claimTokenHash = claimTokenHash ?? ClaimTokens.Hash("allocated-token"),
        claimTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        credentialHash = "pbkdf2$salt$hash",
        capabilities = new
        {
            channels = new object[] { new { id = "soil-1", metric = "soil_moisture", label = "Zone 1" }, new { id = "air-t", metric = "temperature" } },
            actuators = actuators ?? new[] { new { id = "pump-1", kind = "pump", hardLimits = new { maxRunSeconds = 20, minIntervalSeconds = 600, dailyCapSeconds = 120 } } },
        },
    };

    private async Task Publish(byte[] body)
    {
        await using var connection = await stack.BrokerFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var id = Guid.NewGuid().ToString();
        await channel.BasicPublishAsync(EventNames.Exchange, EventNames.DeviceAllocated, mandatory: false,
            new BasicProperties
            {
                MessageId = id,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Headers = new Dictionary<string, object?> { ["messageId"] = id, ["occurredAt"] = DateTimeOffset.UtcNow.ToString("o"), ["schemaVersion"] = "1" },
            },
            body);
    }

    private Task Publish(object payload) => Publish(JsonSerializer.SerializeToUtf8Bytes(payload, Web));

    private async Task<HttpResponseMessage> WaitForDevice(string id, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        HttpResponseMessage response;
        do
        {
            response = await stack.Client.GetAsync($"{V2}/devices/{id}");
            if (response.StatusCode == HttpStatusCode.OK) return response;
            response.Dispose();
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Device {id} was never registered.");
    }

    [Fact]
    public async Task A_device_allocated_message_registers_the_device_ready_to_be_shipped_and_claimed()
    {
        var device = NewId();

        await Publish(Allocated(device));
        using var response = await WaitForDevice(device);
        var registered = (await response.Content.ReadFromJsonAsync<Device>(Web))!;

        Assert.Equal("allocated", registered.State);
        Assert.Equal(["soil-1", "air-t"], registered.Capabilities.Channels.Select(c => c.Id));
        Assert.Equal("Zone 1", registered.Capabilities.Channels[0].Label);
        Assert.Equal(20, registered.Capabilities.Actuators.Single().HardLimits.MaxRunSeconds);

        // The claim token that came in the message is the one that works.
        using var ship = await stack.Client.PostAsJsonAsync($"{V2}/devices/{device}/ship", new { });
        using var claim = await stack.Client.PostAsJsonAsync($"{V2}/devices/{device}/claim", new { token = "allocated-token", ownerId = "user-1" });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
    }

    [Fact]
    public async Task The_same_message_delivered_twice_registers_the_device_once_and_changes_nothing()
    {
        var device = NewId();
        var message = Allocated(device);

        await Publish(message);
        await WaitForDevice(device);
        // Ship it, so any second registration that "reset" the device would show.
        using var ship = await stack.Client.PostAsJsonAsync($"{V2}/devices/{device}/ship", new { });
        await Publish(message);
        await Publish(message);
        await Task.Delay(2000);

        using var response = await stack.Client.GetAsync($"{V2}/devices/{device}");
        Assert.Equal("shipped", (await response.Content.ReadFromJsonAsync<Device>(Web))!.State);
    }

    private async Task<(string Body, string? MessageId)> NextDeadLetter(Func<string, bool> match, int seconds = 30)
    {
        await using var connection = await stack.BrokerFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var result = await channel.BasicGetAsync("pulse.dead-letters", autoAck: true);
            if (result is not null)
            {
                var body = Encoding.UTF8.GetString(result.Body.ToArray());
                if (match(body)) return (body, result.BasicProperties.MessageId);
                continue;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("Nothing matching arrived in the dead-letter queue.");
    }

    [Fact]
    public async Task A_message_that_can_never_be_registered_is_parked_in_the_dead_letter_queue_and_not_retried()
    {
        var device = NewId();

        // An actuator without hard limits, which is what the contract used to allow and the registry refuses.
        await Publish(Allocated(device, actuators: new[] { new { id = "pump-1", kind = "pump" } }));
        var (body, _) = await NextDeadLetter(b => b.Contains(device));

        Assert.Contains(device, body);
        using var response = await stack.Client.GetAsync($"{V2}/devices/{device}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_message_that_is_not_json_is_parked_too_and_does_not_stop_the_ones_behind_it()
    {
        var marker = $"not-json-{Guid.NewGuid():N}";
        var device = NewId();

        await Publish(Encoding.UTF8.GetBytes($"this is not json {marker}"));
        await Publish(Allocated(device));

        await NextDeadLetter(b => b.Contains(marker));
        using var response = await WaitForDevice(device); // the good message behind it was still handled
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_claim_token_hash_in_the_wrong_format_is_parked()
    {
        var device = NewId();

        await Publish(Allocated(device, claimTokenHash: "a-plain-token"));

        await NextDeadLetter(b => b.Contains(device));
        using var response = await stack.Client.GetAsync($"{V2}/devices/{device}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
