using McpOrchestrator;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.LocalLlm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// The LLM-backed orchestrator host: identical to the core host, but the `request` planner is the
// in-process local model wrapped with the heuristic as a fallback (so it still works before the
// model has downloaded, or if loading/inference fails). Set MCP_ORCHESTRATOR_PLANNER=heuristic to
// force heuristic-only and skip the model entirely.
await OrchestratorHost.RunAsync(args, services =>
{
    var options = LocalLlmOptions.FromEnvironment();

    if (string.Equals(
            Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_PLANNER"), "heuristic",
            StringComparison.OrdinalIgnoreCase))
    {
        services.AddSingleton<IRoutePlanner>(sp => sp.GetRequiredService<HeuristicRoutePlanner>());
        return;
    }

    options.Enabled = true;
    services.AddSingleton(options);
    services.AddSingleton<ModelProvisioner>(sp => new ModelProvisioner(
        sp.GetRequiredService<LocalLlmOptions>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ModelProvisioner>()));
    services.AddSingleton<LocalLlm>(sp => new LocalLlm(
        sp.GetRequiredService<LocalLlmOptions>(),
        sp.GetRequiredService<ModelProvisioner>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<LocalLlm>()));
    services.AddSingleton<IRoutePlanner>(sp => new FallbackRoutePlanner(
        new LlmRoutePlanner(
            sp.GetRequiredService<LocalLlm>(),
            sp.GetRequiredService<ILogger<LlmRoutePlanner>>(),
            options.ModelFileName),
        sp.GetRequiredService<HeuristicRoutePlanner>(),
        sp.GetRequiredService<ILogger<FallbackRoutePlanner>>()));
});
