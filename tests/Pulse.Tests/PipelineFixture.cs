using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pulse.Api.Storage;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redpanda;

namespace Pulse.Tests;

[CollectionDefinition(Name)]
public sealed class PipelineCollection : ICollectionFixture<PipelineFixture>
{
    public const string Name = "pipeline";
}

/// <summary>
/// The whole backend in one process: the real API with its stream processors and SignalR hub,
/// talking to a Redpanda broker and a TimescaleDB instance running in containers.
/// Started once and shared by every test in the collection.
/// </summary>
public sealed class PipelineFixture : IAsyncLifetime
{
    private static readonly string[] EnvKeys =
    [
        "ConnectionStrings__Timescale", "Kafka__BootstrapServers", "Kafka__AutoOffsetReset",
        "Alerts__NoData__Timeout", "Alerts__NoData__CheckInterval", "Registry__Claim__PerCallerPerMinute",
    ];

    private readonly RedpandaContainer _redpanda = new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v24.3.7").Build();

    private readonly PostgreSqlContainer _timescale = new PostgreSqlBuilder("timescale/timescaledb:2.17.2-pg16")
        .WithDatabase("pulse")
        .WithUsername("pulse")
        .WithPassword("pulse")
        .Build();

    private WebApplicationFactory<Program>? _factory;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_redpanda.StartAsync(), _timescale.StartAsync());

        // No schema is applied here: the API migrates the empty database itself on startup, as it does in compose.
        // Environment variables rather than ConfigureAppConfiguration: Program reads some settings while it is
        // still building the host, before test overrides are applied.
        Environment.SetEnvironmentVariable("ConnectionStrings__Timescale", _timescale.GetConnectionString());
        Environment.SetEnvironmentVariable("Kafka__BootstrapServers", _redpanda.GetBootstrapAddress());
        // A consumer group that has not joined yet must still see messages sent right before it did.
        Environment.SetEnvironmentVariable("Kafka__AutoOffsetReset", "Earliest");
        // Short enough to watch a device go silent within a test. Tests that are not about silence only look
        // at threshold alerts, because every test device falls silent a few seconds after its last reading.
        Environment.SetEnvironmentVariable("Alerts__NoData__Timeout", "00:00:03");
        Environment.SetEnvironmentVariable("Alerts__NoData__CheckInterval", "00:00:00.500");

        // Every test in the process shares one caller as far as the claim limiter can tell, so the per-caller limit is out of the way.
        // The per-device limit keeps its default, and its test relies on it.
        Environment.SetEnvironmentVariable("Registry__Claim__PerCallerPerMinute", "1000");

        _factory = new WebApplicationFactory<Program>();
        Client = _factory.CreateClient(); // also starts the host, and with it the stream processors
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();

        foreach (var key in EnvKeys) Environment.SetEnvironmentVariable(key, null);

        await Task.WhenAll(_redpanda.DisposeAsync().AsTask(), _timescale.DisposeAsync().AsTask());
    }

    /// <summary>
    /// A second host on the same database that serves HTTP only: no stream processors, no monitors, just the migration.
    /// Tests that need different settings, such as authentication on, use it so the shared stack keeps one consumer per group.
    /// </summary>
    public WebApplicationFactory<Program> CreateHttpOnlyHost(Action<IServiceCollection> configure, params (string Key, string Value)[] settings) =>
        _factory!.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddHostedService<MigrationService>();
                configure(services);
            });
        });

    /// <summary>A SignalR client for the in-process server. Long polling, because the test server has no WebSockets.</summary>
    public HubConnection CreateHubConnection() =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Client.BaseAddress!, "/hubs/live"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory!.Server.CreateHandler();
            })
            .Build();
}
