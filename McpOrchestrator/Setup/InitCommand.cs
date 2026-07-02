using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using McpOrchestrator.Orchestration;

namespace McpOrchestrator.Setup;

/// <summary>
/// The <c>mcp-orchestrator init</c> subcommand: turns an existing MCP host config
/// (<c>.mcp.json</c> / <c>.vscode/mcp.json</c> / Cursor / Claude Desktop) into an orchestrator
/// setup in one step. It lifts every stdio server into a generated
/// <c>orchestrator.config.json</c> (one capability each), then rewrites the host config so the
/// agent launches only the orchestrator — pointed at that new file via
/// <c>MCP_ORCHESTRATOR_CONFIG</c>. The original host config is backed up first.
/// </summary>
/// <remarks>
/// Exit codes: <c>0</c> success · <c>1</c> usage / IO / parse error.
/// Unless <c>--no-summarize</c> is given, init connects to each server once and auto-generates its
/// one-line <c>summary</c> (see <see cref="SummaryGenerator"/>); the user only reviews the result.
/// </remarks>
public static class InitCommand
{
    /// <summary>The pinned, never-published version pack-local.ps1 uses for the local dev feed (--dev-feed).</summary>
    private const string DevVersion = "9.9.9-dev";

    /// <summary>
    /// The default (no --command, no --dev-feed) launch: run the tool from nuget.org via
    /// <c>dotnet tool execute</c>, pinned to the version of the tool running <em>right now</em> —
    /// deterministic, already in the local NuGet cache (this very process came from it), and
    /// therefore offline-safe. Unpinned would re-resolve "latest" on host starts instead.
    /// </summary>
    private static string[] DefaultExecuteArgs()
    {
        var version = typeof(InitCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // Strip SourceLink build metadata ("0.2.3+abc123" → "0.2.3").
        var plus = version?.IndexOf('+') ?? -1;
        if (plus >= 0)
        {
            version = version![..plus];
        }

        return string.IsNullOrWhiteSpace(version)
            ? new[] { "tool", "execute", "McpOrchestrator", "--yes" }
            : new[] { "tool", "execute", "McpOrchestrator", "--version", version, "--yes" };
    }

    /// <summary>Entry point for the subcommand. <paramref name="args"/> excludes the leading "init".</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { /* redirected or unsupported — fine. */ }

        ParsedArgs parsed;
        try
        {
            parsed = ParsedArgs.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"init: {ex.Message}");
            Console.Error.WriteLine("Run 'mcp-orchestrator init --help' for usage.");
            return 1;
        }

        if (parsed.ShowHelp)
        {
            Console.Out.WriteLine(HelpText);
            return 0;
        }

        // No <host-config> argument? Look in the current directory for one to adopt — whatever MCP
        // host config the tool you use drops there (.mcp.json, .vscode/mcp.json, .cursor/mcp.json).
        // First match by the precedence below wins.
        if (parsed.HostConfigPath is null)
        {
            var discovered = DiscoverHostConfig(Directory.GetCurrentDirectory());
            if (discovered is null)
            {
                Console.Error.WriteLine(
                    "init: a host config path is required, e.g. 'mcp-orchestrator init .mcp.json'. " +
                    $"With none given, init looks in the current directory for: {HostConfigCandidateList} — none were found.");
                Console.Error.WriteLine("Run 'mcp-orchestrator init --help' for usage.");
                return 1;
            }

            parsed = parsed.WithHostConfig(discovered);
            Console.Error.WriteLine($"init: no host config given; using discovered {discovered}.");
        }

        try
        {
            return await ExecuteAsync(parsed);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"init: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Host-config filenames probed, in precedence order, when no <c>&lt;host-config&gt;</c> argument
    /// is given. These are the files different MCP clients drop in a project root.
    /// <c>orchestrator.config.json</c> is deliberately absent — that's init's <em>output</em>, not a
    /// host config to adopt. Relative to the search directory.
    /// </summary>
    private static readonly string[] HostConfigCandidates =
    {
        ".mcp.json",
        Path.Combine(".vscode", "mcp.json"),
        Path.Combine(".cursor", "mcp.json"),
        "mcp.json",
    };

    /// <summary>Human-readable list of the probed host-config locations, for help text and errors.</summary>
    internal static string HostConfigCandidateList =>
        string.Join(", ", HostConfigCandidates.Select(c => c.Replace('\\', '/')));

    /// <summary>
    /// Probes <paramref name="directory"/> for the first existing host config in
    /// <see cref="HostConfigCandidates"/>. Returns its full path, or <c>null</c> if none exist.
    /// </summary>
    internal static string? DiscoverHostConfig(string directory)
    {
        foreach (var relativePath in HostConfigCandidates)
        {
            var candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static async Task<int> ExecuteAsync(ParsedArgs parsed)
    {
        var hostPath = Path.GetFullPath(parsed.HostConfigPath!);
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException($"host config not found at '{hostPath}'.", hostPath);
        }

        var hostDir = Path.GetDirectoryName(hostPath) ?? Directory.GetCurrentDirectory();
        var outPath = Path.GetFullPath(parsed.OutPath ?? Path.Combine(hostDir, "orchestrator.config.json"));

        if (File.Exists(outPath) && !parsed.Force && !parsed.DryRun && !parsed.PrintCentral && parsed.CentralUrl is null)
        {
            throw new IOException(
                $"'{outPath}' already exists. Re-run with --force to overwrite it, or pass --out <path> to write elsewhere.");
        }

        var hostText = File.ReadAllText(hostPath);

        // How the host should launch the orchestrator, most specific wins:
        //  --dev-feed  : run from a local folder feed (same pattern as pack-local.ps1).
        //  --command   : an explicit command — the globally installed tool or the AOT binary.
        //  default     : `dotnet tool execute` from nuget.org, pinned to the version running now,
        //                so nothing needs to be installed and the entry stays offline-safe
        //                (the pinned version is already in the local NuGet cache).
        var (orchestratorCommand, orchestratorArgs) = parsed.DevFeed is not null
            ? ("dotnet", new[] { "tool", "execute", "McpOrchestrator", "--version", DevVersion, "--source", Path.GetFullPath(parsed.DevFeed), "--yes" })
            : parsed.OrchestratorCommand is not null
                ? (parsed.OrchestratorCommand, Array.Empty<string>())
                : ("dotnet", DefaultExecuteArgs());

        // Unless opted out, connect to each importable server once and derive its summary from
        // what the server itself declares (instructions, then tool names). A server that won't
        // start just keeps its TODO placeholder — probing never fails the init.
        IReadOnlyDictionary<string, string>? summaries = null;
        if (!parsed.NoSummarize && parsed.CentralUrl is null)
        {
            IReadOnlyList<CapabilityDescriptor> toProbe;
            try
            {
                toProbe = HostConfigImport.Parse(hostText).Capabilities;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"could not parse '{hostPath}' as JSON: {ex.Message}", ex);
            }

            if (toProbe.Count > 0)
            {
                Console.Error.WriteLine(
                    $"Connecting to {toProbe.Count} server(s) once to generate summaries (--no-summarize skips this)…");
                summaries = await SummaryGenerator.GenerateAsync(toProbe, CancellationToken.None);
            }
        }

        InitPlan plan;
        try
        {
            plan = Plan(hostText, Path.GetFileName(hostPath), outPath, orchestratorCommand, orchestratorArgs, summaries, parsed.CentralUrl);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"could not parse '{hostPath}' as JSON: {ex.Message}", ex);
        }

        // Joining a central catalog is valid even when the host config has no stdio servers to
        // lift — the point is only to add the orchestrator entry pointing at the URL.
        if (plan.ImportedNames.Count == 0 && parsed.CentralUrl is null)
        {
            throw new InvalidDataException(
                "no importable stdio servers found in the host config. " +
                "The orchestrator only relays stdio servers; remote (http/sse) entries are left untouched.");
        }

        if (parsed.PrintCentral)
        {
            // Only the catalog itself on stdout — pipe-able straight into whatever serves the
            // central config URL. No file writes, no host-config rewrite, no backup.
            Console.Out.Write(plan.OrchestratorConfigText);
            return 0;
        }

        if (parsed.DryRun)
        {
            Console.Out.WriteLine($"# DRY RUN — nothing written.\n");
            if (parsed.CentralUrl is null)
            {
                Console.Out.WriteLine($"# Would write {outPath}:");
                Console.Out.WriteLine(plan.OrchestratorConfigText);
                Console.Out.WriteLine();
            }
            Console.Out.WriteLine($"# Would rewrite {hostPath} to:");
            Console.Out.WriteLine(plan.NewHostConfigText);
            WriteSummary(plan, outPath, hostPath, backupPath: null, dryRun: true, parsed.CentralUrl);
            return 0;
        }

        // Order matters: back up + write the new catalog before touching the host config, so a
        // failure never leaves the host pointing at a file that does not exist. With --central-url
        // there is no catalog file — the host points at the URL instead.
        if (parsed.CentralUrl is null)
        {
            File.WriteAllText(outPath, plan.OrchestratorConfigText);
        }
        var backupPath = BackUp(hostPath);
        File.WriteAllText(hostPath, plan.NewHostConfigText);

        WriteSummary(plan, outPath, hostPath, backupPath, dryRun: false, parsed.CentralUrl);
        return 0;
    }

    /// <summary>
    /// Pure transform (no IO): given the host config text, produces the rewritten host config and the
    /// generated orchestrator catalog. Imports every stdio server as a capability; leaves remote
    /// (http/sse/url) servers in place; drops any pre-existing orchestrator entry and re-adds a fresh
    /// one pointing at <paramref name="outConfigPath"/>. Exposed for tests.
    /// </summary>
    internal static InitPlan Plan(
        string hostConfigText, string sourceLabel, string outConfigPath,
        string orchestratorCommand, IReadOnlyList<string>? orchestratorArgs = null,
        IReadOnlyDictionary<string, string>? summaries = null,
        string? centralUrl = null)
    {
        // Classify every server (shared with `profile --host-config`); init then mutates the same DOM.
        var import = HostConfigImport.Parse(hostConfigText);

        // Apply the auto-generated summaries (if the caller probed for any) over the TODO
        // placeholders, remembering which capabilities got one so they can be marked in the output.
        var autoSummarized = new List<string>();
        foreach (var capability in import.Capabilities)
        {
            if (summaries is not null && summaries.TryGetValue(capability.Name, out var summary))
            {
                capability.Summary = summary;
                autoSummarized.Add(capability.Name);
            }
        }

        // Drop the imported servers (replaced by the single orchestrator entry) and any pre-existing
        // orchestrator entry — re-adding a fresh one below keeps re-running init idempotent. Remote
        // servers are left in place so they keep working alongside the orchestrator.
        foreach (var key in import.Imported.Concat(import.OrchestratorKeys))
        {
            import.Container.Remove(key);
        }

        // The single entry the agent now sees. Only the "servers" shape conventionally carries "type".
        var orchestrator = new JsonObject();
        if (import.ContainerKey == "servers")
        {
            orchestrator["type"] = "stdio";
        }
        orchestrator["command"] = orchestratorCommand;
        var argsArray = new JsonArray();
        foreach (var a in orchestratorArgs ?? Array.Empty<string>())
        {
            // Cast to JsonNode so the JsonNode? overload is chosen, not the generic Add<T> (which
            // trips the trim/AOT analyzers).
            argsArray.Add((JsonNode)a);
        }
        orchestrator["args"] = argsArray;
        // Consumer of a central catalog (--central-url): point the orchestrator at the URL and
        // write no local catalog at all. Otherwise: the generated local file, as always.
        orchestrator["env"] = new JsonObject
        {
            [centralUrl is null ? "MCP_ORCHESTRATOR_CONFIG" : "MCP_ORCHESTRATOR_CONFIG_URL"] =
                centralUrl ?? outConfigPath,
        };
        import.Container["orchestrator"] = orchestrator;

        var newHostText = import.Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var configText = centralUrl is null
            ? BuildConfigText(import.Capabilities.ToList(), sourceLabel, autoSummarized)
            : string.Empty;

        return new InitPlan(newHostText, configText, import.Imported, import.SkippedRemote, import.ContainerKey, autoSummarized);
    }

    // The generated catalog is a human-edited file, so use the relaxed encoder: don't escape '<',
    // '>', '&', apostrophes, etc. into \uXXXX. Built once from the source-gen context's options
    // (so resolution stays AOT-safe) with only the encoder swapped.
    private static readonly JsonSerializerOptions WriteOptions =
        new(OrchestratorConfigWriteJsonContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Marker appended to summary lines the tool generated itself (the config reader skips // comments).</summary>
    internal const string AutoSummaryComment = "// auto-generated — refine for better routing";

    /// <summary>Serializes the generated catalog with a short header comment guiding the user through the summaries.</summary>
    private static string BuildConfigText(List<CapabilityDescriptor> capabilities, string sourceLabel, IReadOnlyList<string> autoSummarized)
    {
        var config = new OrchestratorConfig { Capabilities = capabilities };
        var typeInfo = (JsonTypeInfo<OrchestratorConfig>)WriteOptions.GetTypeInfo(typeof(OrchestratorConfig));
        var json = JsonSerializer.Serialize(config, typeInfo);
        if (autoSummarized.Count > 0)
        {
            json = TagAutoSummaries(json, capabilities, autoSummarized);
        }

        var header = new StringBuilder();
        header.AppendLine($"// Orchestrator catalog generated by `mcp-orchestrator init` from {sourceLabel}.");
        header.AppendLine("// Each entry is one downstream MCP server. For each capability, fill in a one-line");
        header.AppendLine("// \"summary\" — the agent reads it to choose which capability to call. An optional");
        header.AppendLine("// \"instructions\" when/how hint can be added by hand, but the summary usually suffices.");
        if (autoSummarized.Count > 0)
        {
            header.AppendLine("// Summaries marked \"auto-generated\" were derived from each server's own instructions");
            header.AppendLine("// or tool list — review them and refine any that read poorly.");
        }
        header.AppendLine("// Reference: https://github.com/Byggarepop/dotnet-mcp-orchestrator#configuration-reference");
        return header.ToString() + json + Environment.NewLine;
    }

    /// <summary>
    /// Appends <see cref="AutoSummaryComment"/> to the summary line of each auto-summarized
    /// capability. Each capability serializes exactly one "summary" property at the capability
    /// indent depth, in catalog order, so the Nth such line belongs to <paramref name="capabilities"/>[N].
    /// (The depth check keeps a user env var literally named "summary" from being miscounted.)
    /// </summary>
    private static string TagAutoSummaries(string json, List<CapabilityDescriptor> capabilities, IReadOnlyList<string> autoSummarized)
    {
        var auto = new HashSet<string>(autoSummarized, StringComparer.Ordinal);
        var lines = json.Split('\n');
        var next = 0;
        for (var i = 0; i < lines.Length && next < capabilities.Count; i++)
        {
            if (!lines[i].StartsWith("      \"summary\":", StringComparison.Ordinal))
            {
                continue;
            }

            if (auto.Contains(capabilities[next++].Name))
            {
                var body = lines[i].TrimEnd('\r');
                var newline = body.Length == lines[i].Length ? string.Empty : "\r";
                lines[i] = $"{body} {AutoSummaryComment}{newline}";
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>Copies the host config to a sibling <c>.bak</c> file, avoiding clobbering an earlier backup.</summary>
    private static string BackUp(string hostPath)
    {
        var backup = hostPath + ".bak";
        for (var n = 2; File.Exists(backup); n++)
        {
            backup = $"{hostPath}.bak.{n}";
        }
        File.Copy(hostPath, backup);
        return backup;
    }

    private static void WriteSummary(InitPlan plan, string outPath, string hostPath, string? backupPath, bool dryRun, string? centralUrl = null)
    {
        var w = Console.Out;
        w.WriteLine();
        if (centralUrl is null)
        {
            w.WriteLine($"Imported {plan.ImportedNames.Count} server(s) into the orchestrator catalog:");
        }
        else if (plan.ImportedNames.Count > 0)
        {
            w.WriteLine($"Removed {plan.ImportedNames.Count} stdio server(s) from the host config — make sure the central catalog covers them:");
        }
        foreach (var name in plan.ImportedNames)
        {
            w.WriteLine($"  • {name}");
        }

        if (plan.SkippedRemote.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"Left {plan.SkippedRemote.Count} non-stdio server(s) in {Path.GetFileName(hostPath)} (the orchestrator relays stdio only):");
            foreach (var name in plan.SkippedRemote)
            {
                w.WriteLine($"  • {name}");
            }
        }

        if (!dryRun)
        {
            w.WriteLine();
            if (centralUrl is null)
            {
                w.WriteLine($"Wrote catalog : {outPath}");
            }
            else
            {
                w.WriteLine($"Central config: {centralUrl}");
            }
            if (backupPath is not null)
            {
                w.WriteLine($"Backed up host: {backupPath}");
            }
            w.WriteLine($"Rewrote host  : {hostPath}");
        }

        w.WriteLine();
        w.WriteLine("Next:");
        if (centralUrl is not null)
        {
            w.WriteLine("  1. If the URL requires auth, set MCP_ORCHESTRATOR_CONFIG_AUTH as a user/machine");
            w.WriteLine("     environment variable — never commit the credential to the host config.");
            w.WriteLine("  2. Restart your MCP host so it picks up the orchestrator.");
        }
        else
        {
            w.WriteLine(plan.AutoSummarized.Count > 0
                ? $"  1. Review the auto-generated summaries in {Path.GetFileName(outPath)} and refine any that look off."
                : $"  1. Open {Path.GetFileName(outPath)} and add a one-line \"summary\" for each capability.");
            w.WriteLine("  2. Restart your MCP host so it picks up the orchestrator.");
        }
    }

    /// <summary>The result of <see cref="Plan"/>: the two file bodies plus what was imported/skipped/summarized.</summary>
    internal sealed record InitPlan(
        string NewHostConfigText,
        string OrchestratorConfigText,
        IReadOnlyList<string> ImportedNames,
        IReadOnlyList<string> SkippedRemote,
        string ContainerKey,
        IReadOnlyList<string> AutoSummarized);

    /// <summary>Parsed, validated command-line options for the subcommand.</summary>
    private sealed class ParsedArgs
    {
        public string? HostConfigPath { get; private init; }
        public string? OutPath { get; private init; }
        /// <summary>Explicit launch command from --command, or null for the install-free default.</summary>
        public string? OrchestratorCommand { get; private init; }
        public string? DevFeed { get; private init; }
        public bool Force { get; private init; }
        public bool DryRun { get; private init; }
        public bool NoSummarize { get; private init; }
        public bool PrintCentral { get; private init; }
        public string? CentralUrl { get; private init; }
        public bool ShowHelp { get; private init; }

        /// <summary>Returns a copy with the host config set to an auto-detected path. Used when no
        /// <c>&lt;host-config&gt;</c> argument was given on the command line.</summary>
        public ParsedArgs WithHostConfig(string path) => new()
        {
            HostConfigPath = path,
            OutPath = OutPath,
            OrchestratorCommand = OrchestratorCommand,
            DevFeed = DevFeed,
            Force = Force,
            DryRun = DryRun,
            NoSummarize = NoSummarize,
            PrintCentral = PrintCentral,
            CentralUrl = CentralUrl,
            ShowHelp = ShowHelp,
        };

        public static ParsedArgs Parse(string[] args)
        {
            string? host = null, outPath = null, command = null, devFeed = null, centralUrl = null;
            bool force = false, dryRun = false, noSummarize = false, printCentral = false, help = false;

            for (var i = 0; i < args.Length; i++)
            {
                var (flag, inlineValue) = SplitInline(args[i]);
                switch (flag)
                {
                    case "--out":
                        outPath = inlineValue ?? Next(args, ref i, flag);
                        break;
                    case "--command":
                        command = inlineValue ?? Next(args, ref i, flag);
                        break;
                    case "--dev-feed":
                        devFeed = inlineValue ?? Next(args, ref i, flag);
                        break;
                    case "--force":
                        force = true;
                        break;
                    case "--dry-run":
                        dryRun = true;
                        break;
                    case "--no-summarize":
                        noSummarize = true;
                        break;
                    case "--print-central":
                        printCentral = true;
                        break;
                    case "--central-url":
                        centralUrl = inlineValue ?? Next(args, ref i, flag);
                        break;
                    case "-h":
                    case "--help":
                        help = true;
                        break;
                    default:
                        if (flag.StartsWith('-'))
                        {
                            throw new ArgumentException($"unknown option '{args[i]}'.");
                        }
                        if (host is not null)
                        {
                            throw new ArgumentException($"unexpected extra argument '{args[i]}'.");
                        }
                        host = args[i];
                        break;
                }
            }

            // A null host is no longer an error here: RunAsync tries to auto-detect one from the
            // current directory before failing. The mutual-exclusivity check still applies.
            if (command is not null && devFeed is not null)
            {
                throw new ArgumentException("--command and --dev-feed are mutually exclusive (each sets how the host launches the orchestrator).");
            }

            if (printCentral && dryRun)
            {
                throw new ArgumentException("--print-central and --dry-run are mutually exclusive (each is its own print-only mode).");
            }

            if (centralUrl is not null)
            {
                if (printCentral)
                {
                    throw new ArgumentException(
                        "--central-url and --print-central are mutually exclusive: --print-central produces a central "
                        + "catalog (team lead); --central-url consumes one (developer joining).");
                }
                if (outPath is not null)
                {
                    throw new ArgumentException("--out has no effect with --central-url (no catalog file is written).");
                }

                // Same rule as the runtime: fail a typo here, not at the host's first start.
                Orchestration.Reload.CentralConfigOptions.RequireHttps(centralUrl, "--central-url");
            }

            return new ParsedArgs
            {
                HostConfigPath = host,
                OutPath = outPath,
                OrchestratorCommand = command,
                DevFeed = devFeed,
                Force = force,
                DryRun = dryRun,
                NoSummarize = noSummarize,
                PrintCentral = printCentral,
                CentralUrl = centralUrl,
                ShowHelp = help,
            };
        }

        private static (string Flag, string? InlineValue) SplitInline(string arg)
        {
            var eq = arg.IndexOf('=');
            return eq < 0 ? (arg, null) : (arg[..eq], arg[(eq + 1)..]);
        }

        private static string Next(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"option '{flag}' requires a value.");
            }
            return args[++i];
        }
    }

    private const string HelpText = """
        mcp-orchestrator init — adopt an existing MCP host config into the orchestrator

        USAGE
          mcp-orchestrator init [options]                  (auto-detect a host config in the cwd)
          mcp-orchestrator init <host-config> [options]

        WHAT IT DOES
          1. Reads your MCP host config (.mcp.json / .vscode/mcp.json / Cursor / Claude Desktop).
          2. Connects to each stdio server once and writes orchestrator.config.json next to the
             host config — one capability per server, its "summary" auto-generated from the
             server's own instructions or tool list (skip with --no-summarize).
          3. Backs up the host config (<name>.bak), then rewrites it to launch ONLY the
             orchestrator, pointed at the new catalog via MCP_ORCHESTRATOR_CONFIG.
          Remote (http/sse) servers can't be relayed over stdio; they're left in place untouched.

        AUTO-DETECT
          With no <host-config>, init looks in the current directory and adopts the first host config
          it finds, in this order: .mcp.json, .vscode/mcp.json, .cursor/mcp.json, mcp.json. So you can
          just cd into the project and run 'mcp-orchestrator init'.

        ARGUMENTS
          <host-config>          Path to the MCP host config to adopt. Optional — if omitted, init
                                 auto-detects one from the current directory (see AUTO-DETECT).

        OPTIONS
          --out <path>           Where to write the catalog. Default: orchestrator.config.json
                                 next to the host config.
          --command <cmd>        Command the host should launch for the orchestrator. Default:
                                 install-free 'dotnet tool execute McpOrchestrator --version
                                 <this version> --yes' (nuget.org, cached locally). Pass
                                 'mcp-orchestrator' if you installed the tool globally, or the
                                 absolute path to the AOT binary.
          --dev-feed <path>      Launch the orchestrator from a local folder feed instead, so the
                                 host always runs the latest local build (see pack-local.ps1):
                                 dotnet tool execute McpOrchestrator --version 9.9.9-dev
                                   --source <path> --yes. Mutually exclusive with --command.
          --force                Overwrite an existing catalog file.
          --dry-run              Print both files and the summary; write nothing.
          --print-central        Print ONLY the generated catalog to stdout and write nothing
                                 (no host-config rewrite, no backup) — pipe it into whatever
                                 serves your team's central config URL
                                 (MCP_ORCHESTRATOR_CONFIG_URL).
          --central-url <url>    Join a centrally served catalog: rewrite the host config so the
                                 orchestrator reads its catalog from <url>
                                 (MCP_ORCHESTRATOR_CONFIG_URL) instead of a local file. Writes no
                                 catalog, connects to nothing; stdio servers are still lifted out
                                 of the host config — the central catalog is assumed to cover
                                 them. HTTPS required (plain http only for localhost). If the URL
                                 needs auth, set MCP_ORCHESTRATOR_CONFIG_AUTH as an OS-level env
                                 var — don't put credentials in the host config.
          --no-summarize         Don't connect to the servers to auto-generate summaries; keep
                                 the TODO placeholders instead. Use when a server is slow or
                                 side-effectful to start. A server that fails to start is skipped
                                 silently either way (it keeps the TODO placeholder).
          -h, --help             Show this help.

        NEXT
          Review the auto-generated "summary" lines in the catalog — the agent reads them to choose
          which capability to call — refine any that look off, then restart your MCP host.
        """;
}

/// <summary>
/// Write-side source-gen context for the generated catalog: camelCase names, indented, and
/// null-omitting so the emitted file matches the documented config style. Kept separate from the
/// (read-only, case-insensitive) <see cref="OrchestratorConfigJsonContext"/> so neither path's
/// options surprise the other.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OrchestratorConfig))]
internal sealed partial class OrchestratorConfigWriteJsonContext : JsonSerializerContext;
