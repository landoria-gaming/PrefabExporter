using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PrefabExporter.Core;

// Writes objects and their serialized references to binary indexes.
internal sealed class AssetIndexWriter
{
    private readonly AssetsManager manager;
    private readonly AssetsFileInstance file;
    private readonly string source;
    private readonly TextWriter assets;
    private readonly TextWriter references;
    private readonly CancellationToken cancellation;
    private readonly Action<string> warn;
    private readonly SoftReferenceManifest manifest;
    private Dictionary<long, string> paths = new();

    public long Objects { get; private set; }
    public long Links { get; private set; }
    public List<(string Collection, long PathId)> SceneRoots { get; } = new();

    // Stores the source and output writers.
    public AssetIndexWriter(AssetsManager manager, AssetsFileInstance file, string source,
        TextWriter assets, TextWriter references, CancellationToken cancellation, Action<string> warn,
        SoftReferenceManifest manifest)
    {
        this.manager = manager;
        this.file = file;
        this.source = source;
        this.assets = assets;
        this.references = references;
        this.cancellation = cancellation;
        this.warn = warn;
        this.manifest = manifest;
    }

    // Writes each object in the serialized file.
    public void Write()
    {
        paths = ReadContainerPaths();
        foreach (var item in file.file.AssetInfos)
        {
            cancellation.ThrowIfCancellationRequested();
            WriteObject(item);
        }
    }

    // Reads asset paths from bundle containers.
    private Dictionary<long, string> ReadContainerPaths()
    {
        var result = new Dictionary<long, string>();
        foreach (var container in file.file.GetAssetsOfType(AssetClassID.AssetBundle))
        {
            try
            {
                var tree = manager.GetBaseField(file, container);
                foreach (var entry in tree["m_Container"]["Array"].Children)
                {
                    var pointer = entry["second"]["asset"];
                    if (pointer["m_FileID"].AsInt == 0)
                    {
                        result[pointer["m_PathID"].AsLong] = entry["first"].AsString;
                    }
                }
            }
            catch (Exception error)
            {
                warn($"container {source}: {error.Message}");
            }
        }
        return result;
    }

    // Reads and writes one object and its references.
    private void WriteObject(AssetFileInfo item)
    {
        Objects++;
        string type = ((AssetClassID)item.TypeId).ToString();
        string name = "";
        var found = new List<object>();
        try
        {
            var root = manager.GetBaseField(file, item);
            CheckScriptFields(item, root);
            CaptureSceneRoot(item, root);
            var nameField = root.Children.FirstOrDefault(child => child.FieldName == "m_Name");
            if (nameField != null)
            {
                name = nameField.AsString;
            }
            Walk(root, "", found);
        }
        catch (Exception error)
        {
            warn($"object {source} {file.name} {item.PathId} {type}: {error.Message}");
        }
        assets.WriteLine(JsonSerializer.Serialize(new { source, collection = file.name,
            path_id = item.PathId, type, name, asset_path = paths.GetValueOrDefault(item.PathId),
            soft_id = manifest.FindId(paths.GetValueOrDefault(item.PathId)) }));
        if (found.Count > 0)
        {
            Links += found.Count;
            references.WriteLine(JsonSerializer.Serialize(new { collection = file.name,
                path_id = item.PathId, references = found }));
        }
    }

    // Records root transforms that belong to a scene.
    private void CaptureSceneRoot(AssetFileInfo item, AssetTypeValueField root)
    {
        if (manifest.Scenes(Path.GetFileName(source)).Count == 0 ||
            (item.TypeId != (int)AssetClassID.Transform && item.TypeId != (int)AssetClassID.RectTransform))
        {
            return;
        }
        var father = root.Children.FirstOrDefault(child => child.FieldName == "m_Father");
        if (father != null && father["m_FileID"].AsInt == 0 && father["m_PathID"].AsLong == 0)
        {
            SceneRoots.Add((file.name, item.PathId));
        }
    }
    // Warns when script fields are empty or unavailable.
    private void CheckScriptFields(AssetFileInfo item, AssetTypeValueField root)
    {
        bool standalone = !source.Contains(Path.Combine("SoftRef", "Bundles"), StringComparison.OrdinalIgnoreCase);
        if (standalone && item.TypeId == (int)AssetClassID.MonoBehaviour && root.Children.Count <= 4)
        {
            warn($"script fields not resolved or empty: {source} {file.name} {item.PathId}");
        }
    }

    // Walks nested fields and collects non-null Unity pointers.
    private void Walk(AssetTypeValueField field, string path, List<object> found)
    {
        if (field.TypeName == "AssetID" && field.FieldName == "m_assetID")
        {
            AddSoftReference(field, path, found);
            return;
        }
        var id = field.Children.FirstOrDefault(child => child.FieldName == "m_FileID");
        var target = field.Children.FirstOrDefault(child => child.FieldName == "m_PathID");
        if (id != null && target != null)
        {
            AddReference(id.AsInt, target.AsLong, path, found);
            return;
        }
        for (int i = 0; i < field.Children.Count; i++)
        {
            var child = field.Children[i];
            Walk(child, path + "/" + child.FieldName + "[" + i + "]", found);
        }
    }

    // Adds a non-empty SoftReference ID and its manifest path.
    private void AddSoftReference(AssetTypeValueField field, string path, List<object> found)
    {
        string[] names = { "v3", "v2", "v1", "v0" };
        var values = new uint[4];
        for (int i = 0; i < names.Length; i++)
        {
            var child = field.Children.FirstOrDefault(item => item.FieldName == names[i]);
            if (child == null)
            {
                warn($"SoftReference ID fields missing: {source} {file.name} {path}");
                return;
            }
            values[i] = child.AsUInt;
        }
        if (values.All(value => value == 0))
        {
            return;
        }
        string id = string.Concat(values.Select(value => value.ToString("x8")));
        string? assetPath = manifest.FindPath(id);
        found.Add(new { field = path, collection = "softref", path_id = 0,
            asset_id = id, asset_path = assetPath });
    }
    // Adds a reference and resolves its collection name when available.
    private void AddReference(int fileId, long pathId, string path, List<object> found)
    {
        if (pathId == 0)
        {
            return;
        }
        var external = file.file.Metadata.Externals;
        string? collection = fileId == 0 ? file.name :
            fileId > 0 && fileId <= external.Count ? external[fileId - 1].PathName : null;
        found.Add(new { field = path, file_id = fileId, path_id = pathId, collection });
    }
}
