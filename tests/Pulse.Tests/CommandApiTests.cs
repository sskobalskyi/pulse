using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pulse.Core;

namespace Pulse.Tests;

/// <summary>The command API on the whole stack: what a sender gets back, and what the queue does with a command nobody can deliver.</summary>
[Collection(PipelineCollection.Name)]
public sealed class CommandApiTests(PipelineFixture stack)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string V2 = "/api/pulse/v2";

    private static string NewId() => $"cmd-{Guid.NewGuid():N}"[..14];

    private async Task<string> Pot()
    {
        var id = NewId();
        using var response = await stack.Client.PostAsJsonAsync($"{V2}/devices", new
        {
            id,
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
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    private static object Run(Guid? id = null, int seconds = 10, TimeSpan? validFor = null, string actuator = "pump-1") => new
    {
        commandId = id ?? Guid.NewGuid(),
        type = "run_actuator",
        actuatorId = actuator,
        durationSeconds = seconds,
        expiresAt = DateTimeOffset.UtcNow + (validFor ?? TimeSpan.FromMinutes(2)),
    };

    private Task<HttpResponseMessage> Send(string device, object body) => stack.Client.PostAsJsonAsync($"{V2}/devices/{device}/commands", body);

    private Task<HttpResponseMessage> SendRaw(string device, string json) =>
        stack.Client.PostAsync($"{V2}/devices/{device}/commands", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private async Task<JsonElement> Get(string device, Guid command)
    {
        using var response = await stack.Client.GetAsync($"{V2}/devices/{device}/commands/{command}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_command_is_accepted_and_starts_out_queued()
    {
        var pot = await Pot();
        var id = Guid.NewGuid();

        using var response = await Send(pot, Run(id));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var command = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((id, pot, "run_actuator", "pump-1", "queued"),
            (command.GetProperty("commandId").GetGuid(), command.GetProperty("deviceId").GetString(), command.GetProperty("type").GetString(),
             command.GetProperty("actuatorId").GetString(), command.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Null, command.GetProperty("finishedAt").ValueKind);
        Assert.False(command.TryGetProperty("reason", out _)); // absent, not null: the contract's reason is a string
    }

    [Fact]
    public async Task Sending_a_command_twice_is_one_command_and_both_answers_are_the_same()
    {
        var pot = await Pot();
        var body = Run();

        using var first = await Send(pot, body);
        using var second = await Send(pot, body);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Single((await stack.Client.GetFromJsonAsync<JsonElement[]>($"{V2}/devices/{pot}/commands", Web))!);
    }

    [Fact]
    public async Task The_same_command_id_for_something_else_is_a_conflict_with_its_own_code()
    {
        var pot = await Pot();
        var id = Guid.NewGuid();
        using var first = await Send(pot, Run(id, seconds: 10));

        using var other = await Send(pot, Run(id, seconds: 12));

        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("command_id_reused", await Code(other));
        // The stored one is untouched: still the run of ten seconds that was queued, not a command of twelve.
        Assert.Contains((await Get(pot, id)).GetProperty("status").GetString(), new[] { "queued", "sent" });
        Assert.Single((await stack.Client.GetFromJsonAsync<JsonElement[]>($"{V2}/devices/{pot}/commands", Web))!);
    }

    [Fact]
    public async Task Commands_the_device_could_never_run_are_refused_with_a_reason_a_sender_can_switch_on()
    {
        var pot = await Pot();

        using var tooLong = await Send(pot, Run(seconds: 21));
        using var noSuchActuator = await Send(pot, Run(actuator: "pump-9"));
        using var late = await Send(pot, Run(validFor: TimeSpan.FromSeconds(-5)));
        using var noType = await Send(pot, new { commandId = Guid.NewGuid(), actuatorId = "pump-1", expiresAt = DateTimeOffset.UtcNow.AddMinutes(1) });
        using var noExpiry = await Send(pot, new { commandId = Guid.NewGuid(), type = "stop_actuator", actuatorId = "pump-1" });

        Assert.All(new[] { tooLong, noSuchActuator, late, noType, noExpiry }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal("validation_failed", await Code(tooLong));
        Assert.Equal("validation_failed", await Code(noSuchActuator));
        Assert.Equal("command_expired", await Code(late));
        Assert.Equal("validation_failed", await Code(noType));
        Assert.Equal("validation_failed", await Code(noExpiry));
        Assert.Empty((await stack.Client.GetFromJsonAsync<JsonElement[]>($"{V2}/devices/{pot}/commands", Web))!); // none of them was stored
    }

    [Fact]
    public async Task A_body_that_is_not_a_command_is_a_bad_request_and_not_a_server_error()
    {
        var pot = await Pot();

        using var broken = await SendRaw(pot, "{ nope");
        using var nothing = await SendRaw(pot, "null");
        using var badId = await SendRaw(pot, "{\"commandId\":\"x\"}");

        Assert.All(new[] { broken, nothing, badId }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
    }

    [Fact]
    public async Task An_unknown_device_is_not_found_and_so_is_an_unknown_command()
    {
        var pot = await Pot();

        using var unknown = await Send("nope", Run());
        using var missing = await stack.Client.GetAsync($"{V2}/devices/{pot}/commands/{Guid.NewGuid()}");
        using var notAGuid = await stack.Client.GetAsync($"{V2}/devices/{pot}/commands/abc");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notAGuid.StatusCode);
    }

    [Fact]
    public async Task A_retired_device_takes_no_commands_and_its_waiting_ones_fail()
    {
        var pot = await Pot();
        var waiting = Guid.NewGuid();
        using var queued = await Send(pot, Run(waiting));

        using var retire = await stack.Client.PostAsJsonAsync($"{V2}/devices/{pot}/retire", new { });
        using var after = await Send(pot, Run());

        Assert.Equal(HttpStatusCode.Conflict, after.StatusCode);
        Assert.Equal("invalid_state", await Code(after));
        var command = await Get(pot, waiting);
        Assert.Equal(("failed", "device_retired"), (command.GetProperty("status").GetString(), command.GetProperty("reason").GetString()));
        Assert.NotEqual(JsonValueKind.Null, command.GetProperty("finishedAt").ValueKind);
    }

    [Fact]
    public async Task A_command_nobody_answers_expires_on_its_own()
    {
        var pot = await Pot();
        var id = Guid.NewGuid();
        using var queued = await Send(pot, Run(id, validFor: TimeSpan.FromSeconds(2)));

        var command = await Get(pot, id);
        for (var i = 0; i < 40 && command.GetProperty("status").GetString() is "queued" or "sent"; i++)
        {
            await Task.Delay(250);
            command = await Get(pot, id);
        }

        Assert.Equal(("expired", "expired"), (command.GetProperty("status").GetString(), command.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task The_list_is_limited_and_newest_first()
    {
        var pot = await Pot();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(Guid.NewGuid());
            using var sent = await Send(pot, Run(ids[i]));
            await Task.Delay(20);
        }

        var two = await stack.Client.GetFromJsonAsync<JsonElement[]>($"{V2}/devices/{pot}/commands?limit=2", Web);
        using var tooMany = await stack.Client.GetAsync($"{V2}/devices/{pot}/commands?limit=100000");

        Assert.Equal([ids[2], ids[1]], two!.Select(c => c.GetProperty("commandId").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, tooMany.StatusCode); // clamped, as the other limits are
    }
}
