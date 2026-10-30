using System.Text.Json;

namespace PrefabExporter.Core;

// Provides random access to reference records using a compact in-memory offset list.
internal sealed class ReferenceLookup : IDisposable
{
    private readonly BinaryIndexReader file;
    private readonly string sourcePath;
    private readonly List<ReferencePosition> positions = new();

    // Opens the reference file and builds offsets without retaining its contents.
    public ReferenceLookup(string path, string? tablePath, IProgress<string>? progress, CancellationToken cancellation)
    {
        sourcePath = path;
        file = new BinaryIndexReader(path);
        try
        {
            if (tablePath == null)
            {
                Build(progress, cancellation);
                positions.Sort((a, b) => a.Hash.CompareTo(b.Hash));
            }
            else if (!ReferenceLookupFile.TryLoad(tablePath, path, positions, cancellation))
            {
                throw new InvalidDataException("The reference lookup is missing or invalid. Rebuild the index.");
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    // Returns the table path associated with a binary reference file.
    public static string TablePath(string references)
    {
        string name = Path.GetFileNameWithoutExtension(references);
        return Path.Combine(Path.GetDirectoryName(references)!, name + ".offsets.bin");
    }

    // Saves the sorted offsets for reuse by later searches.
    public void Save(string path, CancellationToken cancellation)
    {
        ReferenceLookupFile.Save(path, sourcePath, positions, cancellation);
    }

    // Computes a stable, case-insensitive key hash across application runs.
    internal static long Hash(string key)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char character in key.ToUpperInvariant())
        {
            hash = unchecked((hash ^ character) * 1099511628211UL);
        }
        return unchecked((long)hash);
    }

    // Streams binary records and stores their source keys and byte positions.
    private void Build(IProgress<string>? progress, CancellationToken cancellation)
    {
        long nextReport = 0;
        while (file.Position < file.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            long offset = file.Position;
            using var document = file.Read() ?? throw new EndOfStreamException();
            var item = document.RootElement;
            string key = Explorer.Key(item.GetProperty("collection").GetString(), item.GetProperty("path_id").GetInt64());
            positions.Add(new ReferencePosition(Hash(key), offset, checked((int)(file.Position - offset))));
            if (file.Position >= nextReport)
            {
                progress?.Report($"Preparing reference lookup... {file.Position * 100 / file.Length}%");
                nextReport = file.Position + 32 * 1024 * 1024;
            }
        }
    }

    // Reads all source records for a key and checks keys to handle hash collisions.
    public IEnumerable<JsonDocument> Find(string key, CancellationToken cancellation)
    {
        long hash = Hash(key);
        int low = 0;
        int high = positions.Count;
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
            var position = positions[i];
            var document = file.ReadAt(position.Offset);
            var item = document.RootElement;
            string actual = Explorer.Key(item.GetProperty("collection").GetString(), item.GetProperty("path_id").GetInt64());
            if (string.Equals(key, actual, StringComparison.OrdinalIgnoreCase))
            {
                yield return document;
            }
            else
            {
                document.Dispose();
            }
        }
    }

    // Closes the reference file.
    public void Dispose()
    {
        file.Dispose();
    }
}
