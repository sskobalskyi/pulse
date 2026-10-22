using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pulse.Api.Hubs;
using Pulse.Api.Storage;
using Pulse.Core;
using static Pulse.Tests.AuthTestKit;

namespace Pulse.Tests;

/// <summary>
/// With authentication on, the dashboard endpoints and the live hub follow the same rule as the registry:
/// a user gets what belongs to the devices they own, a service caller gets everything, and a device nobody
/// has claimed is for services only.
/// </summary>
[Collection(PipelineCollection.Name)]
public sealed class OwnerScopingTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _host;
    private readonly HttpClient _client;

    public OwnerScopingTests(PipelineFixture stack)
    {
        _host = CreateHost(stack);
        _client = _host.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    private PulseDb Db => _host.Services.GetRequiredService<PulseDb>();

    private LiveFanout Fanout => _host.Services.GetRequiredService<LiveFanout>();

    private static string OwnerName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<T[]> GetArray<T>(string url, string token)
    {
        using var response = await Send(HttpMethod.Get, url, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T[]>(Web))!;
    }

    /// <summary>Registers, ships and claims a device as the service.</summary>
    private async Task<string> DeviceOwnedBy(string owner)
    {
        var service = ServiceToken();
        var id = NewId();
        using var registered = await Send(HttpMethod.Post, "/api/pulse/v2/devices", service, Registration(id));
        using var shipped = await Send(HttpMethod.Post, $"/api/pulse/v2/devices/{id}/ship", service);
        using var claimed = await Send(HttpMethod.Post, $"/api/pulse/v2/devices/{id}/claim", service, new { token = "token", ownerId = owner });
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        return id;
    }

    private async Task Reported(string deviceId)
    {
        var reading = new TelemetryReading(deviceId, "soil_moisture", 21, DateTimeOffset.UtcNow);
        await Db.InsertReadingsAsync([reading], default);
        await Db.TouchLastSeenAsync([reading], default);
    }

    [Fact]
    public async Task The_dashboard_device_list_shows_a_user_only_their_own_series()
    {
        var alice = OwnerName("alice");
        var aliceDevice = await DeviceOwnedBy(alice);
        var bobDevice = await DeviceOwnedBy(OwnerName("bob"));
        var strayDevice = NewId(); // never registered: it belongs to nobody
        foreach (var id in new[] { aliceDevice, bobDevice, strayDevice }) await Reported(id);

        var seenByAlice = await GetArray<DeviceSummary>("/api/devices", Token(alice));
        var seenByService = await GetArray<DeviceSummary>("/api/devices", ServiceToken());

        Assert.Equal([aliceDevice], seenByAlice.Select(d => d.DeviceId));
        Assert.Contains(seenByService, d => d.DeviceId == aliceDevice);
        Assert.Contains(seenByService, d => d.DeviceId == bobDevice);
        Assert.Contains(seenByService, d => d.DeviceId == strayDevice);
    }

    [Fact]
    public async Task Alerts_are_limited_to_the_callers_own_devices()
    {
        var alice = OwnerName("alice");
        var aliceDevice = await DeviceOwnedBy(alice);
        var bobDevice = await DeviceOwnedBy(OwnerName("bob"));
        var strayDevice = NewId();
        var now = DateTimeOffset.UtcNow;
        await Db.InsertAlertsAsync([.. new[] { aliceDevice, bobDevice, strayDevice }.Select(id => new Alert(id, "soil_moisture", "rule", 5, now))], default);

        var seenByAlice = await GetArray<Alert>("/api/alerts?limit=500", Token(alice));
        var seenByService = await GetArray<Alert>("/api/alerts?limit=500", ServiceToken());

        Assert.Equal([aliceDevice], seenByAlice.Select(a => a.DeviceId));
        Assert.Equal(3, seenByService.Count(a => a.DeviceId == aliceDevice || a.DeviceId == bobDevice || a.DeviceId == strayDevice));
    }

    [Fact]
    public async Task The_chart_series_of_someone_elses_device_is_not_found()
    {
        var alice = OwnerName("alice");
        var aliceDevice = await DeviceOwnedBy(alice);
        var bobDevice = await DeviceOwnedBy(OwnerName("bob"));
        var strayDevice = NewId();
        foreach (var id in new[] { aliceDevice, bobDevice, strayDevice }) await Reported(id);
        static string Series(string id) => $"/api/devices/{id}/series?metric=soil_moisture&minutes=5";

        using var own = await Send(HttpMethod.Get, Series(aliceDevice), Token(alice));
        using var theirs = await Send(HttpMethod.Get, Series(bobDevice), Token(alice));
        using var stray = await Send(HttpMethod.Get, Series(strayDevice), Token(alice));
        using var asService = await Send(HttpMethod.Get, Series(bobDevice), ServiceToken());

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode); // not "forbidden": its existence is not revealed
        Assert.Equal(HttpStatusCode.NotFound, stray.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asService.StatusCode);
    }

    [Fact]
    public async Task The_channel_series_of_the_registry_api_follows_the_same_rule()
    {
        var alice = OwnerName("alice");
        var aliceDevice = await DeviceOwnedBy(alice);
        var bobDevice = await DeviceOwnedBy(OwnerName("bob"));
        var strayDevice = NewId(); // never registered
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("o"));
        string Series(string id, string channel = "soil-1") => $"/api/pulse/v2/devices/{id}/channels/{channel}/series?from={from}";

        using var own = await Send(HttpMethod.Get, Series(aliceDevice), Token(alice));
        using var ownUnknownChannel = await Send(HttpMethod.Get, Series(aliceDevice, "nope"), Token(alice));
        using var theirs = await Send(HttpMethod.Get, Series(bobDevice), Token(alice));
        using var stray = await Send(HttpMethod.Get, Series(strayDevice), Token(alice));
        using var strayAsService = await Send(HttpMethod.Get, Series(strayDevice), ServiceToken());
        using var theirsAsService = await Send(HttpMethod.Get, Series(bobDevice), ServiceToken());

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ownUnknownChannel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stray.StatusCode);
        Assert.Equal(HttpStatusCode.OK, strayAsService.StatusCode); // a service may read a series nobody registered
        Assert.Equal(HttpStatusCode.OK, theirsAsService.StatusCode);
    }

    [Fact]
    public async Task The_rules_of_a_device_can_be_read_by_its_owner_and_services_only()
    {
        var alice = OwnerName("alice");
        var aliceDevice = await DeviceOwnedBy(alice);
        var bobDevice = await DeviceOwnedBy(OwnerName("bob"));
        var rule = new[] { new { channelId = "soil-1", kind = "threshold", min = 30, max = 80 } };
        using var set = await Send(HttpMethod.Put, $"/api/pulse/v2/devices/{aliceDevice}/rules", ServiceToken(), rule);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        using var own = await Send(HttpMethod.Get, $"/api/pulse/v2/devices/{aliceDevice}/rules", Token(alice));
        using var theirs = await Send(HttpMethod.Get, $"/api/pulse/v2/devices/{bobDevice}/rules", Token(alice));
        using var asService = await Send(HttpMethod.Get, $"/api/pulse/v2/devices/{bobDevice}/rules", ServiceToken());
        using var userWrites = await Send(HttpMethod.Put, $"/api/pulse/v2/devices/{aliceDevice}/rules", Token(alice), rule);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Single((await own.Content.ReadFromJsonAsync<JsonElement[]>(Web))!);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asService.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, userWrites.StatusCode); // even for their own device: Greenhaus decides what rules a tier may have
    }

    [Fact]
    public async Task Live_readings_reach_only_the_owner_and_services()
    {
        var alice = OwnerName("alice");
        var bob = OwnerName("bob");
        var aliceDevice = await DeviceOwnedBy(alice);
        var bobDevice = await DeviceOwnedBy(bob);
        var strayDevice = NewId();
        await using var forAlice = await Listen(Token(alice));
        await using var forBob = await Listen(Token(bob));
        await using var forService = await Listen(ServiceToken());
        TelemetryReading Reading(string id, double value) => new(id, "soil_moisture", value, DateTimeOffset.UtcNow);

        await Fanout.SendReadingsAsync([Reading(aliceDevice, 1), Reading(bobDevice, 2), Reading(strayDevice, 3)], default);
        // Messages to one connection arrive in order, so once the marker is there everything sent before it is too.
        await Fanout.SendReadingsAsync([Reading(aliceDevice, 99), Reading(bobDevice, 99), Reading(strayDevice, 99)], default);
        await Task.WhenAll(forAlice.ReceivedReading(99), forBob.ReceivedReading(99), forService.ReceivedReading(99));

        Assert.Equal([aliceDevice], forAlice.ReadingDevices.Distinct());
        Assert.Equal([bobDevice], forBob.ReadingDevices.Distinct());
        Assert.Equivalent(new[] { aliceDevice, bobDevice, strayDevice }, forService.ReadingDevices.Distinct().ToArray());
    }

    [Fact]
    public async Task Live_alerts_reach_only_the_owner_and_services()
    {
        var alice = OwnerName("alice");
        var bob = OwnerName("bob");
        var aliceDevice = await DeviceOwnedBy(alice);
        var bobDevice = await DeviceOwnedBy(bob);
        var strayDevice = NewId();
        await using var forAlice = await Listen(Token(alice));
        await using var forBob = await Listen(Token(bob));
        await using var forService = await Listen(ServiceToken());
        Alert Alert(string id, double value) => new(id, "soil_moisture", "rule", value, DateTimeOffset.UtcNow);

        await Fanout.SendAlertsAsync([Alert(aliceDevice, 1), Alert(bobDevice, 2), Alert(strayDevice, 3)], default);
        await Fanout.SendAlertsAsync([Alert(aliceDevice, 99), Alert(bobDevice, 99), Alert(strayDevice, 99)], default);
        await Task.WhenAll(forAlice.ReceivedAlert(99), forBob.ReceivedAlert(99), forService.ReceivedAlert(99));

        Assert.Equal([aliceDevice], forAlice.AlertDevices.Distinct());
        Assert.Equal([bobDevice], forBob.AlertDevices.Distinct());
        Assert.Equivalent(new[] { aliceDevice, bobDevice, strayDevice }, forService.AlertDevices.Distinct().ToArray());
    }

    [Fact]
    public async Task A_device_claimed_after_a_connection_opened_reaches_its_new_owner_straight_away()
    {
        var alice = OwnerName("alice");
        await using var forAlice = await Listen(Token(alice));
        var service = ServiceToken();
        var id = NewId();
        using var registered = await Send(HttpMethod.Post, "/api/pulse/v2/devices", service, Registration(id));
        using var shipped = await Send(HttpMethod.Post, $"/api/pulse/v2/devices/{id}/ship", service);
        TelemetryReading Reading(double value) => new(id, "soil_moisture", value, DateTimeOffset.UtcNow);

        await Fanout.SendReadingsAsync([Reading(98)], default); // nobody owns it yet: nothing for alice
        using var claimed = await Send(HttpMethod.Post, $"/api/pulse/v2/devices/{id}/claim", service, new { token = "token", ownerId = alice });
        await Fanout.SendReadingsAsync([Reading(99)], default);
        await forAlice.ReceivedReading(99);

        Assert.Equal([99.0], forAlice.ReadingValues);
    }

    [Fact]
    public async Task A_batch_with_no_readings_sends_nothing()
    {
        await using var forService = await Listen(ServiceToken());

        await Fanout.SendReadingsAsync([], default);
        await Fanout.SendReadingsAsync([new TelemetryReading(NewId(), "soil_moisture", 99, DateTimeOffset.UtcNow)], default);
        await forService.ReceivedReading(99);

        Assert.Single(forService.ReadingValues);
    }

    /// <summary>A live connection as one caller, collecting what it is sent. Long polling because the test server has no WebSockets.</summary>
    private async Task<LiveListener> Listen(string token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/live"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _host.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        var listener = new LiveListener(connection);
        await connection.StartAsync();
        return listener;
    }

    private sealed class LiveListener : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly List<TelemetryReading> _readings = [];
        private readonly List<Alert> _alerts = [];

        public LiveListener(HubConnection connection)
        {
            _connection = connection;
            connection.On<TelemetryReading[]>("readings", batch => { lock (_readings) _readings.AddRange(batch); });
            connection.On<Alert[]>("alerts", batch => { lock (_alerts) _alerts.AddRange(batch); });
        }

        public IReadOnlyList<string> ReadingDevices { get { lock (_readings) return [.. _readings.Select(r => r.DeviceId)]; } }

        public IReadOnlyList<double> ReadingValues { get { lock (_readings) return [.. _readings.Select(r => r.Value)]; } }

        public IReadOnlyList<string> AlertDevices { get { lock (_alerts) return [.. _alerts.Select(a => a.DeviceId)]; } }

        public Task ReceivedReading(double value) => Until(() => { lock (_readings) return _readings.Any(r => r.Value == value); });

        public Task ReceivedAlert(double value) => Until(() => { lock (_alerts) return _alerts.Any(a => a.Value == value); });

        private static async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The live push never arrived.");
                await Task.Delay(50);
            }
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
