namespace PrefabExporter.Core;

// Stores the fields needed to search and resolve an indexed asset.
internal sealed record IndexedAsset(string? Source, string? Collection, long PathId,
    string? Type, string? Name, string? AssetPath, string? SoftId);
