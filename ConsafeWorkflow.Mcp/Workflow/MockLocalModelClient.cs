namespace ConsafeWorkflow.Mcp.Workflow;

/// <summary>
/// Placeholder <see cref="ILocalModelClient"/> that returns a canned reply without calling
/// any real endpoint. Lets the engine exercise the local-model round-trip before a model is
/// chosen. Swap for a real Ollama / LM Studio HTTP client (POST to
/// <c>/v1/chat/completions</c>) once the target model is decided.
/// </summary>
public sealed class MockLocalModelClient : ILocalModelClient
{
    public Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        // Logged to stderr so the round-trip is visible in the IDE's MCP output panel.
        Console.Error.WriteLine(
            $"[local-model:mock] system={Truncate(systemPrompt)} user={Truncate(userPrompt)}");

        var reply = $"(mock local-model reply to: \"{userPrompt}\")";
        return Task.FromResult(reply);
    }

    private static string Truncate(string value, int max = 80) =>
        value.Length <= max ? value : value[..max] + "…";
}
