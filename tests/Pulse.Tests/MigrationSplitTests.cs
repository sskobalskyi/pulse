using Pulse.Api.Storage;

namespace Pulse.Tests;

public class MigrationSplitTests
{
    [Fact]
    public void A_script_without_a_marker_is_one_part()
    {
        var parts = SchemaMigrator.SplitParts("CREATE TABLE a (x int);\nCREATE TABLE b (y int);\n");

        Assert.Equal(["CREATE TABLE a (x int);\nCREATE TABLE b (y int);"], parts);
    }

    [Fact]
    public void A_marker_line_separates_two_commands()
    {
        var parts = SchemaMigrator.SplitParts("SELECT 1;\n-- split\nSELECT 2;\n");

        Assert.Equal(["SELECT 1;", "SELECT 2;"], parts);
    }

    [Fact]
    public void Windows_line_endings_and_spacing_around_the_marker_are_fine()
    {
        var parts = SchemaMigrator.SplitParts("SELECT 1;\r\n  --   split  \r\nSELECT 2;\r\n");

        Assert.Equal(["SELECT 1;", "SELECT 2;"], parts);
    }

    [Fact]
    public void Markers_at_the_edges_and_back_to_back_leave_no_empty_parts()
    {
        var parts = SchemaMigrator.SplitParts("-- split\nSELECT 1;\n-- split\n-- split\nSELECT 2;\n-- split\n");

        Assert.Equal(["SELECT 1;", "SELECT 2;"], parts);
    }

    [Fact]
    public void The_word_split_inside_other_text_does_not_split()
    {
        var script = "-- This is not a split of anything.\nSELECT 'a -- split b';\n-- split into two? no\n";

        Assert.Single(SchemaMigrator.SplitParts(script));
    }

    [Fact]
    public void An_empty_script_has_no_parts() =>
        Assert.Empty(SchemaMigrator.SplitParts("  \n\n"));
}
