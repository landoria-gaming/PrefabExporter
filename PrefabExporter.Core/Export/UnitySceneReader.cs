using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PrefabExporter.Core;

// Opens serialized Unity objects needed to reconstruct prefab transforms.
internal sealed class UnitySceneReader : IDisposable
{
    private readonly AssetsManager manager = new();
    private readonly Dictionary<string, AssetsFileInstance> files = new(StringComparer.OrdinalIgnoreCase);

    // Reads one indexed Unity object from the installed game.
    public AssetTypeValueField Read(IndexedAsset asset)
    {
        string source = asset.Source ?? throw new InvalidDataException("Asset source is missing.");
        string collection = asset.Collection ?? throw new InvalidDataException("Asset collection is missing.");
        string key = source + "|" + collection;
        if (!files.TryGetValue(key, out var file))
        {
            file = Open(source, collection);
            files.Add(key, file);
        }
        var info = file.file.AssetInfos.FirstOrDefault(item => item.PathId == asset.PathId)
            ?? throw new InvalidDataException("Asset is missing from its collection.");
        return manager.GetBaseField(file, info);
    }

    // Opens a collection from a bundle or standalone Unity data file.
    private AssetsFileInstance Open(string source, string collection)
    {
        bool bundled = source.Contains(Path.Combine("SoftRef", "Bundles"), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(source).Equals("data.unity3d", StringComparison.OrdinalIgnoreCase);
        if (bundled)
        {
            var bundle = manager.LoadBundleFile(source, true);
            int index = bundle.file.GetFileIndex(collection);
            if (index < 0)
            {
                throw new InvalidDataException("Asset collection is missing from its bundle.");
            }
            return manager.LoadAssetsFileFromBundle(bundle, index, false);
        }
        manager.LoadClassPackage(Path.Combine(AppContext.BaseDirectory, "unitypy-type-trees.tpk"));
        var file = manager.LoadAssetsFile(source, false);
        manager.LoadClassDatabaseFromPackage(file.file.Metadata.UnityVersion);
        return file;
    }

    // Releases open Unity files.
    public void Dispose()
    {
        manager.UnloadAllAssetsFiles(true);
        manager.UnloadAllBundleFiles();
    }
}
