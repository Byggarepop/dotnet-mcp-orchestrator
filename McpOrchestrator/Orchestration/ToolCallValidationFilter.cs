using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// The tools/call request filter that applies <see cref="ToolCallValidation"/> before the SDK
/// binds a call's arguments to the tool method. An invalid call returns a structured
/// <see cref="ErrorView"/> naming the exact problem instead of the SDK's generic binding
/// failure; a call rescued by a parameter alias proceeds, with the rewrite logged and a note
/// appended to the result so the model learns the canonical name.
/// </summary>
internal static class ToolCallValidationFilter
{
    /// <summary>Wraps the next tools/call handler. Pass to <c>AddCallToolFilter</c>.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Attach(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, cancellationToken) =>
    {
        var toolName = context.Params?.Name;
        var tools = context.Server.ServerOptions.ToolCollection;
        if (toolName is null || tools is null || !tools.TryGetPrimitive(toolName, out var tool))
        {
            // Unknown tool — let the SDK produce its own (already descriptive) error.
            return await next(context, cancellationToken);
        }

        var validation = ToolCallValidation.ValidateAndNormalize(
            tool.ProtocolTool.InputSchema, context.Params!.Arguments);

        var logger = context.Server.Services?.GetService<ILoggerFactory>()
            ?.CreateLogger("McpOrchestrator.ToolCallValidation");
        foreach (var alias in validation.AppliedAliases)
        {
            logger?.LogWarning(
                "tools/call '{Tool}': parameter '{Synonym}' accepted as an alias for '{Canonical}'.",
                toolName, alias.Synonym, alias.Canonical);
        }

        if (validation.Error is not null)
        {
            logger?.LogWarning("tools/call '{Tool}' rejected: {Error}", toolName, validation.Error);
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = OrchestratorJson.Serialize(new ErrorView(validation.Error)) }],
            };
        }

        context.Params.Arguments = validation.NormalizedArguments;

        var result = await next(context, cancellationToken);

        if (validation.AppliedAliases.Count > 0)
        {
            var note = string.Join(" ", validation.AppliedAliases.Select(a =>
                $"Note: parameter '{a.Synonym}' was accepted as an alias for '{a.Canonical}' — use '{a.Canonical}' in future calls."));
            result.Content = [.. result.Content ?? [], new TextContentBlock { Text = note }];
        }

        return result;
    };
}
