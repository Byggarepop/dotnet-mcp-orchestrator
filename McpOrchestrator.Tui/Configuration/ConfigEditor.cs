using McpOrchestrator.Orchestration;
using System.Text.Json;

namespace McpOrchestrator.Tui.Configuration
{
    /// <summary>
    /// Handles every TUI action load → mutate → validate → atomically save.
    /// </summary>
    internal sealed class ConfigEditor
    {
        public const string OfficialRegistryName = "official";
        public const string OfficialRegistryUrl = "https://registry.modelcontextprotocol.io";

        /// <summary>
        /// Loads the orchestrator configuration from the specified JSON file path.
        /// </summary>
        /// <param name="path">Path to orchestrator.config.json</param>
        /// <returns>A parsed config, or an empty one when the file doesn't exist yet, always containing the official registry</returns>
        /// <exception cref="ArgumentException">Path is null or whitespace</exception>
        /// <exception cref="JsonException">Unreadable/malformed content</exception>
        public OrchestratorConfig Load(string path)
        {
            var config = new OrchestratorConfig();

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path cannot be null or whitespace.", nameof(path));

            if (File.Exists(path))
            {
                var jsonAsString = File.ReadAllText(path);
                config = JsonSerializer.Deserialize(jsonAsString, TuiJsonContext.Default.OrchestratorConfig);

                if (config is null)
                    throw new JsonException("Config deserialized to null — the file may be malformed or empty.");
            }

            AddOfficialRegistryIfMissing(config);

            return config;
        }

        /// <summary>
        /// Checks the config against the rules the orchestrator enforces when it loads:
        /// capability names non-empty and unique (case-insensitive), commands non-empty,
        /// and registry sources with a name and an absolute http(s) url.
        /// </summary>
        /// <param name="config">The config to check.</param>
        /// <returns>Human-readable problems; empty when the config is valid.</returns>
        public IReadOnlyList<string> Validate(OrchestratorConfig config)
        {
            var problems = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var capability in config.Capabilities)
            {
                if (string.IsNullOrWhiteSpace(capability.Name))
                {
                    problems.Add("A capability has an empty name.");
                    continue;
                }

                if (!seenNames.Add(capability.Name))
                    problems.Add($"Duplicate capability name '{capability.Name}'.");

                if (string.IsNullOrWhiteSpace(capability.Command))
                    problems.Add($"Capability '{capability.Name}' has no command.");
            }

            foreach (var registry in config.Registries)
            {
                if (string.IsNullOrWhiteSpace(registry.Name))
                    problems.Add("A registry source has an empty name.");

                if (!Uri.TryCreate(registry.Url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    problems.Add($"Registry source '{registry.Name}' has an invalid url '{registry.Url}' — expected an absolute http(s) url.");
                }
            }

            return problems;
        }

        /// <summary>
        /// Validates and atomically writes the config: serializes to a temp file next to the
        /// target, then swaps it in, keeping the previous file as <c>&lt;path&gt;.bak</c>.
        /// The temp file lives in the target's directory so the final rename never crosses
        /// volumes, and the orchestrator's file watcher sees it as a normal change.
        /// </summary>
        /// <param name="config">The config to write.</param>
        /// <param name="path">Path to orchestrator.config.json.</param>
        /// <exception cref="InvalidOperationException">The config failed validation; nothing was written.</exception>
        public void Save(OrchestratorConfig config, string path)
        {
            var problems = Validate(config);
            if (problems.Count > 0)
                throw new InvalidOperationException("Refusing to save an invalid config: " + string.Join(" ", problems));

            var json = JsonSerializer.Serialize(config, TuiJsonContext.Default.OrchestratorConfig);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var tempPath = Path.Combine(directory, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(tempPath, json);

                if (File.Exists(path))
                    File.Replace(tempPath, path, path + ".bak");
                else
                    File.Move(tempPath, path);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        /// <summary>
        /// Inserts the official registry as the first source when no source already uses its url.
        /// </summary>
        /// <param name="config">The config to normalize.</param>
        private void AddOfficialRegistryIfMissing(OrchestratorConfig config)
        {
            if (!config.Registries.Any(r => r.Url.Equals(OfficialRegistryUrl, StringComparison.OrdinalIgnoreCase)))
            {
                config.Registries.Insert(0, new RegistrySource
                {
                    Name = OfficialRegistryName,
                    Url = OfficialRegistryUrl
                });
            }
        }

    }
}
