using System.Text.Json.Nodes;

namespace PrefabExporter.Core;

// Holds one prefab hierarchy node and its serialized components.
internal sealed record VisualNode(int Parent, JsonObject Game, JsonObject Transform, List<string> Components);
