using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PrefabExporter.Core;

namespace PrefabExporter;

// Provides the minimal prefab search interface.
public partial class MainWindow : Window
{
    private Settings? settings;
    private CancellationTokenSource? cancel;
    private bool versionAlreadyIndexed;
    private CancellationTokenSource? suggestionsCancellation;
    private bool suggestionsDisplayed;
    private bool selectingSuggestion;

    // Initializes the window and reads its configuration.
    public MainWindow()
    {
        InitializeComponent();
        try
        {
            settings = JsonSerializer.Deserialize<Settings>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (settings == null || string.IsNullOrWhiteSpace(settings.ValheimPath) ||
                string.IsNullOrWhiteSpace(settings.IndexPath))
            {
                throw new InvalidDataException("Set ValheimPath and IndexPath in appsettings.json next to the application.");
            }
            settings = settings with { IndexPath = Path.GetFullPath(settings.IndexPath, AppContext.BaseDirectory) };
            Output.Text = "Configuration loaded. Index the game before searching.";
            RefreshIndexes();
        }
        catch (Exception error)
        {
            ApplicationLog.Error(error);
            Output.Text = error.Message;
            IndexButton.IsEnabled = false;
            UpdateSearchAvailability();
        }
    }

    // Lists completed indexes and selects the most recently completed one.
    private void RefreshIndexes()
    {
        if (settings == null)
        {
            return;
        }
        var indexes = IndexCatalog.Find(settings.IndexPath);
        string? version = DetectGameVersion(settings.ValheimPath);
        GameStatus.Text = $"Valheim path: {settings.ValheimPath} | Version: {version ?? "unavailable"}";
        GameStatus.ToolTip = GameStatus.Text;
        versionAlreadyIndexed = version != null && indexes.Any(entry =>
            string.Equals(entry.Version, version, StringComparison.OrdinalIgnoreCase));
        IndexSelector.ItemsSource = indexes;
        IndexSelector.SelectedIndex = IndexSelector.Items.Count > 0 ? 0 : -1;
        SetIndexingState(cancel != null);
    }

    // Detects the configured game version when its files are available.
    private static string? DetectGameVersion(string gamePath)
    {
        try
        {
            return Indexer.Version(gamePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Shows saved diagnostics when the selected index changes.
    private void IndexSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CancelSuggestions();
        if (IndexSelector.SelectedItem is not IndexEntry entry)
        {
            UpdateSearchAvailability();
            return;
        }
        Output.Text = $"Selected index: {entry.Version}";
        UpdateSearchAvailability();
        _ = UpdateSuggestions();
    }

    // Enables searches only when a completed index is selected.
    private void UpdateSearchAvailability()
    {
        bool available = cancel == null;
        bool selected = IndexSelector.SelectedItem is IndexEntry;
        PrefabName.IsEnabled = available && selected;
        RefsButton.IsEnabled = available && selected;
        ExtractButton.IsEnabled = available && selected;
        UnityExportButton.IsEnabled = available && selected;
        IndexSelector.IsEnabled = available && IndexSelector.Items.Count > 0;
    }

    // Starts indexing without blocking the window.
    private async void Index_Click(object sender, RoutedEventArgs e)
    {
        if (settings == null)
        {
            return;
        }
        long logStart = ApplicationLog.Length;
        SetIndexingState(true);
        cancel = new CancellationTokenSource();
        IndexProgressBar.Value = 0;
        IndexProgressBar.Visibility = Visibility.Visible;
        IndexProgressBar.IsIndeterminate = true;
        try
        {
            var progress = new Progress<string>(message => Output.Text = message);
            var indexingProgress = new Progress<IndexProgress>(UpdateIndexProgress);
            await Task.Run(() => Indexer.Run(settings, progress, cancel.Token, indexingProgress, ApplicationLog.Writer));
            IndexProgressBar.IsIndeterminate = false;
            IndexProgressBar.Value = IndexProgressBar.Maximum;
            RefreshIndexes();
            ShowIndexDiagnostics(logStart, null);
        }
        catch (OperationCanceledException)
        {
            IndexProgressBar.Value = 0;
            Output.Text = "Indexing canceled.";
        }
        catch (Exception error)
        {
            IndexProgressBar.Value = 0;
            ShowIndexDiagnostics(logStart, error.Message);
        }
        finally
        {
            IndexProgressBar.IsIndeterminate = false;
            IndexProgressBar.Visibility = Visibility.Collapsed;
            cancel.Dispose();
            cancel = null;
            SetIndexingState(false);
        }
    }

    // Updates the progress bar for file processing and reference auditing.
    private void UpdateIndexProgress(IndexProgress progress)
    {
        IndexProgressBar.Maximum = Math.Max(progress.TotalFiles, 1);
        IndexProgressBar.Value = progress.CompletedFiles;
        IndexProgressBar.IsIndeterminate = progress.IsAuditing;
    }

    // Shows the indexing result and diagnostics written during this run.
    private void ShowIndexDiagnostics(long start, string? error)
    {
        string details = ApplicationLog.ReadSince(start);
        string heading = error != null ? "Indexing failed: " + error :
            string.IsNullOrWhiteSpace(details) ? "Indexing complete. No warnings or errors." :
            "Indexing complete. Warnings and errors:";
        Output.Text = string.IsNullOrWhiteSpace(details) ? heading : heading + "\n\n" + details;
    }

    // Enables controls appropriate to the current indexing state.
    private void SetIndexingState(bool indexing)
    {
        if (indexing)
        {
            CancelSuggestions();
        }
        IndexButton.IsEnabled = !indexing && !versionAlreadyIndexed;
        IndexButton.ToolTip = versionAlreadyIndexed ? "A completed index for this game version already exists." : null;
        bool selected = IndexSelector.SelectedItem is IndexEntry;
        PrefabName.IsEnabled = !indexing && selected;
        RefsButton.IsEnabled = !indexing && selected;
        ExtractButton.IsEnabled = !indexing && selected;
        UnityExportButton.IsEnabled = !indexing && selected;
        IndexSelector.IsEnabled = !indexing && IndexSelector.Items.Count > 0;
        CancelButton.IsEnabled = indexing;
    }

    // Requests cancellation of the current indexing run.
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        cancel?.Cancel();
    }

    // Starts autocomplete after the search text changes.
    private async void PrefabName_TextChanged(object sender, TextChangedEventArgs e)
    {
        await UpdateSuggestions();
    }

    // Schedules suggestions for the current text and selected index.
    private async Task UpdateSuggestions()
    {
        if (!IsLoaded || selectingSuggestion)
        {
            return;
        }
        CancelSuggestions();
        string query = PrefabName.Text.Trim();
        if (query.Length < 2 || IndexSelector.SelectedItem is not IndexEntry || cancel != null)
        {
            if (query.Length < 2)
            {
                Output.Clear();
            }
            return;
        }
        var request = new CancellationTokenSource();
        suggestionsCancellation = request;
        try
        {
            await LoadSuggestions(query, request.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested)
            {
                ApplicationLog.Error(error);
                Output.Text = "Error: " + error.Message;
            }
        }
        finally
        {
            if (suggestionsCancellation == request)
            {
                suggestionsCancellation = null;
            }
            request.Dispose();
        }
    }

    // Reads matching prefabs in the background and displays the current result.
    private async Task LoadSuggestions(string query, CancellationToken cancellation)
    {
        string assets = GetIndex("assets");
        await Task.Delay(150, cancellation);
        var matches = await Task.Run(() => PrefabSearch.Find(assets, query, cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        Output.Text = matches.Count == 0 ? "No results." :
            string.Join(Environment.NewLine, matches.Select(item => $"{item.Name} | {item.Path}"));
        suggestionsDisplayed = matches.Count > 0;
    }

    // Stops an obsolete autocomplete request.
    private void CancelSuggestions()
    {
        suggestionsDisplayed = false;
        suggestionsCancellation?.Cancel();
        suggestionsCancellation = null;
    }

    // Selects a suggestion from the text area on double-click.
    private void Output_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!suggestionsDisplayed)
        {
            return;
        }
        int line = Output.GetLineIndexFromCharacterIndex(Output.CaretIndex);
        string text = Output.GetLineText(line).TrimEnd('\r', '\n');
        int separator = text.IndexOf(" | ", StringComparison.Ordinal);
        if (separator < 0)
        {
            return;
        }
        selectingSuggestion = true;
        PrefabName.Text = text[..separator];
        selectingSuggestion = false;
        CancelSuggestions();
        Output.Text = text;
    }

    // Follows references for an exact prefab name.
    private async void References_Click(object sender, RoutedEventArgs e)
    {
        string query = PrefabName.Text.Trim();
        if (query.Length == 0)
        {
            Output.Text = "Enter the exact prefab name.";
            return;
        }
        var progress = new Progress<string>(message => Output.Text = message);
        await Execute(() =>
        {
            string assets = GetIndex("assets");
            string references = GetIndex("references");
            return token => Explorer.References(assets, references, query, progress, token);
        });
    }

    // Extracts the selected prefab resources into the application output directory.
    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        string query = PrefabName.Text.Trim();
        if (query.Length == 0)
        {
            Output.Text = "Enter the exact prefab name.";
            return;
        }
        var progress = new Progress<string>(message => Output.Text = message);
        await Execute(() =>
        {
            string assets = GetIndex("assets");
            string references = GetIndex("references");
            return token => PrefabExtractor.Extract(assets, references, query, AppContext.BaseDirectory, progress, token, ApplicationLog.Writer, settings?.AssetRipperPath);
        });
    }

    // Exports a Unity project containing the selected prefab and loaded assets.
    private async void UnityExport_Click(object sender, RoutedEventArgs e)
    {
        string query = PrefabName.Text.Trim();
        if (query.Length == 0)
        {
            Output.Text = "Enter the exact prefab name.";
            return;
        }
        var progress = new Progress<string>(message => Output.Text = message);
        await Execute(() =>
        {
            string assets = GetIndex("assets");
            string references = GetIndex("references");
            return token => UnityProjectExporter.Export(assets, references, query, AppContext.BaseDirectory,
                settings?.AssetRipperPath, progress, token);
        });
    }

    // Returns a file from the selected completed index.
    private string GetIndex(string name)
    {
        if (IndexSelector.SelectedItem is not IndexEntry entry)
        {
            throw new InvalidOperationException("Select a completed index first.");
        }
        string assets = Path.Combine(entry.Directory, $"assets-{entry.Version}.bin");
        string references = Path.Combine(entry.Directory, $"references-{entry.Version}.bin");
        if (File.Exists(Path.Combine(entry.Directory, "log.txt.previous")) || !File.Exists(assets) || !File.Exists(references))
        {
            throw new InvalidOperationException("The selected index is no longer complete.");
        }
        return Path.Combine(entry.Directory, $"{name}-{entry.Version}.bin");
    }

    // Runs a search and displays its result or error.
    private async Task Execute(Func<Func<CancellationToken, Task<string>>> prepare)
    {
        SetIndexingState(true);
        cancel = new CancellationTokenSource();
        try
        {
            Output.Text = "Searching...";
            var action = prepare();
            Output.Text = await Task.Run(() => action(cancel.Token));
        }
        catch (OperationCanceledException)
        {
            Output.Text = "Search canceled.";
        }
        catch (Exception error)
        {
            ApplicationLog.Error(error);
            Output.Text = "Error: " + error.Message;
        }
        finally
        {
            cancel.Dispose();
            cancel = null;
            SetIndexingState(false);
        }
    }
}
