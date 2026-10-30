namespace PrefabExporter.Core;

// Identifies an asset exposed by a running AssetRipper session.
internal sealed record AssetRipperMatch(long PathId, string Type, string Collection, string ViewPath);
