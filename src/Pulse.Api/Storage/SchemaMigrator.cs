using System.Reflection;
using Npgsql;

namespace Pulse.Api.Storage;

/// <summary>
/// Applies the SQL scripts in <c>db/migrations</c>, embedded in this assembly, in file name order.
/// The scripts that have run are recorded in <c>schema_migrations</c>. A database lock serialises several
/// instances starting at once, so each script runs exactly once.
/// </summary>
/// <remarks>
/// Scripts run as written, without a wrapping transaction, because TimescaleDB statements such as creating a
/// continuous aggregate cannot run inside one. A script that does not need that should wrap itself in
/// BEGIN and COMMIT so a failure leaves nothing half applied.
/// </remarks>
public sealed class SchemaMigrator(NpgsqlDataSource dataSource, ILogger<SchemaMigrator> logger)
{
    private const string ResourcePrefix = "Pulse.Migrations.";
    private const string InitialVersion = "0001_initial";

    /// <summary>Arbitrary but fixed: every instance must use the same number.</summary>
    private const long LockKey = 7_300_000;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await ExecuteAsync(conn, $"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            await ExecuteAsync(conn, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    text        PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """, ct);

            var applied = await AppliedAsync(conn, ct);
            if (applied.Count == 0 && await TableExistsAsync(conn, "readings", ct))
            {
                await AdoptExistingDatabaseAsync(conn, ct);
                applied.Add(InitialVersion);
            }

            var ran = new List<string>();
            foreach (var (version, sql) in Scripts())
            {
                if (applied.Contains(version)) continue;

                logger.LogInformation("Applying migration {Version}", version);
                await ExecuteAsync(conn, sql, ct);
                await using var record = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES (@version)", conn);
                record.Parameters.AddWithValue("version", version);
                await record.ExecuteNonQueryAsync(ct);
                ran.Add(version);
            }

            return ran;
        }
        finally
        {
            // Not cancellable: the lock must not outlive this call because the caller was shutting down.
            await ExecuteAsync(conn, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }
    }

    /// <summary>
    /// Databases created before migrations existed got their schema from the compose init script, which is
    /// exactly <c>0001_initial</c>. Record it as applied instead of running it again.
    /// </summary>
    private async Task AdoptExistingDatabaseAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // device_last_seen was the last table the init script added. Without it the database is older than 0001.
        if (!await TableExistsAsync(conn, "device_last_seen", ct))
        {
            throw new InvalidOperationException(
                "The database was created by an older version of the init script and cannot be migrated. " +
                "Recreate it with `docker compose down -v`.");
        }

        logger.LogInformation("Existing database detected, recording {Version} as applied", InitialVersion);
        await using var record = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES (@version)", conn);
        record.Parameters.AddWithValue("version", InitialVersion);
        await record.ExecuteNonQueryAsync(ct);
    }

    private static IEnumerable<(string Version, string Sql)> Scripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            var version = Path.GetFileNameWithoutExtension(name[ResourcePrefix.Length..]);
            yield return (version, reader.ReadToEnd());
        }
    }

    private static async Task<HashSet<string>> AppliedAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT version FROM schema_migrations", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct)) versions.Add(reader.GetString(0));
        return versions;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("name", "public." + table);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Brings the database up to date before anything else starts, so the stream processors never see an old schema.</summary>
public sealed class MigrationService(SchemaMigrator migrator) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) => await migrator.MigrateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
