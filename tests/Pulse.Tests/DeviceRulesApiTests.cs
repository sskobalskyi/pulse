using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Core;

namespace Pulse.Tests;

/// <summary>Per-device alert rules through the real API and pipeline.</summary>
[Collection(PipelineCollection.Name)]
public sealed class DeviceRulesApiTests(PipelineFixture stack)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string V2 = "/api/pulse/v2";

    private static string NewId() => $"rl-{Guid.NewGuid():N}"[..14];

    private async Task<string> Pot()
    {
        var id = NewId();
        using var response = await stack.Client.PostAsJsonAsync($"{V2}/devices", new
        {
            id,
            model = "greenhaus-pot-v1",
            capabilities = new
            {
                channels = new[] { new { id = "soil-1", metric = "soil_moisture" }, new { id = "soil-2", metric = "soil_moisture" }, new { id = "air-t", metric = "temperature" } },
                actuators = Array.Empty<object>(),
            },
            credentialHash = "h",
            claimTokenHash = ClaimTokens.Hash("t"),
            claimTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    private Task<HttpResponseMessage> SetRules(string id, params object[] rules) => stack.Client.PutAsJsonAsync($"{V2}/devices/{id}/rules", rules);

    private async Task<JsonElement[]> GetRules(string id) => (await stack.Client.GetFromJsonAsync<JsonElement[]>($"{V2}/devices/{id}/rules", Web))!;

    private static object Threshold(string channel, double? min, double? max, double? margin = null) =>
        new Dictionary<string, object?> { ["channelId"] = channel, ["kind"] = "threshold", ["min"] = min, ["max"] = max, ["margin"] = margin }
            .Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value);

    private static object NoData(string channel, int seconds) => new { channelId = channel, kind = "no_data", timeoutSeconds = seconds };

    private async Task Send(string device, string channel, string metric, double value)
    {
        using var response = await stack.Client.PostAsJsonAsync("/api/telemetry", new[] { new TelemetryReading(device, metric, value, DateTimeOffset.UtcNow, channel) });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private async Task<Alert[]> Alerts(string device, AlertKind kind = AlertKind.Threshold) =>
        (await stack.Client.GetFromJsonAsync<Alert[]>("/api/alerts?limit=500", Web))!.Where(a => a.DeviceId == device && a.Kind == kind).ToArray();

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done, int seconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        T value;
        do
        {
            value = await read();
            if (done(value)) return value;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Condition not met in {seconds} s. Last value: {JsonSerializer.Serialize(value)}");
    }

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    // --- storing and reading rules ---

    [Fact]
    public async Task A_device_starts_with_no_rules_of_its_own()
    {
        Assert.Empty(await GetRules(await Pot()));
    }

    [Fact]
    public async Task Rules_are_stored_and_read_back_as_they_were_set()
    {
        var pot = await Pot();

        using var response = await SetRules(pot, NoData("soil-2", 300), Threshold("soil-1", 30, 80, 5), Threshold("air-t", null, 35));
        var stored = (await response.Content.ReadFromJsonAsync<JsonElement[]>(Web))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, stored.Length);
        var soil = stored.Single(r => r.GetProperty("channelId").GetString() == "soil-1");
        Assert.Equal((30.0, 80.0, 5.0), (soil.GetProperty("min").GetDouble(), soil.GetProperty("max").GetDouble(), soil.GetProperty("margin").GetDouble()));
        var air = stored.Single(r => r.GetProperty("channelId").GetString() == "air-t");
        Assert.False(air.TryGetProperty("min", out _)); // a bound that was not set is not made up
        Assert.Equal(35.0, air.GetProperty("max").GetDouble());
        Assert.Equal(300, stored.Single(r => r.GetProperty("kind").GetString() == "no_data").GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal((await GetRules(pot)).Select(r => r.ToString()), stored.Select(r => r.ToString()));
    }

    [Fact]
    public async Task Setting_rules_replaces_the_old_ones_and_an_empty_list_clears_them()
    {
        var pot = await Pot();
        await SetRules(pot, Threshold("soil-1", 30, 80), Threshold("soil-2", 30, 80));

        await SetRules(pot, Threshold("soil-1", 10, 90));
        var afterReplace = await GetRules(pot);
        await SetRules(pot);
        var afterClear = await GetRules(pot);

        Assert.Equal(["soil-1"], afterReplace.Select(r => r.GetProperty("channelId").GetString()));
        Assert.Equal(10.0, afterReplace[0].GetProperty("min").GetDouble());
        Assert.Empty(afterClear);
    }

    [Fact]
    public async Task A_failed_replace_leaves_the_old_rules_untouched()
    {
        var pot = await Pot();
        await SetRules(pot, Threshold("soil-1", 30, 80));

        using var bad = await SetRules(pot, Threshold("soil-2", 10, 90), Threshold("soil-9", 1, 2));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(["soil-1"], (await GetRules(pot)).Select(r => r.GetProperty("channelId").GetString()));
    }

    [Fact]
    public async Task A_rule_about_a_channel_the_device_does_not_have_is_unknown_channel()
    {
        using var response = await SetRules(await Pot(), Threshold("soil-9", 1, 2));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unknown_channel", await Code(response));
    }

    [Theory]
    [InlineData("""[{"channelId":"soil-1","kind":"threshold"}]""", "at least one of min and max")]
    [InlineData("""[{"channelId":"soil-1","kind":"threshold","min":80,"max":30}]""", "min must be below max")]
    [InlineData("""[{"channelId":"soil-1","kind":"threshold","min":10,"max":20,"margin":5}]""", "could never clear")]
    [InlineData("""[{"channelId":"soil-1","kind":"threshold","min":10,"margin":-1}]""", "margin must be zero or more")]
    [InlineData("""[{"channelId":"soil-1","kind":"no_data","timeoutSeconds":5}]""", "at least 10")]
    [InlineData("""[{"channelId":"soil-1","kind":"no_data"}]""", "timeoutSeconds is required")]
    [InlineData("""[{"channelId":"soil-1","kind":"alarm","min":1}]""", "kind must be")]
    [InlineData("""[{"channelId":"soil-1","kind":"threshold","min":1},{"channelId":"soil-1","kind":"threshold","max":9}]""", "only one rule of each kind")]
    public async Task Rules_that_make_no_sense_are_refused_with_the_reason(string body, string reason)
    {
        using var response = await stack.Client.PutAsync($"{V2}/devices/{await Pot()}/rules", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await Code(response));
        Assert.Contains(reason, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_body_that_is_not_a_list_is_a_bad_request()
    {
        using var response = await stack.Client.PutAsync($"{V2}/devices/{await Pot()}/rules", new StringContent("""{"channelId":"soil-1"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_retired_device_takes_no_new_rules_but_its_rules_can_still_be_read()
    {
        var pot = await Pot();
        await SetRules(pot, Threshold("soil-1", 30, 80));
        await stack.Client.PostAsJsonAsync($"{V2}/devices/{pot}/retire", new { });

        using var set = await SetRules(pot, Threshold("soil-1", 10, 90));

        Assert.Equal(HttpStatusCode.Conflict, set.StatusCode);
        Assert.Equal("invalid_state", await Code(set));
        Assert.Single(await GetRules(pot));
    }

    // --- rules at work ---

    [Fact]
    public async Task A_rule_of_its_own_raises_an_alert_where_the_platform_has_none()
    {
        var pot = await Pot();
        await SetRules(pot, Threshold("soil-1", 40, 60));

        await Send(pot, "soil-1", "soil_moisture", 35); // dry for zone 1
        await Send(pot, "soil-2", "soil_moisture", 35); // zone 2 has no rule: the platform has none for soil moisture either

        var alerts = await Eventually(() => Alerts(pot), a => a.Length >= 1);
        await Task.Delay(1500);
        alerts = await Alerts(pot);
        var alert = Assert.Single(alerts);
        Assert.Equal(("soil-1", AlertState.Raised), (alert.ChannelId, alert.State));
        Assert.Equal("soil_moisture outside [40, 60]", alert.Rule);
    }

    [Fact]
    public async Task A_margin_keeps_the_alert_raised_until_the_value_is_well_back()
    {
        var pot = await Pot();
        await SetRules(pot, Threshold("soil-1", 40, 60, 5));

        await Send(pot, "soil-1", "soil_moisture", 30); // breach
        await Eventually(() => Alerts(pot), a => a.Length == 1);
        await Send(pot, "soil-1", "soil_moisture", 42); // back inside, but within the margin of 5: still raised
        await Send(pot, "soil-1", "soil_moisture", 50); // clear of the margin: resolved

        var alerts = await Eventually(() => Alerts(pot), a => a.Length == 2);
        Assert.Equal([AlertState.Resolved, AlertState.Raised], alerts.Select(a => a.State));
        Assert.Equal(50, alerts.First(a => a.State == AlertState.Resolved).Value);
    }

    [Fact]
    public async Task Changing_a_rule_resolves_the_alert_of_the_old_range_instead_of_leaving_it_raised_for_ever()
    {
        var pot = await Pot();
        await SetRules(pot, Threshold("soil-1", 40, 60));
        await Send(pot, "soil-1", "soil_moisture", 35);
        await Eventually(() => Alerts(pot), a => a.Length == 1);

        await SetRules(pot, Threshold("soil-1", 30, 60)); // 35 is fine under the new range
        await Send(pot, "soil-1", "soil_moisture", 35);

        var alerts = await Eventually(() => Alerts(pot), a => a.Length == 2);
        var resolved = alerts.Single(a => a.State == AlertState.Resolved);
        Assert.Equal("soil_moisture outside [40, 60]", resolved.Rule);
    }

    [Fact]
    public async Task Removing_the_rules_resolves_their_alerts_and_the_platform_defaults_apply_again()
    {
        var pot = await Pot();
        await SetRules(pot, Threshold("air-t", 20, 25));
        await Send(pot, "air-t", "temperature", 30); // above 25: raised under the device's rule
        await Eventually(() => Alerts(pot), a => a.Length == 1);

        await SetRules(pot); // back to the platform's range for temperature, [-10, 80]
        await Send(pot, "air-t", "temperature", 30);

        var alerts = await Eventually(() => Alerts(pot), a => a.Length == 2);
        Assert.Equal([AlertState.Resolved, AlertState.Raised], alerts.Select(a => a.State));
        Assert.Equal("temperature outside [20, 25]", alerts[0].Rule);

        await Send(pot, "air-t", "temperature", 95); // and now the platform's own rule does fire
        var later = await Eventually(() => Alerts(pot), a => a.Length == 3);
        Assert.Contains(later, a => a.State == AlertState.Raised && a.Rule == "temperature outside [-10, 80]");
    }

    [Fact]
    public async Task A_rule_on_one_device_does_not_change_another_device_with_the_same_channel()
    {
        var tight = await Pot();
        var plain = await Pot();
        await SetRules(tight, Threshold("air-t", 20, 25));

        await Send(tight, "air-t", "temperature", 30);
        await Send(plain, "air-t", "temperature", 30);

        await Eventually(() => Alerts(tight), a => a.Length == 1);
        await Task.Delay(1500);
        Assert.Empty(await Alerts(plain)); // 30 degrees is fine under the platform's range
    }

    [Fact]
    public async Task A_no_data_rule_gives_one_channel_its_own_patience()
    {
        var pot = await Pot();
        // The platform's timeout in these tests is 3 seconds. Zone 1 may stay quiet for 15, zone 2 has no rule.
        await SetRules(pot, NoData("soil-1", 15));
        await Send(pot, "soil-1", "soil_moisture", 50);
        await Send(pot, "soil-2", "soil_moisture", 50);

        var first = await Eventually(() => Alerts(pot, AlertKind.NoData), a => a.Any(x => x.State == AlertState.Raised));
        var early = Assert.Single(first);
        Assert.Equal(("soil-2", "no data for 3s"), (early.ChannelId, early.Rule)); // the one on the platform default, first

        var both = await Eventually(() => Alerts(pot, AlertKind.NoData), a => a.Count(x => x.State == AlertState.Raised) == 2, seconds: 60);
        var own = both.Single(a => a.ChannelId == "soil-1");
        Assert.Equal("no data for 15s", own.Rule);
        Assert.InRange(own.Value, 15, 30);
    }
}
