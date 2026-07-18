using System.Collections.ObjectModel;
using McpOrchestrator.Orchestration;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace McpOrchestrator.Tui.UI;

/// <summary>
/// The main TUI screen: configured servers on the left, details of the selected server on
/// the right, and a status bar with key hints. Later steps wire editing (Step 7), the
/// registry browser (Step 8), and the add flow (Step 9) into this window.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly OrchestratorConfig _config;
    private readonly ListView _serverList;
    private readonly FrameView _detailPane;

    /// <summary>Builds the two-pane layout over the given (already loaded) config.</summary>
    /// <param name="config">The user's orchestrator config.</param>
    public MainWindow(OrchestratorConfig config)
    {
        _config = config;
        Title = "McpOrchestrator";

        var leftPane = new FrameView
        {
            Title = "Servers",
            X = 0,
            Y = 0,
            Width = Dim.Percent(30),
            Height = Dim.Fill(1),
        };

        _serverList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        _serverList.SetSource(new ObservableCollection<string>(config.Capabilities.Select(FormatServer)));
        leftPane.Add(_serverList);

        _detailPane = new FrameView
        {
            Title = "Details",
            X = Pos.Right(leftPane),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };

        var statusBar = new StatusBar(new[]
        {
            new Shortcut(Key.Q, "Quit", () => App?.RequestStop(this)),
            new Shortcut(Key.A, "Add from registry", static () => { }),
            new Shortcut(Key.D, "Delete", static () => { }),
        });

        Add(leftPane, _detailPane, statusBar);
    }

    /// <summary>One list row: name plus transport and enabled state at a glance.</summary>
    private static string FormatServer(CapabilityDescriptor capability) =>
        $"{(capability.Enabled ? " " : "·")} {capability.Name} ({capability.Transport})";
}
