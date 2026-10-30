namespace PrefabExporter.Core;

// Stores the configured paths for the game and tools.
public record Settings(string ValheimPath, string AssetRipperPath = "");
