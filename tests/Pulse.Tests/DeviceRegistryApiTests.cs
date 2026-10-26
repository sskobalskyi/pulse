using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Core;

namespace Pulse.Tests;

/// <summary>
/// The device registry through its HTTP surface, against the real API with its database and stream processors.
/// Responses are checked against <c>contracts/pulse-device-api.yaml</c>, not just against what the tests expect.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class DeviceRegistryApiTests(PipelineFixture stack)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly OpenApiContract Contract = new(ContractPath());

    /// <summary>Operations that exist in the contract and are implemented. Keep in step with the endpoints.</summary>
    private static readonly string[] Implemented =
        ["listDevices", "registerDevice", "getDevice", "markShipped", "claimDevice", "retireDevice", "calibrateChannel", "getSeries", "getRules", "setRules", "sendCommand", "listCommands", "getCommand"];

    /// <summary>Operations of the contract that later changes will implement. Listing them keeps the gap visible.</summary>
    private static readonly string[] NotYet = [];

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pulse.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "contracts", "pulse-device-api.yaml");
    }

    private static string NewId() => $"reg-{Guid.NewGuid():N}"[..14];

    private static object Registration(string id, string token = "token-1", DateTimeOffset? expires = null, string model = "greenhaus-pot-v1") => new
    {
        id,
        model,
        capabilities = new
        {
            channels = new object[]
            {
                new { id = "soil-1", metric = "soil_moisture", label = "Zone 1", unit = "%" },
                new { id = "soil-2", metric = "soil_moisture", label = "Zone 2" },
                new { id = "air-t", metric = "temperature" },
            },
            actuators = new[] { new { id = "pump-1", kind = "pump", hardLimits = new { maxRunSeconds = 20, minIntervalSeconds = 600, dailyCapSeconds = 120 } } },
        },
        credentialHash = "pbkdf2$salt$hash",
        claimTokenHash = ClaimTokens.Hash(token),
        claimTokenExpiresAt = expires ?? DateTimeOffset.UtcNow.AddHours(1),
    };

    private string Url(string path) => Contract.Url(path);

    private async Task<HttpResponseMessage> Register(string id, string token = "token-1", DateTimeOffset? expires = null) =>
        await stack.Client.PostAsJsonAsync(Url("/devices"), Registration(id, token, expires));

    private async Task<Device> RegisterOk(string id, string token = "token-1")
    {
        using var response = await Register(id, token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<Device>(Web))!;
    }

    private Task<HttpResponseMessage> Post(string path, object? body = null) =>
        stack.Client.PostAsJsonAsync(Url(path), body ?? new { });

    private static async Task<string?> ProblemCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task Every_implemented_operation_matches_the_contract()
    {
        var id = NewId();

        // registerDevice: 201 created, 200 when repeated, 400 for an invalid body
        using var created = await Register(id);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await Contract.Verify(created, "POST", "/devices");
        using var repeated = await Register(id);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        await Contract.Verify(repeated, "POST", "/devices");
        using var invalid = await Post("/devices", new { id = "", model = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await Contract.Verify(invalid, "POST", "/devices");

        // getDevice and listDevices
        using var get = await stack.Client.GetAsync(Url($"/devices/{id}"));
        await Contract.Verify(get, "GET", "/devices/{deviceId}");
        using var missing = await stack.Client.GetAsync(Url("/devices/nope"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await Contract.Verify(missing, "GET", "/devices/{deviceId}");
        using var list = await stack.Client.GetAsync(Url("/devices?state=allocated"));
        await Contract.Verify(list, "GET", "/devices");
        using var badFilter = await stack.Client.GetAsync(Url("/devices?state=sleeping"));
        Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
        await Contract.Verify(badFilter, "GET", "/devices");

        // markShipped: 200, 404, 409
        using var shipped = await Post($"/devices/{id}/ship");
        Assert.Equal(HttpStatusCode.OK, shipped.StatusCode);
        await Contract.Verify(shipped, "POST", "/devices/{deviceId}/ship");
        using var shipUnknown = await Post("/devices/nope/ship");
        await Contract.Verify(shipUnknown, "POST", "/devices/{deviceId}/ship");

        // claimDevice: 409 for a wrong token, 200, then 409 again because the token works once, 404
        using var noToken = await Post($"/devices/{id}/claim", new { ownerId = "user-1" });
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        await Contract.Verify(noToken, "POST", "/devices/{deviceId}/claim");
        using var wrongToken = await Post($"/devices/{id}/claim", new { token = "not-it", ownerId = "user-1" });
        Assert.Equal(HttpStatusCode.Conflict, wrongToken.StatusCode);
        await Contract.Verify(wrongToken, "POST", "/devices/{deviceId}/claim");
        using var claimed = await Post($"/devices/{id}/claim", new { token = "token-1", ownerId = "user-1" });
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        await Contract.Verify(claimed, "POST", "/devices/{deviceId}/claim");
        using var claimedAgain = await Post($"/devices/{id}/claim", new { token = "token-1", ownerId = "user-2" });
        Assert.Equal(HttpStatusCode.Conflict, claimedAgain.StatusCode);
        using var claimUnknown = await Post("/devices/nope/claim", new { token = "x", ownerId = "u" });
        Assert.Equal(HttpStatusCode.NotFound, claimUnknown.StatusCode);
        await Contract.Verify(claimUnknown, "POST", "/devices/{deviceId}/claim");

        // The claim limit is documented as 429. It is 5 attempts a minute per device, and 4 were made on this one above.
        using var fifth = await Post($"/devices/{id}/claim", new { token = "x", ownerId = "u" });
        using var sixth = await Post($"/devices/{id}/claim", new { token = "x", ownerId = "u" });
        Assert.Equal(HttpStatusCode.Conflict, fifth.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        await Contract.Verify(sixth, "POST", "/devices/{deviceId}/claim");
        Assert.NotNull(sixth.Headers.RetryAfter);

        // calibrateChannel: 200 and 404
        using var calibrated = await stack.Client.PutAsJsonAsync(Url($"/devices/{id}/channels/soil-1/calibration"), new { dryRaw = 820.5, wetRaw = 310 });
        Assert.Equal(HttpStatusCode.OK, calibrated.StatusCode);
        await Contract.Verify(calibrated, "PUT", "/devices/{deviceId}/channels/{channelId}/calibration");
        using var calibrateBad = await stack.Client.PutAsJsonAsync(Url($"/devices/{id}/channels/soil-1/calibration"), new { dryRaw = 5, wetRaw = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, calibrateBad.StatusCode);
        await Contract.Verify(calibrateBad, "PUT", "/devices/{deviceId}/channels/{channelId}/calibration");
        using var calibrateMissing = await stack.Client.PutAsJsonAsync(Url($"/devices/{id}/channels/nope/calibration"), new { dryRaw = 1, wetRaw = 2 });
        Assert.Equal(HttpStatusCode.NotFound, calibrateMissing.StatusCode);
        await Contract.Verify(calibrateMissing, "PUT", "/devices/{deviceId}/channels/{channelId}/calibration");

        // getSeries: 200, and 404 for a channel the device does not have
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("o"));
        using var series = await stack.Client.GetAsync(Url($"/devices/{id}/channels/soil-1/series?from={from}"));
        Assert.Equal(HttpStatusCode.OK, series.StatusCode);
        await Contract.Verify(series, "GET", "/devices/{deviceId}/channels/{channelId}/series");
        using var seriesMissing = await stack.Client.GetAsync(Url($"/devices/{id}/channels/nope/series?from={from}"));
        Assert.Equal(HttpStatusCode.NotFound, seriesMissing.StatusCode);
        await Contract.Verify(seriesMissing, "GET", "/devices/{deviceId}/channels/{channelId}/series");

        // setRules and getRules: 200, 400 for rules that make no sense, 404
        var ruleSet = new object[]
        {
            new { channelId = "soil-1", kind = "threshold", min = 30, max = 80, margin = 5 },
            new { channelId = "soil-1", kind = "no_data", timeoutSeconds = 120 },
        };
        using var rulesStored = await stack.Client.PutAsJsonAsync(Url($"/devices/{id}/rules"), ruleSet);
        Assert.Equal(HttpStatusCode.OK, rulesStored.StatusCode);
        await Contract.Verify(rulesStored, "PUT", "/devices/{deviceId}/rules");
        using var rulesBad = await stack.Client.PutAsJsonAsync(Url($"/devices/{id}/rules"), new object[] { new { channelId = "soil-9", kind = "threshold", min = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, rulesBad.StatusCode);
        await Contract.Verify(rulesBad, "PUT", "/devices/{deviceId}/rules");
        using var rulesMissing = await stack.Client.PutAsJsonAsync(Url("/devices/nope/rules"), ruleSet);
        Assert.Equal(HttpStatusCode.NotFound, rulesMissing.StatusCode);
        await Contract.Verify(rulesMissing, "PUT", "/devices/{deviceId}/rules");
        using var rulesRead = await stack.Client.GetAsync(Url($"/devices/{id}/rules"));
        Assert.Equal(HttpStatusCode.OK, rulesRead.StatusCode);
        await Contract.Verify(rulesRead, "GET", "/devices/{deviceId}/rules");
        using var rulesReadMissing = await stack.Client.GetAsync(Url("/devices/nope/rules"));
        Assert.Equal(HttpStatusCode.NotFound, rulesReadMissing.StatusCode);
        await Contract.Verify(rulesReadMissing, "GET", "/devices/{deviceId}/rules");

        // sendCommand: 202, 400, 404, 409 (a command id for something else, then a retired device). listCommands and getCommand: 200, 404
        var commandId = Guid.NewGuid();
        var command = new { commandId, type = "run_actuator", actuatorId = "pump-1", durationSeconds = 10, expiresAt = DateTimeOffset.UtcNow.AddMinutes(2) };
        Assert.Empty(Contract.ValidateRequest("POST", "/devices/{deviceId}/commands", command));
        using var commandQueued = await stack.Client.PostAsJsonAsync(Url($"/devices/{id}/commands"), command);
        Assert.Equal(HttpStatusCode.Accepted, commandQueued.StatusCode);
        await Contract.Verify(commandQueued, "POST", "/devices/{deviceId}/commands");
        using var commandInvalid = await stack.Client.PostAsJsonAsync(Url($"/devices/{id}/commands"), new { commandId = Guid.NewGuid(), type = "run_actuator", actuatorId = "pump-1", durationSeconds = 21, expiresAt = DateTimeOffset.UtcNow.AddMinutes(2) });
        Assert.Equal(HttpStatusCode.BadRequest, commandInvalid.StatusCode);
        await Contract.Verify(commandInvalid, "POST", "/devices/{deviceId}/commands");
        using var commandUnknown = await stack.Client.PostAsJsonAsync(Url("/devices/nope/commands"), command);
        Assert.Equal(HttpStatusCode.NotFound, commandUnknown.StatusCode);
        await Contract.Verify(commandUnknown, "POST", "/devices/{deviceId}/commands");
        using var commandReused = await stack.Client.PostAsJsonAsync(Url($"/devices/{id}/commands"), new { commandId, type = "run_actuator", actuatorId = "pump-1", durationSeconds = 11, expiresAt = DateTimeOffset.UtcNow.AddMinutes(2) });
        Assert.Equal(HttpStatusCode.Conflict, commandReused.StatusCode);
        await Contract.Verify(commandReused, "POST", "/devices/{deviceId}/commands");
        using var commandList = await stack.Client.GetAsync(Url($"/devices/{id}/commands?limit=10"));
        Assert.Equal(HttpStatusCode.OK, commandList.StatusCode);
        await Contract.Verify(commandList, "GET", "/devices/{deviceId}/commands");
        using var commandListMissing = await stack.Client.GetAsync(Url("/devices/nope/commands"));
        Assert.Equal(HttpStatusCode.NotFound, commandListMissing.StatusCode);
        await Contract.Verify(commandListMissing, "GET", "/devices/{deviceId}/commands");
        using var commandRead = await stack.Client.GetAsync(Url($"/devices/{id}/commands/{commandId}"));
        Assert.Equal(HttpStatusCode.OK, commandRead.StatusCode);
        await Contract.Verify(commandRead, "GET", "/devices/{deviceId}/commands/{commandId}");
        using var commandReadMissing = await stack.Client.GetAsync(Url($"/devices/{id}/commands/{Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.NotFound, commandReadMissing.StatusCode);
        await Contract.Verify(commandReadMissing, "GET", "/devices/{deviceId}/commands/{commandId}");

        // retireDevice: 200 and 404
        using var retired = await Post($"/devices/{id}/retire");
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
        await Contract.Verify(retired, "POST", "/devices/{deviceId}/retire");
        using var retireUnknown = await Post("/devices/nope/retire");
        await Contract.Verify(retireUnknown, "POST", "/devices/{deviceId}/retire");

        Assert.Equal(Implemented.Order(), Contract.Covered.Order());
    }

    [Fact]
    public void The_contract_has_exactly_the_operations_this_test_knows_about()
    {
        // When the contract grows, this fails and says which operation is new, instead of silently going untested.
        Assert.Equal(
            Implemented.Concat(NotYet).Order(),
            Contract.Operations.Select(o => o.OperationId).Order());
    }

    [Fact]
    public async Task A_device_is_registered_with_its_capabilities_and_starts_allocated()
    {
        var id = NewId();

        var device = await RegisterOk(id);

        Assert.Equal("allocated", device.State);
        Assert.Null(device.OwnerId);
        Assert.Equal("unknown", device.Connection);
        Assert.Null(device.LastSeenAt);
        Assert.Equal(["soil-1", "soil-2", "air-t"], device.Capabilities.Channels.Select(c => c.Id)); // order is kept
        Assert.Equal("Zone 1", device.Capabilities.Channels[0].Label);
        Assert.Equal(20, device.Capabilities.Actuators.Single().HardLimits.MaxRunSeconds);
    }

    [Fact]
    public async Task Registering_twice_returns_the_first_registration_unchanged()
    {
        var id = NewId();
        await RegisterOk(id);

        using var second = await stack.Client.PostAsJsonAsync(Url("/devices"), Registration(id, token: "other", model: "a-different-model"));
        var device = (await second.Content.ReadFromJsonAsync<Device>(Web))!;

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("greenhaus-pot-v1", device.Model);
        // ...and the claim token of the first registration still works, the second one's does not.
        await Post($"/devices/{id}/ship");
        using var claim = await Post($"/devices/{id}/claim", new { token = "token-1", ownerId = "u" });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
    }

    [Fact]
    public async Task An_invalid_registration_is_rejected_with_every_problem_listed()
    {
        using var response = await stack.Client.PostAsJsonAsync(Url("/devices"), new
        {
            id = "bad id",
            model = "m",
            capabilities = new { channels = new[] { new { id = "c", metric = "weather" } }, actuators = Array.Empty<object>() },
            credentialHash = "h",
            claimTokenHash = "not-a-hash",
            claimTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains("id may only contain", detail);
        Assert.Contains("unknown metric 'weather'", detail);
        Assert.Contains("claimTokenHash", detail);
    }

    [Fact]
    public async Task A_malformed_body_is_a_problem_with_a_code_too()
    {
        using var response = await stack.Client.PostAsync(Url("/devices"), new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_request", await ProblemCode(response));
    }

    [Fact]
    public async Task A_device_cannot_be_claimed_before_it_ships()
    {
        var id = NewId();
        await RegisterOk(id);

        using var response = await Post($"/devices/{id}/claim", new { token = "token-1", ownerId = "u" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("invalid_state", await ProblemCode(response));
    }

    [Fact]
    public async Task Shipping_twice_is_accepted_but_a_claimed_device_cannot_ship()
    {
        var id = NewId();
        await RegisterOk(id);

        using var first = await Post($"/devices/{id}/ship");
        using var again = await Post($"/devices/{id}/ship");
        await Post($"/devices/{id}/claim", new { token = "token-1", ownerId = "u" });
        using var afterClaim = await Post($"/devices/{id}/ship");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, afterClaim.StatusCode);
    }

    [Fact]
    public async Task A_wrong_token_is_reported_as_token_invalid_and_leaves_the_device_claimable()
    {
        var id = NewId();
        await RegisterOk(id);
        await Post($"/devices/{id}/ship");

        using var wrong = await Post($"/devices/{id}/claim", new { token = "guess", ownerId = "u" });
        using var right = await Post($"/devices/{id}/claim", new { token = "token-1", ownerId = "u" });

        Assert.Equal("token_invalid", await ProblemCode(wrong));
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task Claiming_binds_the_device_to_its_owner()
    {
        var id = NewId();
        await RegisterOk(id);
        await Post($"/devices/{id}/ship");

        using var response = await Post($"/devices/{id}/claim", new { token = "token-1", ownerId = "user-42" });
        var device = (await response.Content.ReadFromJsonAsync<Device>(Web))!;

        Assert.Equal("claimed", device.State);
        Assert.Equal("user-42", device.OwnerId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"token":"t"}""")]
    [InlineData("""{"ownerId":"u"}""")]
    [InlineData("""{"token":"","ownerId":"u"}""")]
    public async Task A_claim_needs_a_token_and_an_owner(string body)
    {
        var id = NewId();
        await RegisterOk(id);
        await Post($"/devices/{id}/ship");

        using var response = await stack.Client.PostAsync(Url($"/devices/{id}/claim"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_device_list_can_be_filtered_by_state_and_owner()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        var mine = NewId();
        var other = NewId();
        await RegisterOk(mine);
        await RegisterOk(other);
        await Post($"/devices/{mine}/ship");
        await Post($"/devices/{mine}/claim", new { token = "token-1", ownerId = owner });

        var byOwner = (await stack.Client.GetFromJsonAsync<Device[]>(Url($"/devices?ownerId={owner}"), Web))!;
        var claimed = (await stack.Client.GetFromJsonAsync<Device[]>(Url("/devices?state=claimed"), Web))!;
        var allocated = (await stack.Client.GetFromJsonAsync<Device[]>(Url("/devices?state=allocated"), Web))!;

        Assert.Equal([mine], byOwner.Select(d => d.Id));
        Assert.Contains(claimed, d => d.Id == mine);
        Assert.DoesNotContain(claimed, d => d.Id == other);
        Assert.Contains(allocated, d => d.Id == other);

        using var bad = await stack.Client.GetAsync(Url("/devices?state=sleeping"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_state_filter", await ProblemCode(bad));
    }

    [Fact]
    public async Task The_first_reading_of_a_claimed_device_makes_it_active()
    {
        var id = NewId();
        await RegisterOk(id);
        await Post($"/devices/{id}/ship");
        await Post($"/devices/{id}/claim", new { token = "token-1", ownerId = "u" });
        Assert.Equal("claimed", (await Get(id)).State);

        using var ingest = await stack.Client.PostAsJsonAsync("/api/telemetry", new[] { new TelemetryReading(id, "temperature", 21, DateTimeOffset.UtcNow) });
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);

        var device = await Eventually(() => Get(id), d => d.State == "active");
        Assert.NotNull(device.LastSeenAt);
        Assert.Equal("online", device.Connection);
    }

    [Fact]
    public async Task Readings_do_not_activate_a_device_that_is_not_claimed()
    {
        var id = NewId();
        await RegisterOk(id);
        await Post($"/devices/{id}/ship");

        await stack.Client.PostAsJsonAsync("/api/telemetry", new[] { new TelemetryReading(id, "temperature", 21, DateTimeOffset.UtcNow) });
        await Eventually(() => Get(id), d => d.LastSeenAt is not null); // the reading was processed

        Assert.Equal("shipped", (await Get(id)).State);
    }

    [Fact]
    public async Task A_retired_device_is_refused_data_and_no_longer_raises_no_data_alerts()
    {
        var id = NewId();
        await RegisterOk(id);
        await Post($"/devices/{id}/ship");
        await Post($"/devices/{id}/claim", new { token = "token-1", ownerId = "u" });
        await stack.Client.PostAsJsonAsync("/api/telemetry", new[] { new TelemetryReading(id, "temperature", 21, DateTimeOffset.UtcNow) });

        // It reports once and then goes quiet. The no-data timeout of these tests is 3 seconds.
        await Eventually(() => Alerts(id), a => a.Any(x => x.Kind == AlertKind.NoData && x.State == AlertState.Raised));

        using var retire = await Post($"/devices/{id}/retire");
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        Assert.Equal("retired", (await retire.Content.ReadFromJsonAsync<Device>(Web))!.State);

        // The open no-data alert is resolved, because the series is no longer watched.
        var alerts = await Eventually(() => Alerts(id), a => a.Any(x => x.Kind == AlertKind.NoData && x.State == AlertState.Resolved));
        Assert.Equal(1, alerts.Count(x => x.State == AlertState.Raised && x.Kind == AlertKind.NoData));

        // Its data is refused, and nothing it had still queued can make it silent again.
        using var ingest = await stack.Client.PostAsJsonAsync("/api/telemetry", new[] { new TelemetryReading(id, "temperature", 22, DateTimeOffset.UtcNow) });
        Assert.Equal(HttpStatusCode.Conflict, ingest.StatusCode);
        Assert.Equal("device_retired", await ProblemCode(ingest));

        await Task.Delay(TimeSpan.FromSeconds(5)); // longer than the no-data timeout and a few checks
        Assert.Single((await Alerts(id)).Where(x => x.Kind == AlertKind.NoData && x.State == AlertState.Raised));

        // Retiring again is accepted.
        using var again = await Post($"/devices/{id}/retire");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task A_device_that_was_never_shipped_can_be_retired_and_its_token_stops_working()
    {
        var id = NewId();
        await RegisterOk(id);

        using var retire = await Post($"/devices/{id}/retire");
        await Post($"/devices/{id}/ship");
        using var claim = await Post($"/devices/{id}/claim", new { token = "token-1", ownerId = "u" });

        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, claim.StatusCode);
        Assert.Equal("retired", (await Get(id)).State);
    }

    [Fact]
    public async Task Data_from_a_device_that_was_never_registered_is_still_accepted()
    {
        // The standalone demo's simulator never registers anything.
        using var response = await stack.Client.PostAsJsonAsync("/api/telemetry", new[] { new TelemetryReading($"demo-{Guid.NewGuid():N}"[..12], "temperature", 21, DateTimeOffset.UtcNow) });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Calibration_is_stored_with_the_channel_and_shown_on_the_device()
    {
        var id = NewId();
        await RegisterOk(id);

        using var response = await stack.Client.PutAsJsonAsync(Url($"/devices/{id}/channels/soil-1/calibration"), new { dryRaw = 820.5, wetRaw = 310 });
        var channel = (await response.Content.ReadFromJsonAsync<Channel>(Web))!;
        var device = await Get(id);

        Assert.Equal(820.5, channel.Calibration!.DryRaw);
        Assert.Equal(310, channel.Calibration.WetRaw);
        Assert.Equal(channel.Calibration, device.Capabilities.Channels.Single(c => c.Id == "soil-1").Calibration);
        Assert.Null(device.Capabilities.Channels.Single(c => c.Id == "soil-2").Calibration); // other channels are untouched
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"dryRaw":1}""")]
    [InlineData("""{"dryRaw":5,"wetRaw":5}""")]
    public async Task An_unusable_calibration_is_rejected(string body)
    {
        var id = NewId();
        await RegisterOk(id);

        using var response = await stack.Client.PutAsync(Url($"/devices/{id}/channels/soil-1/calibration"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<Device> Get(string id) =>
        (await stack.Client.GetFromJsonAsync<Device>(Url($"/devices/{id}"), Web))!;

    private async Task<Alert[]> Alerts(string id) =>
        (await stack.Client.GetFromJsonAsync<Alert[]>("/api/alerts?limit=500", Web))!.Where(a => a.DeviceId == id).ToArray();

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        T value;
        do
        {
            value = await read();
            if (done(value)) return value;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Condition not met in 45 s. Last value: {JsonSerializer.Serialize(value)}");
    }
}
