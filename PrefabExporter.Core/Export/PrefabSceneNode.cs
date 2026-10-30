namespace PrefabExporter.Core;

// Describes one active prefab transform and its optional mesh and materials.
internal sealed record PrefabSceneNode(string Name, int Parent, float[] Position, float[] Rotation,
    float[] Scale, string? Mesh, List<PrefabSceneMaterial> Materials);
