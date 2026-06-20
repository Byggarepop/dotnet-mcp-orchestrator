namespace McpOrchestrator.Orchestration.LocalLlm;

/// <summary>
/// Configuration for the optional embedded local-LLM route planner. Bound from environment
/// variables so it can be enabled without code changes. The model is small and runs on CPU;
/// its weights are downloaded on first use (not shipped) into <see cref="CacheDirectory"/>.
/// </summary>
public sealed class LocalLlmOptions
{
    /// <summary>Whether the local-LLM planner is enabled. Default off (the heuristic is used).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Path to a local GGUF model. If set and present, it is used as-is (no download).
    /// Takes precedence over <see cref="ModelUrl"/>.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>URL the model is downloaded from on first use when <see cref="ModelPath"/> is not set.</summary>
    public string ModelUrl { get; set; } =
        "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf";

    /// <summary>File name the downloaded model is cached under.</summary>
    public string ModelFileName { get; set; } = "qwen2.5-0.5b-instruct-q4_k_m.gguf";

    /// <summary>Directory the downloaded model is cached in. Defaults to a per-user app-data folder.</summary>
    public string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "McpOrchestrator", "models");

    /// <summary>Context window in tokens. Small is fine — routing prompts are short.</summary>
    public uint ContextSize { get; set; } = 2048;

    /// <summary>CPU threads for inference. 0/null lets llama.cpp choose.</summary>
    public int? Threads { get; set; }

    /// <summary>Maximum tokens to generate per routing step (tool name / arguments JSON).</summary>
    public int MaxTokens { get; set; } = 256;

    /// <summary>
    /// Reads options from environment variables:
    /// <c>MCP_ORCHESTRATOR_PLANNER=llm</c> enables it; <c>MCP_ORCHESTRATOR_LLM_MODEL</c> (path),
    /// <c>MCP_ORCHESTRATOR_LLM_URL</c>, <c>MCP_ORCHESTRATOR_LLM_CACHE</c>,
    /// <c>MCP_ORCHESTRATOR_LLM_THREADS</c> override the defaults.
    /// </summary>
    public static LocalLlmOptions FromEnvironment()
    {
        var options = new LocalLlmOptions
        {
            Enabled = string.Equals(
                Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_PLANNER"), "llm",
                StringComparison.OrdinalIgnoreCase),
        };

        if (Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_LLM_MODEL") is { Length: > 0 } path)
        {
            options.ModelPath = path;
        }
        if (Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_LLM_URL") is { Length: > 0 } url)
        {
            options.ModelUrl = url;
        }
        if (Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_LLM_CACHE") is { Length: > 0 } cache)
        {
            options.CacheDirectory = cache;
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_LLM_THREADS"), out var threads)
            && threads > 0)
        {
            options.Threads = threads;
        }

        return options;
    }

    /// <summary>The resolved local file path the model should live at when downloaded.</summary>
    public string ResolvedModelPath =>
        !string.IsNullOrWhiteSpace(ModelPath) ? ModelPath! : Path.Combine(CacheDirectory, ModelFileName);
}
