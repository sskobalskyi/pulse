using Npgsql;

namespace Pulse.Tests;

/// <summary>Applies the same schema script the compose stack uses, so tests run against the real tables and indexes.</summary>
public static class TestSchema
{
    public static async Task ApplyAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(await File.ReadAllTextAsync(Path()), conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string Path()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Pulse.slnx")))
            dir = dir.Parent;

        return System.IO.Path.Combine(dir?.FullName ?? throw new FileNotFoundException("Pulse.slnx not found above the test binaries"),
            "db", "init", "001-schema.sql");
    }
}
