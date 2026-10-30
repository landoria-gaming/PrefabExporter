namespace PrefabExporter.Core;

// Contains the scene data sent to the FBX exporter.
internal sealed record PrefabScenePlan(string Name, string Output, List<PrefabSceneNode> Nodes);
