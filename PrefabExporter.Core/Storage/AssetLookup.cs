namespace PrefabExporter.Core;

// Provides direct access to asset records by object key, prefab name, or SoftReference ID.
internal sealed class AssetLookup : IDisposable
{
    private readonly BinaryIndexReader file;
    private readonly string source;
    private readonly List<ReferencePosition> positions = new();

    // Opens an asset index and either loads or builds its offset table.
    public AssetLookup(string path, string? table, IProgress<string>? progress, CancellationToken cancellation)
    {
        source = path;
        file = new BinaryIndexReader(path);
        try
        {
            if (table == null)
            {
                Build(progress, cancellation);
                positions.Sort((a, b) => a.Hash.CompareTo(b.Hash));
            }
            else if (!ReferenceLookupFile.TryLoad(table, path, positions, cancellation))
            {
                throw new InvalidDataException("The asset lookup is missing or invalid.");
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    // Writes the lookup table for future searches.
    public void Save(string path, CancellationToken cancellation)
    {
        ReferenceLookupFile.Save(path, source, positions, cancellation);
    }

    // Reads assets once and indexes object, prefab, and SoftReference keys.
    private void Build(IProgress<string>? progress, CancellationToken cancellation)
    {
        long nextReport = 0;
        while (file.Position < file.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            long offset = file.Position;
            var item = file.ReadAsset() ?? throw new EndOfStreamException();
            int length = checked((int)(file.Position - offset));
            Add("K:", Explorer.Key(item.Collection, item.PathId), offset, length);
            if (PrefabName(item) is string prefab)
            {
                Add("P:", prefab, offset, length);
            }
            if (!string.IsNullOrEmpty(item.SoftId))
            {
                Add("S:", item.SoftId, offset, length);
            }
            if (file.Position >= nextReport)
            {
                progress?.Report($"Building asset lookup... {file.Position * 100 / file.Length}%");
                nextReport = file.Position + 32 * 1024 * 1024;
            }
        }
    }

    // Adds a hashed key and the source record position.
    private void Add(string prefix, string key, long offset, int length)
    {
        positions.Add(new ReferencePosition(ReferenceLookup.Hash(prefix + key), offset, length));
    }

    // Returns assets whose stored key matches, checking for hash collisions.
    public IEnumerable<IndexedAsset> Find(string prefix, string key, CancellationToken cancellation)
    {
        long hash = ReferenceLookup.Hash(prefix + key);
        int low = 0, high = positions.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (positions[middle].Hash < hash)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        for (int i = low; i < positions.Count && positions[i].Hash == hash; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var item = file.ReadAssetAt(positions[i].Offset);
            string? actual = prefix switch
            {
                "K:" => Explorer.Key(item.Collection, item.PathId),
                "P:" => PrefabName(item),
                "S:" => item.SoftId,
                _ => null
            };
            if (string.Equals(key, actual, StringComparison.OrdinalIgnoreCase))
            {
                yield return item;
            }
        }
    }

    // Returns the path-based prefab name for a root GameObject.
    private static string? PrefabName(IndexedAsset item)
    {
        return item.Type == "GameObject" && item.AssetPath != null &&
            item.AssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(item.AssetPath) : null;
    }

    // Closes the asset index file.
    public void Dispose()
    {
        file.Dispose();
    }
}
