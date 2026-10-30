namespace PrefabExporter.Core;

// Stores the configured game, index, and AssetRipper paths.
public record Settings(string ValheimPath, string IndexPath, string AssetRipperPath = "");
