using System.Text.Json;
using SkiaSharp;

namespace PrefabExporter.Core;

// Crops sprites returned as full atlas images by AssetRipper.
internal static class SpriteCropper
{
    // Uses the sprite render rectangle to extract one PNG icon.
    public static byte[] Crop(byte[] image, string metadata)
    {
        using var json = JsonDocument.Parse(metadata);
        var render = json.RootElement.GetProperty("m_RD");
        var rect = render.GetProperty("m_TextureRect");
        int x = (int)rect.GetProperty("m_X").GetSingle();
        int y = (int)rect.GetProperty("m_Y").GetSingle();
        int width = (int)rect.GetProperty("m_Width").GetSingle();
        int height = (int)rect.GetProperty("m_Height").GetSingle();
        uint settings = render.GetProperty("m_SettingsRaw").GetUInt32();
        if (((settings >> 2) & 15) != 0)
        {
            throw new NotSupportedException("Rotated or flipped atlas sprites are not supported.");
        }
        using var original = SKBitmap.Decode(image) ?? throw new InvalidDataException("Invalid sprite image.");
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Sprite rectangle is empty.");
        }
        if (original.Width == width && original.Height == height)
        {
            return image;
        }
        int top = original.Height - y - height;
        if (x < 0 || top < 0 || x + width > original.Width || top + height > original.Height)
        {
            throw new InvalidDataException("Sprite rectangle is outside the atlas.");
        }
        using var cropped = new SKBitmap();
        if (!original.ExtractSubset(cropped, new SKRectI(x, top, x + width, top + height)))
        {
            throw new InvalidDataException("Could not crop the sprite.");
        }
        using var picture = SKImage.FromBitmap(cropped);
        using var encoded = picture.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
