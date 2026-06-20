using McpOrchestrator;

// The lean orchestrator host: routes through the dependency-free heuristic planner for `request`.
// For LLM-backed `request`, run the optional McpOrchestrator.LocalLlm package instead, which reuses
// this same host and swaps in the local-model planner.
await OrchestratorHost.RunAsync(args);
