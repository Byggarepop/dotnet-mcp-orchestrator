using System.ComponentModel;
using System.Text.Json;
using ConsafeWorkflow.Mcp.Workflow;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ConsafeWorkflow.Mcp.Tools;

/// <summary>
/// MCP tool surface for the stateful workflow engine.
/// </summary>
[McpServerToolType]
public sealed class WorkflowTool
{
    /// <summary>
    /// Looks up the session by id, advances the workflow state machine, and returns the
    /// next directed message.
    /// <para>
    /// Input handling: if the agent forwarded the user's message in
    /// <paramref name="userInput"/>, it is used as-is. If it is empty and the client
    /// supports <em>elicitation</em>, the server asks the user directly (through the
    /// client's textbox) so a dropped message does not stall the workflow. If neither is
    /// available, it falls back to a two-phase loop.
    /// </para>
    /// <para>
    /// The returned string is a directed instruction the agent must present to the user
    /// <strong>verbatim</strong> — do not interpret, rephrase, or skip it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Shell behaviour: the session store, elicitation, and engine seams are exercised,
    /// but the engine returns a fixed placeholder for all inputs.
    /// </remarks>
    [McpServerTool(Name = "get_next_step")]
    [Description(
        "Advance the stateful workflow for a conversation and return the next directed " +
        "message to present to the user verbatim. CRITICAL: 'userInput' MUST be the " +
        "user's latest chat message copied word-for-word. Do NOT pass editor/IDE " +
        "context (e.g. 'Current file: ...'), file contents, selections, summaries, or " +
        "your own interpretation. If the user has not typed anything yet, pass an empty " +
        "string. 'sessionId' must be a stable id reused for every call in the same " +
        "conversation.")]
    public static async Task<string> GetNextStep(
        McpServer server,
        ISessionStore sessionStore,
        IWorkflowEngine engine,
        [Description(
            "Stable identifier for this conversation/session. Reuse the exact same value " +
            "on every call within the conversation.")]
        string sessionId,
        [Description(
            "The user's latest chat message, copied VERBATIM (character for character). " +
            "Empty string on the first invocation. Never substitute editor/IDE context " +
            "such as 'Current file: ...', file contents, selections, or a paraphrase — " +
            "pass only the literal text the user typed.")]
        string userInput,
        CancellationToken cancellationToken)
    {
        sessionId ??= string.Empty;
        userInput ??= string.Empty;

        // Diagnostic: log exactly what the agent relayed as tool arguments. This is the
        // only way to measure the "userInput always blank" problem — the value is chosen
        // by the model, not piped from the chat box, so it is frequently empty or IDE
        // context. Compare this against the reliable prompt/elicitation channels.
        await Console.Error.WriteLineAsync(
            $"[tool] get_next_step sessionId={sessionId} userInput={JsonSerializer.Serialize(userInput)}");

        var session = sessionStore.GetOrCreate(sessionId);

        // The step currently awaiting an answer: the one we posed last turn (with its menu
        // choices, if any), or — for a brand-new session — the engine's opening step.
        string pendingMessage;
        IReadOnlyList<string>? pendingChoices;
        if (session.PendingPrompt is null)
        {
            var initial = engine.InitialStep;
            pendingMessage = initial.Message;
            pendingChoices = initial.Choices;
        }
        else
        {
            pendingMessage = session.PendingPrompt;
            pendingChoices = session.PendingChoices;
        }

        var elicitationAvailable =
            ElicitationEnabled && server.ClientCapabilities?.Elicitation is not null;

        // Resolve the user's answer for this turn.
        string answer;
        if (!string.IsNullOrWhiteSpace(userInput))
        {
            // The agent forwarded the user's message — use it. (The engine validates it
            // against any menu choices and re-asks if it doesn't match.)
            answer = userInput;
        }
        else if (elicitationAvailable)
        {
            // The agent forwarded nothing. Ask directly: if this step has choices, the user
            // gets a single-select menu; otherwise a free-text box. Either way the value
            // reaches us verbatim, with no model in between.
            var elicited = await ElicitReplyAsync(
                server, pendingMessage, cancellationToken, ElicitKind.Question, pendingChoices);

            if (elicited is null)
            {
                // Declined / cancelled / timed out: re-present the same step next turn.
                SavePending(sessionStore, session, pendingMessage, pendingChoices);
                return pendingMessage;
            }

            answer = elicited;
        }
        else
        {
            // No input and no elicitation support (degrades, never breaks). Classic
            // two-phase loop: present the step now, advance on the next call.
            if (session.PendingPrompt is null)
            {
                SavePending(sessionStore, session, pendingMessage, pendingChoices);
                return pendingMessage;
            }

            answer = userInput; // empty; let the engine decide how to handle it.
        }

        // The engine turns the user's answer into the next step. It may call a local model
        // (ILocalModelClient) while deciding, so this is awaited.
        var step = await engine.AdvanceAsync(session, answer, cancellationToken);

        // Act on what the engine wants to happen next:
        //  - AwaitUser      : keep the loop going; remember this step (and its choices) so we
        //                     can collect the answer to it next turn.
        //  - DelegateToAgent: hand the message to Copilot to act on; nothing to elicit.
        //  - Complete       : workflow finished; stop and mark the session done.
        if (step.Outcome == WorkflowOutcome.AwaitUser)
        {
            session.PendingPrompt = step.Message;
            session.PendingChoices = step.Choices?.ToList();
        }
        else
        {
            session.PendingPrompt = null;
            session.PendingChoices = null;
            if (step.Outcome == WorkflowOutcome.Complete)
            {
                session.State = WorkflowState.Completed;
            }
        }

        sessionStore.Save(session);
        return step.Message;
    }

    /// <summary>
    /// Stores the step we are still waiting on (message + any menu choices) and persists the
    /// session, so the same step is re-presented on the next call.
    /// </summary>
    private static void SavePending(
        ISessionStore sessionStore,
        WorkflowSession session,
        string message,
        IReadOnlyList<string>? choices)
    {
        session.PendingPrompt = message;
        session.PendingChoices = choices?.ToList();
        sessionStore.Save(session);
    }

    /// <summary>
    /// Master switch for the elicitation path. Set
    /// <c>CONSAFEWORKFLOW_DISABLE_ELICITATION=1</c> to force the plain <c>userInput</c>
    /// path (useful when a client advertises elicitation but mishandles it, e.g. some
    /// Visual Studio builds).
    /// </summary>
    private static readonly bool ElicitationEnabled =
        !string.Equals(
            Environment.GetEnvironmentVariable("CONSAFEWORKFLOW_DISABLE_ELICITATION"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Safety timeout for a single elicitation round-trip. Guards against clients that
    /// advertise the capability but never render the form (which would otherwise hang the
    /// tool call indefinitely). Override with
    /// <c>CONSAFEWORKFLOW_ELICIT_TIMEOUT_SECONDS</c> (0 disables the timeout).
    /// </summary>
    private static readonly TimeSpan ElicitTimeout =
        TimeSpan.FromSeconds(
            int.TryParse(
                Environment.GetEnvironmentVariable("CONSAFEWORKFLOW_ELICIT_TIMEOUT_SECONDS"),
                out var seconds) && seconds >= 0
                ? seconds
                : 120);

    /// <summary>
    /// What an elicitation dialog is asking for — used only to tune the input field's
    /// label so the dialog reads naturally. Extend as new dialog purposes appear.
    /// </summary>
    private enum ElicitKind
    {
        /// <summary>The user is answering the workflow's current directed question.</summary>
        Question,

        /// <summary>The user is supplying free-form text (no specific question posed).</summary>
        FreeText,
    }

    /// <summary>
    /// Asks the user for a free-form reply via client elicitation. Returns the user's
    /// text, or null if they declined/cancelled, the client errored, or it timed out — in
    /// every one of those cases the caller keeps the existing (agent-supplied) input.
    /// This method never throws.
    /// </summary>
    private static async Task<string?> ElicitReplyAsync(
        McpServer server,
        string prompt,
        CancellationToken cancellationToken,
        ElicitKind kind = ElicitKind.Question,
        IReadOnlyList<string>? choices = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (ElicitTimeout > TimeSpan.Zero)
        {
            linked.CancelAfter(ElicitTimeout);
        }

        // The dialog body (Message) already carries the context the user reads; ElicitKind
        // only tunes the input field's label so the dialog reads naturally for its purpose.
        var (fieldTitle, fieldDescription) = kind switch
        {
            ElicitKind.FreeText => ("Text", "Enter your text."),
            _ => ("Reply", "Your answer to the question above."),
        };

        // When the step offers a fixed set of choices, render a single-select menu (enum);
        // otherwise a free-text box.
        ElicitRequestParams.PrimitiveSchemaDefinition replySchema = choices is { Count: > 0 }
            ? new ElicitRequestParams.UntitledSingleSelectEnumSchema
            {
                Title = fieldTitle,
                Description = "Choose one option.",
                Enum = choices.ToList(),
            }
            : new ElicitRequestParams.StringSchema
            {
                Title = fieldTitle,
                Description = fieldDescription,
            };

        try
        {
            var result = await server.ElicitAsync(
                new ElicitRequestParams
                {
                    Message = prompt,
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties =
                        {
                            ["reply"] = replySchema,
                        },
                        Required = new List<string> { "reply" },
                    },
                },
                linked.Token);

            // Diagnostic: dump exactly what the client returned so we can see how VS
            // (or any client) shapes its elicitation response.
            await Console.Error.WriteLineAsync(
                $"[elicit] raw response: action={result.Action ?? "<null>"} " +
                $"isAccepted={result.IsAccepted} " +
                $"content={SerializeContent(result.Content)}");

            // Only treat an explicit accept as a real answer. The MCP spec uses
            // Action="accept", but some clients (e.g. Visual Studio) send "accepted" and
            // don't set IsAccepted — so match any "accept*" value, case-insensitive, or
            // the flag.
            var accepted =
                (result.Action is { } action
                 && action.StartsWith("accept", StringComparison.OrdinalIgnoreCase))
                || result.IsAccepted;

            if (accepted && result.Content is { Count: > 0 } content)
            {
                // Prefer the "reply" field we asked for, but tolerate clients that key the
                // value differently by falling back to the first usable property.
                if (content.TryGetValue("reply", out var reply)
                    && TryReadString(reply, out var replyText))
                {
                    return replyText;
                }

                foreach (var value in content.Values)
                {
                    if (TryReadString(value, out var anyText))
                    {
                        return anyText;
                    }
                }
            }

            // declined / cancelled / empty — fall back to the agent-supplied input.
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our safety timeout fired: the client advertised elicitation but didn't answer.
            await Console.Error.WriteLineAsync(
                "[elicit] timed out waiting for the client; falling back to userInput.");
            return null;
        }
        catch (Exception ex)
        {
            // Any client/protocol error must not break the tool — degrade to userInput.
            await Console.Error.WriteLineAsync(
                $"[elicit] failed ({ex.GetType().Name}: {ex.Message}); falling back to userInput.");
            return null;
        }
    }

    /// <summary>
    /// Reads a JSON value as a non-empty string. Strings are returned directly; other
    /// primitive kinds (numbers/booleans) are coerced via their raw text so a stray
    /// non-string answer still gets through.
    /// </summary>
    private static bool TryReadString(JsonElement value, out string text)
    {
        text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => string.Empty,
        };

        return !string.IsNullOrEmpty(text);
    }

    private static string SerializeContent(IDictionary<string, JsonElement>? content)
    {
        if (content is null)
        {
            return "<null>";
        }

        try
        {
            return JsonSerializer.Serialize(content);
        }
        catch
        {
            return "<unserializable>";
        }
    }
}
