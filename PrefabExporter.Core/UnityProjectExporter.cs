namespace PrefabExporter.Core;

// Exports the selected prefab's source files as an AssetRipper Unity project.
public static class UnityProjectExporter
{
    // Loads the prefab's known source files and exports them without overwriting existing output.
    public static async Task<string> Export(string assetsPath, string refsPath, string name, string appDirectory,
        string? assetRipperPath, IProgress<string>? progress, CancellationToken cancellation)
    {
        ValidateName(name);
        var plan = Explorer.SelectForExtraction(assetsPath, refsPath, name, progress, cancellation);
        string[] files = plan.Nodes.Values.Select(asset => asset.Source).Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0 || files.Any(path => !File.Exists(path)))
        {
            throw new FileNotFoundException("One or more required game files are missing.");
        }
        string destination = Path.Combine(appDirectory, "output", name, "unity");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"Output folder already exists: {destination}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string stage = Path.Combine(Path.GetDirectoryName(destination)!, ".unity-" + Guid.NewGuid().ToString("N"));
        return await ExportFiles(files, name, destination, stage, plan.Missing, appDirectory, assetRipperPath, progress, cancellation);
    }

    // Rejects names that could escape the output directory.
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
        {
            throw new ArgumentException("Enter a valid exact prefab name.");
        }
    }

    // Exports into a temporary folder and publishes it when the prefab exists.
    private static async Task<string> ExportFiles(string[] files, string name, string destination, string stage,
        int missing, string appDirectory, string? assetRipperPath, IProgress<string>? progress, CancellationToken cancellation)
    {
        try
        {
            using var session = new AssetRipperSession(AssetRipperLocation.Resolve(assetRipperPath, appDirectory));
            await session.WaitUntilReady(cancellation);
            progress?.Report($"Loading {files.Length} game files in AssetRipper...");
            await session.Load(files, cancellation);
            progress?.Report("Exporting Unity project with AssetRipper. This can take several minutes and include many other assets...");
            await session.ExportUnityProject(stage, cancellation);
            cancellation.ThrowIfCancellationRequested();
            string[] prefabs = Directory.Exists(stage) ? Directory.GetFiles(stage, name + ".prefab", SearchOption.AllDirectories) : [];
            if (prefabs.Length == 0)
            {
                throw new InvalidDataException("AssetRipper did not export the selected prefab.");
            }
            string[] relative = prefabs.Select(path => Path.GetRelativePath(stage, path)).ToArray();
            Directory.Move(stage, destination);
            return $"Unity export: {destination}\n\n" + string.Join("\n", relative) +
                $"\n\nWARNING: Export includes other assets in the loaded files. {missing} indexed visual references could not be resolved. " +
                "Scripts, shaders and other references may require repair in Unity.";
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, true);
            }
        }
    }
}
