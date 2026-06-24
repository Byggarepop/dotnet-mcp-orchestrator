namespace McpOrchestrator.Profiling;

/// <summary>
/// Counts tokens for a piece of text. The <c>profile</c> command's whole job is token
/// economics, so token counting is isolated behind this interface for one reason: the default
/// is a local tokenizer (cheap, deterministic, CI-friendly), but the spec calls for a future
/// live-usage backend that reports real API <c>usage</c> numbers. Swapping that in must not
/// require rewriting the profiler — only providing another <see cref="ITokenCounter"/>.
/// </summary>
public interface ITokenCounter
{
    /// <summary>Tokenizer/encoding name to disclose in output, e.g. <c>cl100k_base</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The model family this tokenizer approximates, e.g. <c>claude-sonnet</c>. Honesty hook:
    /// a local BPE is an approximation across model families, never an exact match.
    /// </summary>
    string Approximates { get; }

    /// <summary>
    /// Cross-model tolerance to disclose alongside every number (e.g. <c>10</c> for ±10%). The
    /// honest benchmarks in this space all disclose tokenizer + tolerance; this command must too.
    /// </summary>
    int CrossModelTolerancePct { get; }

    /// <summary>Counts the tokens in <paramref name="text"/>. Empty/null text is zero tokens.</summary>
    int Count(string? text);
}
