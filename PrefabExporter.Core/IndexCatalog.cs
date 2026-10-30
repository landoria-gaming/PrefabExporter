namespace PrefabExporter.Core;

// Finds completed indexes without persisting indexing status.
public static class IndexCatalog
{
    // Lists complete indexes from newest to oldest.
    public static IReadOnlyList<IndexEntry> Find(string indexRoot)
    {
        var entries = new List<IndexEntry>();
        if (!Directory.Exists(indexRoot))
        {
            return entries;
        }
        foreach (string versionDirectory in Directory.EnumerateDirectories(indexRoot))
        {
            string version = Path.GetFileName(versionDirectory);
            if (!Version.TryParse(version, out _))
            {
                continue;
            }
            AddEntry(entries, versionDirectory, version);
            string installations = Path.Combine(versionDirectory, "installations");
            if (Directory.Exists(installations))
            {
                foreach (string directory in Directory.EnumerateDirectories(installations))
                {
                    AddEntry(entries, directory, version);
                }
            }
        }
        return entries.OrderByDescending(entry => entry.CompletedUtc)
            .ThenBy(entry => entry.Directory, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Adds an index only when its published files are present.
    private static void AddEntry(List<IndexEntry> entries, string directory, string version)
    {
        string assets = Path.Combine(directory, $"assets-{version}.jsonl");
        string references = Path.Combine(directory, $"references-{version}.jsonl");
        string log = Path.Combine(directory, "log.txt");
        if (!File.Exists(assets) || !File.Exists(references) ||
            File.Exists(log + ".previous") || new FileInfo(assets).Length == 0)
        {
            return;
        }
        entries.Add(new IndexEntry(directory, version, File.GetLastWriteTimeUtc(references)));
    }
}
