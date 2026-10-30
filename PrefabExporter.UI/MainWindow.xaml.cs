using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PrefabExporter.Core;

namespace PrefabExporter.UI;

// Provides the prefab search and extraction interface.
public partial class MainWindow : Window
{
    private Settings? settings;
    private IReadOnlyList<GamePrefab> prefabs = [];
    private CancellationTokenSource? suggestions;
    private bool suggestionsDisplayed;
    private bool selectingSuggestion;
    private GamePrefab? selectedPrefab;

    // Initializes the window and starts reading the game manifest.
    public MainWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
        SetBusy(true);
    }

    // Reads configuration and prefab names without blocking the window.
    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            settings = JsonSerializer.Deserialize<Settings>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (settings == null || string.IsNullOrWhiteSpace(settings.ValheimPath) ||
                string.IsNullOrWhiteSpace(settings.AssetRipperPath))
            {
                throw new InvalidDataException("Set ValheimPath and AssetRipperPath in appsettings.json.");
            }
            Output.Text = "Reading prefab names from the game manifest...";
            prefabs = await Task.Run(() => GamePrefabs.List(settings.ValheimPath));
            string version = await Task.Run(() => DetectVersion(settings.ValheimPath));
            GameStatus.Text = $"Valheim path: {settings.ValheimPath} | Version: {version}";
            GameStatus.ToolTip = GameStatus.Text;
            Output.Text = $"Loaded {prefabs.Count:N0} prefab entries from the game manifest.";
        }
        catch (Exception error)
        {
            settings = null;
            ApplicationLog.Error(error);
            Output.Text = "Error: " + error.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Reads the game version when the managed assembly is available.
    private static string DetectVersion(string gamePath)
    {
        try
        {
            return GameVersion.Version(gamePath);
        }
        catch (Exception)
        {
            return "unavailable";
        }
    }

    // Enables controls for the current operation state.
    private void SetBusy(bool busy)
    {
        bool available = !busy && settings != null;
        PrefabName.IsEnabled = available;
        RefsButton.IsEnabled = available;
        ExtractButton.IsEnabled = available;
        OperationProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OperationProgressBar.IsIndeterminate = busy;
    }

    // Updates autocomplete after the search text changes.
    private async void PrefabName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || selectingSuggestion)
        {
            return;
        }
        selectedPrefab = null;
        suggestionsDisplayed = false;
        suggestions?.Cancel();
        string query = PrefabName.Text.Trim();
        if (query.Length < 2 || settings == null || !PrefabName.IsEnabled)
        {
            if (query.Length < 2)
            {
                Output.Clear();
            }
            return;
        }
        var request = new CancellationTokenSource();
        suggestions = request;
        try
        {
            await Task.Delay(150, request.Token);
            var matches = prefabs.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            request.Token.ThrowIfCancellationRequested();
            Output.Text = matches.Length == 0 ? "No results." :
                string.Join(Environment.NewLine, matches.Select(item => $"{item.Name} | {item.Path}"));
            suggestionsDisplayed = matches.Length > 0;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (suggestions == request)
            {
                suggestions = null;
            }
            request.Dispose();
        }
    }

    // Selects a prefab suggestion from the text area.
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
        selectedPrefab = prefabs.FirstOrDefault(item => item.Path.Equals(text[(separator + 3)..], StringComparison.OrdinalIgnoreCase));
        if (selectedPrefab == null)
        {
            return;
        }
        selectingSuggestion = true;
        PrefabName.Text = selectedPrefab.Name;
        selectingSuggestion = false;
        suggestionsDisplayed = false;
        Output.Text = text;
    }

    // Displays the prefab's visual references using AssetRipper.
    private async void References_Click(object sender, RoutedEventArgs e)
    {
        await Execute(false);
    }

    // Exports the prefab's assets and GLB files.
    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        await Execute(true);
    }

    // Resolves an exact prefab name and handles ambiguous names.
    private GamePrefab ResolvePrefab()
    {
        string name = PrefabName.Text.Trim();
        if (selectedPrefab != null && selectedPrefab.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return selectedPrefab;
        }
        var matches = prefabs.Where(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(matches.Length == 0 ? "Prefab not found in the game manifest." :
                "Multiple prefabs have this name. Double-click a suggestion to choose its path.");
        }
        return matches[0];
    }

    // Runs an operation and displays its result or error.
    private async Task Execute(bool extract)
    {
        try
        {
            var prefab = ResolvePrefab();
            suggestions?.Cancel();
            suggestionsDisplayed = false;
            SetBusy(true);
            var progress = new Progress<string>(message => Output.Text = message);
            Output.Text = "Loading prefab...";
            string result = await Task.Run(() => DirectPrefabAccess.Run(prefab, settings!.AssetRipperPath,
                AppContext.BaseDirectory, extract, progress, CancellationToken.None));
            ApplicationLog.Warnings(result);
            Output.Text = result;
        }
        catch (Exception error)
        {
            ApplicationLog.Error(error);
            Output.Text = "Error: " + error.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }
}
