using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Reload;

/// <summary>
/// Something to run after a config change has been applied — the reload pipeline's last stage.
/// Implementations never throw: a failing hook is logged and the reload stays applied.
/// </summary>
internal interface IConfigChangeHook
{
    Task RunAsync(ConfigChangeEvent change, CancellationToken cancellationToken);
}

/// <summary>
/// What one applied change did: where it came from (the config path/URL, or the skill source ids
/// for a skill refresh), the capability diff (with the skill diff riding on it), and the trigger
/// (<c>config-change</c>, <c>directory-change</c>, <c>scheduled-refresh</c>).
/// </summary>
internal sealed record ConfigChangeEvent(string Source, ReloadDiff Diff, string Trigger);

/// <summary>
/// The JSON handed to the hook script (on stdin and in <c>MCP_ORCHESTRATOR_CHANGE_JSON</c>).
/// Names are the diff categories as logged: added / removed / restarted / updated / unchanged.
/// </summary>
internal sealed record ConfigChangePayload(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("changedAtUtc")] DateTimeOffset ChangedAtUtc,
    [property: JsonPropertyName("added")] IReadOnlyList<string> Added,
    [property: JsonPropertyName("removed")] IReadOnlyList<string> Removed,
    [property: JsonPropertyName("restarted")] IReadOnlyList<string> Restarted,
    [property: JsonPropertyName("updated")] IReadOnlyList<string> Updated,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("skillsAdded")] IReadOnlyList<string> SkillsAdded,
    [property: JsonPropertyName("skillsRemoved")] IReadOnlyList<string> SkillsRemoved,
    [property: JsonPropertyName("skillsUpdated")] IReadOnlyList<string> SkillsUpdated);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ConfigChangePayload))]
internal sealed partial class ConfigChangeJsonContext : JsonSerializerContext;

/// <summary>
/// Runs a user script after every applied config reload (file edit or central-URL change) and
/// after a skill refresh (git/http poll or directory event) that changed the served skills. The
/// script path comes from <c>--on-config-change &lt;path&gt;</c> or
/// <c>MCP_ORCHESTRATOR_ON_CONFIG_CHANGE</c>; the interpreter is picked by extension
/// (<c>.ps1</c> → pwsh/powershell, <c>.py</c> → python, <c>.sh</c> → bash, <c>.cmd</c>/<c>.bat</c>
/// → cmd, anything else runs directly). The script receives the diff as environment variables
/// (<c>MCP_ORCHESTRATOR_CHANGE_SOURCE</c>, <c>…_ADDED</c>, <c>…_REMOVED</c>, <c>…_RESTARTED</c>,
/// <c>…_UPDATED</c> — semicolon-separated capability names — <c>…_SKILLS_ADDED</c>,
/// <c>…_SKILLS_REMOVED</c>, <c>…_SKILLS_UPDATED</c> — skill names — <c>…_TRIGGER</c>, and
/// <c>…_JSON</c>) and the same JSON
/// on stdin. Its working directory is the script's folder; stdout/stderr go to the orchestrator log
/// (never to the MCP stdout). Runs are serialized and bounded by a timeout
/// (<c>MCP_ORCHESTRATOR_ON_CONFIG_CHANGE_TIMEOUT_SECONDS</c>, default 120). Not run at startup —
/// only when a change is applied during the session.
/// </summary>
internal sealed class ScriptConfigChangeHook : IConfigChangeHook
{
    public const string ArgumentName = "--on-config-change";
    public const string EnvironmentVariable = "MCP_ORCHESTRATOR_ON_CONFIG_CHANGE";
    public const string TimeoutVariable = "MCP_ORCHESTRATOR_ON_CONFIG_CHANGE_TIMEOUT_SECONDS";
    public const int DefaultTimeoutSeconds = 120;

    private readonly string _scriptPath;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ScriptConfigChangeHook(string scriptPath, TimeSpan timeout, ILogger logger)
    {
        _scriptPath = Path.GetFullPath(scriptPath);
        _timeout = timeout;
        _logger = logger;
    }

    public string ScriptPath => _scriptPath;

    /// <summary>
    /// The configured script path: <c>--on-config-change &lt;path&gt;</c> (or
    /// <c>--on-config-change=&lt;path&gt;</c>) on the command line, else the
    /// <c>MCP_ORCHESTRATOR_ON_CONFIG_CHANGE</c> environment variable. <c>null</c> when neither is set.
    /// </summary>
    internal static string? ResolveScriptPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == ArgumentName)
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }
            if (args[i].StartsWith(ArgumentName + "=", StringComparison.Ordinal))
            {
                return args[i][(ArgumentName.Length + 1)..];
            }
        }

        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    /// <summary>The run timeout from <c>MCP_ORCHESTRATOR_ON_CONFIG_CHANGE_TIMEOUT_SECONDS</c>, else the default.</summary>
    internal static TimeSpan ResolveTimeout()
    {
        var raw = Environment.GetEnvironmentVariable(TimeoutVariable);
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    }

    /// <summary>
    /// Builds the hook from the command line and environment, or returns <c>null</c> when no script
    /// is configured. A missing script file is reported but the hook is still returned: the error
    /// repeats on every change, which is more useful than a silently inert hook.
    /// </summary>
    internal static ScriptConfigChangeHook? FromArguments(string[] args, ILogger logger)
    {
        var path = ResolveScriptPath(args);
        if (path is null)
        {
            return null;
        }

        var hook = new ScriptConfigChangeHook(path, ResolveTimeout(), logger);
        if (!File.Exists(hook.ScriptPath))
        {
            logger.LogWarning(
                "Config change hook script not found: {Script} ({Argument} / {Variable}). It is retried "
                + "on each config change; until the file exists every change logs an error.",
                hook.ScriptPath, ArgumentName, EnvironmentVariable);
        }

        return hook;
    }

    public async Task RunAsync(ConfigChangeEvent change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_scriptPath))
            {
                _logger.LogError("Config change hook skipped: script not found at {Script}.", _scriptPath);
                return;
            }

            var payload = ToPayload(change);
            var json = JsonSerializer.Serialize(payload, ConfigChangeJsonContext.Default.ConfigChangePayload);
            var startInfo = BuildStartInfo(_scriptPath, payload, json);

            _logger.LogInformation(
                "Config change hook starting: {Script} (trigger={Trigger}; capabilities: {Added} added, {Removed} removed, "
                + "{Restarted} restarted, {Updated} updated; skills: {SkillsAdded} added, {SkillsRemoved} removed, "
                + "{SkillsUpdated} updated).",
                _scriptPath, payload.Trigger, payload.Added.Count, payload.Removed.Count, payload.Restarted.Count,
                payload.Updated.Count, payload.SkillsAdded.Count, payload.SkillsRemoved.Count, payload.SkillsUpdated.Count);

            var stopwatch = Stopwatch.StartNew();
            using var process = StartWithFallback(startInfo);
            if (process is null)
            {
                return;
            }

            // Feed the payload and close stdin so a script that reads it to EOF (or inherits the
            // handle) never waits on us; mirror both output streams into the log.
            var stdoutTask = PumpAsync(process.StandardOutput, LogLevel.Information);
            var stderrTask = PumpAsync(process.StandardError, LogLevel.Warning);
            try
            {
                await process.StandardInput.WriteAsync(json);
            }
            catch (IOException)
            {
                // The script exited without reading stdin; nothing to do.
            }
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                _logger.LogError(
                    "Config change hook timed out after {Seconds}s and was killed: {Script} (raise {Variable}).",
                    (int)_timeout.TotalSeconds, _scriptPath, TimeoutVariable);
                return;
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            if (process.ExitCode == 0)
            {
                _logger.LogInformation(
                    "Config change hook finished in {Elapsed}ms: {Script}.", stopwatch.ElapsedMilliseconds, _scriptPath);
            }
            else
            {
                _logger.LogError(
                    "Config change hook exited with code {ExitCode} after {Elapsed}ms: {Script}.",
                    process.ExitCode, stopwatch.ElapsedMilliseconds, _scriptPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Config change hook failed: {Script}.", _scriptPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static ConfigChangePayload ToPayload(ConfigChangeEvent change)
    {
        var skills = change.Diff.Skills ?? Skills.SkillDiff.Empty;
        return new(
            change.Source,
            change.Trigger,
            DateTimeOffset.UtcNow,
            change.Diff.Added,
            change.Diff.Removed,
            change.Diff.Restarted,
            change.Diff.MetadataUpdated,
            change.Diff.Unchanged,
            skills.Added,
            skills.Removed,
            skills.Updated);
    }

    /// <summary>
    /// The launch for <paramref name="scriptPath"/>: interpreter by extension, the script's folder
    /// as working directory, all three streams redirected, and the change exposed as environment
    /// variables. A <c>.ps1</c> is launched through pwsh; on Windows a missing pwsh falls back to
    /// Windows PowerShell at start time (see <see cref="StartWithFallback"/>).
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string scriptPath, ConfigChangePayload payload, string json)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        switch (Path.GetExtension(scriptPath).ToLowerInvariant())
        {
            case ".ps1":
                startInfo.FileName = "pwsh";
                foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
                {
                    startInfo.ArgumentList.Add(a);
                }
                break;
            case ".py":
                startInfo.FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
                startInfo.ArgumentList.Add(scriptPath);
                break;
            case ".sh":
                startInfo.FileName = "bash";
                startInfo.ArgumentList.Add(scriptPath);
                break;
            case ".cmd":
            case ".bat":
                startInfo.FileName = "cmd";
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(scriptPath);
                break;
            default:
                startInfo.FileName = scriptPath;
                break;
        }

        startInfo.Environment["MCP_ORCHESTRATOR_CHANGE_SOURCE"] = payload.Source;
        startInfo.Environment["MCP_ORCHESTRATOR_CHANGE_TRIGGER"] = payload.Trigger;
        startInfo.Environment["MCP_ORCHESTRATOR_CHANGE_ADDED"] = string.Join(';', payload.Added);
        startInfo.Environment["MCP_ORCHESTRATOR_CHANGE_REMOVED"] = string.Join(';', payload.Removed);
        startInfo.Environment["MCP_ORCHESTRATOR_CHANGE_RESTARTED"] = string.Join(';', payload.Restarted);
        startInfo.Environment["MCP_ORCHESTRATOR_CHANGE_UPDATED"] = string.Join(';', payload.Updated);
        startInfo.Environment["MCP_ORCHESTRATOR_CHANGE_SKILLS_ADDED"] = string.Join(';', payload.SkillsAdded);
        startInfo.Environment["MCP_ORCHESTRATOR_CHANGE_SKILLS_REMOVED"] = string.Join(';', payload.SkillsRemoved);
        startInfo.Environment["MCP_ORCHESTRATOR_CHANGE_SKILLS_UPDATED"] = string.Join(';', payload.SkillsUpdated);
        startInfo.Environment["MCP_ORCHESTRATOR_CHANGE_JSON"] = json;
        return startInfo;
    }

    private Process? StartWithFallback(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex)
            when (startInfo.FileName == "pwsh" && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogDebug(ex, "pwsh not found; falling back to Windows PowerShell.");
            startInfo.FileName = "powershell";
            try
            {
                return Process.Start(startInfo);
            }
            catch (System.ComponentModel.Win32Exception inner)
            {
                _logger.LogError(inner, "Config change hook could not start: neither pwsh nor powershell is on PATH.");
                return null;
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogError(ex, "Config change hook could not start {FileName} for {Script}.", startInfo.FileName, _scriptPath);
            return null;
        }
    }

    private async Task PumpAsync(StreamReader reader, LogLevel level)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 0)
            {
                _logger.Log(level, "[hook] {Line}", line);
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
    }
}
