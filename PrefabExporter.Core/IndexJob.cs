using AssetsTools.NET.Extra;
using System.Text.Json;

namespace PrefabExporter.Core;

// Coordinates one indexing run and its output files.
internal sealed class IndexJob
{
    private readonly Settings settings;
    private readonly IProgress<string> progress;
    private readonly CancellationToken cancellation;
    private readonly IProgress<IndexProgress>? indexingProgress;
    private string data = "";
    private StreamWriter log = null!;
    private int warnings;
    private long objects;
    private long links;
    private SoftReferenceManifest manifest = null!;
    private bool publishing;

    // Stores the inputs for the indexing run.
    public IndexJob(Settings settings, IProgress<string> progress, CancellationToken cancellation,
        IProgress<IndexProgress>? indexingProgress)
    {
        this.settings = settings;
        this.progress = progress;
        this.cancellation = cancellation;
        this.indexingProgress = indexingProgress;
    }

    // Validates paths and writes the index files.
    public void Run()
    {
        string game = Path.GetFullPath(settings.ValheimPath);
        string destination = Path.GetFullPath(settings.IndexPath);
        ValidateDestination(game, destination);
        data = FindDataDirectory(game);
        string bundles = Path.Combine(data, "StreamingAssets", "SoftRef", "Bundles");
        if (!Directory.Exists(bundles))
        {
            throw new DirectoryNotFoundException("SoftRef bundles not found.");
        }
        string softRef = Path.Combine(data, "StreamingAssets", "SoftRef");
        manifest = SoftReferenceManifest.Load(Path.Combine(softRef, "manifest"),
            Path.Combine(softRef, "manifest_extended"));
        string version = Indexer.Version(game);
        destination = IndexLocation.Resolve(destination, game, version);
        ValidateDestination(game, destination);
        Directory.CreateDirectory(destination);
        string assets = Path.Combine(destination, $"assets-{version}.jsonl");
        string refs = Path.Combine(destination, $"references-{version}.jsonl");
        string logPath = Path.Combine(destination, "log.txt");
        try
        {
            using var assetWriter = new StreamWriter(assets + ".partial", false);
            using var refWriter = new StreamWriter(refs + ".partial", false);
            using var logWriter = new StreamWriter(logPath + ".partial", false) { AutoFlush = true };
            log = logWriter;
            CheckUnityVersion();
            RunFiles(GetSources(bundles), assetWriter, refWriter, assets, refs, logPath);
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException && !publishing)
            {
                string partialLog = logPath + ".partial";
                string details = File.Exists(partialLog) ? File.ReadAllText(partialLog) : "";
                File.AppendAllText(logPath, details + $"ERROR: {error.Message}{Environment.NewLine}");
            }
            CleanPartialFiles(assets, refs, logPath);
            throw;
        }
    }

    // Removes temporary files left by an interrupted indexing run.
    private static void CleanPartialFiles(string assets, string refs, string logPath)
    {
        foreach (string path in new[] { assets + ".partial", refs + ".partial", logPath + ".partial" })
        {
            File.Delete(path);
        }
    }

    // Rejects an index destination inside the game directory.
    private static void ValidateDestination(string game, string destination)
    {
        if (destination.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            destination.Equals(game, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The index must be outside the game directory.");
        }
    }

    // Finds the game data directory.
    private static string FindDataDirectory(string game)
    {
        return Directory.EnumerateDirectories(game, "*_Data", SearchOption.TopDirectoryOnly).FirstOrDefault()
            ?? throw new DirectoryNotFoundException("Game data directory not found.");
    }

    // Lists bundles and standalone serialized files.
    private (string path, bool bundle)[] GetSources(string bundles)
    {
        return Directory.GetFiles(bundles).Select(path => (path, true))
            .Concat(Directory.GetFiles(data).Where(IsStandalone).Select(path => (path, false)))
            .Concat(GetPackagedData().Select(path => (path, true)))
            .Concat(GetUnityResources().Select(path => (path, false))).ToArray();
    }

    // Lists the packaged player data used by some distributions.
    private IEnumerable<string> GetPackagedData()
    {
        string path = Path.Combine(data, "data.unity3d");
        if (File.Exists(path))
        {
            yield return path;
        }
    }
    // Lists the Unity built-in resources shipped with the game.
    private IEnumerable<string> GetUnityResources()
    {
        string resources = Path.Combine(data, "Resources");
        foreach (string name in new[] { "unity default resources", "unity_builtin_extra" })
        {
            string path = Path.Combine(resources, name);
            if (File.Exists(path))
            {
                yield return path;
            }
            else if (!File.Exists(Path.Combine(data, "data.unity3d")))
            {
                Warn($"Unity resource file missing: {path}");
            }
        }
    }
    // Checks whether a file is a standalone Unity asset file.
    private static bool IsStandalone(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) ||
            name == "globalgamemanagers" || name.StartsWith("level");
    }

    // Records the Unity version used by the game.
    private void CheckUnityVersion()
    {
        try
        {
            var probe = new AssetsManager();
            var info = LoadGameManagers(probe);
            _ = info.file.Metadata.UnityVersion;
            probe.UnloadAllAssetsFiles(true);
            probe.UnloadAllBundleFiles();
        }
        catch (Exception error)
        {
            Warn($"Unity version check: {error.Message}");
        }
    }
    // Opens game manager data from a loose file or packaged player data.
    private AssetsFileInstance LoadGameManagers(AssetsManager manager)
    {
        string loose = Path.Combine(data, "globalgamemanagers");
        if (File.Exists(loose))
        {
            return manager.LoadAssetsFile(loose, false);
        }
        string archive = Path.Combine(data, "data.unity3d");
        var bundle = manager.LoadBundleFile(archive, true);
        int index = bundle.file.GetFileIndex("globalgamemanagers");
        if (index < 0 || !bundle.file.IsAssetsFile(index))
        {
            throw new InvalidDataException("globalgamemanagers not found in data.unity3d.");
        }
        return manager.LoadAssetsFileFromBundle(bundle, index, false);
    }
    // Processes all sources and publishes an audited index.
    private void RunFiles((string path, bool bundle)[] sources, StreamWriter assets, StreamWriter refs,
        string assetsPath, string refsPath, string logPath)
    {
        indexingProgress?.Report(new IndexProgress(0, sources.Length, false));
        for (int i = 0; i < sources.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress.Report($"Indexing {i + 1}/{sources.Length}: {Path.GetFileName(sources[i].path)} — {objects:N0} objects");
            ProcessSource(sources[i], assets, refs);
            indexingProgress?.Report(new IndexProgress(i + 1, sources.Length, false));
        }
        assets.Close();
        refs.Close();
        indexingProgress?.Report(new IndexProgress(sources.Length, sources.Length, true));
        AuditReferences(assetsPath + ".partial", refsPath + ".partial");
        cancellation.ThrowIfCancellationRequested();
        PublishIndex(assetsPath, refsPath, logPath);
        indexingProgress?.Report(new IndexProgress(sources.Length, sources.Length, false));
        progress.Report($"Indexing complete: {objects:N0} objects, {links:N0} references, {warnings:N0} warnings.");
    }

    // Makes the log visible only after both index files are published.
    private void PublishIndex(string assets, string refs, string logPath)
    {
        publishing = true;
        string previousLog = logPath + ".previous";
        if (File.Exists(logPath))
        {
            File.Move(logPath, previousLog, true);
        }
        else if (!File.Exists(previousLog))
        {
            File.WriteAllText(previousLog, "");
        }
        File.Move(assets + ".partial", assets, true);
        File.Move(refs + ".partial", refs, true);
        log.Close();
        File.Move(logPath + ".partial", logPath, true);
        File.Delete(previousLog);
    }

    // Audits indexed references and writes only warnings to the log.
    private void AuditReferences(string assets, string references)
    {
        progress.Report("Auditing indexed references...");
        var auditor = new ReferenceAuditor(manifest, Path.Combine(data, "StreamingAssets", "SoftRef", "Bundles"), cancellation);
        auditor.Run(assets, references, log);
    }
    // Opens one source and records its objects.
    private void ProcessSource((string path, bool bundle) source, StreamWriter assets, StreamWriter refs)
    {
        var manager = new AssetsManager();
        try
        {
            if (source.bundle)
            {
                ProcessBundle(manager, source.path, assets, refs);
            }
            else
            {
                ProcessStandalone(manager, source.path, assets, refs);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            Warn($"file {source.path}: {error.Message}");
        }
        finally
        {
            manager.UnloadAllAssetsFiles(true);
            manager.UnloadAllBundleFiles();
            manager.MonoTempGenerator?.Dispose();
        }
    }

    // Processes each serialized file inside a bundle.
    private void ProcessBundle(AssetsManager manager, string path, StreamWriter assets, StreamWriter refs)
    {
        var bundle = manager.LoadBundleFile(path, true);
        var roots = new List<(string Collection, long PathId)>();
        bool playerData = Path.GetFileName(path).Equals("data.unity3d", StringComparison.OrdinalIgnoreCase);
        if (playerData)
        {
            PrepareTypeData(manager);
            ValidatePackagedFiles(bundle);
        }
        for (int i = 0; i < bundle.file.GetAllFileNames().Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (bundle.file.IsAssetsFile(i))
            {
                var file = manager.LoadAssetsFileFromBundle(bundle, i, false);
                if (playerData)
                {
                    manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
                }
                ProcessFile(manager, file, path, assets, refs, roots);
            }
        }
        WriteScenes(path, roots, assets, refs);
    }

    // Warns when required player files are absent from both layouts.
    private void ValidatePackagedFiles(BundleFileInstance bundle)
    {
        string[] required = { "globalgamemanagers", "resources.assets", "Resources/unity_builtin_extra",
            "Resources/unity default resources" };
        foreach (string name in required)
        {
            string loose = Path.Combine(data, name.Replace('/', Path.DirectorySeparatorChar));
            int index = bundle.file.GetFileIndex(name);
            if (!File.Exists(loose) && (index < 0 || !bundle.file.IsAssetsFile(index)))
            {
                Warn($"Player asset file missing: {name}");
            }
        }
    }
    // Opens a standalone file using the game assemblies and type trees.
    private void ProcessStandalone(AssetsManager manager, string path, StreamWriter assets, StreamWriter refs)
    {
        PrepareTypeData(manager);
        var file = manager.LoadAssetsFile(path, false);
        manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
        ProcessFile(manager, file, path, assets, refs);
    }

    // Loads type trees and game assemblies for files without embedded type trees.
    private void PrepareTypeData(AssetsManager manager)
    {
        string managed = Path.Combine(data, "Managed");
        if (Directory.Exists(managed))
        {
            manager.MonoTempGenerator = new MemoryMonoGenerator(managed);
        }
        else
        {
            Warn($"Managed assemblies missing: {managed}");
        }
        manager.LoadClassPackage(Path.Combine(AppContext.BaseDirectory, "unitypy-type-trees.tpk"));
    }
    // Writes all objects from one serialized file.
    private void ProcessFile(AssetsManager manager, AssetsFileInstance file, string path,
        StreamWriter assets, StreamWriter refs, List<(string Collection, long PathId)>? roots = null)
    {
        var writer = new AssetIndexWriter(manager, file, path, assets, refs, cancellation, Warn, manifest);
        writer.Write();
        objects += writer.Objects;
        links += writer.Links;
        roots?.AddRange(writer.SceneRoots);
    }

    // Writes synthetic scene entries and their root transforms.
    private void WriteScenes(string bundle, List<(string Collection, long PathId)> roots,
        StreamWriter assets, StreamWriter refs)
    {
        var scenes = manifest.Scenes(Path.GetFileName(bundle));
        if (scenes.Count > 1)
        {
            Warn($"Multiple scenes share bundle {bundle}; scene roots cannot be assigned reliably.");
        }
        foreach (string scene in scenes)
        {
            string id = manifest.FindId(scene)!;
            string collection = "scene-" + id;
            assets.WriteLine(JsonSerializer.Serialize(new { source = bundle, collection, path_id = 0,
                type = "Scene", name = Path.GetFileNameWithoutExtension(scene), asset_path = scene, soft_id = id }));
            objects++;
            if (scenes.Count == 1 && roots.Count > 0)
            {
                var references = roots.Select(root => new { field = "scene root", file_id = 0,
                    path_id = root.PathId, collection = root.Collection }).ToArray();
                refs.WriteLine(JsonSerializer.Serialize(new { collection, path_id = 0, references }));
                links += references.Length;
            }
        }
    }
    // Records a warning in the index log.
    private void Warn(string message)
    {
        warnings++;
        log.WriteLine($"WARNING {message}");
    }
}
