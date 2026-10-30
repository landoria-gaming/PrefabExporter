using AssetsTools.NET.Extra;

namespace PrefabExporter.Core;

// Reads texture property names from a material in the installed game.
internal static class MaterialTextureSlots
{
    // Returns texture property names by their serialized array position.
    public static IReadOnlyDictionary<int, MaterialTextureSlot> Read(IndexedAsset material)
    {
        var slots = new Dictionary<int, MaterialTextureSlot>();
        if (string.IsNullOrEmpty(material.Source) || !File.Exists(material.Source))
        {
            return slots;
        }
        var manager = new AssetsManager();
        try
        {
            var file = OpenFile(manager, material);
            var info = file.file.AssetInfos.First(item => item.PathId == material.PathId);
            var entries = manager.GetBaseField(file, info)["m_SavedProperties"]["m_TexEnvs"]["Array"].Children;
            for (int i = 0; i < entries.Count; i++)
            {
                var value = entries[i]["second"];
                slots[i] = new MaterialTextureSlot(entries[i]["first"].AsString,
                    new[] { value["m_Scale"]["x"].AsFloat, value["m_Scale"]["y"].AsFloat },
                    new[] { value["m_Offset"]["x"].AsFloat, value["m_Offset"]["y"].AsFloat });
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return slots;
        }
        finally
        {
            manager.UnloadAllAssetsFiles(true);
            manager.UnloadAllBundleFiles();
        }
        return slots;
    }

    // Opens the material collection from a bundle or standalone asset file.
    private static AssetsFileInstance OpenFile(AssetsManager manager, IndexedAsset material)
    {
        string source = material.Source!;
        bool bundled = source.Contains(Path.Combine("SoftRef", "Bundles"), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(source).Equals("data.unity3d", StringComparison.OrdinalIgnoreCase);
        if (!bundled)
        {
            return manager.LoadAssetsFile(source, false);
        }
        var bundle = manager.LoadBundleFile(source, true);
        int index = bundle.file.GetFileIndex(material.Collection!);
        if (index < 0)
        {
            throw new InvalidDataException("Material collection not found in bundle.");
        }
        return manager.LoadAssetsFileFromBundle(bundle, index, false);
    }
}
