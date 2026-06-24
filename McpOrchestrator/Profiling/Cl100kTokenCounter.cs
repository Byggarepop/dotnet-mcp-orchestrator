using Microsoft.ML.Tokenizers;

namespace McpOrchestrator.Profiling;

/// <summary>
/// The default <see cref="ITokenCounter"/>: the <c>cl100k_base</c> byte-pair encoding (the
/// Claude/GPT-4-class tokenizer) via <c>Microsoft.ML.Tokenizers</c>. The vocab is embedded by
/// the <c>Data.Cl100kBase</c> package, so counting is offline, deterministic, and CI-friendly —
/// no network, no API key, no cost.
/// </summary>
/// <remarks>
/// This is an <em>approximation</em> across model families. Different models tokenize the same
/// text slightly differently, so every number derived from this counter is disclosed with a
/// ±<see cref="CrossModelTolerancePct"/>% tolerance. For exact per-model accounting, a future
/// live-usage <see cref="ITokenCounter"/> can read real API <c>usage</c> numbers instead.
/// </remarks>
public sealed class Cl100kTokenCounter : ITokenCounter
{
    // The encoding name understood by Microsoft.ML.Tokenizers and the conventional disclosure name.
    private const string EncodingName = "cl100k_base";

    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding(EncodingName);

    /// <inheritdoc />
    public string Name => EncodingName;

    /// <inheritdoc />
    public string Approximates => "claude-sonnet";

    /// <inheritdoc />
    public int CrossModelTolerancePct => 10;

    /// <inheritdoc />
    public int Count(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : _tokenizer.CountTokens(text);
}
