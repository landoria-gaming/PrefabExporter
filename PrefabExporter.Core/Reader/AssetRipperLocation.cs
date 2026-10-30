namespace PrefabExporter.Core;

// Resolves the AssetRipper executable from the configured path.
internal static class AssetRipperLocation
{
    // Accepts an executable or a directory, with relative paths based on the application directory.
    public static string Resolve(string? configured, string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("Set AssetRipperPath in appsettings.json to the asset reader executable or directory.");
        }
        string path = Path.GetFullPath(configured, appDirectory);
        if (File.Exists(path))
        {
            return path;
        }
        if (Directory.Exists(path))
        {
            string executable = Path.Combine(path, "AssetRipper.GUI.Free.exe");
            if (File.Exists(executable))
            {
                return executable;
            }
        }
        throw new FileNotFoundException("Asset reader executable not found at the configured AssetRipperPath.", path);
    }
}
