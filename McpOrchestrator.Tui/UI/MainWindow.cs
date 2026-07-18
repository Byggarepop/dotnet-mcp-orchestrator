using System.Collections.ObjectModel;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Tui.Configuration;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Editor;
using Terminal.Gui.Views;

namespace McpOrchestrator.Tui.UI;

/// <summary>
/// The main TUI screen: configured servers on the left, editable details of the selected
/// server on the right, and a status bar with key hints. Changes are persisted through
/// <see cref="ConfigEditor"/> (validated, atomic, with backup); the orchestrator hot-reloads
/// the file on its own. The registry browser (Steps 8-9) hangs off the 'a' key.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly OrchestratorConfig _config;
    private readonly ConfigEditor _editor;
    private readonly string _configPath;

    private readonly ListView _serverList;
    private readonly TextField _summaryField;
    private readonly TextField _commandField;
    private readonly Editor _argsField;
    private readonly Editor _envField;
    private readonly CheckBox _enabledBox;

    /// <summary>Builds the two-pane layout over the given (already loaded) config.</summary>
    /// <param name="config">The user's orchestrator config.</param>
    /// <param name="editor">Persists edits back to disk.</param>
    /// <param name="configPath">Where <paramref name="config"/> was loaded from and is saved to.</param>
    public MainWindow(OrchestratorConfig config, ConfigEditor editor, string configPath)
    {
        _config = config;
        _editor = editor;
        _configPath = configPath;
        Title = $"McpOrchestrator — {configPath}";

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
        leftPane.Add(_serverList);

        var detailPane = new FrameView
        {
            Title = "Details",
            X = Pos.Right(leftPane),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };

        // Label column on the left of each field; fields stack vertically.
        var summaryLabel = new Label { X = 0, Y = 0, Text = "Summary:" };
        _summaryField = new TextField { X = 10, Y = 0, Width = Dim.Fill() };
        var commandLabel = new Label { X = 0, Y = 2, Text = "Command:" };
        _commandField = new TextField { X = 10, Y = 2, Width = Dim.Fill() };
        var argsLabel = new Label { X = 0, Y = 4, Text = "Args:" };
        _argsField = new Editor { X = 10, Y = 4, Width = Dim.Fill(), Height = 4 };
        var envLabel = new Label { X = 0, Y = 9, Text = "Env:" };
        _envField = new Editor { X = 10, Y = 9, Width = Dim.Fill(), Height = 4 };
        _enabledBox = new CheckBox { X = 10, Y = 14, Text = "Enabled" };
        var hint = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Text = "One arg per line; env as KEY=VALUE per line. Ctrl+S saves.",
        };
        detailPane.Add(summaryLabel, _summaryField, commandLabel, _commandField,
            argsLabel, _argsField, envLabel, _envField, _enabledBox, hint);

        // The shortcuts are display hints; the actual key handling happens in OnAppKeyDown,
        // ahead of view-level processing (see the Initialized subscription below).
        var statusBar = new StatusBar(new[]
        {
            new Shortcut(Key.Q, "Quit", () => App?.RequestStop(this)),
            new Shortcut(Key.A, "Add from registry", OpenRegistryBrowser),
            new Shortcut(Key.D, "Delete", DeleteSelected),
            new Shortcut(Key.S.WithCtrl, "Save", SaveSelected),
        });

        Add(leftPane, detailPane, statusBar);

        _serverList.ValueChanged += (_, _) => ShowSelected();

        // Application-level interception: fires before any view sees the key, so the
        // ListView's incremental letter-search cannot swallow our shortcuts. Text entry
        // stays safe via the typing guard in OnAppKeyDown.
        Initialized += (_, _) =>
        {
            if (App is not null)
                App.Keyboard.KeyDown += OnAppKeyDown;
        };

        RefreshList(0);
    }

    /// <summary>Handles the window's shortcut keys ahead of view-level key processing.</summary>
    private void OnAppKeyDown(object? sender, Key key)
    {
        if (App is null || key.Handled || !ReferenceEquals(App.TopRunnableView, this))
            return;

        var typing = MostFocused is TextField or Terminal.Gui.Editor.Editor;
        if (key == Key.Q && !typing)
        {
            App.RequestStop(this);
            key.Handled = true;
        }
        else if (key == Key.A && !typing)
        {
            OpenRegistryBrowser();
            key.Handled = true;
        }
        else if (key == Key.D && !typing)
        {
            DeleteSelected();
            key.Handled = true;
        }
        else if (key == Key.S.WithCtrl)
        {
            SaveSelected();
            key.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && App is not null)
            App.Keyboard.KeyDown -= OnAppKeyDown;
        base.Dispose(disposing);
    }

    /// <summary>The capability backing the current list selection, or null when the list is empty.</summary>
    private CapabilityDescriptor? Selected =>
        _serverList.SelectedItem is int index && index >= 0 && index < _config.Capabilities.Count
            ? _config.Capabilities[index]
            : null;

    /// <summary>Rebuilds the list rows from the config and restores a sane selection.</summary>
    private void RefreshList(int selectIndex)
    {
        _serverList.SetSource(new ObservableCollection<string>(_config.Capabilities.Select(FormatServer)));
        if (_config.Capabilities.Count > 0)
            _serverList.SelectedItem = Math.Clamp(selectIndex, 0, _config.Capabilities.Count - 1);
        ShowSelected();
    }

    /// <summary>Populates the detail fields from the selected capability (or blanks them).</summary>
    private void ShowSelected()
    {
        var capability = Selected;
        _summaryField.Text = capability?.Summary ?? string.Empty;
        _commandField.Text = capability?.Command ?? string.Empty;
        _argsField.Text = capability is null ? string.Empty : string.Join('\n', capability.Args);
        _envField.Text = capability is null
            ? string.Empty
            : string.Join('\n', capability.Env.Select(e => $"{e.Key}={e.Value}"));
        _enabledBox.Value = capability?.Enabled is true ? CheckState.Checked : CheckState.UnChecked;
    }

    /// <summary>Writes the detail fields back to the selected capability and saves the config.</summary>
    private void SaveSelected()
    {
        var capability = Selected;
        if (capability is null)
            return;

        capability.Summary = _summaryField.Text;
        capability.Command = _commandField.Text;
        capability.Args = SplitLines(_argsField.Text).ToList();
        capability.Env = SplitLines(_envField.Text)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? (string?)parts[1] : null);
        capability.Enabled = _enabledBox.Value == CheckState.Checked;

        TrySave(_serverList.SelectedItem ?? 0);
    }

    /// <summary>Opens the modal registry browser; on return, shows any newly added server.</summary>
    private void OpenRegistryBrowser()
    {
        // Reentry guard: ignore the app-wide 'a' unless this window is the active session.
        if (App is null || !ReferenceEquals(App.TopRunnableView, this))
            return;
        using (var browser = new RegistryBrowserWindow(_config, _editor, _configPath))
        {
            App.Run(browser);
        }
        RefreshList(Math.Max(0, _config.Capabilities.Count - 1));
    }

    /// <summary>Removes the selected server after a confirm dialog, then saves.</summary>
    private void DeleteSelected()
    {
        var capability = Selected;
        if (capability is null)
            return;

        var choice = MessageBox.Query(App!, "Remove server",
            $"Remove '{capability.Name}' from the config?", "Remove", "Cancel");
        if (choice != 0)
            return;

        var index = _serverList.SelectedItem ?? 0;
        _config.Capabilities.Remove(capability);
        TrySave(index);
    }

    /// <summary>Saves via <see cref="ConfigEditor"/>, surfacing validation errors instead of crashing.</summary>
    private void TrySave(int selectIndex)
    {
        try
        {
            _editor.Save(_config, _configPath);
            RefreshList(selectIndex);
        }
        catch (InvalidOperationException problem)
        {
            MessageBox.ErrorQuery(App!, "Invalid config", problem.Message, "Ok");
        }
    }

    /// <summary>One list row: name plus transport and enabled state at a glance.</summary>
    private static string FormatServer(CapabilityDescriptor capability) =>
        $"{(capability.Enabled ? " " : "·")} {capability.Name} ({capability.Transport})";

    /// <summary>Non-empty trimmed lines of a multi-line field.</summary>
    private static IEnumerable<string> SplitLines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
