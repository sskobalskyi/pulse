using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Pulse.Core;

namespace Pulse.Simulator;

public sealed class SimulatorOptions
{
    public string ApiUrl { get; set; } = "http://localhost:5080";

    /// <summary>Credentials for an API that requires authentication. Left empty, the simulator sends no token.</summary>
    public SimulatorAuthOptions Auth { get; set; } = new();

    public int Devices { get; set; } = 20;
    public int IntervalMs { get; set; } = 1000;

    /// <summary>Probability per reading that a device starts a fault: a run of out-of-range readings, to exercise the alert rules.</summary>
    public double AnomalyChance { get; set; } = 0.002;

    public int AnomalyMinTicks { get; set; } = 5;

    public int AnomalyMaxTicks { get; set; } = 15;

    /// <summary>Probability per reading that a sensor goes quiet. The default dropout outlasts the API's 60 second no-data timeout.</summary>
    public double DropoutChance { get; set; } = 0.0002;

    public int DropoutMinTicks { get; set; } = 90;

    public int DropoutMaxTicks { get; set; } = 150;
}

/// <summary>Emits a random-walk reading per device and metric every interval, as one batch per tick.</summary>
public sealed class Worker(IHttpClientFactory httpFactory, TokenProvider tokens, IOptions<SimulatorOptions> options, ILogger<Worker> logger) : BackgroundService
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
        var dropoutTicksLeft = new Dictionary<(string, string), int>();

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

                // A dropout is a sensor that goes quiet: it sends nothing for a while, then comes back.
                dropoutTicksLeft.TryGetValue(key, out var quiet);
                if (quiet == 0 && rng.NextDouble() < o.DropoutChance)
                    quiet = rng.Next(o.DropoutMinTicks, o.DropoutMaxTicks + 1);

                dropoutTicksLeft[key] = Math.Max(quiet - 1, 0);
                if (quiet > 0) continue;

                batch.Add(new TelemetryReading(key.Item1, key.Item2, Math.Round(value, 2), now));
            }

            if (batch.Count == 0) continue; // every sensor happens to be quiet; the API rejects empty batches

            // One span per tick: the HTTP call below becomes its child and carries the trace into the API.
            using var activity = Source.StartActivity("simulator tick");
            activity?.SetTag("pulse.readings", batch.Count);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{o.ApiUrl}/api/telemetry") { Content = JsonContent.Create(batch) };
                if (await tokens.GetAsync(stoppingToken) is { } token) request.Headers.Authorization = new("Bearer", token);

                using var response = await http.SendAsync(request, stoppingToken);
                response.EnsureSuccessStatusCode();
                ReadingsSent.Add(batch.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException)
            {
                SendFailures.Add(1);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                logger.LogWarning("Could not send readings, the API or the identity provider is unavailable: {Message}", ex.Message);
            }
        }
    }
}
