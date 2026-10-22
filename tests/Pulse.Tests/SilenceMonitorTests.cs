using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pulse.Api.Auth;
using Pulse.Api.Hubs;
using Pulse.Api.Processing;
using Pulse.Api.Registry;
using Pulse.Api.Storage;
using Pulse.Core;
using Testcontainers.PostgreSql;

namespace Pulse.Tests;

/// <summary>
/// Several SilenceMonitor instances sharing one database, which is what running more than one API instance looks like.
/// </summary>
public sealed class SilenceMonitorTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb:2.17.2-pg16")
        .WithDatabase("pulse")
        .WithUsername("pulse")
        .WithPassword("pulse")
        .Build();

    private NpgsqlDataSource _dataSource = null!;
    private PulseDb _db = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await TestSchema.ApplyAsync(_dataSource);
        _db = new PulseDb(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private SilenceMonitor NewInstance()
    {
        var options = Options.Create(new SilenceOptions { Timeout = Timeout });
        var fanout = new LiveFanout(new RecordingHub(), new DeviceRegistry(_dataSource, TimeProvider.System, options), Options.Create(new AuthOptions()));
        return new SilenceMonitor(options, _db, fanout, NullLogger<SilenceMonitor>.Instance);
    }

    /// <summary>Makes dev-1 report once and then stay quiet for longer than the timeout.</summary>
    private async Task MakeDeviceSilent()
    {
        await _db.TouchLastSeenAsync([new TelemetryReading("dev-1", "temperature", 20, DateTimeOffset.UtcNow)], default);
        await DbClock.DelayAsync(_dataSource, Timeout + TimeSpan.FromMilliseconds(500));
    }

    private async Task<Alert[]> NoDataAlerts() =>
        (await _db.GetAlertsAsync(100, null, default)).Where(a => a.Kind == AlertKind.NoData).ToArray();

    [Fact]
    public async Task Instances_checking_at_the_same_time_raise_the_alert_once()
    {
        await MakeDeviceSilent();
        var instances = Enumerable.Range(0, 4).Select(_ => NewInstance()).ToArray();

        await Task.WhenAll(instances.Select(i => i.CheckOnceAsync(default)));
        await Task.WhenAll(instances.Select(i => i.CheckOnceAsync(default)));

        Assert.Equal([AlertState.Raised], (await NoDataAlerts()).Select(a => a.State));
    }

    [Fact]
    public async Task A_follower_does_nothing_while_another_instance_holds_the_lock()
    {
        await MakeDeviceSilent();
        var follower = NewInstance();
        bool? followerRan = null;

        await _db.TryRunExclusiveAsync(SilenceMonitor.LeaderLockKey, async () =>
        {
            followerRan = await follower.CheckOnceAsync(default);
        }, default);

        Assert.False(followerRan);
        Assert.Empty(await NoDataAlerts());

        // Once the other instance is gone, the follower takes over and does the work.
        Assert.True(await follower.CheckOnceAsync(default));
        Assert.Single(await NoDataAlerts());
    }

    [Fact]
    public async Task A_new_leader_continues_from_what_the_previous_one_stored()
    {
        await MakeDeviceSilent();
        await NewInstance().CheckOnceAsync(default); // the first leader raises the alert, then goes away

        var secondLeader = NewInstance(); // has never seen the alert in memory
        await secondLeader.CheckOnceAsync(default);
        Assert.Single(await NoDataAlerts()); // no duplicate

        await _db.TouchLastSeenAsync([new TelemetryReading("dev-1", "temperature", 21, DateTimeOffset.UtcNow)], default);
        await secondLeader.CheckOnceAsync(default);

        Assert.Equal([AlertState.Resolved, AlertState.Raised], (await NoDataAlerts()).Select(a => a.State));
    }

    // Just enough of SignalR to count what would have been pushed to browsers.
    private sealed class RecordingHub : IHubContext<LiveHub>
    {
        public IHubClients Clients { get; } = new RecordingClients();

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingClients : IHubClients
    {
        public IClientProxy All { get; } = new Sink();

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Client(string connectionId) => throw new NotSupportedException();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy Group(string groupName) => throw new NotSupportedException();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId) => throw new NotSupportedException();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class Sink : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
