using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Pulse.Core;

namespace Pulse.Simulator;

public sealed class SimulatorOptions
{
    public string ApiUrl { get; set; } = "http://localhost:5080";
    public int Devices { get; set; } = 20;
    public int IntervalMs { get; set; } = 1000;

    /// <summary>Probability per reading that a device starts a fault: a run of out-of-range readings, to exercise the alert rules.</summary>
    public double AnomalyChance { get; set; } = 0.002;

    public int AnomalyMinTicks { get; set; } = 5;

    public int AnomalyMaxTicks { get; set; } = 15;
}

/// <summary>Emits a random-walk reading per device and metric every interval, as one batch per tick.</summary>
public sealed class Worker(IHttpClientFactory httpFactory, IOptions<SimulatorOptions> options, ILogger<Worker> logger) : BackgroundService
{
    public const string TelemetryName = "Pulse.Simulator";

    private static readonly ActivitySource Source = new(TelemetryName);
    private static readonly Meter Meter = new(TelemetryName);
    private static readonly Counter<long> ReadingsSent = Meter.CreateCounter<long>("pulse.simulator.readings.sent", "{reading}");
    private static readonly Counter<long> SendFailures = Meter.CreateCounter<long>("pulse.simulator.send.failures", "{batch}");

    private static readonly (string Metric, double Start, double Step, double Spike)[] Metrics =
    [
        ("temperature", 22, 0.3, 120),
        ("humidity", 50, 0.8, 99),
        ("battery", 100, 0.02, 3),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var http = httpFactory.CreateClient();
        var rng = new Random();
        var state = Enumerable.Range(1, o.Devices)
            .SelectMany(d => Metrics.Select(m => (Key: ($"device-{d:000}", m.Metric), Value: m.Start + rng.NextDouble() * 4 - 2)))
            .ToDictionary(x => x.Key, x => x.Value);

        var faultTicksLeft = new Dictionary<(string, string), int>();

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(o.IntervalMs));
        logger.LogInformation("Simulating {Devices} devices every {Interval} ms against {Url}", o.Devices, o.IntervalMs, o.ApiUrl);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTimeOffset.UtcNow;
            var batch = new List<TelemetryReading>(state.Count);

            foreach (var key in state.Keys.ToList())
            {
                var spec = Metrics.First(m => m.Metric == key.Item2);
                var next = state[key] + (rng.NextDouble() - 0.5) * 2 * spec.Step;
                if (key.Item2 == "battery") next = Math.Min(next, 100);
                state[key] = next;

                // A fault lasts several ticks, then the metric drops back to its normal random walk.
                faultTicksLeft.TryGetValue(key, out var left);
                if (left == 0 && rng.NextDouble() < o.AnomalyChance)
                    left = rng.Next(o.AnomalyMinTicks, o.AnomalyMaxTicks + 1);

                var value = left > 0 ? spec.Spike : next;
                faultTicksLeft[key] = Math.Max(left - 1, 0);
                batch.Add(new TelemetryReading(key.Item1, key.Item2, Math.Round(value, 2), now));
            }

            // One span per tick: the HTTP call below becomes its child and carries the trace into the API.
            using var activity = Source.StartActivity("simulator tick");
            activity?.SetTag("pulse.readings", batch.Count);

            try
            {
                using var response = await http.PostAsJsonAsync($"{o.ApiUrl}/api/telemetry", batch, stoppingToken);
                response.EnsureSuccessStatusCode();
                ReadingsSent.Add(batch.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException)
            {
                SendFailures.Add(1);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                logger.LogWarning("Ingestion API unavailable: {Message}", ex.Message);
            }
        }
    }
}
