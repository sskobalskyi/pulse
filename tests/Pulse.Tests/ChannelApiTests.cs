using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Pulse.Core;

namespace Pulse.Tests;

/// <summary>Channels through the real API: ingestion with several sensors of one metric, ranges, series and alerts per channel.</summary>
[Collection(PipelineCollection.Name)]
public sealed class ChannelApiTests(PipelineFixture stack)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly OpenApiContract Contract = new(ContractPath());
    private const string V2 = "/api/pulse/v2";

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pulse.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "contracts", "pulse-device-api.yaml");
    }

    private static string NewId() => $"ch-{Guid.NewGuid():N}"[..14];

    private async Task<string> RegisteredPot(params (string Id, string Metric)[] channels)
    {
        var id = NewId();
        using var response = await stack.Client.PostAsJsonAsync($"{V2}/devices", new
        {
            id,
            model = "greenhaus-pot-v1",
            capabilities = new { channels = channels.Select(c => new { id = c.Id, metric = c.Metric }).ToArray(), actuators = Array.Empty<object>() },
            credentialHash = "h",
            claimTokenHash = ClaimTokens.Hash("t"),
            claimTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    private Task<HttpResponseMessage> Send(params TelemetryReading[] readings) => stack.Client.PostAsJsonAsync("/api/telemetry", readings);

    private async Task SendOk(params TelemetryReading[] readings)
    {
        using var response = await Send(readings);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static TelemetryReading At(string device, string metric, double value, string? channel = null, double secondsAgo = 5) =>
        new(device, metric, value, DateTimeOffset.UtcNow.AddSeconds(-secondsAgo), channel);

    private async Task<JsonElement[]> Series(string device, string channel, string query = "")
    {
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o"));
        using var response = await stack.Client.GetAsync($"{V2}/devices/{device}/channels/{channel}/series?from={from}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement[]>(Web))!;
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        T value;
        do
        {
            value = await read();
            if (done(value)) return value;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Condition not met in 45 s. Last value: {JsonSerializer.Serialize(value)}");
    }

    // --- ingestion ---

    [Fact]
    public async Task Readings_of_two_zones_are_kept_apart()
    {
        var pot = await RegisteredPot(("soil-1", "soil_moisture"), ("soil-2", "soil_moisture"), ("air-t", "temperature"));

        await SendOk(At(pot, "soil_moisture", 31, "soil-1"), At(pot, "soil_moisture", 62, "soil-2"), At(pot, "temperature", 21));

        Assert.Equal([31.0], (await Eventually(() => Series(pot, "soil-1"), p => p.Length == 1)).Select(p => p.GetProperty("value").GetDouble()));
        Assert.Equal([62.0], (await Eventually(() => Series(pot, "soil-2"), p => p.Length == 1)).Select(p => p.GetProperty("value").GetDouble()));
        Assert.Equal([21.0], (await Eventually(() => Series(pot, "air-t"), p => p.Length == 1)).Select(p => p.GetProperty("value").GetDouble())); // no channel named: the only temperature channel
    }

    [Fact]
    public async Task A_reading_that_does_not_say_which_of_two_zones_it_is_for_is_refused()
    {
        var pot = await RegisteredPot(("soil-1", "soil_moisture"), ("soil-2", "soil_moisture"));

        using var response = await Send(At(pot, "soil_moisture", 40));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ambiguous_channel", await Code(response));
        Assert.Contains("soil-1, soil-2", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("soil_moisture", "soil-9", "unknown_channel")]
    [InlineData("ph", null, "unknown_channel")]
    [InlineData("temperature", "soil-1", "metric_mismatch")]
    public async Task Readings_that_do_not_fit_what_the_device_declared_are_refused(string metric, string? channel, string expectedCode)
    {
        var pot = await RegisteredPot(("soil-1", "soil_moisture"), ("air-t", "temperature"));

        using var response = await Send(At(pot, metric, 7, channel)); // 7 is plausible for all three metrics, so only the channel is wrong

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await Code(response));
    }

    [Fact]
    public async Task One_bad_reading_refuses_the_whole_batch_and_stores_none_of_it()
    {
        var pot = await RegisteredPot(("soil-1", "soil_moisture"), ("air-t", "temperature"));

        using var response = await Send(At(pot, "soil_moisture", 40, "soil-1"), At(pot, "ph", 6));
        await Task.Delay(1500);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await Series(pot, "soil-1")); // the good reading was not stored either
    }

    [Fact]
    public async Task A_device_nobody_registered_keeps_sending_without_channels()
    {
        var demo = $"demo-{Guid.NewGuid():N}"[..12];

        await SendOk(At(demo, "temperature", 21), At(demo, "humidity", 40), At(demo, "anything", 5, "whatever"));

        // Its channel is its metric, so the dashboard's series query by metric still works for it.
        using var legacy = await stack.Client.GetAsync($"/api/devices/{demo}/series?metric=temperature&minutes=5");
        var points = await Eventually(
            async () => (await (await stack.Client.GetAsync($"/api/devices/{demo}/series?metric=temperature&minutes=5")).Content.ReadFromJsonAsync<JsonElement[]>())!,
            p => p.Length == 1);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(21.0, points[0].GetProperty("avg").GetDouble());
    }

    [Theory]
    [InlineData("ph", 14.5)]
    [InlineData("ph", -1)]
    [InlineData("soil_moisture", 4095)]
    [InlineData("overflow", 2)]
    [InlineData("tank_level", 120)]
    public async Task A_reading_outside_what_its_metric_can_measure_is_refused(string metric, double value)
    {
        using var response = await Send(At($"range-{Guid.NewGuid():N}"[..12], metric, value));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("value_out_of_range", await Code(response));
        Assert.Contains("between", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_edges_of_a_metrics_range_are_accepted_and_unknown_metrics_are_not_judged()
    {
        var demo = $"range-{Guid.NewGuid():N}"[..12];

        await SendOk(At(demo, "ph", 0), At(demo, "ph", 14, secondsAgo: 4), At(demo, "legacy_gauge", 1e9));
    }

    // --- series ---

    [Fact]
    public async Task Series_responses_match_the_contract_at_every_resolution()
    {
        var pot = await RegisteredPot(("soil-1", "soil_moisture"));
        var now = DateTimeOffset.UtcNow;
        await SendOk(new TelemetryReading(pot, "soil_moisture", 30, now.AddSeconds(-30), "soil-1"), new TelemetryReading(pot, "soil_moisture", 50, now.AddSeconds(-20), "soil-1"));
        await Eventually(() => Series(pot, "soil-1"), p => p.Length == 2);
        await using (var source = NpgsqlDataSource.Create(stack.DatabaseConnectionString))
        await using (var refresh = source.CreateCommand("CALL refresh_continuous_aggregate('readings_1m', NULL, NULL)"))
            await refresh.ExecuteNonQueryAsync();
        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("o"));
        var path = $"/devices/{pot}/channels/soil-1/series";

        foreach (var resolution in new[] { "raw", "1m", "1h" })
        {
            using var response = await stack.Client.GetAsync($"{V2}{path}?from={from}&resolution={resolution}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await Contract.Verify(response, "GET", "/devices/{deviceId}/channels/{channelId}/series");
        }

        using var unknown = await stack.Client.GetAsync($"{V2}/devices/{pot}/channels/nope/series?from={from}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await Contract.Verify(unknown, "GET", "/devices/{deviceId}/channels/{channelId}/series");
    }

    [Fact]
    public async Task Raw_points_are_single_readings_and_aggregates_carry_minimum_and_maximum()
    {
        var pot = await RegisteredPot(("soil-1", "soil_moisture"));
        var now = DateTimeOffset.UtcNow;
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(-3);
        await SendOk(new TelemetryReading(pot, "soil_moisture", 20, minute.AddSeconds(5), "soil-1"), new TelemetryReading(pot, "soil_moisture", 40, minute.AddSeconds(15), "soil-1"));
        await Eventually(() => Series(pot, "soil-1"), p => p.Length == 2);
        await using (var source = NpgsqlDataSource.Create(stack.DatabaseConnectionString))
        await using (var refresh = source.CreateCommand("CALL refresh_continuous_aggregate('readings_1m', NULL, NULL)"))
            await refresh.ExecuteNonQueryAsync();

        var raw = await Series(pot, "soil-1");
        var perMinute = await Series(pot, "soil-1", "&resolution=1m");

        Assert.All(raw, p => Assert.False(p.TryGetProperty("min", out _) || p.TryGetProperty("max", out _)));
        var bucket = Assert.Single(perMinute);
        Assert.Equal((30.0, 20.0, 40.0), (bucket.GetProperty("value").GetDouble(), bucket.GetProperty("min").GetDouble(), bucket.GetProperty("max").GetDouble()));
    }

    [Theory]
    [InlineData("", "invalid_range")]
    [InlineData("?from=yesterday-ish", "invalid_range")]
    [InlineData("?from=2026-10-21T12:00:00Z&to=2026-10-21T11:00:00Z", "invalid_range")]
    [InlineData("?from=2026-10-21T12:00:00Z&to=nope", "invalid_range")]
    [InlineData("?from=2026-10-21T10:00:00Z&to=2026-10-21T12:00:00Z&resolution=5m", "invalid_resolution")]
    [InlineData("?from=2026-10-21T00:00:00Z&to=2026-10-21T12:00:00Z&resolution=raw", "window_too_large")]
    [InlineData("?from=2026-10-01T00:00:00Z&to=2026-10-21T12:00:00Z&resolution=1m", "window_too_large")]
    [InlineData("?from=2024-01-01T00:00:00Z&to=2026-10-21T12:00:00Z&resolution=1h", "window_too_large")]
    public async Task A_series_request_that_makes_no_sense_says_why(string query, string expectedCode)
    {
        var pot = await RegisteredPot(("soil-1", "soil_moisture"));

        using var response = await stack.Client.GetAsync($"{V2}/devices/{pot}/channels/soil-1/series{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await Code(response));
    }

    // --- alerts ---

    [Fact]
    public async Task Each_channel_raises_and_resolves_its_own_alert()
    {
        var pot = await RegisteredPot(("inside", "temperature"), ("outside", "temperature"));
        async Task<Alert[]> Alerts() => (await stack.Client.GetFromJsonAsync<Alert[]>("/api/alerts?limit=500", Web))!.Where(a => a.DeviceId == pot && a.Kind == AlertKind.Threshold).ToArray();

        // Temperature is allowed in [-10, 80]. The inside sensor overheats, the outside one is fine.
        await SendOk(At(pot, "temperature", 95, "inside", 3), At(pot, "temperature", 20, "outside", 3));
        var first = await Eventually(Alerts, a => a.Length == 1);
        Assert.Equal(("inside", AlertState.Raised), (first[0].ChannelId, first[0].State));
        Assert.Equal("temperature", first[0].Metric);

        // The outside sensor overheats as well: a second, separate alert. Then the inside one cools down.
        await SendOk(At(pot, "temperature", 96, "outside", 2));
        await SendOk(At(pot, "temperature", 50, "inside", 1));
        var all = await Eventually(Alerts, a => a.Length == 3);

        Assert.Equivalent(
            new[] { ("inside", AlertState.Raised), ("outside", AlertState.Raised), ("inside", AlertState.Resolved) },
            all.Select(a => (a.ChannelId, a.State)).ToArray());
    }

    [Fact]
    public async Task A_sensor_that_goes_quiet_is_noticed_even_though_the_device_still_reports()
    {
        var pot = await RegisteredPot(("inside", "temperature"), ("outside", "temperature"));
        async Task<Alert[]> NoData() => (await stack.Client.GetFromJsonAsync<Alert[]>("/api/alerts?limit=500", Web))!.Where(a => a.DeviceId == pot && a.Kind == AlertKind.NoData).ToArray();
        await SendOk(At(pot, "temperature", 20, "inside"), At(pot, "temperature", 20, "outside"));

        using var keepAlive = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!keepAlive.IsCancellationRequested)
            {
                await Send(At(pot, "temperature", 20, "outside", 0));
                await Task.Delay(500);
            }
        });

        try
        {
            var alerts = await Eventually(NoData, a => a.Any(x => x.State == AlertState.Raised));
            var raised = Assert.Single(alerts);
            Assert.Equal("inside", raised.ChannelId); // only the silent sensor, the device as a whole is alive

            var devices = (await stack.Client.GetFromJsonAsync<DeviceSummaryDto[]>("/api/devices", Web))!.Where(d => d.DeviceId == pot).ToArray();
            Assert.Equal(["inside", "outside"], devices.Select(d => d.Channel));
            Assert.Equal([true, false], devices.Select(d => d.Silent));
        }
        finally
        {
            await keepAlive.CancelAsync();
            await heartbeat;
        }
    }

    private sealed record DeviceSummaryDto(string DeviceId, string Metric, string Channel, bool Silent);
}
