using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pulse.Api.Storage;
using Testcontainers.PostgreSql;

namespace Pulse.Tests;

/// <summary>One TimescaleDB container, and a fresh empty database inside it for every test.</summary>
public sealed class SchemaMigratorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb:2.17.2-pg16")
        .WithDatabase("postgres")
        .WithUsername("pulse")
        .WithPassword("pulse")
        .Build();

    private readonly List<NpgsqlDataSource> _opened = [];

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var dataSource in _opened) await dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task<NpgsqlDataSource> NewDatabase()
    {
        var name = "t_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString);
        _opened.Add(dataSource);
        return dataSource;
    }

    private static SchemaMigrator Migrator(NpgsqlDataSource dataSource) => new(dataSource, NullLogger<SchemaMigrator>.Instance);

    private static async Task<string[]> Journal(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand("SELECT version FROM schema_migrations ORDER BY version");
        await using var reader = await cmd.ExecuteReaderAsync();
        var versions = new List<string>();
        while (await reader.ReadAsync()) versions.Add(reader.GetString(0));
        return [.. versions];
    }

    private static async Task<bool> TableExists(NpgsqlDataSource dataSource, string table)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT to_regclass('public.{table}') IS NOT NULL");
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private static string InitialScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pulse.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "db", "migrations", "0001_initial.sql"));
    }

    [Fact]
    public async Task An_empty_database_gets_every_migration()
    {
        var db = await NewDatabase();

        var ran = await Migrator(db).MigrateAsync();

        Assert.Equal(["0001_initial", "0002_device_registry", "0003_channels", "0004_device_rules", "0005_outbox"], ran);
        Assert.Equal(ran, await Journal(db));
        Assert.True(await TableExists(db, "readings"));
        Assert.True(await TableExists(db, "devices"));
        Assert.True(await TableExists(db, "device_channels"));
    }

    [Fact]
    public async Task Running_again_applies_nothing()
    {
        var db = await NewDatabase();
        await Migrator(db).MigrateAsync();

        var second = await Migrator(db).MigrateAsync();

        Assert.Empty(second);
    }

    [Fact]
    public async Task A_database_from_before_migrations_existed_is_adopted_not_rebuilt()
    {
        var db = await NewDatabase();
        await using (var legacy = db.CreateCommand(InitialScript())) await legacy.ExecuteNonQueryAsync(); // what the old compose init script did
        await using (var data = db.CreateCommand("INSERT INTO readings VALUES ('dev-1', 'temperature', 21.5, now())")) await data.ExecuteNonQueryAsync();

        var ran = await Migrator(db).MigrateAsync();

        Assert.Equal(["0002_device_registry", "0003_channels", "0004_device_rules", "0005_outbox"], ran); // 0001 was recorded, not run again, which would have failed on the existing tables
        Assert.Equal(["0001_initial", "0002_device_registry", "0003_channels", "0004_device_rules", "0005_outbox"], await Journal(db));
        await using var count = db.CreateCommand("SELECT count(*) FROM readings");
        Assert.Equal(1L, await count.ExecuteScalarAsync()); // the data survived
    }

    private static string Script(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pulse.slnx"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "db", "migrations", name));
    }

    private static async Task Run(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Data_written_before_channels_existed_keeps_working_afterwards()
    {
        var db = await NewDatabase();
        await Run(db, InitialScript());
        // Start of a minute five minutes ago, so two readings 10 seconds apart always share a one-minute bucket.
        var now = DateTime.UtcNow.AddMinutes(-5);
        var recent = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        await using (var seed = db.CreateCommand("""
            INSERT INTO readings (device_id, metric, value, ts) VALUES
                ('dev-1', 'temperature', 21.5, @t), ('dev-1', 'temperature', 22.5, @t + interval '10 seconds'), ('dev-2', 'humidity', 40, @t);
            INSERT INTO device_last_seen (device_id, metric, last_seen_at) VALUES ('dev-1', 'temperature', now()), ('dev-2', 'humidity', now());
            INSERT INTO alerts (device_id, metric, rule, value, ts, state) VALUES ('dev-1', 'temperature', 'too hot', 95, @t, 'Raised');
            """))
        {
            seed.Parameters.AddWithValue("t", recent);
            await seed.ExecuteNonQueryAsync();
        }

        await Migrator(db).MigrateAsync();

        // Every existing row is on the channel named after its metric.
        Assert.Equal(0L, await Scalar(db, "SELECT count(*) FROM readings WHERE channel IS DISTINCT FROM metric"));
        Assert.Equal(0L, await Scalar(db, "SELECT count(*) FROM device_last_seen WHERE channel IS DISTINCT FROM metric"));
        Assert.Equal(0L, await Scalar(db, "SELECT count(*) FROM alerts WHERE channel IS DISTINCT FROM metric"));
        Assert.Equal(3L, await Scalar(db, "SELECT count(*) FROM readings"));

        // The aggregate was rebuilt with the new grouping and already holds the old data.
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM readings_1m WHERE device_id = 'dev-1' AND channel = 'temperature' AND samples = 2"));

        // The identity of a reading moved from (device, metric, ts) to (device, channel, ts).
        Assert.True((bool)(await Scalar(db, "SELECT to_regclass('public.ux_readings_channel_identity') IS NOT NULL"))!);
        Assert.True((bool)(await Scalar(db, "SELECT to_regclass('public.ux_readings_identity') IS NULL"))!);

        // And a reading sent again is still recognised as the same one.
        var again = new[] { new Pulse.Core.TelemetryReading("dev-1", "temperature", 21.5, new DateTimeOffset(recent, TimeSpan.Zero)) };
        await new PulseDb(db).InsertReadingsAsync(again, default);
        Assert.Equal(3L, await Scalar(db, "SELECT count(*) FROM readings"));
    }

    [Fact]
    public async Task The_channels_migration_can_be_run_again_after_a_start_that_stopped_part_way()
    {
        var db = await NewDatabase();
        await Migrator(db).MigrateAsync();
        await Run(db, "INSERT INTO readings (device_id, channel, metric, value, ts) VALUES ('dev-1', 'soil-1', 'soil_moisture', 40, now())");

        // What a restart does after a crash between the steps: run every part of the script again.
        foreach (var part in SchemaMigrator.SplitParts(Script("0003_channels.sql"))) await Run(db, part);

        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM readings WHERE channel = 'soil-1'")); // data intact, the channel kept
    }

    [Fact]
    public async Task A_database_older_than_the_first_migration_is_refused_with_a_clear_message()
    {
        var db = await NewDatabase();
        await using (var old = db.CreateCommand("CREATE TABLE readings (device_id text)")) await old.ExecuteNonQueryAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Migrator(db).MigrateAsync());

        Assert.Contains("docker compose down -v", error.Message);
    }

    [Fact]
    public async Task Instances_starting_at_the_same_time_apply_each_migration_once()
    {
        var db = await NewDatabase();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Migrator(db).MigrateAsync()));

        Assert.Equal(["0001_initial", "0002_device_registry", "0003_channels", "0004_device_rules", "0005_outbox"], await Journal(db));
        Assert.Equal(5, results.Sum(r => r.Count)); // five scripts ran in total, across all four instances
    }
}
