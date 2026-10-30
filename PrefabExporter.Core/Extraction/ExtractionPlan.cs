namespace PrefabExporter.Core;

// Stores the selected visual graph for extraction and model assembly.
internal sealed record ExtractionPlan(List<IndexedAsset> Assets, int Missing,
    Dictionary<string, IndexedAsset> Nodes, List<ReferenceEdge> Edges, HashSet<string> Roots);
