using System.Numerics;
using System.Text.Json.Nodes;
using Assimp;

namespace PrefabExporter.Core;

// Converts Unity material textures into GLB materials.
internal static class GlbMaterials
{
    // Adds one material and its embedded textures to the scene.
    public static async Task<int> Add(AssetReader reader, Dictionary<string, JsonObject> objects, Dictionary<string, string> paths, Scene scene, Mesh mesh, string? key, CancellationToken token)
    {
        var material = new Material { Name = key != null && objects.ContainsKey(key) ? objects[key]["m_Name"]?.GetValue<string>() ?? "Material" : "Material", ColorDiffuse = Vector4.One };
        if (key != null && objects.TryGetValue(key, out var data))
        {
            var envs = data["m_SavedProperties"]?["m_TexEnvs"];
            await Texture(reader, paths, scene, mesh, material, key, envs, ["_BumpMap", "_NormalMap"], true, token);
            await Texture(reader, paths, scene, mesh, material, key, envs, ["_MainTex", "_BaseMap", "_BaseColorMap"], false, token);
        }
        scene.Materials.Add(material);
        return scene.Materials.Count - 1;
    }

    // Embeds a material texture and applies its UV mapping.
    private static async Task Texture(AssetReader reader, Dictionary<string, string> paths, Scene scene, Mesh mesh, Material material, string source, JsonNode? envs, string[] names, bool normal, CancellationToken token)
    {
        foreach (string name in names)
        {
            var entry = envs?[name];
            if (entry?["m_Texture"]?["m_PathID"]?.GetValue<long>() is not long id || id == 0) { continue; }
            string? key = await reader.Resolve(source, entry["m_Texture"], false, token);
            if (key == null || !paths.TryGetValue(key, out var path)) { throw new InvalidDataException($"Required texture {name} could not be extracted."); }
            int channel = normal ? 1 : 0;
            ApplyUv(mesh, entry, channel);
            int index = scene.Textures.Count;
            scene.Textures.Add(new EmbeddedTexture("png", await File.ReadAllBytesAsync(path, token), Path.GetFileName(path)));
            var slot = new TextureSlot("*" + index, normal ? TextureType.Normals : TextureType.Diffuse, 0, TextureMapping.FromUV, channel, 1, TextureOperation.Multiply, TextureWrapMode.Wrap, TextureWrapMode.Wrap, 0);
            if (normal) { material.TextureNormal = slot; } else { material.TextureDiffuse = slot; }
            return;
        }
    }

    // Bakes texture scale and offset into the selected UV channel.
    private static void ApplyUv(Mesh mesh, JsonNode entry, int channel)
    {
        var source = mesh.TextureCoordinateChannels[0];
        if (source.Count == 0) { return; }
        var scale = entry["m_Scale"]!;
        var offset = entry["m_Offset"]!;
        float sx = scale["m_X"]!.GetValue<float>(), sy = scale["m_Y"]!.GetValue<float>();
        float ox = offset["m_X"]!.GetValue<float>(), oy = offset["m_Y"]!.GetValue<float>();
        var copy = source.Select(uv => new Vector3(uv.X * sx + ox, uv.Y * sy + oy, uv.Z)).ToArray();
        if (channel == 0) { source.Clear(); source.AddRange(copy); }
        else { mesh.TextureCoordinateChannels[channel].Clear(); mesh.TextureCoordinateChannels[channel].AddRange(copy); mesh.UVComponentCount[channel] = 2; }
    }
}
