namespace PrefabExporter.Core;

// Describes one material and its extracted texture maps.
internal sealed record PrefabSceneMaterial(string Name, string? Albedo, string? Normal,
    MaterialTextureSlot? AlbedoSlot, MaterialTextureSlot? NormalSlot);
