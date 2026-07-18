using System.Collections.ObjectModel;
using System.Text;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Tui.Configuration;
using McpOrchestrator.Tui.Registry;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Editor;
using Terminal.Gui.Views;

namespace McpOrchestrator.Tui.UI;

/// <summary>
/// Modal registry browser (opened with 'a' from the main window): debounced search over
/// the active registry source, results on the left, entry details on the right. Enter on
/// an entry runs the add-to-config flow (option picker, env-var prompts, save); Ctrl+R
/// cycles between the sources configured in the config's "registries" section.
/// </summary>
internal sealed class RegistryBrowserWindow : Window
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly OrchestratorConfig _config;
    private readonly ConfigEditor _editor;
    private readonly string _configPath;
    private readonly ServerMappingService _mapper = new();

    private readonly Label _sourceLabel;
    private readonly TextField _searchField;
    private readonly ListView _resultsList;
    private readonly Editor _detailView;

    private readonly List<RegistryServerEntry> _entries = new();
    private string? _nextCursor;
    private int _sourceIndex;
    private object? _debounceToken;
    private CancellationTokenSource? _searchCts;

    /// <summary>Builds the browser over the config's registry sources (never empty — the official one is guaranteed).</summary>
    /// <param name="config">The user's orchestrator config; receives added servers.</param>
    /// <param name="editor">Persists the config after an add.</param>
    /// <param name="configPath">Where the config is saved to.</param>
    public RegistryBrowserWindow(OrchestratorConfig config, ConfigEditor editor, string configPath)
    {
        _config = config;
        _editor = editor;
        _configPath = configPath;
        Title = "MCP Registry";

        _sourceLabel = new Label { X = 0, Y = 0, Text = SourceText() };
        var searchLabel = new Label { X = 0, Y = 1, Text = "Search:" };
        _searchField = new TextField { X = 8, Y = 1, Width = Dim.Fill() };

        var resultsPane = new FrameView
        {
            Title = "Results",
            X = 0,
            Y = 2,
            Width = Dim.Percent(40),
            Height = Dim.Fill(1),
        };
        _resultsList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        resultsPane.Add(_resultsList);

        var detailPane = new FrameView
        {
            Title = "Details",
            X = Pos.Right(resultsPane),
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _detailView = new Editor { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true };
        detailPane.Add(_detailView);

        // The shortcuts are display hints; the actual key handling happens in OnAppKeyDown,
        // ahead of view-level processing (see the Initialized subscription below).
        var statusBar = new StatusBar(new[]
        {
            new Shortcut(Key.Esc, "Close", () => App?.RequestStop(this)),
            new Shortcut(Key.Enter, "Add", AddSelected),
            new Shortcut(Key.R.WithCtrl, "Next source", CycleSource),
            new Shortcut(Key.L.WithCtrl, "Load more", LoadMore),
        });

        Add(_sourceLabel, searchLabel, _searchField, resultsPane, detailPane, statusBar);

        _searchField.TextChanged += (_, _) => RestartDebounce();
        _resultsList.ValueChanged += (_, _) => ShowDetail();

        Initialized += (_, _) =>
        {
            if (App is not null)
                App.Keyboard.KeyDown += OnAppKeyDown;
            StartSearch();
        };
    }

    /// <summary>Handles the browser's shortcut keys ahead of view-level key processing.</summary>
    private void OnAppKeyDown(object? sender, Key key)
    {
        if (App is null || key.Handled || !ReferenceEquals(App.TopRunnableView, this))
            return;

        if (key == Key.Esc)
        {
            App.RequestStop(this);
            key.Handled = true;
        }
        else if (key == Key.Enter && ReferenceEquals(MostFocused, _resultsList))
        {
            AddSelected();
            key.Handled = true;
        }
        else if (key == Key.R.WithCtrl)
        {
            CycleSource();
            key.Handled = true;
        }
        else if (key == Key.L.WithCtrl)
        {
            LoadMore();
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

    private RegistrySource ActiveSource => _config.Registries[_sourceIndex];

    private RegistryServerDetail? SelectedEntry =>
        _resultsList.SelectedItem is int index && index >= 0 && index < _entries.Count
            ? _entries[index].Server
            : null;

    private string SourceText() => $"Source: {ActiveSource.Name}  (Ctrl+R to switch)";

    /// <summary>Restarts the debounce timer; the search fires once typing pauses.</summary>
    private void RestartDebounce()
    {
        if (App is null)
            return;
        if (_debounceToken is not null)
            App.RemoveTimeout(_debounceToken);
        _debounceToken = App.AddTimeout(DebounceDelay, () =>
        {
            _debounceToken = null;
            StartSearch();
            return false;
        });
    }

    /// <summary>Fetches the first page for the current search text and source.</summary>
    private void StartSearch() => FetchPage(null);

    /// <summary>Appends the next page, if the registry reported one.</summary>
    private void LoadMore()
    {
        if (_nextCursor is not null)
            FetchPage(_nextCursor);
    }

    /// <summary>Switches to the next configured registry source and re-runs the search.</summary>
    private void CycleSource()
    {
        _sourceIndex = (_sourceIndex + 1) % _config.Registries.Count;
        _sourceLabel.Text = SourceText();
        StartSearch();
    }

    /// <summary>
    /// Fetches one page off the UI thread, cancelling any in-flight search (stale results
    /// must never overwrite newer ones), and marshals the update back via App.Invoke.
    /// </summary>
    private void FetchPage(string? cursor)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var search = _searchField.Text;
        var sourceUrl = ActiveSource.Url;

        Task.Run(async () =>
        {
            try
            {
                using var client = new RegistryClient(sourceUrl);
                var page = await client.SearchAsync(
                    string.IsNullOrWhiteSpace(search) ? null : search, cursor, cts.Token);
                App?.Invoke(() =>
                {
                    if (cts.IsCancellationRequested)
                        return;
                    if (cursor is null)
                        _entries.Clear();
                    _entries.AddRange(page.Entries);
                    _nextCursor = page.NextCursor;
                    RefreshResults();
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer search; nothing to show.
            }
            catch (Exception problem)
            {
                App?.Invoke(() => _detailView.Text = $"Registry error: {problem.Message}");
            }
        });
    }

    /// <summary>Rebuilds the result rows and refreshes the detail pane.</summary>
    private void RefreshResults()
    {
        _resultsList.SetSource(new ObservableCollection<string>(
            _entries.Select(e => $"{e.Server.Name}  {e.Server.Version}")));
        if (_entries.Count > 0)
            _resultsList.SelectedItem = 0;
        ShowDetail();
    }

    /// <summary>Renders the selected entry's description, version, packages, and remotes.</summary>
    private void ShowDetail()
    {
        var detail = SelectedEntry;
        if (detail is null)
        {
            _detailView.Text = _entries.Count == 0 ? "No results." : string.Empty;
            return;
        }

        var text = new StringBuilder();
        text.AppendLine(detail.Title ?? detail.Name);
        text.AppendLine($"Version: {detail.Version}");
        text.AppendLine();
        text.AppendLine(detail.Description ?? "(no description)");
        text.AppendLine();
        foreach (var option in _mapper.GetOptions(detail))
        {
            text.AppendLine(option.Addable
                ? $"[addable] {option.Label}"
                : $"[not addable] {option.Label} — {option.NotAddableReason}");
        }
        if (_nextCursor is not null)
            text.AppendLine().AppendLine("More results available (Ctrl+L).");
        _detailView.Text = text.ToString();
    }

    /// <summary>
    /// The add flow: pick an addable option (dialog when several), prompt for declared env
    /// vars (masked for secrets), map to a config entry, save, and close the browser.
    /// </summary>
    private void AddSelected()
    {
        var detail = SelectedEntry;
        if (detail is null || App is null)
            return;

        var options = _mapper.GetOptions(detail);
        var addable = options.Where(o => o.Addable).ToList();
        if (addable.Count == 0)
        {
            var reasons = options.Count == 0
                ? "This entry declares no packages or remotes."
                : string.Join("\n", options.Select(o => $"{o.Label}: {o.NotAddableReason}"));
            MessageBox.Query(App, "Cannot add", reasons, "Ok");
            return;
        }

        var chosen = addable[0];
        if (addable.Count > 1)
        {
            var pick = MessageBox.Query(App, "Choose install option",
                "This server can be added in more than one way:",
                addable.Select(o => o.Label).ToArray());
            if (pick is not int pickedIndex || pickedIndex < 0)
                return;
            chosen = addable[pickedIndex];
        }

        var envValues = new Dictionary<string, string?>();
        foreach (var prompt in _mapper.GetEnvPrompts(chosen.Package!))
        {
            var value = PromptForEnvValue(prompt);
            if (value is null && prompt.Variable.IsRequired)
                return; // Cancelled a required variable: abort the whole add.
            if (value is not null)
                envValues[prompt.Variable.Name] = value;
        }

        var descriptor = _mapper.Map(detail, chosen.Package!, envValues, _config.Capabilities.Select(c => c.Name));
        _config.Capabilities.Add(descriptor);
        try
        {
            _editor.Save(_config, _configPath);
        }
        catch (InvalidOperationException problem)
        {
            _config.Capabilities.Remove(descriptor);
            MessageBox.ErrorQuery(App, "Invalid config", problem.Message, "Ok");
            return;
        }

        MessageBox.Query(App, "Added", $"Added as '{descriptor.Name}'.", "Ok");
        App.RequestStop(this);
    }

    /// <summary>
    /// Modal single-field prompt for one environment variable. Enter accepts, Esc cancels
    /// (returns null); secret-looking variables get a masked input.
    /// </summary>
    private string? PromptForEnvValue(EnvPrompt prompt)
    {
        if (App is null)
            return null;

        string? result = null;
        var variable = prompt.Variable;
        using var dialog = new Window
        {
            Title = variable.IsRequired ? $"{variable.Name} (required)" : variable.Name,
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = 60,
            Height = 7,
        };
        var description = new Label { X = 0, Y = 0, Text = variable.Description ?? string.Empty };
        var field = new TextField { X = 0, Y = 2, Width = Dim.Fill(), Secret = prompt.Masked };
        var hint = new Label { X = 0, Y = 4, Text = "Enter: accept   Esc: cancel" };
        dialog.Add(description, field, hint);

        field.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                if (variable.IsRequired && string.IsNullOrWhiteSpace(field.Text))
                    return; // Required: keep the dialog open until a value is given or Esc.
                result = field.Text;
                App.RequestStop(dialog);
                key.Handled = true;
            }
            else if (key == Key.Esc)
            {
                result = null;
                App.RequestStop(dialog);
                key.Handled = true;
            }
        };

        App.Run(dialog);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
