namespace ConsafeWorkflow.Mcp.Workflow;

/// <summary>
/// Gated, menu-fronted workflow. The entry point is a <strong>gate</strong>: the user must
/// type <c>menu</c> to begin; anything else is answered with a reminder. Typing <c>menu</c>
/// opens an options menu; "Create a component" runs a pattern → name → confirm flow that
/// hands Copilot a scaffold instruction (<see cref="WorkflowOutcome.DelegateToAgent"/>); the
/// other options finish locally (<see cref="WorkflowOutcome.Complete"/>).
/// <para>
/// State is tracked as a string in <see cref="WorkflowSession.Data"/> ("state"); collected
/// values share the bag. Choice steps carry both their options in the message text (so they
/// read well when the answer comes as forwarded chat text) and in
/// <see cref="WorkflowStep.Choices"/> (so they render as a single-select dropdown when the
/// answer comes via elicitation). The local model (<see cref="ILocalModelClient"/>) normalises
/// a free-text pattern answer when the deterministic match is inconclusive.
/// </para>
/// </summary>
public sealed class ComponentWorkflowEngine : IWorkflowEngine
{
    // Menu options.
    private const string OptCreate = "Create a component";
    private const string OptList = "List supported patterns";
    private const string OptCancel = "Cancel";

    // States.
    private const string StateGate = "gate";
    private const string StateMenu = "menu";
    private const string StatePattern = "pattern";
    private const string StateName = "name";
    private const string StateConfirm = "confirm";

    private const string GatePrompt = "Type \"menu\" to see the available options.";
    private const string GateReject = "Please write \"menu\" for the available options.";
    private const string NameQuestion = "What should the component be named?";

    private readonly ILocalModelClient _localModel;

    public ComponentWorkflowEngine(ILocalModelClient localModel) => _localModel = localModel;

    public WorkflowStep InitialStep => new(GatePrompt);

    public async Task<WorkflowStep> AdvanceAsync(
        WorkflowSession session,
        string userInput,
        CancellationToken cancellationToken = default)
    {
        // Re-activate a brand-new or just-completed session so it starts fresh at the gate.
        if (session.State is WorkflowState.New or WorkflowState.Completed)
        {
            session.State = WorkflowState.InProgress;
        }

        var state = session.Data.GetValueOrDefault("state", StateGate);
        return state switch
        {
            StateGate => HandleGate(session, userInput),
            StateMenu => HandleMenu(session, userInput),
            StatePattern => await HandlePatternAsync(session, userInput, cancellationToken),
            StateName => HandleName(session, userInput),
            StateConfirm => HandleConfirm(session, userInput),
            _ => ResetToGate(session),
        };
    }

    private static WorkflowStep HandleGate(WorkflowSession session, string input)
    {
        if (string.Equals(input.Trim(), "menu", StringComparison.OrdinalIgnoreCase))
        {
            session.Data["state"] = StateMenu;
            return MenuStep();
        }

        // Stay at the gate and remind the user how to start.
        return new WorkflowStep(GateReject);
    }

    private static WorkflowStep MenuStep() =>
        new(
            "What would you like to do?\n" +
            $"- {OptCreate}\n" +
            $"- {OptList}\n" +
            $"- {OptCancel}",
            WorkflowOutcome.AwaitUser,
            new[] { OptCreate, OptList, OptCancel });

    private static WorkflowStep HandleMenu(WorkflowSession session, string input)
    {
        var choice = MatchOption(input, OptCreate, OptList, OptCancel);
        switch (choice)
        {
            case OptCreate:
                session.Data["state"] = StatePattern;
                return PatternStep();

            case OptList:
                ResetToGate(session);
                return new WorkflowStep(
                    "Supported patterns:\n" +
                    "- Repository — an interface, an implementing class, and DI registration.\n" +
                    "- CQRS — command/query types with their handlers.\n\n" + GatePrompt,
                    WorkflowOutcome.Complete);

            case OptCancel:
                ResetToGate(session);
                return new WorkflowStep("Okay — nothing to do. " + GatePrompt, WorkflowOutcome.Complete);

            default:
                return MenuStep(); // unrecognised — show the menu again.
        }
    }

    private static WorkflowStep PatternStep() =>
        new(
            "Which pattern should the component use? (Repository / CQRS)",
            WorkflowOutcome.AwaitUser,
            new[] { "Repository", "CQRS" });

    private async Task<WorkflowStep> HandlePatternAsync(
        WorkflowSession session, string input, CancellationToken cancellationToken)
    {
        var pattern = await NormalizePatternAsync(input, cancellationToken);
        if (pattern is null)
        {
            return new WorkflowStep(
                "Sorry, I couldn't tell which pattern you meant. " +
                "Which pattern? (Repository / CQRS)",
                WorkflowOutcome.AwaitUser,
                new[] { "Repository", "CQRS" });
        }

        session.Data["pattern"] = pattern;
        session.Data["state"] = StateName;
        return new WorkflowStep($"Pattern set to {pattern}. {NameQuestion}");
    }

    private static WorkflowStep HandleName(WorkflowSession session, string input)
    {
        var name = input.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new WorkflowStep($"The name can't be empty. {NameQuestion}");
        }

        session.Data["name"] = name;
        session.Data["state"] = StateConfirm;

        var pattern = session.Data.GetValueOrDefault("pattern", "Repository");
        return new WorkflowStep(
            $"Create a {pattern} component named \"{name}\"? (Yes / No)",
            WorkflowOutcome.AwaitUser,
            new[] { "Yes", "No" });
    }

    private WorkflowStep HandleConfirm(WorkflowSession session, string input)
    {
        var choice = MatchOption(input, "Yes", "No");
        switch (choice)
        {
            case "Yes":
                var pattern = session.Data.GetValueOrDefault("pattern", "Repository");
                var name = session.Data.GetValueOrDefault("name", "Component");
                ResetToGate(session); // this component is done; back to the gate next turn.
                return new WorkflowStep(
                    BuildScaffoldInstruction(pattern, name),
                    WorkflowOutcome.DelegateToAgent);

            case "No":
                // Back to the menu so they can pick again without retyping "menu".
                session.Data.Clear();
                session.Data["state"] = StateMenu;
                return MenuStep();

            default:
                return new WorkflowStep(
                    "Please choose Yes or No.",
                    WorkflowOutcome.AwaitUser,
                    new[] { "Yes", "No" });
        }
    }

    private static WorkflowStep ResetToGate(WorkflowSession session)
    {
        session.Data.Clear();
        session.Data["state"] = StateGate;
        return new WorkflowStep(GatePrompt);
    }

    /// <summary>
    /// Maps the user's free-text pattern answer to "Repository" or "CQRS". Tries a
    /// deterministic keyword match first; if inconclusive, asks the local model. Returns
    /// null when neither can decide.
    /// </summary>
    private async Task<string?> NormalizePatternAsync(string input, CancellationToken cancellationToken)
    {
        if (TryMatchPattern(input, out var direct))
        {
            return direct;
        }

        var reply = await _localModel.CompleteAsync(
            systemPrompt:
                "Map the user's message to exactly one of these component patterns: " +
                "Repository, CQRS. Answer with only that single word.",
            userPrompt: input,
            cancellationToken);

        return TryMatchPattern(reply, out var viaModel) ? viaModel : null;
    }

    private static bool TryMatchPattern(string text, out string pattern)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("repo")) { pattern = "Repository"; return true; }
        if (lower.Contains("cqrs")) { pattern = "CQRS"; return true; }
        pattern = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns the option that best matches the user's answer (exact, case-insensitive
    /// first; then a substring match so forwarded free text like "create" still resolves),
    /// or null if none match.
    /// </summary>
    private static string? MatchOption(string input, params string[] options)
    {
        var trimmed = input.Trim();
        foreach (var option in options)
        {
            if (string.Equals(option, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        if (trimmed.Length > 0)
        {
            foreach (var option in options)
            {
                if (option.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                    || trimmed.Contains(option, StringComparison.OrdinalIgnoreCase))
                {
                    return option;
                }
            }
        }

        return null;
    }

    private static string BuildScaffoldInstruction(string pattern, string name)
    {
        var parts = pattern == "Repository"
            ? $"- an interface `I{name}Repository`\n" +
              $"- a class `{name}Repository : I{name}Repository`\n" +
              $"- register it in dependency injection"
            : $"- a command `{name}Command` and handler `{name}CommandHandler`\n" +
              $"- a query `{name}Query` and handler `{name}QueryHandler`\n" +
              $"- register the handlers";

        return
            $"Scaffold a {pattern} component named {name}. Create the following in the " +
            $"project's conventional location:\n{parts}\n" +
            "Follow the existing project conventions, then summarise what you created.";
    }
}
