using System.Text.Json;

namespace PrefabExporter.Core;

// Audits indexed targets using compact object keys rather than whole objects.
internal sealed class ReferenceAuditor
{
    private readonly SoftReferenceManifest manifest;
    private readonly string bundles;
    private readonly CancellationToken cancellation;
    private readonly Dictionary<string, int> collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(int Collection, long Id)> objects = new();
    private readonly Dictionary<string, (int Collection, long Id)> soft = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Category, int Count, string Example)> missing = new();

    // Stores the manifest and paths used to classify missing targets.
    public ReferenceAuditor(SoftReferenceManifest manifest, string bundles, CancellationToken cancellation)
    {
        this.manifest = manifest;
        this.bundles = bundles;
        this.cancellation = cancellation;
    }

    // Audits both JSONL files and writes a grouped text report.
    public void Run(string assets, string references, TextWriter writer)
    {
        ReadAssets(assets);
        ReadReferences(references);
        if (missing.Count == 0)
        {
            return;
        }
        writer.WriteLine($"WARNING: {missing.Count} unresolved targets ({missing.Values.Sum(item => (long)item.Count)} links).");
        writer.WriteLine("Matching uses collection basename and path ID or SoftReference ID.");
        foreach (var group in missing.OrderBy(item => item.Value.Category).ThenBy(item => item.Key)
            .GroupBy(item => item.Value.Category))
        {
            writer.WriteLine($"\n{group.Key}: {group.Count()} targets");
            foreach (var entry in group)
            {
                writer.WriteLine($"  {entry.Key} | occurrences: {entry.Value.Count}\n    Example: {entry.Value.Example}");
            }
        }
    }

    // Reads object identities and SoftReference IDs without retaining their contents.
    private void ReadAssets(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            cancellation.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            var asset = document.RootElement;
            var key = (GetCollection(asset.GetProperty("collection").GetString()), asset.GetProperty("path_id").GetInt64());
            objects.Add(key);
            if (asset.TryGetProperty("soft_id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                soft[id.GetString()!] = key;
            }
        }
    }

    // Checks every indexed reference against the known targets.
    private void ReadReferences(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            cancellation.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            var source = document.RootElement;
            foreach (var reference in source.GetProperty("references").EnumerateArray())
            {
                CheckReference(source, reference);
            }
        }
    }

    // Classifies an unresolved SoftReference or Unity pointer.
    private void CheckReference(JsonElement source, JsonElement reference)
    {
        string field = reference.GetProperty("field").GetString() ?? "";
        string example = $"{source.GetProperty("collection").GetString()} | {source.GetProperty("path_id")} | {field}";
        if (reference.TryGetProperty("asset_id", out var value))
        {
            string id = value.GetString() ?? "";
            if (!soft.ContainsKey(id))
            {
                Record("SoftRef " + id, ClassifySoftReference(id), example);
            }
            return;
        }
        var collection = reference.GetProperty("collection");
        string? name = collection.ValueKind == JsonValueKind.String ? collection.GetString() : null;
        long idValue = reference.GetProperty("path_id").GetInt64();
        int collectionId = FindCollection(name);
        if (collectionId < 0)
        {
            Record($"{name ?? "(unknown collection)"} | {idValue}", "Collection not indexed or missing", example);
        }
        else if (!objects.Contains((collectionId, idValue)))
        {
            Record($"{name} | {idValue}", "Object ID not found in indexed collection", example);
        }
    }

    // Distinguishes unknown IDs, missing bundles, and unmatched manifest entries.
    private string ClassifySoftReference(string id)
    {
        string? path = manifest.FindPath(id);
        if (path == null)
        {
            return "SoftReference ID absent from manifests";
        }
        string? bundle = manifest.FindBundle(id);
        if (bundle == null || !File.Exists(Path.Combine(bundles, bundle)))
        {
            return "Manifest target bundle missing";
        }
        return "Manifest target not matched to indexed object";
    }

    // Records one missing target and an example source.
    private void Record(string key, string category, string example)
    {
        if (missing.TryGetValue(key, out var existing))
        {
            missing[key] = (existing.Category, existing.Count + 1, existing.Example);
        }
        else
        {
            missing[key] = (category, 1, example);
        }
    }

    // Returns or creates a compact numeric ID for a collection.
    private int GetCollection(string? name)
    {
        string key = CollectionName(name);
        if (!collections.TryGetValue(key, out int id))
        {
            id = collections.Count;
            collections.Add(key, id);
        }
        return id;
    }

    // Looks up an indexed collection without adding it.
    private int FindCollection(string? name)
    {
        return collections.TryGetValue(CollectionName(name), out int id) ? id : -1;
    }

    // Uses the same collection normalization as the reference explorer.
    private static string CollectionName(string? name)
    {
        string value = (name ?? "(unknown)").Replace('\\', '/');
        int index = value.LastIndexOf('/');
        return index >= 0 ? value[(index + 1)..] : value;
    }
}
