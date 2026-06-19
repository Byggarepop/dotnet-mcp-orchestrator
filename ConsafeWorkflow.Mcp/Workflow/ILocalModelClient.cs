namespace ConsafeWorkflow.Mcp.Workflow;

/// <summary>
/// Plug-in point for a <strong>local</strong> language model exposed over an
/// OpenAI-compatible endpoint (e.g. Ollama or LM Studio). Intended for workflow
/// tasks such as suggesting parameter values or component names from free-form
/// user input.
/// <para>
/// Intentionally unimplemented in the shell. A future implementation will POST to a
/// configurable <c>/v1/chat/completions</c> endpoint. The interface is kept minimal
/// so it can be injected into <see cref="IWorkflowEngine"/> implementations.
/// </para>
/// </summary>
public interface ILocalModelClient
{
    /// <summary>
    /// Sends a prompt to the local model and returns its completion text.
    /// </summary>
    /// <param name="systemPrompt">Instruction/role context for the model.</param>
    /// <param name="userPrompt">The user-derived content to reason over.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}
