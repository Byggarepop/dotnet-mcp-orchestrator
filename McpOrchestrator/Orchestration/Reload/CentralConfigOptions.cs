namespace McpOrchestrator.Orchestration.Reload;

/// <summary>
/// Settings for centrally managed config mode, parsed from environment variables:
/// <c>MCP_ORCHESTRATOR_CONFIG_URL</c> (turns the mode on), <c>MCP_ORCHESTRATOR_CONFIG_AUTH</c>
/// (optional verbatim Authorization header value; never logged), and
/// <c>MCP_ORCHESTRATOR_CONFIG_POLL_SECONDS</c> (default 300, minimum 10). Source selection is
/// binary: when the URL is set, the local config path is ignored entirely — central and local
/// configs are never merged.
/// </summary>
internal sealed class CentralConfigOptions
{
    public const string UrlVariable = "MCP_ORCHESTRATOR_CONFIG_URL";
    public const string AuthVariable = "MCP_ORCHESTRATOR_CONFIG_AUTH";
    public const string PollSecondsVariable = "MCP_ORCHESTRATOR_CONFIG_POLL_SECONDS";

    public const int DefaultPollSeconds = 300;
    public const int MinimumPollSeconds = 10;

    // Internal (not private) so tests can construct options without mutating process env vars.
    internal CentralConfigOptions(Uri url, string? authorization, TimeSpan pollInterval, string? ignoredLocalConfigPath)
    {
        Url = url;
        Authorization = authorization;
        PollInterval = pollInterval;
        IgnoredLocalConfigPath = ignoredLocalConfigPath;
    }

    public Uri Url { get; }

    /// <summary>Verbatim Authorization header value (e.g. "Bearer eyJ…"), or null. Never log this.</summary>
    public string? Authorization { get; }

    public TimeSpan PollInterval { get; }

    /// <summary>The local <c>MCP_ORCHESTRATOR_CONFIG</c> path being ignored because the URL wins, if any.</summary>
    public string? IgnoredLocalConfigPath { get; }

    /// <summary>
    /// Reads the environment. Returns <c>null</c> when <c>MCP_ORCHESTRATOR_CONFIG_URL</c> is unset
    /// (local file mode, exactly as before).
    /// </summary>
    /// <exception cref="ArgumentException">The URL or poll interval is unusable — better to fail
    /// startup loudly than to silently run with a different config than intended.</exception>
    public static CentralConfigOptions? FromEnvironment()
    {
        var rawUrl = Environment.GetEnvironmentVariable(UrlVariable);
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url))
        {
            throw new ArgumentException($"{UrlVariable} is not a valid absolute URL: '{rawUrl}'.");
        }

        // HTTPS only — a shared catalog decides what processes every developer's machine launches.
        // Plain http is allowed solely for loopback, so local testing needs no certificate.
        var isHttps = string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && url.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
        {
            throw new ArgumentException(
                $"{UrlVariable} must use https (plain http is allowed only for localhost/127.0.0.1): '{rawUrl}'.");
        }

        var pollInterval = TimeSpan.FromSeconds(DefaultPollSeconds);
        var rawSeconds = Environment.GetEnvironmentVariable(PollSecondsVariable);
        if (!string.IsNullOrWhiteSpace(rawSeconds))
        {
            if (!int.TryParse(rawSeconds, out var seconds))
            {
                throw new ArgumentException($"{PollSecondsVariable} is not a number: '{rawSeconds}'.");
            }

            pollInterval = TimeSpan.FromSeconds(Math.Max(seconds, MinimumPollSeconds));
        }

        var auth = Environment.GetEnvironmentVariable(AuthVariable);
        var ignoredLocal = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG");

        return new CentralConfigOptions(
            url,
            string.IsNullOrWhiteSpace(auth) ? null : auth,
            pollInterval,
            string.IsNullOrWhiteSpace(ignoredLocal) ? null : ignoredLocal);
    }
}
