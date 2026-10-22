using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Pulse.Api.Processing;
using Pulse.Api.Registry;
using Pulse.Api.Storage;
using Pulse.Core;
using Testcontainers.PostgreSql;

namespace Pulse.Tests;

/// <summary>The rule store on a real database: what is atomic, what the table refuses by itself, and how rules reach the silence query.</summary>
public sealed class DeviceRuleStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb:2.17.2-pg16")
        .WithDatabase("pulse")
        .WithUsername("pulse")
        .WithPassword("pulse")
        .Build();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    private NpgsqlDataSource _dataSource = null!;
    private DeviceRuleStore _store = null!;
    private DeviceRegistry _registry = null!;
    private PulseDb _db = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await TestSchema.ApplyAsync(_dataSource);
        _store = new DeviceRuleStore(_dataSource, _time);
        _registry = new DeviceRegistry(_dataSource, _time, Options.Create(new SilenceOptions()));
        _db = new PulseDb(_dataSource);
        await Register("pot");
        await Register("other-pot");
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private Task Register(string id) => _registry.RegisterAsync(new DeviceRegistration(
        id,
        "greenhaus-pot-v1",
        new Capabilities([new Channel("soil-1", "soil_moisture"), new Channel("soil-2", "soil_moisture")], []),
        "h",
        ClaimTokens.Hash("t"),
        _time.GetUtcNow().AddDays(1)), default);

    private static DeviceRule Threshold(string channel, double min, double max, double? margin = null) =>
        new(channel, RuleKinds.Threshold, min, max, margin);

    private static DeviceRule NoData(string channel, int seconds) => new(channel, RuleKinds.NoData, TimeoutSeconds: seconds);

    private async Task<long> Count(string sql)
    {
        await using var cmd = _dataSource.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Rules_come_back_in_a_stable_order_with_what_was_stored()
    {
        await _store.ReplaceAsync("pot", [NoData("soil-2", 60), Threshold("soil-2", 10, 90, 2), Threshold("soil-1", 30, 80)], default);

        var rules = await _store.ListAsync("pot", default);

        Assert.Equal([("soil-1", "threshold"), ("soil-2", "threshold"), ("soil-2", "no_data")], rules.Select(r => (r.ChannelId, r.Kind)));
        Assert.Equal(new DeviceRule("soil-2", "threshold", 10, 90, 2), rules[1]);
        Assert.Equal(new DeviceRule("soil-2", "no_data", TimeoutSeconds: 60), rules[2]);
    }

    [Fact]
    public async Task A_replace_that_fails_halfway_leaves_the_old_rules()
    {
        await _store.ReplaceAsync("pot", [Threshold("soil-1", 30, 80)], default);

        // The second rule is about a channel that does not exist: the database refuses it, after the first was inserted.
        await Assert.ThrowsAsync<PostgresException>(() =>
            _store.ReplaceAsync("pot", [Threshold("soil-2", 10, 90), Threshold("soil-9", 1, 2)], default));

        Assert.Equal(["soil-1"], (await _store.ListAsync("pot", default)).Select(r => r.ChannelId));
    }

    [Fact]
    public async Task The_table_refuses_rules_of_the_wrong_shape_whatever_wrote_them()
    {
        await Assert.ThrowsAsync<PostgresException>(() => _store.ReplaceAsync("pot", [new DeviceRule("soil-1", "threshold")], default)); // no bound
        await Assert.ThrowsAsync<PostgresException>(() => _store.ReplaceAsync("pot", [new DeviceRule("soil-1", "no_data", 1, null, null, 60)], default)); // a no-data rule with a bound
        await Assert.ThrowsAsync<PostgresException>(() => _store.ReplaceAsync("pot", [NoData("soil-1", 5)], default)); // too short
        await Assert.ThrowsAsync<PostgresException>(() => _store.ReplaceAsync("pot", [new DeviceRule("soil-1", "threshold", 1, 2, -1)], default)); // negative margin
    }

    [Fact]
    public async Task Rules_go_when_their_device_goes()
    {
        await _store.ReplaceAsync("pot", [Threshold("soil-1", 30, 80)], default);

        await using (var delete = _dataSource.CreateCommand("DELETE FROM devices WHERE id = 'pot'")) await delete.ExecuteNonQueryAsync();

        Assert.Equal(0, await Count("SELECT count(*) FROM device_rules"));
    }

    [Fact]
    public async Task Thresholds_are_looked_up_per_device_and_leave_out_no_data_rules()
    {
        await _store.ReplaceAsync("pot", [Threshold("soil-1", 30, 80), NoData("soil-1", 60)], default);
        await _store.ReplaceAsync("other-pot", [Threshold("soil-1", 10, 20)], default);

        var rules = await _store.ThresholdsForAsync(["pot"], default);

        var only = Assert.Single(rules);
        Assert.Equal(("pot", "soil-1"), only.Key);
        Assert.Equal(30, only.Value.Min);
        Assert.Empty(await _store.ThresholdsForAsync([], default));
    }

    [Fact]
    public async Task A_replace_of_one_device_does_not_touch_another()
    {
        await _store.ReplaceAsync("pot", [Threshold("soil-1", 30, 80)], default);
        await _store.ReplaceAsync("other-pot", [Threshold("soil-1", 10, 20)], default);

        await _store.ReplaceAsync("pot", [], default);

        Assert.Empty(await _store.ListAsync("pot", default));
        Assert.Single(await _store.ListAsync("other-pot", default));
    }

    [Theory]
    [InlineData(null, 1, true, null)]   // no rule of its own: the platform timeout of 1 second applies and 20 seconds of quiet is too long
    [InlineData(60, 1, false, null)]    // a rule allowing 60 seconds: 20 seconds of quiet is fine
    [InlineData(10, 1, true, 10)]       // a rule allowing 10 seconds: 20 seconds is too long, and the series says which timeout applied
    public async Task A_no_data_rule_of_the_channels_own_decides_when_it_is_silent(int? ruleSeconds, int platformSeconds, bool silent, int? reportedTimeout)
    {
        await using (var seen = _dataSource.CreateCommand(
            "INSERT INTO device_last_seen (device_id, channel, metric, last_seen_at) VALUES ('pot', 'soil-1', 'soil_moisture', now() - interval '20 seconds')"))
            await seen.ExecuteNonQueryAsync();
        if (ruleSeconds is { } seconds) await _store.ReplaceAsync("pot", [NoData("soil-1", seconds)], default);

        var series = await _db.GetSilentSeriesAsync(TimeSpan.FromSeconds(platformSeconds), default);

        Assert.Equal(silent ? 1 : 0, series.Count);
        if (silent) Assert.Equal(reportedTimeout is { } t ? TimeSpan.FromSeconds(t) : null, series[0].Timeout);
    }

    [Fact]
    public async Task A_rule_on_one_channel_does_not_change_when_its_sibling_is_silent()
    {
        await using (var seen = _dataSource.CreateCommand("""
            INSERT INTO device_last_seen (device_id, channel, metric, last_seen_at) VALUES
                ('pot', 'soil-1', 'soil_moisture', now() - interval '20 seconds'),
                ('pot', 'soil-2', 'soil_moisture', now() - interval '20 seconds')
            """))
            await seen.ExecuteNonQueryAsync();
        await _store.ReplaceAsync("pot", [NoData("soil-1", 600)], default);

        var series = await _db.GetSilentSeriesAsync(TimeSpan.FromSeconds(1), default);

        Assert.Equal(["soil-2"], series.Select(s => s.Channel)); // soil-1 may be quiet for 10 minutes, soil-2 for 1 second
    }
}
