using System.Numerics;
using System.Text.Json.Nodes;
using Assimp;

namespace PrefabExporter.Core;

// Builds a GLB scene from the prefab hierarchy and extracted resources.
internal static class GlbScene
{
    // Exports all available detail levels.
    public static async Task<List<string>> Export(AssetReader reader, Dictionary<string, JsonObject> objects, string root, Dictionary<string, string> paths, string folder, string name, IProgress<string>? progress, CancellationToken token)
    {
        var nodes = await VisualHierarchy.Build(reader, objects, root, token);
        var groups = await VisualHierarchy.Groups(reader, objects, nodes, token);
        int count = groups.Count == 0 ? 1 : groups.Max(group => group.Count);
        var results = new List<string>();
        for (int level = 0; level < count; level++)
        {
            progress?.Report($"Building GLB {level + 1}/{count}...");
            string file = name + (count > 1 ? $"-lod{level}" : "") + ".glb";
            await ExportLevel(reader, objects, nodes, paths, VisualHierarchy.Excluded(groups, level), Path.Combine(folder, file), token);
            results.Add(file);
        }
        return results;
    }

    // Constructs and writes one scene.
    private static async Task ExportLevel(AssetReader reader, Dictionary<string, JsonObject> objects, List<VisualNode> nodes, Dictionary<string, string> paths, HashSet<string> excluded, string output, CancellationToken token)
    {
        using var context = new AssimpContext();
        var scene = new Scene { RootNode = new Node("Root") };
        var holders = new List<Node>();
        foreach (var item in nodes)
        {
            token.ThrowIfCancellationRequested();
            var holder = new Node(item.Game["m_Name"]?.GetValue<string>() ?? "GameObject") { Transform = Transform(item) };
            (item.Parent < 0 ? scene.RootNode : holders[item.Parent]).Children.Add(holder);
            holders.Add(holder);
            await AddMesh(reader, objects, paths, excluded, item, holder, scene, context, token);
        }
        if (!scene.HasMeshes) { throw new InvalidDataException("No active static mesh was found."); }
        if (!context.ExportFile(scene, output, "glb2")) { throw new IOException("Could not export GLB."); }
        GlbSettings.Apply(output);
    }

    // Converts the Unity transform to glTF coordinates.
    private static Matrix4x4 Transform(VisualNode node)
    {
        if (node.Parent < 0) { return Matrix4x4.Identity; }
        var p = node.Transform["m_LocalPosition"]!;
        var q = node.Transform["m_LocalRotation"]!;
        var s = node.Transform["m_LocalScale"]!;
        var position = new Vector3(-Number(p, "X"), Number(p, "Y"), Number(p, "Z"));
        var rotation = new Quaternion(Number(q, "X"), -Number(q, "Y"), -Number(q, "Z"), Number(q, "W"));
        var scale = new Vector3(Number(s, "X"), Number(s, "Y"), Number(s, "Z"));
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
    }

    // Reads a Unity vector component.
    private static float Number(JsonNode node, string axis) => node["m_" + axis]!.GetValue<float>();

    // Attaches a mesh and its materials to a hierarchy node.
    private static async Task AddMesh(AssetReader reader, Dictionary<string, JsonObject> objects, Dictionary<string, string> paths, HashSet<string> excluded, VisualNode node, Node holder, Scene scene, AssimpContext context, CancellationToken token)
    {
        var filter = node.Components.FirstOrDefault(k => objects[k].ContainsKey("m_Mesh") && !objects[k].ContainsKey("m_Materials"));
        var renderer = node.Components.FirstOrDefault(k => objects[k].ContainsKey("m_Materials"));
        if (node.Components.Any(k => objects[k].ContainsKey("m_Bones"))) { throw new InvalidDataException("Skinned meshes are not supported."); }
        if (filter == null || renderer == null || excluded.Contains(renderer) || objects[renderer]["m_Enabled"]?.GetValue<bool>() == false) { return; }
        string? key = await reader.Resolve(filter, objects[filter]["m_Mesh"], false, token);
        if (key == null || !paths.TryGetValue(key, out string? path)) { throw new InvalidDataException("A required mesh could not be extracted."); }
        var imported = context.ImportFile(path);
        var materials = objects[renderer]["m_Materials"]?.AsArray() ?? [];
        for (int i = 0; i < imported.MeshCount; i++)
        {
            var mesh = imported.Meshes[i];
            string? mat = materials.Count == 0 ? null : await reader.Resolve(renderer, materials[Math.Min(i, materials.Count - 1)], false, token);
            mesh.MaterialIndex = await GlbMaterials.Add(reader, objects, paths, scene, mesh, mat, token);
            holder.MeshIndices.Add(scene.Meshes.Count);
            scene.Meshes.Add(mesh);
        }
    }
}
