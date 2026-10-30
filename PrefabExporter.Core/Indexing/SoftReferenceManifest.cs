namespace PrefabExporter.Core;

// Maps paths in game manifests to their SoftReference identifiers.
internal sealed class SoftReferenceManifest
{
    private readonly Dictionary<string, string> idsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> pathsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> bundlesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> scenesByBundle = new(StringComparer.OrdinalIgnoreCase);

    // Reads asset identifiers and paths from all supplied manifests.
    public static SoftReferenceManifest Load(params string[] paths)
    {
        var manifest = new SoftReferenceManifest();
        foreach (string path in paths)
        {
            manifest.Read(path);
        }
        if (manifest.idsByPath.Count == 0)
        {
            throw new InvalidDataException("SoftRef manifests are empty or unrecognized.");
        }
        return manifest;
    }

    // Reads mappings from one manifest and rejects conflicting entries.
    private void Read(string path)
    {
        string? id = null;
        string? bundle = null;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("- asset ID: ", StringComparison.Ordinal))
            {
                id = line[12..].Trim();
            }
            else if (id != null && line.StartsWith("bundle: ", StringComparison.Ordinal))
            {
                bundle = line[8..].Trim();
            }
            else if (id != null && line.StartsWith("path in bundle: ", StringComparison.Ordinal))
            {
                string assetPath = line[16..].Trim();
                Add(id, assetPath);
                if (bundle != null)
                {
                    bundlesById[id] = bundle;
                }
                AddScene(bundle, assetPath);
                id = null;
            }
        }
    }

    // Stores an unambiguous mapping between an ID and an asset path.
    private void Add(string id, string assetPath)
    {
        if ((pathsById.TryGetValue(id, out string? existingPath) &&
             !string.Equals(existingPath, assetPath, StringComparison.OrdinalIgnoreCase)) ||
            (idsByPath.TryGetValue(assetPath, out string? existingId) &&
             !string.Equals(existingId, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Ambiguous SoftReference mapping: {id} {assetPath}");
        }
        idsByPath[assetPath] = id;
        pathsById[id] = assetPath;
    }

    // Records a scene path under its bundle name.
    private void AddScene(string? bundle, string path)
    {
        if (bundle == null || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (!scenesByBundle.TryGetValue(bundle, out var scenes))
        {
            scenes = new List<string>();
            scenesByBundle[bundle] = scenes;
        }
        if (!scenes.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            scenes.Add(path);
        }
    }

    // Returns scene paths stored in a bundle.
    public IReadOnlyList<string> Scenes(string bundle)
    {
        return scenesByBundle.TryGetValue(bundle, out var scenes) ? scenes : Array.Empty<string>();
    }
    // Finds the asset ID associated with a bundle path.
    public string? FindId(string? path)
    {
        return path != null && idsByPath.TryGetValue(path, out string? id) ? id : null;
    }

    // Finds the bundle associated with an ID.
    public string? FindBundle(string id)
    {
        return bundlesById.GetValueOrDefault(id);
    }
    // Finds the asset path associated with an ID.
    public string? FindPath(string id)
    {
        return pathsById.GetValueOrDefault(id);
    }
}
