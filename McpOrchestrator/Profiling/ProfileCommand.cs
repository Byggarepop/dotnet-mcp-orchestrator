namespace McpOrchestrator.Profiling;

/// <summary>
/// The <c>mcp-orchestrator profile</c> subcommand: measures the token economics of progressive
/// tool discovery. Two modes — static (<c>--config</c>) and trace (<c>--trace</c> + <c>--config</c>).
/// Reports to stdout (table or JSON); progress and errors go to stderr so stdout stays pipeable.
/// </summary>
/// <remarks>
/// Exit codes: <c>0</c> success · <c>1</c> usage/IO/config error · <c>2</c> assertion failed
/// (<c>--assert-favorable</c> on a session the orchestrator loses).
/// </remarks>
public static class ProfileCommand
{
    /// <summary>Entry point for the subcommand. <paramref name="args"/> excludes the leading "profile".</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        // The table uses box-drawing / arrows / ± — make sure a plain Windows console renders them.
        // Guarded: throws when stdout is redirected in some shells, and the (ASCII) JSON output and
        // the MCP server path are unaffected regardless.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
        catch { /* redirected or unsupported — fine. */ }

        ParsedArgs parsed;
        try
        {
            parsed = ParsedArgs.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"profile: {ex.Message}");
            Console.Error.WriteLine("Run 'mcp-orchestrator profile --help' for usage.");
            return 1;
        }

        if (parsed.ShowHelp)
        {
            Console.Out.WriteLine(HelpText);
            return 0;
        }

        try
        {
            parsed.Validate();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"profile: {ex.Message}");
            Console.Error.WriteLine("Run 'mcp-orchestrator profile --help' for usage.");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        ITokenCounter counter = new Cl100kTokenCounter();

        try
        {
            var report = await BuildReportAsync(parsed, counter, cts.Token);

            Console.Out.WriteLine(parsed.Json ? report.ToJson() : ProfileRenderer.Render(report));

            if (parsed.AssertFavorable && report.Summary is { OrchestratorFavorable: false })
            {
                Console.Error.WriteLine(
                    "profile: --assert-favorable failed — the orchestrator is NOT favorable for this session.");
                return 2;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("profile: cancelled.");
            return 1;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or FormatException)
        {
            Console.Error.WriteLine($"profile: {ex.Message}");
            return 1;
        }
    }

    private static async Task<ProfileReport> BuildReportAsync(
        ParsedArgs parsed, ITokenCounter counter, CancellationToken cancellationToken)
    {
        if (parsed.TracePath is not null)
        {
            Console.Error.WriteLine(
                $"Measuring {parsed.ConfigPath} (connecting to each server to size its manifest) " +
                $"and replaying {parsed.TracePath}…");
            return await TraceProfiler.RunAsync(parsed.TracePath, parsed.ConfigPath!, counter, cancellationToken);
        }

        Console.Error.WriteLine(
            $"Measuring {parsed.ConfigPath} (connecting to each server once to size its manifest)…");
        return await StaticProfiler.RunAsync(parsed.ConfigPath!, counter, cancellationToken);
    }

    /// <summary>Parsed, validated command-line options for the subcommand.</summary>
    private sealed class ParsedArgs
    {
        public string? ConfigPath { get; private init; }
        public string? TracePath { get; private init; }
        public bool Json { get; private init; }
        public bool AssertFavorable { get; private init; }
        public bool ShowHelp { get; private init; }

        public static ParsedArgs Parse(string[] args)
        {
            string? config = null, trace = null, format = "table", tokenizer = "cl100k_base";
            bool assertFavorable = false, help = false;

            for (var i = 0; i < args.Length; i++)
            {
                var (flag, inlineValue) = SplitInline(args[i]);

                switch (flag)
                {
                    case "--config":
                        config = inlineValue ?? Next(args, ref i, flag);
                        break;
                    case "--trace":
                        trace = inlineValue ?? Next(args, ref i, flag);
                        break;
                    case "--format":
                        format = (inlineValue ?? Next(args, ref i, flag)).ToLowerInvariant();
                        break;
                    case "--tokenizer":
                        tokenizer = (inlineValue ?? Next(args, ref i, flag)).ToLowerInvariant();
                        break;
                    case "--assert-favorable":
                        assertFavorable = true;
                        break;
                    case "-h":
                    case "--help":
                        help = true;
                        break;
                    default:
                        throw new ArgumentException($"unknown option '{args[i]}'.");
                }
            }

            if (format is not ("table" or "json"))
            {
                throw new ArgumentException($"--format must be 'table' or 'json', not '{format}'.");
            }

            if (tokenizer != "cl100k_base")
            {
                throw new ArgumentException(
                    $"--tokenizer '{tokenizer}' is not supported. The only local tokenizer is 'cl100k_base'.");
            }

            return new ParsedArgs
            {
                ConfigPath = config,
                TracePath = trace,
                Json = format == "json",
                AssertFavorable = assertFavorable,
                ShowHelp = help,
            };
        }

        public void Validate()
        {
            if (ConfigPath is null)
            {
                throw new ArgumentException(
                    TracePath is null
                        ? "a mode is required: --config <path> (static) or --trace <path> --config <path> (trace)."
                        : "--trace requires --config <path> too: manifest sizes are measured from the config.");
            }

            if (AssertFavorable && TracePath is null)
            {
                throw new ArgumentException("--assert-favorable applies to trace mode (it needs a session to judge).");
            }
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
        mcp-orchestrator profile — measure the token economics of progressive tool discovery

        USAGE
          mcp-orchestrator profile --config <path> [options]                          (static)
          mcp-orchestrator profile --trace <session.jsonl> --config <path> [options]  (trace)

        MODES
          static   Resting floor, naive baseline, and the best/worst envelope for a config.
                   Deterministic and CI-friendly. Connects to each server once to size its manifest.
          trace    Replays a recorded session into the realized per-turn curve: orchestrated active
                   vs. naive baseline, load events, never-loaded savings, and break-even.

        OPTIONS
          --config <path>        Orchestrator config to profile. Required in both modes.
          --trace <path>         Session trace (JSONL) to replay. Selects trace mode.
          --format <table|json>  Output format. Default: table. JSON is a superset of the table.
          --tokenizer <name>     Token encoding. Default and only: cl100k_base.
          --assert-favorable     (trace) Exit 2 if the orchestrator is NOT favorable for the session.
                                 Gate a PR on "orchestrator stays favorable for the canonical session".
          -h, --help             Show this help.

        GENERATING A TRACE
          Run the orchestrator with --trace-out <path> (or set MCP_ORCHESTRATOR_TRACE_OUT=<path>);
          it appends one line per discover/route interaction. Then replay that file with --trace.

        NOTES
          Counts use a local cl100k_base tokenizer (Claude/GPT-4-class), disclosed with a ±10%
          cross-model tolerance — an approximation, not exact per-model accounting.
        """;
}
