using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Reload;

/// <summary>
/// The central-mode trigger: polls the config URL on a jittered interval and signals the reload
/// pipeline only when a <em>new, valid</em> payload arrived. Conditional requests
/// (If-None-Match / If-Modified-Since) make an unchanged config cost a 304 that short-circuits
/// before any parse or diff work. A fetched payload is validated (central placeholder policy,
/// size, content type) and atomically written to the cache <em>before</em> the signal fires — the
/// pipeline's load stage then reads that cache, the same path used for offline startup.
/// Consecutive failures back off exponentially (capped at 15 minutes) and reset on success.
/// </summary>
internal sealed class HttpConfigPollTrigger : IConfigReloadTrigger
{
    internal enum PollOutcome
    {
        /// <summary>New valid payload cached and signalled downstream.</summary>
        Applied,
        /// <summary>304 — the server's copy is what we already have; nothing ran.</summary>
        Unchanged,
        /// <summary>Fetch or validation failed; the running config stays (last-known-good).</summary>
        Failed,
    }

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    private const long MaxPayloadBytes = 1024 * 1024;

    private readonly CentralConfigOptions _options;
    private readonly CentralConfigCache _cache;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly Random _random = new();
    private readonly CancellationTokenSource _stop = new();

    private string? _etag;
    private string? _lastModified;
    private int _consecutiveFailures;
    private Func<Task>? _onChanged;

    public HttpConfigPollTrigger(
        CentralConfigOptions options,
        CentralConfigCache cache,
        ILogger logger,
        HttpMessageHandler? handler = null)
    {
        _options = options;
        _cache = cache;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Seeds the conditional-request validators from an existing cache, so the first
    /// poll after a restart can already come back 304.</summary>
    public void Prime(string? etag, string? lastModified)
    {
        _etag = etag;
        _lastModified = lastModified;
    }

    /// <inheritdoc />
    public void Start(Func<Task> onChanged)
    {
        _onChanged = onChanged;
        _ = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(NextDelay(_options.PollInterval, _consecutiveFailures, _random), cancellationToken);
                await PollOnceAsync(_onChanged, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // PollOnceAsync handles its own failures; anything reaching here is a bug — log
                // it and keep the loop alive rather than silently stopping all future reloads.
                _logger.LogError(ex, "Central config poll loop error; continuing.");
            }
        }
    }

    /// <summary>
    /// One poll: conditional GET → (on 200) validate → cache atomically → signal. Exposed to run
    /// the startup fetch and to drive tests without timers.
    /// </summary>
    internal async Task<PollOutcome> PollOnceAsync(Func<Task>? onChanged, CancellationToken cancellationToken)
    {
        string payload, newETag = string.Empty;
        string? etag, lastModified;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.Url);
            if (_etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            }
            else if (_lastModified is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", _lastModified);
            }
            if (_options.Authorization is not null)
            {
                // Verbatim header value; deliberately never logged anywhere below.
                request.Headers.TryAddWithoutValidation("Authorization", _options.Authorization);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                // Nothing changed: short-circuit before any parse/diff work.
                _consecutiveFailures = 0;
                return PollOutcome.Unchanged;
            }

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return Failed(
                    $"the server answered HTTP {(int)response.StatusCode} — authorization failed. "
                    + (_options.Authorization is null
                        ? $"No {CentralConfigOptions.AuthVariable} is set; the endpoint appears to require one."
                        : $"Check that {CentralConfigOptions.AuthVariable} holds a currently valid credential."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed($"the server answered HTTP {(int)response.StatusCode}.");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            {
                return Failed(
                    $"the response content type is '{mediaType}', not a config — the URL likely points at "
                    + "an HTML page (e.g. a login or repository page instead of the raw file).");
            }

            if (response.Content.Headers.ContentLength is > MaxPayloadBytes)
            {
                return Failed($"the payload is {response.Content.Headers.ContentLength} bytes; the limit is {MaxPayloadBytes} (1 MB).");
            }

            payload = await ReadBoundedAsync(response, cancellationToken);
            etag = response.Headers.ETag?.Tag;
            lastModified = response.Content.Headers.LastModified?.UtcDateTime.ToString("R");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return Failed($"the request failed: {ex.Message}");
        }
        catch (PayloadTooLargeException)
        {
            return Failed($"the payload exceeds the {MaxPayloadBytes} byte (1 MB) limit.");
        }

        // Validate before caching: only a payload that passed the same strict (and central-policy)
        // validation the pipeline applies may ever become "last known good".
        var parsed = CapabilityCatalog.TryParseForReload(
            payload, _options.Url.ToString(),
            builtinPlaceholders: EmptyPlaceholders, forbidLocalPlaceholders: true, _logger);
        if (parsed is null)
        {
            _consecutiveFailures++;
            return PollOutcome.Failed; // Rejection details already logged by the validator.
        }

        _cache.Write(payload, new CentralCacheMeta(_options.Url.ToString(), etag, lastModified, DateTimeOffset.UtcNow));
        _etag = etag;
        _lastModified = lastModified;
        _consecutiveFailures = 0;

        if (onChanged is not null)
        {
            await onChanged();
        }

        return PollOutcome.Applied;

        PollOutcome Failed(string reason)
        {
            _consecutiveFailures++;
            _logger.LogError(
                "Central config poll of {Url} failed ({Failures} consecutive): {Reason} Keeping the current config.",
                _options.Url, _consecutiveFailures, reason);
            return PollOutcome.Failed;
        }
    }

    /// <summary>
    /// The delay before the next poll: the configured interval, doubled per consecutive failure up
    /// to a 15-minute cap (never below the configured interval), with ±10% jitter so a whole team
    /// doesn't hit the server in lockstep. Pure — backoff is verified in tests without any clock.
    /// </summary>
    internal static TimeSpan NextDelay(TimeSpan baseInterval, int consecutiveFailures, Random random)
    {
        var delay = baseInterval;
        if (consecutiveFailures > 0)
        {
            var cap = MaxBackoff > baseInterval ? MaxBackoff : baseInterval;
            var backedOff = baseInterval.Ticks * Math.Pow(2, Math.Min(consecutiveFailures, 20));
            delay = TimeSpan.FromTicks((long)Math.Min(backedOff, cap.Ticks));
        }

        var jitter = 0.9 + (random.NextDouble() * 0.2);
        return TimeSpan.FromTicks((long)(delay.Ticks * jitter));
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Content-Length can lie or be absent — enforce the cap while reading.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxPayloadBytes)
            {
                throw new PayloadTooLargeException();
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyPlaceholders =
        new Dictionary<string, string>();

    private sealed class PayloadTooLargeException : Exception;

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _http.Dispose();
    }
}
