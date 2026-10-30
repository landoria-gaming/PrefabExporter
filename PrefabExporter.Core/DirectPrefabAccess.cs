namespace PrefabExporter.Core;

// Inspects and exports prefabs without a persistent index.
public static class DirectPrefabAccess
{
    // Reads visual references and optionally exports a GLB scene.
    public static async Task<string> Run(GamePrefab prefab, string assetRipperPath, string appDirectory,
        bool extract, IProgress<string>? progress, CancellationToken cancellation)
    {
        Validate(prefab);
        string executable = AssetRipperLocation.Resolve(assetRipperPath, appDirectory);
        using var reader = new AssetReader(executable);
        progress?.Report("Loading prefab bundle...");
        await reader.Load(new[] { prefab.Bundle }.Concat(prefab.IconBundles), cancellation);
        string root = await reader.Find(prefab, cancellation);
        var graph = await VisualGraph.Read(reader, root, progress, cancellation);
        var lines = new List<string> { "Prefab: " + prefab.Name };
        lines.AddRange(VisualGraph.Summary(graph.Values));
        if (extract) { lines.AddRange(await Extract(reader, graph, root, prefab.Name, appDirectory, progress, cancellation)); }
        lines.AddRange(reader.Warnings.OrderBy(w => w).Select(w => "WARNING: " + w));
        lines.Add("WARNING: Results cover traversed visual references; other game assets may not be loaded.");
        return string.Join(Environment.NewLine, lines);
    }

    // Exports to a staging folder before publishing the result.
    private static async Task<IEnumerable<string>> Extract(AssetReader reader, Dictionary<string, System.Text.Json.Nodes.JsonObject> graph, string root, string name, string appDirectory, IProgress<string>? progress, CancellationToken token)
    {
        string output = Path.Combine(appDirectory, "output");
        string destination = Path.Combine(output, name);
        if (Directory.Exists(destination) || File.Exists(destination)) { throw new IOException($"Output folder already exists: {destination}"); }
        string stage = Path.Combine(output, ".extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var paths = await VisualResources.Download(reader, graph, stage, token);
            var files = await GlbScene.Export(reader, graph, root, paths, stage, name, progress, token);
            token.ThrowIfCancellationRequested();
            await Publish(stage, destination, token);
            return new[] { $"Extracted to: {destination}" }.Concat(files).ToArray();
        }
        finally
        {
            if (Directory.Exists(stage)) { Directory.Delete(stage, true); }
        }
    }

    // Retries a move if a scanner temporarily locks an output file.
    private static async Task Publish(string stage, string destination, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Move(stage, destination); return; }
            catch (IOException) when (attempt < 5 && !Directory.Exists(destination)) { await Task.Delay(200, token); }
        }
    }

    // Rejects invalid prefab names and missing input bundles.
    private static void Validate(GamePrefab prefab)
    {
        if (string.IsNullOrWhiteSpace(prefab.Name) || prefab.Name is "." or ".." ||
            prefab.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || prefab.Name.EndsWith('.') || prefab.Name.EndsWith(' '))
        { throw new ArgumentException("Invalid prefab name."); }
        if (!File.Exists(prefab.Bundle)) { throw new FileNotFoundException("The prefab bundle is missing.", prefab.Bundle); }
    }
}
