using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
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
        "Alerts__NoData__Timeout", "Alerts__NoData__CheckInterval",
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

        await using (var dataSource = NpgsqlDataSource.Create(_timescale.GetConnectionString()))
            await TestSchema.ApplyAsync(dataSource);

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
