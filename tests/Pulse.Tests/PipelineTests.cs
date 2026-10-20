using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Pulse.Api.Storage;
using Pulse.Core;

namespace Pulse.Tests;

/// <summary>Sends readings through the real HTTP endpoint and checks what comes out of Kafka, TimescaleDB and SignalR.</summary>
[Collection(PipelineCollection.Name)]
public sealed class PipelineTests(PipelineFixture stack)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    // Each test uses its own device ids, so tests do not see each other's data in the shared database.
    private static string NewDevice() => $"it-{Guid.NewGuid():N}"[..12];

    private static TelemetryReading Reading(string device, string metric, double value, DateTimeOffset at) =>
        new(device, metric, value, at);

    private Task<HttpResponseMessage> Post(params TelemetryReading[] readings) =>
        stack.Client.PostAsJsonAsync("/api/telemetry", readings);

    private async Task PostAccepted(params TelemetryReading[] readings)
    {
        using var response = await Post(readings);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Posted_readings_end_up_queryable()
    {
        var device = NewDevice();
        var now = DateTimeOffset.UtcNow;

        await PostAccepted(
            Reading(device, "temperature", 21.5, now.AddSeconds(-2)),
            Reading(device, "temperature", 22.0, now.AddSeconds(-1)),
            Reading(device, "humidity", 40, now));

        var devices = await Eventually(
            async () => (await stack.Client.GetFromJsonAsync<DeviceSummary[]>("/api/devices"))!.Where(d => d.DeviceId == device).ToArray(),
            found => found.Length == 2);

        Assert.Equal(22.0, devices.Single(d => d.Metric == "temperature").Value); // latest reading wins
        Assert.Equal(40, devices.Single(d => d.Metric == "humidity").Value);

        var series = await stack.Client.GetFromJsonAsync<SeriesPoint[]>($"/api/devices/{device}/series?metric=temperature&minutes=5");
        Assert.Equal([21.5, 22.0], series!.Select(p => p.Avg));
    }

    [Fact]
    public async Task Readings_are_pushed_to_connected_browsers()
    {
        var device = NewDevice();
        var received = new TaskCompletionSource<TelemetryReading>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var hub = stack.CreateHubConnection();
        hub.On<TelemetryReading[]>("readings", batch =>
        {
            var mine = batch.FirstOrDefault(r => r.DeviceId == device);
            if (mine is not null) received.TrySetResult(mine);
        });
        await hub.StartAsync();

        await PostAccepted(Reading(device, "temperature", 23.5, DateTimeOffset.UtcNow));

        var reading = await received.Task.WaitAsync(Timeout);
        Assert.Equal(23.5, reading.Value);
        Assert.Equal("temperature", reading.Metric);
    }

    [Fact]
    public async Task A_breach_raises_one_alert_and_recovery_resolves_it()
    {
        var device = NewDevice();
        var now = DateTimeOffset.UtcNow;

        var pushed = new List<Alert>();
        var resolvedPushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = stack.CreateHubConnection();
        hub.On<Alert[]>("alerts", batch =>
        {
            lock (pushed)
            {
                pushed.AddRange(batch.Where(a => a.DeviceId == device && a.Kind == AlertKind.Threshold));
                if (pushed.Any(a => a.State == AlertState.Resolved)) resolvedPushed.TrySetResult();
            }
        });
        await hub.StartAsync();

        // Temperature is allowed in [-10, 80]. Two readings above that, then one well inside the range.
        await PostAccepted(Reading(device, "temperature", 95, now.AddSeconds(-3)));
        await PostAccepted(Reading(device, "temperature", 96, now.AddSeconds(-2)));
        await PostAccepted(Reading(device, "temperature", 50, now.AddSeconds(-1)));

        var alerts = await Eventually(
            async () => await AlertsFor(device, AlertKind.Threshold),
            found => found.Any(a => a.State == AlertState.Resolved));

        // Newest first: one Resolved on the value that came back, one Raised on the first breach, nothing for the second.
        Assert.Equal([AlertState.Resolved, AlertState.Raised], alerts.Select(a => a.State));
        Assert.Equal([50.0, 95.0], alerts.Select(a => a.Value));

        await resolvedPushed.Task.WaitAsync(Timeout);
        lock (pushed)
            Assert.Equal([AlertState.Raised, AlertState.Resolved], pushed.Select(a => a.State));
    }

    [Fact]
    public async Task A_value_in_the_dead_band_does_not_resolve_an_alert()
    {
        var device = NewDevice();
        var now = DateTimeOffset.UtcNow;

        // 79 is back inside [-10, 80] but within the 2 degree margin, so the alert stays raised.
        await PostAccepted(Reading(device, "temperature", 95, now.AddSeconds(-2)));
        await PostAccepted(Reading(device, "temperature", 79, now.AddSeconds(-1)));
        // A later reading for the same group proves the dead-band one has been processed.
        var marker = NewDevice();
        await PostAccepted(Reading(marker, "temperature", 95, now));

        await Eventually(
            async () => (await stack.Client.GetFromJsonAsync<Alert[]>("/api/alerts?limit=500"))!.Any(a => a.DeviceId == marker),
            seen => seen);

        var alerts = await AlertsFor(device, AlertKind.Threshold);
        Assert.Equal([AlertState.Raised], alerts.Select(a => a.State));
    }

    [Fact]
    public async Task A_device_that_stops_reporting_raises_no_data_and_resolves_when_it_returns()
    {
        var device = NewDevice();
        var other = NewDevice();

        var pushed = new TaskCompletionSource<Alert>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = stack.CreateHubConnection();
        hub.On<Alert[]>("alerts", batch =>
        {
            var mine = batch.FirstOrDefault(a => a.DeviceId == device && a.Kind == AlertKind.NoData);
            if (mine is not null) pushed.TrySetResult(mine);
        });
        await hub.StartAsync();

        // Both devices report once, then only `other` keeps going. The timeout in these tests is 3 seconds.
        await PostAccepted(Reading(device, "temperature", 20, DateTimeOffset.UtcNow), Reading(other, "temperature", 20, DateTimeOffset.UtcNow));
        using var keepAlive = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!keepAlive.IsCancellationRequested)
            {
                await PostAccepted(Reading(other, "temperature", 20, DateTimeOffset.UtcNow));
                await Task.Delay(500);
            }
        });

        try
        {
            var raised = await Eventually(
                async () => await AlertsFor(device, AlertKind.NoData),
                found => found.Any(a => a.State == AlertState.Raised));
            Assert.Equal([AlertState.Raised], raised.Select(a => a.State));
            Assert.InRange(raised[0].Value, 3, 15); // seconds of silence when it was noticed
            Assert.Equal("no data for 3s", raised[0].Rule);
            Assert.Equal(AlertState.Raised, (await pushed.Task.WaitAsync(Timeout)).State);

            // The device that kept reporting never went silent.
            Assert.Empty(await AlertsFor(other, AlertKind.NoData));

            // The device comes back.
            await PostAccepted(Reading(device, "temperature", 21, DateTimeOffset.UtcNow));
            var resolved = await Eventually(
                async () => await AlertsFor(device, AlertKind.NoData),
                found => found.Any(a => a.State == AlertState.Resolved));
            Assert.Equal([AlertState.Resolved, AlertState.Raised], resolved.Select(a => a.State));
        }
        finally
        {
            await keepAlive.CancelAsync();
            await heartbeat;
        }
    }

    private async Task<Alert[]> AlertsFor(string device, AlertKind kind) =>
        (await stack.Client.GetFromJsonAsync<Alert[]>("/api/alerts?limit=500"))!
        .Where(a => a.DeviceId == device && a.Kind == kind)
        .ToArray();

    [Fact]
    public async Task Invalid_batches_are_rejected_before_reaching_kafka()
    {
        using var empty = await Post();
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        using var noDevice = await Post(Reading("", "temperature", 20, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.BadRequest, noDevice.StatusCode);

        using var noMetric = await Post(Reading(NewDevice(), " ", 20, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.BadRequest, noMetric.StatusCode);
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        T value;
        do
        {
            value = await read();
            if (done(value)) return value;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Condition not met within {Timeout.TotalSeconds:0} s. Last value: {System.Text.Json.JsonSerializer.Serialize(value)}");
    }
}
