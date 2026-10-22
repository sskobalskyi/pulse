using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pulse.Api.Storage;

namespace Pulse.Tests;

/// <summary>Brings a test database to the current schema with the same migrator the API runs on startup.</summary>
public static class TestSchema
{
    public static Task ApplyAsync(NpgsqlDataSource dataSource) =>
        new SchemaMigrator(dataSource, NullLogger<SchemaMigrator>.Instance).MigrateAsync();
}
