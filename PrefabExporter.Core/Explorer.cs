using System.Text;
using System.Text.Json;

namespace PrefabExporter.Core;

// Finds prefab visual resources through direct asset and reference lookups.
public static class Explorer
{
    // Resolves a prefab and returns its visual resources without saving a report.
    public static async Task<string> References(string assetsPath, string refsPath, string name,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        using var assets = new AssetLookup(assetsPath, ReferenceLookup.TablePath(assetsPath), progress, cancellation);
        using var references = new ReferenceLookup(refsPath, ReferenceLookup.TablePath(refsPath), progress, cancellation);
        var names = new Dictionary<string, IndexedAsset>(StringComparer.OrdinalIgnoreCase);
        var roots = FindRoots(assets, name, names, cancellation);
        if (roots.Count == 0)
        {
            return "Prefab not found.";
        }
        string temporary = Path.GetTempFileName();
        try
        {
            Traverse(assets, references, temporary, roots, names, progress, cancellation);
            return await WritePreview(temporary, name, roots, names, cancellation);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // Selects the prefab and its visual assets for extraction.
    internal static ExtractionPlan SelectForExtraction(string assetsPath,
        string refsPath, string name, IProgress<string>? progress, CancellationToken cancellation)
    {
        using var assets = new AssetLookup(assetsPath, ReferenceLookup.TablePath(assetsPath), progress, cancellation);
        using var references = new ReferenceLookup(refsPath, ReferenceLookup.TablePath(refsPath), progress, cancellation);
        var names = new Dictionary<string, IndexedAsset>(StringComparer.OrdinalIgnoreCase);
        var roots = FindRoots(assets, name, names, cancellation);
        if (roots.Count == 0)
        {
            throw new InvalidOperationException("Prefab not found.");
        }
        string temporary = Path.GetTempFileName();
        try
        {
            var visited = Traverse(assets, references, temporary, roots, names, progress, cancellation);
            var selected = names.Where(pair => roots.Contains(pair.Key) || pair.Value.Type is
                "Mesh" or "Material" or "Texture2D" or "Shader" or "Sprite").Select(pair => pair.Value).ToList();
            var edges = File.ReadLines(temporary).Select(line => JsonSerializer.Deserialize<ReferenceEdge>(line)!).ToList();
            return new ExtractionPlan(selected, visited.Count - names.Count, names, edges, roots);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // Finds prefab roots using the name lookup.
    private static HashSet<string> FindRoots(AssetLookup assets, string name,
        Dictionary<string, IndexedAsset> names, CancellationToken cancellation)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in assets.Find("P:", name, cancellation))
        {
            string key = Key(item.Collection, item.PathId);
            names[key] = item;
            roots.Add(key);
        }
        return roots;
    }

    // Follows visual links once and writes temporary edges for the summary.
    private static HashSet<string> Traverse(AssetLookup assets, ReferenceLookup references, string temporary,
        HashSet<string> roots, Dictionary<string, IndexedAsset> names, IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var visited = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        var frontier = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        using var output = new StreamWriter(temporary, false);
        int depth = 0;
        while (frontier.Count > 0)
        {
            progress?.Report($"Reading visual resources: level {depth + 1}, {visited.Count} objects found...");
            var next = ReadLevel(assets, references, frontier, visited, names, output, depth, cancellation);
            visited.UnionWith(next);
            frontier = next;
            depth++;
        }
        return visited;
    }

    // Reads only links needed for visual resources and related icons.
    private static HashSet<string> ReadLevel(AssetLookup assets, ReferenceLookup references,
        HashSet<string> frontier, HashSet<string> visited, Dictionary<string, IndexedAsset> names,
        StreamWriter output, int depth, CancellationToken cancellation)
    {
        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string from in frontier)
        {
            if (!names.TryGetValue(from, out var source))
            {
                continue;
            }
            foreach (var document in references.Find(from, cancellation))
            {
                using (document)
                {
                    foreach (var reference in document.RootElement.GetProperty("references").EnumerateArray())
                    {
                        string field = reference.GetProperty("field").GetString() ?? "";
                        if (!VisualSummary.IsVisualLink(source.Type, field))
                        {
                            continue;
                        }
                        var edge = MakeEdge(reference, from, depth, assets, names, cancellation);
                        output.WriteLine(JsonSerializer.Serialize(edge));
                        if (!visited.Contains(edge.To))
                        {
                            next.Add(edge.To);
                        }
                    }
                }
            }
        }
        return next;
    }

    // Resolves one target and caches its asset metadata for the summary.
    private static ReferenceEdge MakeEdge(JsonElement reference, string from, int depth, AssetLookup assets,
        Dictionary<string, IndexedAsset> names, CancellationToken cancellation)
    {
        string field = reference.GetProperty("field").GetString() ?? "";
        string key;
        IndexedAsset? item;
        if (reference.TryGetProperty("asset_id", out var value))
        {
            string id = value.GetString() ?? "";
            item = assets.Find("S:", id, cancellation).LastOrDefault();
            key = item == null ? "softref|" + id : Key(item.Collection, item.PathId);
        }
        else
        {
            var collection = reference.GetProperty("collection");
            key = Key(collection.ValueKind == JsonValueKind.String ? collection.GetString() : null,
                reference.GetProperty("path_id").GetInt64());
            item = names.TryGetValue(key, out var known) ? known : assets.Find("K:", key, cancellation).LastOrDefault();
        }
        if (item != null)
        {
            names[key] = item;
        }
        return new ReferenceEdge(depth, from, field, key);
    }

    // Displays the visual files needed by the prefab without saving a report.
    private static async Task<string> WritePreview(string temporary, string name,
        HashSet<string> roots, Dictionary<string, IndexedAsset> names, CancellationToken cancellation)
    {
        var preview = new StringBuilder();
        preview.AppendLine($"Prefab: {name}");
        foreach (string root in roots)
        {
            if (names.TryGetValue(root, out var item))
            {
                preview.AppendLine($"Root: {item.Name} | {item.Type} | {item.AssetPath}");
            }
        }
        preview.Append(await VisualSummary.Build(temporary, roots, names, cancellation));
        return preview.ToString();
    }

    // Builds a key from the collection name and object ID.
    internal static string Key(string? collection, long id)
    {
        string name = (collection ?? "(unknown)").Replace('\\', '/');
        int index = name.LastIndexOf('/');
        if (index >= 0)
        {
            name = name[(index + 1)..];
        }
        return name + "|" + id;
    }
}
