using McpOrchestrator.Update;
using Xunit;

namespace McpOrchestrator.Tests;

public class SelfUpdaterTests
{
    [Fact]
    public void FindHash_returns_the_hash_for_the_named_asset()
    {
        var sums =
            "aaa111  McpOrchestrator-0.2.0-win-x64.zip\n" +
            "bbb222  McpOrchestrator-0.2.0-linux-x64.zip\n";

        Assert.Equal("bbb222", SelfUpdater.FindHash(sums, "McpOrchestrator-0.2.0-linux-x64.zip"));
        Assert.Equal("aaa111", SelfUpdater.FindHash(sums, "McpOrchestrator-0.2.0-win-x64.zip"));
    }

    [Fact]
    public void FindHash_tolerates_a_leading_path_prefix()
    {
        var sums = "cafe  ./McpOrchestrator-0.2.0-osx-arm64.zip\n";
        Assert.Equal("cafe", SelfUpdater.FindHash(sums, "McpOrchestrator-0.2.0-osx-arm64.zip"));
    }

    [Fact]
    public void FindHash_returns_null_when_the_asset_is_absent()
    {
        var sums = "aaa111  some-other-file.zip\n";
        Assert.Null(SelfUpdater.FindHash(sums, "McpOrchestrator-0.2.0-win-x64.zip"));
    }

    [Theory]
    [InlineData("v0.2.0", 0, 2, 0)]
    [InlineData("0.2.0", 0, 2, 0)]   // tolerant of a missing leading 'v'
    [InlineData("v1.10.3", 1, 10, 3)]
    public void ParseVersion_strips_the_tag_prefix_and_normalizes(string tag, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), SelfUpdater.ParseVersion(tag));
    }

    [Fact]
    public void ParseVersion_returns_null_for_junk()
    {
        Assert.Null(SelfUpdater.ParseVersion("not-a-version"));
        Assert.Null(SelfUpdater.ParseVersion(null));
    }

    [Fact]
    public void ParseVersion_orders_releases_so_newer_compares_greater()
    {
        Assert.True(SelfUpdater.ParseVersion("v0.3.0") > SelfUpdater.ParseVersion("v0.2.0"));
        Assert.True(SelfUpdater.ParseVersion("v0.2.0") == SelfUpdater.ParseVersion("0.2.0"));
    }
}
