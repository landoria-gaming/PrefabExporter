using AssetsTools.NET;

namespace PrefabExporter.Core;

// Reconstructs active prefab hierarchy, transforms, meshes and material mappings.
internal sealed class PrefabSceneBuilder
{
    private readonly ExtractionPlan plan;
    private readonly Dictionary<string, string> paths;
    private readonly UnitySceneReader reader = new();
    private readonly Dictionary<string, List<ReferenceEdge>> edges;
    private readonly HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedLodRenderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> selectedLodRenderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly int lodLevel;
    private readonly List<PrefabSceneNode> nodes = new();

    // Stores the indexed graph and extracted file paths.
    public PrefabSceneBuilder(ExtractionPlan plan, Dictionary<string, string> paths, int lodLevel = 0)
    {
        this.plan = plan;
        this.paths = paths;
        this.lodLevel = lodLevel;
        edges = plan.Edges.GroupBy(edge => edge.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    // Reports the highest LOD index found in active groups.
    public int MaximumLodLevel { get; private set; }

    // Builds the scene from one unambiguous prefab root.
    public PrefabScenePlan Build(string name, string output, CancellationToken cancellation)
    {
        using (reader)
        {
            if (plan.Roots.Count != 1 || plan.Missing != 0)
            {
                throw new InvalidDataException("Prefab hierarchy is ambiguous or contains unresolved objects.");
            }
            AddGameObject(plan.Roots.Single(), -1, true, cancellation);
            if (!nodes.Any(node => node.Mesh != null))
            {
                throw new InvalidDataException("No active mesh was found in the prefab.");
            }
            return new PrefabScenePlan(name, output, nodes);
        }
    }

    // Adds an active GameObject, its components, and its child transforms.
    private void AddGameObject(string key, int parent, bool root, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!visited.Add(key))
        {
            return;
        }
        var game = GetAsset(key);
        var raw = reader.Read(game);
        if (!root && !raw["m_IsActive"].AsBool)
        {
            return;
        }
        var components = Outgoing(key, "/m_Component[").Select(edge => edge.To).ToArray();
        ConfigureLods(components);
        string transformKey = components.FirstOrDefault(component => GetAsset(component).Type is "Transform" or "RectTransform")
            ?? throw new InvalidDataException("GameObject transform is missing.");
        var transform = reader.Read(GetAsset(transformKey));
        var node = MakeNode(game.Name ?? "GameObject", parent, root, transform, components);
        int index = nodes.Count;
        nodes.Add(node);
        foreach (var child in Outgoing(transformKey, "/m_Children["))
        {
            string? childObject = Outgoing(child.To, "/m_GameObject[").Select(edge => edge.To).FirstOrDefault();
            if (childObject == null)
            {
                throw new InvalidDataException("A child GameObject could not be resolved.");
            }
            AddGameObject(childObject, index, false, cancellation);
        }
    }

    // Selects the requested LOD's renderers for enabled groups on this object.
    private void ConfigureLods(string[] components)
    {
        foreach (string key in components.Where(key => GetAsset(key).Type == "LODGroup"))
        {
            if (!reader.Read(GetAsset(key))["m_Enabled"].AsBool)
            {
                continue;
            }
            var lods = Outgoing(key, "/m_LODs[").Select(edge => (Edge: edge, Level: LodLevel(edge.Field)))
                .Where(item => item.Level >= 0).ToList();
            if (!lods.Any(item => item.Level == 0))
            {
                continue;
            }
            int max = lods.Max(item => item.Level);
            MaximumLodLevel = Math.Max(MaximumLodLevel, max);
            int selected = lods.Where(item => item.Level <= lodLevel).Max(item => item.Level);
            foreach (var item in lods)
            {
                if (item.Level == selected)
                {
                    selectedLodRenderers.Add(item.Edge.To);
                    excludedLodRenderers.Remove(item.Edge.To);
                }
                else if (!selectedLodRenderers.Contains(item.Edge.To))
                {
                    excludedLodRenderers.Add(item.Edge.To);
                }
            }
        }
    }

    // Extracts the LOD array index from a reference field.
    private static int LodLevel(string field)
    {
        const string marker = "/Array[0]/data[";
        int start = field.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return -1;
        }
        start += marker.Length;
        int end = field.IndexOf(']', start);
        return end >= 0 && int.TryParse(field.AsSpan(start, end - start), out int level) ? level : -1;
    }

    // Builds a node and attaches its renderer's mesh and materials.
    private PrefabSceneNode MakeNode(string name, int parent, bool root, AssetTypeValueField transform,
        string[] components)
    {
        float[] position = root ? new float[] { 0, 0, 0 } : Vector(transform["m_LocalPosition"], false);
        float[] rotation = root ? new float[] { 0, 0, 0, 1 } : Vector(transform["m_LocalRotation"], true);
        float[] scale = root ? new float[] { 1, 1, 1 } : Vector(transform["m_LocalScale"], false);
        string? filter = components.FirstOrDefault(key => GetAsset(key).Type == "MeshFilter");
        string? renderer = components.FirstOrDefault(key => GetAsset(key).Type == "MeshRenderer");
        if (components.Any(key => GetAsset(key).Type == "SkinnedMeshRenderer"))
        {
            throw new NotSupportedException("Skinned meshes are not yet supported in FBX output.");
        }
        string? mesh = filter == null || renderer == null ? null : FindMesh(filter, renderer);
        var materials = mesh == null ? new List<PrefabSceneMaterial>() : ReadMaterials(renderer!);
        return new PrefabSceneNode(name, parent, position, rotation, scale, mesh, materials);
    }

    // Finds the extracted mesh when its renderer is enabled.
    private string? FindMesh(string filter, string renderer)
    {
        var component = reader.Read(GetAsset(renderer));
        if (!component["m_Enabled"].AsBool || excludedLodRenderers.Contains(renderer))
        {
            return null;
        }
        string key = Outgoing(filter, "/m_Mesh[").Select(edge => edge.To).FirstOrDefault()
            ?? throw new InvalidDataException("MeshFilter has no mesh.");
        return paths.TryGetValue(key, out string? path) ? path :
            throw new InvalidDataException("The mesh was not extracted.");
    }

    // Builds material entries in renderer order.
    private List<PrefabSceneMaterial> ReadMaterials(string renderer)
    {
        var materials = new List<PrefabSceneMaterial>();
        foreach (var edge in Outgoing(renderer, "/m_Materials["))
        {
            var material = GetAsset(edge.To);
            var slots = MaterialTextureSlots.Read(material);
            string? albedo = null, normal = null;
            MaterialTextureSlot? albedoSlot = null, normalSlot = null;
            foreach (var texture in Outgoing(edge.To, "/m_SavedProperties["))
            {
                if (!slots.TryGetValue(VisualSummary.TextureIndex(texture.Field), out var slot))
                {
                    throw new InvalidDataException("A material texture slot could not be identified.");
                }
                if (slot.Name is "_MainTex" or "_BaseMap" or "_BaseColorMap")
                {
                    albedo = TexturePath(texture.To);
                    albedoSlot = slot;
                }
                else if (slot.Name is "_BumpMap" or "_NormalMap")
                {
                    normal = TexturePath(texture.To);
                    normalSlot = slot;
                }
            }
            materials.Add(new PrefabSceneMaterial(material.Name ?? "Material", albedo, normal, albedoSlot, normalSlot));
        }
        return materials;
    }

    // Returns the extracted texture or fails instead of creating a misleading model.
    private string TexturePath(string key)
    {
        return paths.TryGetValue(key, out string? path) ? path :
            throw new InvalidDataException("A material texture was not extracted.");
    }

    // Lists outgoing edges for a serialized field.
    private IEnumerable<ReferenceEdge> Outgoing(string key, string field)
    {
        return edges.TryGetValue(key, out var list) ? list.Where(edge =>
            edge.Field.StartsWith(field, StringComparison.Ordinal)) : Enumerable.Empty<ReferenceEdge>();
    }

    // Resolves an object in the selected graph.
    private IndexedAsset GetAsset(string key)
    {
        return plan.Nodes.TryGetValue(key, out var asset) ? asset :
            throw new InvalidDataException("A prefab object could not be resolved.");
    }

    // Reads a serialized three- or four-component vector.
    private static float[] Vector(AssetTypeValueField field, bool quaternion)
    {
        return quaternion ? new[] { field["x"].AsFloat, field["y"].AsFloat, field["z"].AsFloat, field["w"].AsFloat } :
            new[] { field["x"].AsFloat, field["y"].AsFloat, field["z"].AsFloat };
    }
}
