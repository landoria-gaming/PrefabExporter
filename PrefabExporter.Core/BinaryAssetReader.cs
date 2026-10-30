using System.Text.Json;

namespace PrefabExporter.Core;

// Reads asset fields directly from binary records.
internal static class BinaryAssetReader
{
    // Reads the fields of one asset without converting it back to JSON.
    public static IndexedAsset Read(BinaryReader reader)
    {
        Expect(reader, JsonValueKind.Object);
        int count = reader.ReadInt32();
        if (count < 0 || count > 10000000)
        {
            throw new InvalidDataException("Invalid asset field count.");
        }
        string? source = null, collection = null, type = null, name = null, path = null, soft = null;
        long? id = null;
        for (int i = 0; i < count; i++)
        {
            switch (reader.ReadString())
            {
                case "source": source = ReadString(reader); break;
                case "collection": collection = ReadString(reader); break;
                case "path_id": id = ReadInteger(reader); break;
                case "type": type = ReadString(reader); break;
                case "name": name = ReadString(reader); break;
                case "asset_path": path = ReadString(reader); break;
                case "soft_id": soft = ReadString(reader); break;
                default: BinaryJson.Skip(reader); break;
            }
        }
        if (id == null)
        {
            throw new InvalidDataException("Asset path ID is missing.");
        }
        return new IndexedAsset(source, collection, id.Value, type, name, path, soft);
    }

    // Reads a nullable string field.
    private static string? ReadString(BinaryReader reader)
    {
        var kind = (JsonValueKind)reader.ReadByte();
        if (kind == JsonValueKind.Null)
        {
            return null;
        }
        if (kind != JsonValueKind.String)
        {
            throw new InvalidDataException("Invalid asset string field.");
        }
        return reader.ReadString();
    }

    // Reads a signed integer field.
    private static long ReadInteger(BinaryReader reader)
    {
        Expect(reader, JsonValueKind.Number);
        if (!reader.ReadBoolean())
        {
            throw new InvalidDataException("Invalid asset integer field.");
        }
        return reader.ReadInt64();
    }

    // Checks a binary value type.
    private static void Expect(BinaryReader reader, JsonValueKind kind)
    {
        if ((JsonValueKind)reader.ReadByte() != kind)
        {
            throw new InvalidDataException("Unexpected binary value type.");
        }
    }
}
