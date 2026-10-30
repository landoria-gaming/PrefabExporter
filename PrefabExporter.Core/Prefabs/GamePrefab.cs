namespace PrefabExporter.Core;

// Identifies one prefab and its source bundle from the game manifest.
public sealed record GamePrefab(string Name, string Path, string Bundle, IReadOnlyList<string> IconBundles);
