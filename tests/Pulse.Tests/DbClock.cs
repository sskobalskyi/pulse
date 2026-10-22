using Npgsql;

namespace Pulse.Tests;

/// <summary>
/// The clock of the database in a container. Silence is judged by the database's <c>now()</c>, and under Docker
/// Desktop that clock is not in step with the test process: measured against the host it wandered by several
/// hundred milliseconds. A test that sleeps on the host clock and then checks a timeout of a second can be
/// wrong either way, so tests that wait for a timeout wait on this clock instead.
/// </summary>
public static class DbClock
{
    public static async Task<DateTimeOffset> NowAsync(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand("SELECT clock_timestamp()");
        return new DateTimeOffset((DateTime)(await cmd.ExecuteScalarAsync())!);
    }

    /// <summary>Returns once the database's clock has moved on by at least <paramref name="duration"/>.</summary>
    public static async Task DelayAsync(NpgsqlDataSource db, TimeSpan duration)
    {
        var until = await NowAsync(db) + duration;
        while (await NowAsync(db) < until) await Task.Delay(50);
    }
}
