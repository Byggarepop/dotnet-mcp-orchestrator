using McpOrchestrator.Tui.Configuration;
using McpOrchestrator.Tui.UI;
using Terminal.Gui.App;

var editor = new ConfigEditor();
var configPath = editor.ResolveConfigPath(Environment.CurrentDirectory);
var config = editor.Load(configPath);

using var app = Application.Create();
app.Init();
using var window = new MainWindow(config, editor, configPath);
app.Run(window);

return 0;
