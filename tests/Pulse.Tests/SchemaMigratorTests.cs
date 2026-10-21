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

        Assert.Equal(["0001_initial", "0002_device_registry"], ran);
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

        Assert.Equal(["0002_device_registry"], ran); // 0001 was recorded, not run again, which would have failed on the existing tables
        Assert.Equal(["0001_initial", "0002_device_registry"], await Journal(db));
        await using var count = db.CreateCommand("SELECT count(*) FROM readings");
        Assert.Equal(1L, await count.ExecuteScalarAsync()); // the data survived
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

        Assert.Equal(["0001_initial", "0002_device_registry"], await Journal(db));
        Assert.Equal(2, results.Sum(r => r.Count)); // two scripts ran in total, across all four instances
    }
}
