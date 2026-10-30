using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrefabExporter.Core;

// Keeps installations with the same game version from sharing index files.
public static class IndexLocation
{
    // Selects the existing index directory or an installation-specific subdirectory.
    public static string Resolve(string indexRoot, string gamePath, string version)
    {
        string root = Path.Combine(Path.GetFullPath(indexRoot), version);
        string game = Path.GetFullPath(gamePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string assets = Path.Combine(root, $"assets-{version}.bin");
        if (!File.Exists(assets) || MatchesGame(assets, game))
        {
            return root;
        }
        string normalized = OperatingSystem.IsWindows() ? game.ToUpperInvariant() : game;
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        return Path.Combine(root, "installations", id);
    }

    // Checks the source path stored in the first indexed asset.
    private static bool MatchesGame(string assets, string game)
    {
        using var reader = new BinaryIndexReader(assets);
        using var document = reader.Read();
        if (document == null)
        {
            return false;
        }
        if (!document.RootElement.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        string path = Path.GetFullPath(source.GetString()!);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(game + Path.DirectorySeparatorChar, comparison);
    }
}
