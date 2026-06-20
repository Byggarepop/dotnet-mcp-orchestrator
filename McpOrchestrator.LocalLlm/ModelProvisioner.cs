using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.LocalLlm;

/// <summary>
/// Ensures the GGUF model is present on disk, downloading it once on first use into the cache
/// directory. Each attempt downloads to a <c>.partial</c> file, validates it (expected size, plus
/// the GGUF magic bytes — so a truncated body or an HTML error page is rejected rather than saved
/// as the model), and only then atomically renames it into place. Transient failures are retried.
/// </summary>
public sealed class ModelProvisioner
{
    private const int MaxAttempts = 3;

    /// <summary>The four bytes every GGUF file starts with.</summary>
    private static readonly byte[] GgufMagic = "GGUF"u8.ToArray();

    private readonly LocalLlmOptions _options;
    private readonly ILogger _logger;
    private readonly HttpClient _http;

    /// <summary>Creates a provisioner for the given options.</summary>
    public ModelProvisioner(LocalLlmOptions options, ILogger logger, HttpClient? http = null)
    {
        _options = options;
        _logger = logger;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>
    /// Returns the local path to a ready-to-load model, downloading (and validating) it if necessary.
    /// </summary>
    /// <exception cref="InvalidOperationException">An explicit <c>ModelPath</c> is missing, or every download attempt failed.</exception>
    public async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        // An explicitly configured model path must already exist — never download over it.
        if (!string.IsNullOrWhiteSpace(_options.ModelPath))
        {
            if (!File.Exists(_options.ModelPath))
            {
                throw new InvalidOperationException(
                    $"Configured local LLM model not found at '{_options.ModelPath}'.");
            }
            return _options.ModelPath!;
        }

        var target = _options.ResolvedModelPath;
        if (File.Exists(target))
        {
            return target;
        }

        Directory.CreateDirectory(_options.CacheDirectory);
        var partial = target + ".partial";

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            _logger.LogInformation(
                "Downloading local LLM model from {Url} to {Target} (attempt {Attempt}/{Max}, first run only)...",
                _options.ModelUrl, target, attempt, MaxAttempts);
            try
            {
                await DownloadAsync(partial, cancellationToken);
                ValidateGguf(partial);

                // Atomic publish: a reader either sees no file or the complete, validated file.
                File.Move(partial, target, overwrite: true);
                _logger.LogInformation("Local LLM model ready at {Target}.", target);
                return target;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                TryDelete(partial);
                _logger.LogWarning(ex, "Model download attempt {Attempt}/{Max} failed.", attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
                }
            }
        }

        throw new InvalidOperationException(
            $"Failed to download a valid model from '{_options.ModelUrl}' after {MaxAttempts} attempts.", lastError);
    }

    /// <summary>Downloads the URL into <paramref name="partial"/>, checking the byte count against Content-Length.</summary>
    private async Task DownloadAsync(string partial, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            _options.ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var expectedLength = response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        if (expectedLength is { } expected)
        {
            var actual = new FileInfo(partial).Length;
            if (actual != expected)
            {
                throw new IOException($"Truncated download: got {actual} bytes, expected {expected}.");
            }
        }
    }

    /// <summary>Throws if the file does not begin with the GGUF magic bytes.</summary>
    private static void ValidateGguf(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        if (stream.Read(head) < head.Length || !head.SequenceEqual(GgufMagic))
        {
            throw new InvalidDataException(
                "Downloaded file is not a GGUF model (wrong magic bytes) — the URL may be wrong or returned an error page.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort — a stray .partial is harmless and will be overwritten next time.
        }
    }
}
