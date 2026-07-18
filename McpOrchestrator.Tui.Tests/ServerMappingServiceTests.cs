using McpOrchestrator.Tui.Registry;
using Xunit;

namespace McpOrchestrator.Tui.Tests;

/// <summary>
/// Tests for <see cref="ServerMappingService"/>: the deterministic package→config mapping
/// rules, name derivation and deduplication, option enumeration, and env-var prompts.
/// </summary>
public sealed class ServerMappingServiceTests
{
    private static readonly string[] NoNames = [];
    private static readonly Dictionary<string, string?> NoEnv = new();

    private readonly ServerMappingService _service = new();

    private static RegistryServerDetail Detail(string name, params RegistryPackage[] packages) =>
        new(name, "A test server.", null, "1.0.0", packages.ToList(), null);

    private static RegistryPackage Package(string type, string identifier, string? version = null) =>
        new(type, identifier, version, null, null);

    [Fact]
    public void Npm_maps_to_npx_with_pinned_version()
    {
        var descriptor = _service.Map(
            Detail("io.github.foo/bar-mcp", Package("npm", "@foo/bar-mcp", "1.2.3")),
            Package("npm", "@foo/bar-mcp", "1.2.3"), NoEnv, NoNames);

        Assert.Equal("npx", descriptor.Command);
        Assert.Equal(new List<string> { "-y", "@foo/bar-mcp@1.2.3" }, descriptor.Args);
        Assert.Equal("bar-mcp", descriptor.Name);
        Assert.Equal("A test server.", descriptor.Summary);
        Assert.True(descriptor.Enabled);
    }

    [Fact]
    public void Npm_without_version_omits_version_pin()
    {
        var descriptor = _service.Map(
            Detail("x/y", Package("npm", "y")), Package("npm", "y"), NoEnv, NoNames);

        Assert.Equal(new List<string> { "-y", "y" }, descriptor.Args);
    }

    [Fact]
    public void Pypi_maps_to_uvx()
    {
        var descriptor = _service.Map(
            Detail("x/y", Package("pypi", "some-server")), Package("pypi", "some-server"), NoEnv, NoNames);

        Assert.Equal("uvx", descriptor.Command);
        Assert.Equal(new List<string> { "some-server" }, descriptor.Args);
    }

    [Fact]
    public void Nuget_maps_to_dnx_with_yes()
    {
        var descriptor = _service.Map(
            Detail("x/y", Package("nuget", "Some.Server")), Package("nuget", "Some.Server"), NoEnv, NoNames);

        Assert.Equal("dnx", descriptor.Command);
        Assert.Equal(new List<string> { "Some.Server", "--yes" }, descriptor.Args);
    }

    [Fact]
    public void Oci_maps_to_docker_run()
    {
        var descriptor = _service.Map(
            Detail("x/y", Package("oci", "ghcr.io/x/y:1")), Package("oci", "ghcr.io/x/y:1"), NoEnv, NoNames);

        Assert.Equal("docker", descriptor.Command);
        Assert.Equal(new List<string> { "run", "-i", "--rm", "ghcr.io/x/y:1" }, descriptor.Args);
    }

    [Fact]
    public void Unsupported_package_type_throws()
    {
        Assert.Throws<NotSupportedException>(() => _service.Map(
            Detail("x/y", Package("mcpb", "z")), Package("mcpb", "z"), NoEnv, NoNames));
    }

    [Fact]
    public void Env_values_land_in_env_block()
    {
        var env = new Dictionary<string, string?> { ["API_KEY"] = "abc", ["MODE"] = null };

        var descriptor = _service.Map(
            Detail("x/y", Package("pypi", "z")), Package("pypi", "z"), env, NoNames);

        Assert.Equal("abc", descriptor.Env["API_KEY"]);
        Assert.True(descriptor.Env.ContainsKey("MODE"));
    }

    [Theory]
    [InlineData("io.github.foo/bar-mcp", "bar-mcp")]
    [InlineData("plain-name", "plain-name")]
    public void DeriveName_uses_last_segment_or_whole_name(string registryName, string expected)
    {
        Assert.Equal(expected, _service.DeriveName(registryName, NoNames));
    }

    [Theory]
    [InlineData(new[] { "bar-mcp" }, "bar-mcp-2")]
    [InlineData(new[] { "BAR-MCP" }, "bar-mcp-2")]
    [InlineData(new[] { "bar-mcp", "bar-mcp-2" }, "bar-mcp-3")]
    public void DeriveName_deduplicates_with_numeric_suffix(string[] taken, string expected)
    {
        Assert.Equal(expected, _service.DeriveName("io.github.foo/bar-mcp", taken));
    }

    [Fact]
    public void GetOptions_splits_addable_and_not_addable()
    {
        var detail = new RegistryServerDetail(
            "x/y", null, null, "1.0",
            new List<RegistryPackage> { Package("pypi", "ok"), Package("mcpb", "nope") },
            new List<RegistryRemote> { new("streamable-http", "https://example.com/mcp") });

        var options = _service.GetOptions(detail);

        Assert.Equal(3, options.Count);
        Assert.True(options[0].Addable);
        Assert.False(options[1].Addable);
        Assert.Contains("mcpb", options[1].NotAddableReason);
        Assert.False(options[2].Addable);
        Assert.Contains("stdio-only", options[2].NotAddableReason);
    }

    [Theory]
    [InlineData("SLACK_TOKEN", false, true)]   // name matches TOKEN
    [InlineData("api_key", false, true)]       // case-insensitive KEY match
    [InlineData("WORKSPACE", true, true)]      // registry says secret
    [InlineData("WORKSPACE", false, false)]    // nothing secret about it
    public void GetEnvPrompts_masks_secrets_and_credential_like_names(string name, bool isSecret, bool expectMasked)
    {
        var package = new RegistryPackage("pypi", "z", null, null,
            new List<RegistryEnvironmentVariable> { new(name, null, false, isSecret) });

        var prompts = _service.GetEnvPrompts(package);

        Assert.Single(prompts);
        Assert.Equal(expectMasked, prompts[0].Masked);
    }
}
