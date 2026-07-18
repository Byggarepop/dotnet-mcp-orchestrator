using McpOrchestrator.Tui.Configuration;
using McpOrchestrator.Tui.UI;
using Terminal.Gui.App;

// TODO: mirror CapabilityCatalog.ResolveConfigPath (env override, solution dir) instead of cwd only.
var configPath = Path.Combine(Environment.CurrentDirectory, "orchestrator.config.json");

var editor = new ConfigEditor();
var config = editor.Load(configPath);

using var app = Application.Create();
app.Init();
using var window = new MainWindow(config);
app.Run(window);

return 0;
