using System.Text.Json.Serialization;

namespace McpOrchestrator.Tui.Registry;

/// <summary>
/// One page of results from a registry's <c>GET /v0/servers</c> endpoint. Only the fields
/// the TUI displays or maps are modeled; everything else in the response is ignored.
/// </summary>
internal sealed record RegistryServersResponse(
    List<RegistryServerEntry>? Servers,
    RegistryResponseMetadata? Metadata);

/// <summary>Pagination metadata; <see cref="NextCursor"/> is null on the last page.</summary>
internal sealed record RegistryResponseMetadata(string? NextCursor, int? Count);

/// <summary>Wrapper the registry puts around each server (its metadata sibling is ignored).</summary>
internal sealed record RegistryServerEntry(RegistryServerDetail Server);

/// <summary>
/// The server.json subset the TUI shows in the browser detail pane and feeds to the
/// entry-to-config mapping: identity, version, and the installable/connectable options.
/// </summary>
internal sealed record RegistryServerDetail(
    string Name,
    string? Description,
    string? Title,
    string? Version,
    List<RegistryPackage>? Packages,
    List<RegistryRemote>? Remotes);

/// <summary>An installable package option (npm, pypi, nuget, oci, ...).</summary>
internal sealed record RegistryPackage(
    string RegistryType,
    string Identifier,
    string? Version,
    string? RuntimeHint,
    List<RegistryEnvironmentVariable>? EnvironmentVariables);

/// <summary>A remote-hosted option; the orchestrator is stdio-only, so these are shown but not addable.</summary>
internal sealed record RegistryRemote(string Type, string Url);

/// <summary>An environment variable a package declares; secrets get masked input in the add flow.</summary>
internal sealed record RegistryEnvironmentVariable(
    string Name,
    string? Description,
    bool IsRequired,
    bool IsSecret);

/// <summary>A parsed page handed to the TUI: the entries plus the cursor for the next page, if any.</summary>
internal sealed record RegistryPage(List<RegistryServerEntry> Entries, string? NextCursor);

/// <summary>
/// Source-generation context for registry responses (camelCase wire format, case-insensitive
/// reads). Read-only — the TUI never writes registry JSON back.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RegistryServersResponse))]
internal sealed partial class RegistryJsonContext : JsonSerializerContext;
