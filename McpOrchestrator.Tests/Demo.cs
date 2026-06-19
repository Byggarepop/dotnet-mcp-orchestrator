using McpOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpOrchestrator.Tests;

/// <summary>
/// Test helpers for spinning up the demo MCP as a real downstream process and wiring the
/// orchestrator's services around it. Tests launch the compiled <c>McpOrchestrator.DemoMcp.dll</c>
/// directly (via <c>dotnet &lt;dll&gt;</c>) rather than <c>dotnet run</c>, so there is no build
/// step on the hot path and startup is fast and deterministic.
/// </summary>
internal static class Demo
{
    /// <summary>The repository root, located by walking up for the solution file.</summary>
    public static string SolutionDir { get; } = FindSolutionDir();

    /// <summary>Absolute path to the built demo server assembly.</summary>
    public static string DemoDll { get; } = LocateDemoDll();

    /// <summary>
    /// Builds a capability descriptor that launches the demo server with the given persona
    /// (jira / codegen / diag). Timeouts default to null (orchestrator defaults) unless set.
    /// </summary>
    public static CapabilityDescriptor Capability(
        string name, string persona, int? connectTimeout = null, int? callTimeout = null) => new()
    {
        Name = name,
        Summary = $"Demo capability ({persona}).",
        Instructions = $"Demo persona '{persona}'.",
        Enabled = true,
        Transport = "stdio",
        Command = "dotnet",
        Args = new List<string> { DemoDll, "--persona", persona },
        WorkingDirectory = SolutionDir,
        ConnectTimeoutSeconds = connectTimeout,
        CallTimeoutSeconds = callTimeout,
    };

    /// <summary>Creates a catalog from the given descriptors using a no-op logger.</summary>
    public static ICapabilityCatalog Catalog(params CapabilityDescriptor[] capabilities) =>
        CapabilityCatalog.FromDescriptors(capabilities, NullLogger.Instance);

    /// <summary>Creates a connection manager over the given descriptors. Dispose it to stop the children.</summary>
    public static DownstreamConnectionManager Connections(params CapabilityDescriptor[] capabilities) =>
        Pair(capabilities).Connections;

    /// <summary>
    /// Creates a catalog and a connection manager that share it — needed when a test drives the
    /// orchestrator tool methods, which take both. Dispose the manager to stop the children.
    /// </summary>
    public static (ICapabilityCatalog Catalog, DownstreamConnectionManager Connections) Pair(
        params CapabilityDescriptor[] capabilities)
    {
        var catalog = Catalog(capabilities);
        var connections = new DownstreamConnectionManager(
            catalog, NullLoggerFactory.Instance, new NullLogger<DownstreamConnectionManager>());
        return (catalog, connections);
    }

    /// <summary>The standard jira + codegen + diag trio most integration tests use.</summary>
    public static (ICapabilityCatalog Catalog, DownstreamConnectionManager Connections) StandardPair() => Pair(
        Capability("jira", "jira"),
        Capability("codegen", "codegen"),
        Capability("diag", "diag"));

    private static string FindSolutionDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "McpOrchestrator.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate McpOrchestrator.slnx above the test assembly.");
    }

    private static string LocateDemoDll()
    {
        // Mirror the configuration (Debug/Release) the tests were built in.
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        var dll = Path.Combine(
            SolutionDir, "McpOrchestrator.DemoMcp", "bin", configuration, "net10.0", "McpOrchestrator.DemoMcp.dll");

        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"Demo server not built at '{dll}'. Build the solution before running the tests.", dll);
        }

        return dll;
    }
}
