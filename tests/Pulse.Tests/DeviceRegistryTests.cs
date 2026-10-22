using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Pulse.Api.Processing;
using Pulse.Api.Registry;
using Pulse.Api.Storage;
using Pulse.Core;
using Testcontainers.PostgreSql;

namespace Pulse.Tests;

/// <summary>The registry against a real database, with a clock the tests control.</summary>
public sealed class DeviceRegistryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb:2.17.2-pg16")
        .WithDatabase("pulse")
        .WithUsername("pulse")
        .WithPassword("pulse")
        .Build();

    private readonly FakeTimeProvider _time = new(T0);
    private NpgsqlDataSource _dataSource = null!;
    private DeviceRegistry _registry = null!;
    private PulseDb _db = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await TestSchema.ApplyAsync(_dataSource);
        _db = new PulseDb(_dataSource);
        _registry = new DeviceRegistry(_dataSource, _time, Options.Create(new SilenceOptions { Timeout = TimeSpan.FromMinutes(1) }));
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private DeviceRegistration Registration(string id = "dev-1", string token = "token", TimeSpan? validFor = null) => new(
        id,
        "greenhaus-pot-v1",
        new Capabilities(
            [new Channel("soil-1", "soil_moisture", "Zone 1", "%"), new Channel("soil-2", "soil_moisture")],
            [new ActuatorCapability("pump-1", "pump", new HardLimits(20, 600, 120), ThermalCutoff: null)]),
        "credential-hash",
        ClaimTokens.Hash(token),
        _time.GetUtcNow() + (validFor ?? TimeSpan.FromDays(30)));

    private async Task<Device> Shipped(string id = "dev-1", string token = "token", TimeSpan? validFor = null)
    {
        await _registry.RegisterAsync(Registration(id, token, validFor), default);
        return (await _registry.ShipAsync(id, default)).Value!;
    }

    private TelemetryReading Reading(string id = "dev-1") => new(id, "temperature", 21, _time.GetUtcNow());

    [Fact]
    public async Task A_claim_token_expires()
    {
        await Shipped(validFor: TimeSpan.FromDays(7));
        _time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));

        var result = await _registry.ClaimAsync("dev-1", "token", "owner", default);

        Assert.Equal(RegistryError.TokenExpired, result.Error);
        Assert.Equal("shipped", (await _registry.GetAsync("dev-1", null, default))!.State); // still claimable with a fresh token, none issued here
    }

    [Fact]
    public async Task A_token_is_valid_right_up_to_its_expiry()
    {
        await Shipped(validFor: TimeSpan.FromDays(7));
        _time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));

        Assert.True((await _registry.ClaimAsync("dev-1", "token", "owner", default)).IsOk);
    }

    [Fact]
    public async Task An_expired_answer_is_only_given_to_whoever_has_the_real_token()
    {
        await Shipped(validFor: TimeSpan.FromDays(1));
        _time.Advance(TimeSpan.FromDays(2));

        var guess = await _registry.ClaimAsync("dev-1", "wrong", "owner", default);

        Assert.Equal(RegistryError.TokenInvalid, guess.Error); // not "expired", which would confirm the device exists and is shipped
    }

    [Fact]
    public async Task Two_claims_at_the_same_time_cannot_both_win()
    {
        await Shipped();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => _registry.ClaimAsync("dev-1", "token", $"owner-{i}", default)));

        Assert.Single(results, r => r.IsOk);
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(RegistryError.InvalidState, r.Error));
        var winner = results.Single(r => r.IsOk).Value!;
        Assert.Equal(winner.OwnerId, (await _registry.GetAsync("dev-1", null, default))!.OwnerId); // the stored owner is the winner's
    }

    [Fact]
    public async Task A_claimed_devices_token_is_erased()
    {
        await Shipped();
        await _registry.ClaimAsync("dev-1", "token", "owner", default);

        await using var cmd = _dataSource.CreateCommand("SELECT claim_token_hash, claim_token_expires_at FROM devices WHERE id = 'dev-1'");
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public async Task Claiming_a_device_that_does_not_exist_is_not_found()
    {
        Assert.Equal(RegistryError.NotFound, (await _registry.ClaimAsync("ghost", "token", "owner", default)).Error);
    }

    [Fact]
    public async Task A_database_constraint_stops_a_claimed_device_from_having_no_owner()
    {
        await Shipped();

        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = _dataSource.CreateCommand("UPDATE devices SET state = 'claimed' WHERE id = 'dev-1'");
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.Equal("ck_devices_owner", error.ConstraintName);
    }

    [Fact]
    public async Task A_caller_sees_only_the_devices_they_own()
    {
        foreach (var id in new[] { "a1", "a2", "b1", "free" }) await Shipped(id);
        foreach (var (id, owner) in new[] { ("a1", "alice"), ("a2", "alice"), ("b1", "bob") })
            await _registry.ClaimAsync(id, "token", owner, default);

        Assert.Equal(["a1", "a2"], (await _registry.ListAsync(null, "alice", default)).Select(d => d.Id));
        Assert.Equal(["b1"], (await _registry.ListAsync(null, "bob", default)).Select(d => d.Id));
        Assert.Empty(await _registry.ListAsync(null, "nobody", default));
        Assert.Equal(4, (await _registry.ListAsync(null, null, default)).Count); // no scope: everything, including the unclaimed one
    }

    [Fact]
    public async Task A_device_outside_the_scope_looks_like_it_does_not_exist()
    {
        await Shipped("b1");
        await _registry.ClaimAsync("b1", "token", "bob", default);

        Assert.Null(await _registry.GetAsync("b1", "alice", default));
        Assert.NotNull(await _registry.GetAsync("b1", "bob", default));
        Assert.Equal(RegistryError.NotFound, (await _registry.CalibrateAsync("b1", "soil-1", 800, 300, "alice", default)).Error);
        Assert.True((await _registry.CalibrateAsync("b1", "soil-1", 800, 300, "bob", default)).IsOk);
    }

    [Fact]
    public async Task Connection_follows_how_recently_the_device_reported()
    {
        var shortLived = new DeviceRegistry(_dataSource, _time, Options.Create(new SilenceOptions { Timeout = TimeSpan.FromSeconds(3) }));
        await Shipped();
        Assert.Equal("unknown", (await shortLived.GetAsync("dev-1", null, default))!.Connection);

        await _db.TouchLastSeenAsync([Reading()], default);
        Assert.Equal("online", (await shortLived.GetAsync("dev-1", null, default))!.Connection);

        await DbClock.DelayAsync(_dataSource, TimeSpan.FromSeconds(3.5));
        Assert.Equal("offline", (await shortLived.GetAsync("dev-1", null, default))!.Connection);
    }

    [Fact]
    public async Task Retiring_forgets_the_last_seen_rows_so_the_device_is_no_longer_watched()
    {
        await Shipped();
        await _db.TouchLastSeenAsync([Reading()], default);

        await _registry.RetireAsync("dev-1", default);

        await using var cmd = _dataSource.CreateCommand("SELECT count(*) FROM device_last_seen WHERE device_id = 'dev-1'");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_retired_device_cannot_become_watched_again_by_late_readings()
    {
        await Shipped();
        await _registry.RetireAsync("dev-1", default);

        await _db.TouchLastSeenAsync([Reading()], default); // what the writer does for a batch that was still queued in Kafka

        await using var cmd = _dataSource.CreateCommand("SELECT count(*) FROM device_last_seen WHERE device_id = 'dev-1'");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Retiring_erases_an_unused_claim_token()
    {
        await Shipped();

        await _registry.RetireAsync("dev-1", default);

        Assert.Equal(RegistryError.InvalidState, (await _registry.ClaimAsync("dev-1", "token", "owner", default)).Error);
        await using var cmd = _dataSource.CreateCommand("SELECT claim_token_hash IS NULL FROM devices WHERE id = 'dev-1'");
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Only_claimed_devices_are_activated_and_only_once()
    {
        await Shipped("claimed-one");
        await _registry.ClaimAsync("claimed-one", "token", "owner", default);
        await Shipped("shipped-one");
        await Shipped("retired-one");
        await _registry.RetireAsync("retired-one", default);

        var first = await _registry.ActivateAsync(["claimed-one", "shipped-one", "retired-one", "unknown"], default);
        var second = await _registry.ActivateAsync(["claimed-one"], default);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal("active", (await _registry.GetAsync("claimed-one", null, default))!.State);
        Assert.Equal("shipped", (await _registry.GetAsync("shipped-one", null, default))!.State);
        Assert.Equal("retired", (await _registry.GetAsync("retired-one", null, default))!.State);
    }

    [Fact]
    public async Task Retired_devices_are_picked_out_of_a_batch()
    {
        await Shipped("keeps-going");
        await Shipped("gone");
        await _registry.RetireAsync("gone", default);

        var retired = await _registry.RetiredAmongAsync(["keeps-going", "gone", "never-registered"], default);

        Assert.Equal(["gone"], retired);
    }

    [Fact]
    public async Task Deleting_a_device_takes_its_channels_with_it()
    {
        await Shipped();

        await using (var delete = _dataSource.CreateCommand("DELETE FROM devices WHERE id = 'dev-1'")) await delete.ExecuteNonQueryAsync();

        await using var cmd = _dataSource.CreateCommand("SELECT count(*) FROM device_channels");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }
}
