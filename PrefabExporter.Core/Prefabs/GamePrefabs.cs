namespace PrefabExporter.Core;

// Finds prefabs and their bundles using the game's SoftRef manifests.
public static class GamePrefabs
{
    // Reads and sorts prefab entries directly from the configured game.
    public static IReadOnlyList<GamePrefab> List(string gamePath)
    {
        string data = Directory.EnumerateDirectories(gamePath, "*_Data").FirstOrDefault()
            ?? throw new DirectoryNotFoundException("Game data directory not found.");
        string root = Path.Combine(data, "StreamingAssets", "SoftRef");
        var entries = new Dictionary<string, GamePrefab>(StringComparer.OrdinalIgnoreCase);
        var iconBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in new[] { "manifest", "manifest_extended" })
        {
            ReadManifest(Path.Combine(root, file), root, entries, iconBundles);
        }
        if (entries.Count == 0)
        {
            throw new InvalidDataException("No prefab entries found in the game manifests.");
        }
        return entries.Values.Select(item => item with { IconBundles = iconBundles.ToArray() })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Reads bundle and prefab paths from one manifest.
    private static void ReadManifest(string file, string root, Dictionary<string, GamePrefab> entries, HashSet<string> iconBundles)
    {
        if (!File.Exists(file))
        {
            return;
        }
        string? bundle = null;
        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.StartsWith("- asset ID: ", StringComparison.Ordinal))
            {
                bundle = null;
            }
            else if (line.StartsWith("bundle: ", StringComparison.Ordinal))
            {
                bundle = line[8..].Trim();
            }
            else if (bundle != null && line.StartsWith("path in bundle: ", StringComparison.Ordinal))
            {
                string path = line[16..].Trim().Replace('\\', '/');
                if (path.Contains("/icons/", StringComparison.OrdinalIgnoreCase))
                {
                    iconBundles.Add(Path.Combine(root, "Bundles", bundle));
                }
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    entries[path] = new GamePrefab(name, path, Path.Combine(root, "Bundles", bundle), []);
                }
            }
        }
    }
}
