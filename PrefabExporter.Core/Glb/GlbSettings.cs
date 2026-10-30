using System.Text;
using System.Text.Json.Nodes;

namespace PrefabExporter.Core;

// Applies texture filtering and UV settings to an exported GLB.
internal static class GlbSettings
{
    // Rewrites the JSON chunk while preserving the binary resource chunk.
    public static void Apply(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int jsonLength = BitConverter.ToInt32(data, 12);
        int next = 20 + jsonLength;
        var json = JsonNode.Parse(Encoding.UTF8.GetString(data, 20, jsonLength))!.AsObject();
        foreach (var sampler in json["samplers"]?.AsArray() ?? [])
        {
            sampler!["magFilter"] = 9728;
            sampler["minFilter"] = 9984;
        }
        foreach (var material in json["materials"]?.AsArray() ?? [])
        {
            if (material?["normalTexture"] is JsonObject normal) { normal["texCoord"] = 1; }
        }
        byte[] text = Encoding.UTF8.GetBytes(json.ToJsonString());
        int padded = (text.Length + 3) & ~3;
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);
        writer.Write(data, 0, 8);
        writer.Write(20 + padded + data.Length - next);
        writer.Write(padded);
        writer.Write(0x4E4F534A);
        writer.Write(text);
        for (int i = text.Length; i < padded; i++) { writer.Write((byte)32); }
        writer.Write(data, next, data.Length - next);
    }
}
