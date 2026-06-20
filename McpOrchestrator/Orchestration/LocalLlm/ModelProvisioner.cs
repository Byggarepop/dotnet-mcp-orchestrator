using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.LocalLlm;

/// <summary>
/// Ensures the GGUF model is present on disk, downloading it once on first use into the cache
/// directory. The download is written to a <c>.partial</c> file and atomically renamed on
/// success, so an interrupted download never leaves a half-written model in place.
/// </summary>
public sealed class ModelProvisioner
{
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
    /// Returns the local path to a ready-to-load model, downloading it if necessary.
    /// </summary>
    /// <exception cref="InvalidOperationException">A configured explicit <c>ModelPath</c> does not exist.</exception>
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

        _logger.LogInformation(
            "Downloading local LLM model from {Url} to {Target} (first run only)...",
            _options.ModelUrl, target);

        try
        {
            using (var response = await _http.GetAsync(
                _options.ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var destination = new FileStream(
                    partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(destination, cancellationToken);
                }
            }

            // Atomic publish: a reader either sees no file or the complete file.
            File.Move(partial, target, overwrite: true);
            _logger.LogInformation("Local LLM model ready at {Target}.", target);
            return target;
        }
        catch
        {
            TryDelete(partial);
            throw;
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
