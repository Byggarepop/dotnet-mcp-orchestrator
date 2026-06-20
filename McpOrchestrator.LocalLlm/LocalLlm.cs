using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.LocalLlm;

/// <summary>
/// A thin wrapper around an embedded llama.cpp model (via LLamaSharp) that runs short,
/// grammar-constrained completions for routing. The model is loaded lazily on first use
/// (downloading it if needed) and inference is serialized — a single llama context is not
/// safe for concurrent use.
/// </summary>
public sealed class LocalLlm : IConstrainedCompleter, IAsyncDisposable
{
    private readonly LocalLlmOptions _options;
    private readonly ModelProvisioner _provisioner;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private bool _initialized;

    /// <summary>Creates the wrapper. No model is loaded until the first <see cref="CompleteAsync"/>.</summary>
    public LocalLlm(LocalLlmOptions options, ModelProvisioner provisioner, ILogger logger)
    {
        _options = options;
        _provisioner = provisioner;
        _logger = logger;
    }

    /// <summary>
    /// Runs one completion with a deterministic (temperature 0) sampler constrained by
    /// <paramref name="gbnf"/>. Returns the generated text. Calls are serialized.
    /// </summary>
    public async Task<string> CompleteAsync(
        string systemMessage, string userPrompt, string gbnf, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            var pipeline = new DefaultSamplingPipeline
            {
                Temperature = 0f,
                Grammar = new Grammar(gbnf, GbnfGrammar.Root),
            };

            var inferenceParams = new InferenceParams
            {
                MaxTokens = _options.MaxTokens,
                SamplingPipeline = pipeline,
            };

            // SystemMessage is fixed at executor construction; fold the per-call instruction into
            // the user turn so each routing step gets its own guidance.
            var prompt = $"{systemMessage}\n\n{userPrompt}";

            var sb = new StringBuilder();
            await foreach (var token in _executor!.InferAsync(prompt, inferenceParams, cancellationToken))
            {
                sb.Append(token);
            }

            return sb.ToString().Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Loads the model and builds the executor once. Must be called under <see cref="_gate"/>.</summary>
    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var modelPath = await _provisioner.EnsureModelAsync(cancellationToken);

        var parameters = new ModelParams(modelPath)
        {
            ContextSize = _options.ContextSize,
            GpuLayerCount = 0,
            Threads = _options.Threads,
        };

        _logger.LogInformation("Loading local LLM model from {Path}...", modelPath);
        _weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken);
        _executor = new StatelessExecutor(_weights, parameters)
        {
            // Wrap the prompt with the model's own chat template (Qwen ships one in the GGUF).
            ApplyTemplate = true,
            SystemMessage = "You are a precise routing assistant. Follow the instruction exactly " +
                            "and reply with only the requested output.",
        };
        _initialized = true;
        _logger.LogInformation("Local LLM model loaded.");
    }

    /// <summary>Disposes the loaded model, if any.</summary>
    public async ValueTask DisposeAsync()
    {
        _weights?.Dispose();
        _gate.Dispose();
        await ValueTask.CompletedTask;
    }
}
