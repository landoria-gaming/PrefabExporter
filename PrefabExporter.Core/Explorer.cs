using System.Text;
using System.Text.Json;

namespace PrefabExporter.Core;

// Finds prefabs and follows their indexed references without loading the index.
public static class Explorer
{
    private const int PreviewCharacters = 100000;

    // Resolves a prefab and streams its complete reference report to disk.
    public static async Task<string> References(string assetsPath, string refsPath, string name,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var softIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selected = await FindRoots(assetsPath, name, softIds, cancellation);
        if (selected.Count == 0)
        {
            return "Prefab not found.";
        }
        string temporary = Path.GetTempFileName();
        try
        {
            var visited = await Traverse(refsPath, temporary, selected, softIds, progress, cancellation);
            progress?.Report($"Resolving names: {visited.Count} objects...");
            var names = await ResolveNames(assetsPath, visited, cancellation);
            string report = CreateReportPath(assetsPath, name);
            return await WriteReport(report, temporary, name, selected, names, cancellation);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // Follows every reachable object once and writes edges to a temporary file.
    private static async Task<HashSet<string>> Traverse(string refsPath, string temporary,
        HashSet<string> selected, Dictionary<string, string> softIds, IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var visited = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var frontier = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        using var output = new StreamWriter(temporary, false);
        int depth = 0;
        while (frontier.Count > 0)
        {
            progress?.Report($"Reading references: level {depth + 1}, {visited.Count} objects found...");
            var next = await ReadLevel(refsPath, frontier, visited, output, depth, softIds, cancellation);
            visited.UnionWith(next);
            frontier = next;
            depth++;
        }
        return visited;
    }

    // Finds indexed root objects and maps SoftReference IDs to object keys.
    private static async Task<HashSet<string>> FindRoots(string path, string name, Dictionary<string, string> softIds, CancellationToken cancellation)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = File.OpenText(path);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellation)) != null)
        {
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            string key = Key(item.GetProperty("collection").GetString(), item.GetProperty("path_id").GetInt64());
            if (item.TryGetProperty("soft_id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                softIds[id.GetString()!] = key;
            }
            if (IsPrefab(item, name))
            {
                selected.Add(key);
            }
        }
        return selected;
    }

    // Checks whether an asset is the requested prefab root.
    private static bool IsPrefab(JsonElement item, string name)
    {
        return item.GetProperty("type").GetString() == "GameObject" &&
            item.TryGetProperty("asset_path", out var path) && path.ValueKind == JsonValueKind.String &&
            path.GetString()!.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileNameWithoutExtension(path.GetString()), name, StringComparison.OrdinalIgnoreCase);
    }

    // Streams one level of outgoing links and queues unseen targets.
    private static async Task<HashSet<string>> ReadLevel(string path, HashSet<string> frontier,
        HashSet<string> visited, StreamWriter output, int depth, Dictionary<string, string> softIds,
        CancellationToken cancellation)
    {
        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = File.OpenText(path);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellation)) != null)
        {
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            string from = Key(item.GetProperty("collection").GetString(), item.GetProperty("path_id").GetInt64());
            if (!frontier.Contains(from))
            {
                continue;
            }
            foreach (var reference in item.GetProperty("references").EnumerateArray())
            {
                var edge = MakeEdge(reference, from, depth, softIds);
                await output.WriteLineAsync(JsonSerializer.Serialize(edge));
                if (!visited.Contains(edge.To))
                {
                    next.Add(edge.To);
                }
            }
        }
        return next;
    }

    // Creates one edge and resolves its SoftReference target when possible.
    private static ReferenceEdge MakeEdge(JsonElement reference, string from, int depth,
        Dictionary<string, string> softIds)
    {
        string field = reference.GetProperty("field").GetString() ?? "";
        if (reference.TryGetProperty("asset_id", out var value))
        {
            string id = value.GetString() ?? "";
            string target = softIds.TryGetValue(id, out string? found) ? found : "softref|" + id;
            return new ReferenceEdge(depth, from, field + " [SoftRef " + id + "]", target);
        }
        var collection = reference.GetProperty("collection");
        string key = Key(collection.ValueKind == JsonValueKind.String ? collection.GetString() : null,
            reference.GetProperty("path_id").GetInt64());
        return new ReferenceEdge(depth, from, field, key);
    }

    // Looks up names and locations for reachable objects.
    private static async Task<Dictionary<string, (string Name, string Type, string Source)>> ResolveNames(
        string path, HashSet<string> visited, CancellationToken cancellation)
    {
        var names = new Dictionary<string, (string Name, string Type, string Source)>();
        using var reader = File.OpenText(path);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellation)) != null)
        {
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            string key = Key(item.GetProperty("collection").GetString(), item.GetProperty("path_id").GetInt64());
            if (!visited.Contains(key))
            {
                continue;
            }
            string? source = item.TryGetProperty("asset_path", out var assetPath) &&
                assetPath.ValueKind == JsonValueKind.String ? assetPath.GetString() : item.GetProperty("source").GetString();
            names[key] = (item.GetProperty("name").GetString() ?? "", item.GetProperty("type").GetString() ?? "", source ?? "");
        }
        return names;
    }

    // Creates a unique report path next to the index.
    private static string CreateReportPath(string assetsPath, string name)
    {
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(assetsPath))!, "reports");
        Directory.CreateDirectory(directory);
        string safe = string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return Path.Combine(directory, $"references-{safe}-{Guid.NewGuid():N}.txt");
    }

    // Writes the complete report and returns a bounded preview for the window.
    private static async Task<string> WriteReport(string report, string temporary, string name,
        HashSet<string> selected, Dictionary<string, (string Name, string Type, string Source)> names,
        CancellationToken cancellation)
    {
        using var output = new StreamWriter(report, false);
        var preview = new StringBuilder();
        bool truncated = false;
        await WriteLine(output, preview, $"Prefab: {name}");
        foreach (string root in selected)
        {
            await WriteLine(output, preview, "Root: " + Describe(root, names));
        }
        using var reader = File.OpenText(temporary);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellation)) != null)
        {
            var edge = JsonSerializer.Deserialize<ReferenceEdge>(line)!;
            string formatted = $"Level {edge.Depth + 1} — {Describe(edge.From, names)}\n  {edge.Field} → {Describe(edge.To, names)}";
            if (preview.Length + formatted.Length >= PreviewCharacters)
            {
                truncated = true;
            }
            await WriteLine(output, preview, formatted);
        }
        preview.AppendLine($"\nFull report: {report}");
        if (truncated)
        {
            preview.AppendLine("The display is abbreviated; all traversed references are in the report.");
        }
        return preview.ToString();
    }

    // Writes a report line and adds it to the preview when space remains.
    private static async Task WriteLine(StreamWriter output, StringBuilder preview, string line)
    {
        await output.WriteLineAsync(line);
        if (preview.Length + line.Length < PreviewCharacters)
        {
            preview.AppendLine(line);
        }
    }

    // Describes an object or marks an unresolved target.
    private static string Describe(string key, Dictionary<string, (string Name, string Type, string Source)> names)
    {
        return names.TryGetValue(key, out var item)
            ? $"{(string.IsNullOrEmpty(item.Name) ? "(unnamed)" : item.Name)} | {item.Type} | {item.Source} | {key}"
            : $"Unresolved | {key}";
    }

    // Builds a key from the collection name and object ID.
    private static string Key(string? collection, long id)
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
