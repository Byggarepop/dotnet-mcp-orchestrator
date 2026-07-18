using System.Text.RegularExpressions;
using McpOrchestrator.Orchestration;

namespace McpOrchestrator.Tui.Registry;

/// <summary>
/// One way a registry entry could be added to the config: an installable package, or a
/// remote/unsupported option kept visible so the UI can show why it is greyed out.
/// </summary>
/// <param name="Label">Display text, e.g. "npm: @scope/server" or "remote: https://...".</param>
/// <param name="Package">The package to map, when this option is addable; null otherwise.</param>
/// <param name="NotAddableReason">Why this option cannot be added; null when it can.</param>
internal sealed record MappingOption(string Label, RegistryPackage? Package, string? NotAddableReason)
{
    /// <summary>True when choosing this option can produce a config entry.</summary>
    public bool Addable => NotAddableReason is null;
}

/// <summary>An environment variable the add flow should prompt for, with its input treatment.</summary>
/// <param name="Variable">The declaration from the registry entry.</param>
/// <param name="Masked">True when the input field must hide what the user types.</param>
internal sealed record EnvPrompt(RegistryEnvironmentVariable Variable, bool Masked);

/// <summary>
/// Deterministically maps a registry server entry to a <see cref="CapabilityDescriptor"/>:
/// npm → npx, pypi → uvx, nuget → dnx, oci → docker run. Remote-hosted entries are not
/// mappable (the orchestrator is stdio-only). Pure logic — no I/O — so the TUI views stay thin.
/// </summary>
internal sealed partial class ServerMappingService
{
    private static readonly string[] SupportedTypes = { "npm", "pypi", "nuget", "oci" };

    [GeneratedRegex("KEY|TOKEN|SECRET|PASSWORD", RegexOptions.IgnoreCase)]
    private static partial Regex SecretNamePattern();

    /// <summary>
    /// Lists every way this entry could (or could not) be added: one option per package
    /// and per remote, in registry order, with unsupported ones carrying the reason.
    /// </summary>
    /// <param name="detail">The registry entry to enumerate.</param>
    public IReadOnlyList<MappingOption> GetOptions(RegistryServerDetail detail)
    {
        var options = new List<MappingOption>();

        foreach (var package in detail.Packages ?? [])
        {
            var label = $"{package.RegistryType}: {package.Identifier}";
            options.Add(SupportedTypes.Contains(package.RegistryType, StringComparer.OrdinalIgnoreCase)
                ? new MappingOption(label, package, null)
                : new MappingOption(label, null, $"package type '{package.RegistryType}' is not supported"));
        }

        foreach (var remote in detail.Remotes ?? [])
        {
            options.Add(new MappingOption(
                $"remote ({remote.Type}): {remote.Url}",
                null,
                "remote (HTTP) servers are not supported — McpOrchestrator is stdio-only"));
        }

        return options;
    }

    /// <summary>
    /// The environment variables the add flow should prompt for, flagging which need a
    /// masked input: those the registry marks secret, or whose name looks like a credential
    /// (contains KEY, TOKEN, SECRET, or PASSWORD).
    /// </summary>
    /// <param name="package">The chosen package option.</param>
    public IReadOnlyList<EnvPrompt> GetEnvPrompts(RegistryPackage package) =>
        (package.EnvironmentVariables ?? [])
            .Select(v => new EnvPrompt(v, v.IsSecret || SecretNamePattern().IsMatch(v.Name)))
            .ToList();

    /// <summary>
    /// Maps the chosen package to a config entry using the deterministic per-type rules,
    /// naming it after the registry name's last segment (deduplicated with a numeric suffix).
    /// </summary>
    /// <param name="detail">The registry entry being added.</param>
    /// <param name="package">The chosen package; must be one of the addable options.</param>
    /// <param name="envValues">Environment values collected from the user; copied to the entry's env block.</param>
    /// <param name="existingNames">Config server names already taken.</param>
    /// <exception cref="NotSupportedException">The package's registry type has no mapping rule.</exception>
    public CapabilityDescriptor Map(
        RegistryServerDetail detail,
        RegistryPackage package,
        IReadOnlyDictionary<string, string?> envValues,
        IEnumerable<string> existingNames)
    {
        var (command, args) = package.RegistryType.ToLowerInvariant() switch
        {
            "npm" => ("npx", new List<string> { "-y", WithVersion(package) }),
            "pypi" => ("uvx", new List<string> { package.Identifier }),
            "nuget" => ("dnx", new List<string> { package.Identifier, "--yes" }),
            "oci" => ("docker", new List<string> { "run", "-i", "--rm", package.Identifier }),
            _ => throw new NotSupportedException($"No mapping rule for package type '{package.RegistryType}'."),
        };

        return new CapabilityDescriptor
        {
            Name = DeriveName(detail.Name, existingNames),
            Summary = detail.Description ?? string.Empty,
            Enabled = true,
            Command = command,
            Args = args,
            Env = envValues.ToDictionary(e => e.Key, e => e.Value),
        };
    }

    /// <summary>
    /// Derives a config server name from a registry name's last '/'-segment (the whole name
    /// when there is no slash), appending "-2", "-3", ... while the name is taken
    /// (case-insensitively, matching config name uniqueness).
    /// </summary>
    /// <param name="registryName">The registry entry name, e.g. "io.github.foo/bar-mcp".</param>
    /// <param name="existingNames">Config server names already taken.</param>
    public string DeriveName(string registryName, IEnumerable<string> existingNames)
    {
        var baseName = registryName[(registryName.LastIndexOf('/') + 1)..];
        var taken = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName))
            return baseName;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName}-{suffix}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static string WithVersion(RegistryPackage package) =>
        string.IsNullOrWhiteSpace(package.Version) ? package.Identifier : $"{package.Identifier}@{package.Version}";
}
