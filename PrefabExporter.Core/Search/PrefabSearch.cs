using System.Text;
using System.Text.Json;

namespace PrefabExporter.Core;

// Builds and searches a compact alphabetical list of prefabs.
public static class PrefabSearch
{
    // Finds matching prefabs using a reusable list derived from the asset index.
    public static IReadOnlyList<PrefabSuggestion> Find(string assets, string query, CancellationToken cancellation)
    {
        string directory = Path.GetDirectoryName(assets)!;
        string cache = Path.Combine(directory, "prefabs-" + Path.GetFileNameWithoutExtension(assets)[7..] + ".jsonl");
        if (!File.Exists(cache) || File.GetLastWriteTimeUtc(cache) < File.GetLastWriteTimeUtc(assets))
        {
            throw new InvalidOperationException("The prefab list is missing or outdated. Rebuild this index to search it.");
        }
        var result = new List<PrefabSuggestion>();
        foreach (string line in File.ReadLines(cache))
        {
            cancellation.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            string name = document.RootElement.GetProperty("name").GetString() ?? "";
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new PrefabSuggestion(name, document.RootElement.GetProperty("path").GetString() ?? ""));
            }
        }
        return result;
    }

    // Writes a sorted prefab list as part of an indexing operation.
    internal static void Build(string assets, string destination, IProgress<string> progress,
        CancellationToken cancellation)
    {
        var prefabs = ReadPrefabs(assets, progress, cancellation);
        prefabs.Sort((a, b) =>
        {
            int comparison = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Path, b.Path);
        });
        using var writer = new StreamWriter(destination, false, new UTF8Encoding(false));
        foreach (var prefab in prefabs)
        {
            cancellation.ThrowIfCancellationRequested();
            writer.WriteLine(JsonSerializer.Serialize(new { name = prefab.Name, path = prefab.Path }));
        }
    }

    // Streams asset records and keeps only prefab names and paths.
    private static List<(string Name, string Path)> ReadPrefabs(string assets, IProgress<string> progress,
        CancellationToken cancellation)
    {
        var prefabs = new List<(string Name, string Path)>();
        using var reader = new BinaryIndexReader(assets);
        long nextReport = 0;
        IndexedAsset? item;
        while ((item = reader.ReadAsset()) != null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (reader.Position >= nextReport)
            {
                progress.Report($"Building prefab list... {reader.Position * 100 / reader.Length}%");
                nextReport = reader.Position + 16 * 1024 * 1024;
            }
            if (item.Type == "GameObject" && item.AssetPath != null &&
                item.AssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                prefabs.Add((item.Name ?? "", item.AssetPath));
            }
        }
        return prefabs;
    }
}
