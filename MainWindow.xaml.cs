using System.IO;
using System.Text;
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
        if (IndexSelector.SelectedItem is not IndexEntry entry)
        {
            UpdateSearchAvailability();
            return;
        }
        Output.Text = $"Selected index: {entry.Version}";
        UpdateSearchAvailability();
    }

    // Enables searches only when a completed index is selected.
    private void UpdateSearchAvailability()
    {
        bool available = cancel == null;
        bool selected = IndexSelector.SelectedItem is IndexEntry;
        SearchButton.IsEnabled = available && selected;
        RefsButton.IsEnabled = available && selected;
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
        IndexButton.IsEnabled = !indexing && !versionAlreadyIndexed;
        IndexButton.ToolTip = versionAlreadyIndexed ? "A completed index for this game version already exists." : null;
        bool selected = IndexSelector.SelectedItem is IndexEntry;
        SearchButton.IsEnabled = !indexing && selected;
        RefsButton.IsEnabled = !indexing && selected;
        IndexSelector.IsEnabled = !indexing && IndexSelector.Items.Count > 0;
        CancelButton.IsEnabled = indexing;
    }

    // Requests cancellation of the current indexing run.
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        cancel?.Cancel();
    }

    // Searches for prefabs matching the entered text.
    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        string query = PrefabName.Text.Trim();
        if (query.Length == 0)
        {
            Output.Text = "Enter a prefab name.";
            return;
        }
        await Execute(token => FindPrefabs(GetIndex("assets"), query, token));
    }

    // Lists indexed prefab names matching a search term.
    private static async Task<string> FindPrefabs(string path, string query, CancellationToken cancellation)
    {
        var result = new StringBuilder();
        using var reader = File.OpenText(path);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellation)) != null)
        {
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            string name = item.GetProperty("name").GetString() ?? "";
            if (item.GetProperty("type").GetString() == "GameObject" &&
                item.TryGetProperty("asset_path", out var assetPath) && assetPath.ValueKind == JsonValueKind.String &&
                assetPath.GetString()!.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result.AppendLine($"{name} | {assetPath.GetString()} | {item.GetProperty("path_id")}");
            }
            if (result.Length > 100000)
            {
                result.AppendLine("Results truncated.");
                break;
            }
        }
        return result.Length == 0 ? "No results." : result.ToString();
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
        await Execute(token => Explorer.References(GetIndex("assets"), GetIndex("references"), query, progress, token));
    }

    // Returns a file from the selected completed index.
    private string GetIndex(string name)
    {
        if (IndexSelector.SelectedItem is not IndexEntry entry)
        {
            throw new InvalidOperationException("Select a completed index first.");
        }
        string assets = Path.Combine(entry.Directory, $"assets-{entry.Version}.jsonl");
        string references = Path.Combine(entry.Directory, $"references-{entry.Version}.jsonl");
        if (File.Exists(Path.Combine(entry.Directory, "log.txt.previous")) || !File.Exists(assets) || !File.Exists(references))
        {
            throw new InvalidOperationException("The selected index is no longer complete.");
        }
        return Path.Combine(entry.Directory, $"{name}-{entry.Version}.jsonl");
    }

    // Runs a search and displays its result or error.
    private async Task Execute(Func<CancellationToken, Task<string>> action)
    {
        SetIndexingState(true);
        cancel = new CancellationTokenSource();
        try
        {
            Output.Text = "Searching...";
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
