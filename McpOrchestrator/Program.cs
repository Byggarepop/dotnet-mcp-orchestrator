using McpOrchestrator;

// The orchestrator host: exposes the meta-tools and relays calls to the downstream MCP servers.
// Also dispatches the `profile` subcommand. Returns the process exit code.
return await OrchestratorHost.RunAsync(args);
