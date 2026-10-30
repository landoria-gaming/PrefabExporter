namespace PrefabExporter.Core;

// Stores one edge found while following references.
internal record ReferenceEdge(int Depth, string From, string Field, string To);
