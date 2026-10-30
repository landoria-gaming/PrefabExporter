using System.Text.Json;

namespace PrefabExporter.Core;

// Encodes and decodes JSON values as typed binary data.
internal static class BinaryJson
{
    // Writes a JSON value and its children to a binary stream.
    public static void Write(BinaryWriter writer, JsonElement value)
    {
        writer.Write((byte)value.ValueKind);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.Write(value.EnumerateObject().Count());
                foreach (var property in value.EnumerateObject())
                {
                    writer.Write(property.Name);
                    Write(writer, property.Value);
                }
                break;
            case JsonValueKind.Array:
                writer.Write(value.GetArrayLength());
                foreach (var item in value.EnumerateArray())
                {
                    Write(writer, item);
                }
                break;
            case JsonValueKind.String:
                writer.Write(value.GetString()!);
                break;
            case JsonValueKind.Number:
                bool integer = value.TryGetInt64(out long number);
                writer.Write(integer);
                if (integer)
                {
                    writer.Write(number);
                }
                else
                {
                    writer.Write(value.GetRawText());
                }
                break;
        }
    }

    // Writes a binary value to a JSON writer for existing consumers.
    public static void Read(BinaryReader reader, Utf8JsonWriter writer)
    {
        switch ((JsonValueKind)reader.ReadByte())
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                int properties = ReadCount(reader);
                for (int i = 0; i < properties; i++)
                {
                    writer.WritePropertyName(reader.ReadString());
                    Read(reader, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                int items = ReadCount(reader);
                for (int i = 0; i < items; i++)
                {
                    Read(reader, writer);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(reader.ReadString()); break;
            case JsonValueKind.Number:
                if (reader.ReadBoolean())
                {
                    writer.WriteNumberValue(reader.ReadInt64());
                }
                else
                {
                    writer.WriteRawValue(reader.ReadString());
                }
                break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidDataException("Unknown binary JSON value.");
        }
    }

    // Skips a binary value, including any nested values.
    public static void Skip(BinaryReader reader)
    {
        switch ((JsonValueKind)reader.ReadByte())
        {
            case JsonValueKind.Object:
                int fields = ReadCount(reader);
                for (int i = 0; i < fields; i++)
                {
                    reader.ReadString();
                    Skip(reader);
                }
                break;
            case JsonValueKind.Array:
                int items = ReadCount(reader);
                for (int i = 0; i < items; i++)
                {
                    Skip(reader);
                }
                break;
            case JsonValueKind.String: reader.ReadString(); break;
            case JsonValueKind.Number:
                if (reader.ReadBoolean())
                {
                    reader.ReadInt64();
                }
                else
                {
                    reader.ReadString();
                }
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null: break;
            default: throw new InvalidDataException("Unknown binary JSON value.");
        }
    }

    // Rejects invalid collection counts.
    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 10000000)
        {
            throw new InvalidDataException("Invalid binary JSON collection size.");
        }
        return count;
    }
}
