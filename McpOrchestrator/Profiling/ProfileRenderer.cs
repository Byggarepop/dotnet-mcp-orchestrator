using System.Globalization;
using System.Text;

namespace McpOrchestrator.Profiling;

/// <summary>
/// Renders a <see cref="ProfileReport"/> as the human-facing table. The JSON view
/// (<see cref="ProfileReport.ToJson"/>) is the machine superset; this is the at-a-glance read.
/// </summary>
public static class ProfileRenderer
{
    private const int MaxBaselineRows = 8;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Renders either mode by dispatching on <see cref="ProfileReport.Mode"/>.</summary>
    public static string Render(ProfileReport report) =>
        report.Mode == "trace" ? RenderTrace(report) : RenderStatic(report);

    // ----- static -----------------------------------------------------------------------------

    private static string RenderStatic(ProfileReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("  MCP Orchestrator — Token Profile (static)");
        sb.AppendLine($"  {Tokenizer(r)}");
        sb.AppendLine($"  servers: {r.Config.ServersConnected} connected · {r.Config.ToolsTotal} tools total");
        sb.AppendLine();

        sb.AppendLine("  RESTING STATE");
        sb.AppendLine($"    {"orchestrator system prompt",-34}{Num(r.RestingState.SystemPromptTokens, 8)}");
        sb.AppendLine($"    {$"meta-tools ({MetaToolsLabel(r)})",-34}{Num(r.RestingState.MetaToolsTokens, 8)}");
        sb.AppendLine("    " + new string('─', 42));
        sb.AppendLine($"    {"resting floor",-34}{Num(r.RestingState.FloorTokensPerTurn, 8)} tokens / turn");
        sb.AppendLine();

        sb.AppendLine("  NAIVE BASELINE  (all manifests loaded upfront, every turn)");
        sb.AppendLine($"    {"server",-22}{"tools",6}{"tokens",10}");
        sb.AppendLine("    " + new string('─', 38));
        foreach (var (label, tools, tokens) in BaselineRows(r.NaiveBaseline.ByServer))
        {
            sb.AppendLine($"    {label,-22}{tools,6}{Num(tokens, 10)}");
        }
        sb.AppendLine("    " + new string('─', 38));
        sb.AppendLine($"    {"naive total",-22}{r.Config.ToolsTotal,6}{Num(r.NaiveBaseline.TotalTokensPerTurn, 10)} tokens / turn");
        sb.AppendLine();

        var env = r.Envelope!;
        sb.AppendLine("  ENVELOPE  (over a session — static estimate)");
        sb.AppendLine($"    {"best case   (0 servers routed)",-34}{Num(env.BestCaseTokensPerTurn, 8)} / turn");
        sb.AppendLine($"    {$"worst case  (all {r.Config.ServersConnected} routed)",-34}{Num(env.WorstCaseTokensPerTurn, 8)} / turn");
        sb.AppendLine($"    {"naive (the thing you're beating)",-34}{Num(env.NaiveTokensPerTurn, 8)} / turn  ← flat, paid every turn");
        sb.AppendLine();

        sb.AppendLine($"  The orchestrator wins whenever a session touches fewer than {r.Config.ServersConnected}");
        sb.AppendLine("  servers before the routing overhead is repaid. Run with --trace");
        sb.AppendLine("  for the realized curve on an actual session.");

        AppendUnreachable(sb, r);
        return sb.ToString();
    }

    // ----- trace ------------------------------------------------------------------------------

    private static string RenderTrace(ProfileReport r)
    {
        var t = r.Trace!;
        var s = r.Summary!;
        var toolsByServer = r.NaiveBaseline.ByServer.ToDictionary(x => x.Server, x => x.Tools, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("  MCP Orchestrator — Token Profile (trace)");
        sb.AppendLine(
            $"  session: {t.Turns.Count} turns · {r.Config.ServersTouched} of {r.Config.ServersConnected} servers touched · tokenizer: {r.Tokenizer.Name}");
        sb.AppendLine();

        sb.AppendLine("  PER-TURN  (orchestrated actual vs. naive baseline)");
        sb.AppendLine($"    {"turn",4}  {"loaded this turn",-22}{"active",9}{"naive",9}{"saved",9}");
        sb.AppendLine("    " + new string('─', 54));
        foreach (var row in t.Turns)
        {
            sb.AppendLine(
                $"    {row.Turn,4}  {LoadedCell(row.Loaded, toolsByServer),-22}{Num(row.ActiveTokens, 9)}{Num(row.NaiveTokens, 9)}{Num(row.SavedTokens, 9)}");
        }
        sb.AppendLine("    " + new string('─', 54));
        sb.AppendLine($"    {"cumulative orchestrated",-27}{Num(s.CumulativeOrchestratedTokens, 12)}");
        sb.AppendLine($"    {"cumulative naive",-27}{Num(s.CumulativeNaiveTokens, 12)}");
        sb.AppendLine("    " + new string('─', 39));
        var pct = s.NetSavedPct.ToString("0.0", Inv);
        if (s.OrchestratorFavorable)
        {
            sb.AppendLine($"    {"net saved",-27}{Num(s.NetSavedTokens, 12)} tokens  ({pct}%)");
        }
        else
        {
            // Negative net: the orchestrated path cost MORE. State it plainly.
            sb.AppendLine($"    {"net cost",-27}{Num(-s.NetSavedTokens, 12)} tokens  ({pct}%)");
        }
        sb.AppendLine();

        sb.AppendLine("  LOAD EVENTS");
        if (t.LoadEvents.Count == 0)
        {
            sb.AppendLine("    (none — no manifest was ever loaded this session)");
        }
        foreach (var e in t.LoadEvents)
        {
            var trigger = e.Tool is null ? $"{e.Trigger}()" : $"{e.Trigger}() → \"{e.Tool}\"";
            sb.AppendLine($"    turn {e.Turn,-3} {e.Server,-12} triggered by {trigger}");
        }
        sb.AppendLine(
            $"    {t.NeverLoaded.Servers} {Plural(t.NeverLoaded.Servers, "server")} never loaded — " +
            $"{Num(t.NeverLoaded.UnpaidManifestTokens, 0).Trim()} tokens of manifest never paid");
        sb.AppendLine();

        sb.AppendLine("  BREAK-EVEN");
        if (s.OrchestratorFavorable)
        {
            sb.AppendLine($"    orchestrator overhead repaid at turn {s.BreakEvenTurn}");
            sb.AppendLine(
                $"    (resting {Num(r.RestingState.FloorTokensPerTurn, 0).Trim()} < naive {Num(r.NaiveBaseline.TotalTokensPerTurn, 0).Trim()}; net positive across the");
            sb.AppendLine(
                $"     session because it touches only {r.Config.ServersTouched} of {r.Config.ServersConnected} servers)");
        }
        else
        {
            var deficit = Num(-s.NetSavedTokens, 0).Trim();
            sb.AppendLine($"    overhead never repaid — naive would have been {deficit} tokens cheaper");
            sb.AppendLine("    over this session; orchestrator is the wrong choice for this workload");
        }

        AppendUnreachable(sb, r);
        return sb.ToString();
    }

    // ----- helpers ----------------------------------------------------------------------------

    private static string Tokenizer(ProfileReport r) =>
        $"tokenizer: {r.Tokenizer.Name} ({r.Tokenizer.Approximates} approx, ±{r.Tokenizer.CrossModelTolerancePct}% cross-model)";

    // The orchestrator's three meta-tools, the always-loaded surface (cf. list_capabilities /
    // discover_tools / route).
    private static string MetaToolsLabel(ProfileReport r) => "list/discover/route";

    private static string LoadedCell(IReadOnlyList<string> loaded, IReadOnlyDictionary<string, int> toolsByServer)
    {
        if (loaded.Count == 0)
        {
            return "—";
        }

        return string.Join(", ", loaded.Select(name =>
            toolsByServer.TryGetValue(name, out var tools)
                ? $"+{name} ({tools} {Plural(tools, "tool")})"
                : $"+{name} (? tools)"));
    }

    private static IEnumerable<(string Label, string Tools, int Tokens)> BaselineRows(IReadOnlyList<ServerTokens> servers)
    {
        if (servers.Count <= MaxBaselineRows)
        {
            foreach (var s in servers)
            {
                yield return (s.Server, s.Tools.ToString(Inv), s.Tokens);
            }
            yield break;
        }

        // Show the heaviest few, then aggregate the long tail — matching the spec's "… N more" row.
        var head = servers.Take(MaxBaselineRows - 1).ToList();
        foreach (var s in head)
        {
            yield return (s.Server, s.Tools.ToString(Inv), s.Tokens);
        }

        var tail = servers.Skip(MaxBaselineRows - 1).ToList();
        yield return ($"… {tail.Count} more", tail.Sum(x => x.Tools).ToString(Inv), tail.Sum(x => x.Tokens));
    }

    private static void AppendUnreachable(StringBuilder sb, ProfileReport r)
    {
        if (r.UnreachableServers is { Count: > 0 } unreachable)
        {
            sb.AppendLine();
            sb.AppendLine($"  ⚠ {unreachable.Count} server(s) could not be sized and are excluded from the");
            sb.AppendLine($"    totals above: {string.Join(", ", unreachable)}");
        }
    }

    private static string Num(long value, int width) =>
        value.ToString("N0", Inv).PadLeft(width);

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";
}
