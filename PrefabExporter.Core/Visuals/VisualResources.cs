using System.Text.Json.Nodes;
using SkiaSharp;

namespace PrefabExporter.Core;

// Extracts standard visual resources from the reader.
internal static class VisualResources
{
    // Downloads resources to a staging directory.
    public static async Task<Dictionary<string, string>> Download(AssetReader reader, Dictionary<string, JsonObject> objects, string folder, CancellationToken token)
    {
        var paths = new Dictionary<string, string>();
        foreach (var (key, data) in objects)
        {
            string name = data["m_Name"]?.GetValue<string>() ?? "asset";
            string? type = data.ContainsKey("m_VertexData") ? "meshes" : data.ContainsKey("m_RD") && data.ContainsKey("m_Rect") ? "icons" :
                data.ContainsKey("m_Depth") && data.ContainsKey("m_Width") ? "volume" :
                data.ContainsKey("m_Width") && data.ContainsKey("m_Height") && data.ContainsKey("m_ImageData") ? "textures" : null;
            if (type == null) { continue; }
            if (type == "volume") { reader.Warnings.Add($"3D texture {name} cannot be exported as PNG."); continue; }
            try
            {
                string ext = type == "meshes" ? ".glb" : ".png";
                string url = AssetReader.Url(key, type == "meshes" ? "Model.glb" : "Image") + (ext == ".png" ? "&Extension=png" : "");
                byte[] bytes = await reader.Get(url, token);
                if (ext == ".glb" && !bytes.AsSpan().StartsWith("glTF"u8) || ext == ".png" && !bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                { throw new InvalidDataException("Unexpected resource format."); }
                string path = Unique(Path.Combine(folder, "files", type), name, ext);
                if (type == "icons") { await Sprite(path, bytes, data, token); }
                else { await File.WriteAllBytesAsync(path, bytes, token); paths[key] = path; }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or ArgumentException or InvalidOperationException)
            { reader.Warnings.Add($"Unable to extract {type} {name}: {e.Message}"); }
        }
        return paths;
    }

    // Selects a collision-free filename.
    private static string Unique(string directory, string name, string extension)
    {
        Directory.CreateDirectory(directory);
        string clean = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || "-_ .".Contains(c) ? c : '_')).Trim(' ', '.');
        if (clean.Length == 0) { clean = "asset"; }
        string path = Path.Combine(directory, clean + extension);
        for (int n = 2; File.Exists(path); n++) { path = Path.Combine(directory, $"{clean}-{n}{extension}"); }
        return path;
    }

    // Crops an unrotated sprite from its atlas.
    private static async Task Sprite(string path, byte[] bytes, JsonObject data, CancellationToken token)
    {
        var rd = data["m_RD"]!;
        if (((rd["m_SettingsRaw"]?.GetValue<int>() ?? 0) >> 2 & 15) != 0) { throw new InvalidDataException("Rotated sprites are not supported."); }
        var rect = rd["m_TextureRect"]!;
        int x = (int)rect["m_X"]!.GetValue<double>();
        int y = (int)rect["m_Y"]!.GetValue<double>();
        int w = (int)rect["m_Width"]!.GetValue<double>();
        int h = (int)rect["m_Height"]!.GetValue<double>();
        using var image = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Invalid sprite image.");
        if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > image.Width || y + h > image.Height)
        { throw new InvalidDataException("Sprite rectangle is outside the atlas."); }
        using var cropped = new SKBitmap(w, h);
        if (!image.ExtractSubset(cropped, new SKRectI(x, image.Height - y - h, x + w, image.Height - y)))
        { throw new InvalidDataException("Could not crop sprite."); }
        using var result = SKImage.FromBitmap(cropped);
        using var encoded = result.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, encoded.ToArray(), token);
    }
}
