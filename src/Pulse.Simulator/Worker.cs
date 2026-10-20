using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Pulse.Core;

namespace Pulse.Simulator;

public sealed class SimulatorOptions
{
    public string ApiUrl { get; set; } = "http://localhost:5080";
    public int Devices { get; set; } = 20;
    public int IntervalMs { get; set; } = 1000;

    /// <summary>Probability per reading of an out-of-range spike, to exercise the alert rules.</summary>
    public double AnomalyChance { get; set; } = 0.01;
}

/// <summary>Emits a random-walk reading per device and metric every interval, as one batch per tick.</summary>
public sealed class Worker(IHttpClientFactory httpFactory, IOptions<SimulatorOptions> options, ILogger<Worker> logger) : BackgroundService
{
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

                var value = rng.NextDouble() < o.AnomalyChance ? spec.Spike : next;
                batch.Add(new TelemetryReading(key.Item1, key.Item2, Math.Round(value, 2), now));
            }

            try
            {
                using var response = await http.PostAsJsonAsync($"{o.ApiUrl}/api/telemetry", batch, stoppingToken);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is HttpRequestException)
            {
                logger.LogWarning("Ingestion API unavailable: {Message}", ex.Message);
            }
        }
    }
}
