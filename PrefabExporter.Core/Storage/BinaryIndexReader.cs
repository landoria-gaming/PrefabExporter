using System.Text.Json;

namespace PrefabExporter.Core;

// Streams and seeks versioned binary index records.
internal sealed class BinaryIndexReader : IDisposable
{
    public const int Magic = 0x58444950;
    public const int Version = 1;
    private readonly FileStream stream;
    private readonly BinaryReader reader;
    public long Position => stream.Position;
    public long Length => stream.Length;

    // Opens a binary index and validates its header.
    public BinaryIndexReader(string path)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        reader = new BinaryReader(stream);
        try
        {
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
            {
                throw new InvalidDataException("Unsupported binary index format.");
            }
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    // Reads one record at the current position, or returns null at end of file.
    public JsonDocument? Read()
    {
        if (stream.Position == stream.Length)
        {
            return null;
        }
        int length = reader.ReadInt32();
        if (length <= 0 || length > stream.Length - stream.Position)
        {
            throw new InvalidDataException("Invalid binary index record length.");
        }
        byte[] data = reader.ReadBytes(length);
        if (data.Length != length)
        {
            throw new EndOfStreamException();
        }
        using var input = new BinaryReader(new MemoryStream(data, false));
        using var json = new MemoryStream();
        using (var output = new Utf8JsonWriter(json))
        {
            BinaryJson.Read(input, output);
        }
        if (input.BaseStream.Position != length)
        {
            throw new InvalidDataException("Unexpected data after binary index record.");
        }
        return JsonDocument.Parse(json.ToArray());
    }

    // Reads one asset directly without creating a JSON representation.
    public IndexedAsset? ReadAsset()
    {
        if (stream.Position == stream.Length)
        {
            return null;
        }
        int length = reader.ReadInt32();
        if (length <= 0 || length > stream.Length - stream.Position)
        {
            throw new InvalidDataException("Invalid binary index record length.");
        }
        long end = stream.Position + length;
        var asset = BinaryAssetReader.Read(reader);
        if (stream.Position != end)
        {
            throw new InvalidDataException("Unexpected data in binary asset record.");
        }
        return asset;
    }

    // Reads an asset at its stored byte position.
    public IndexedAsset ReadAssetAt(long offset)
    {
        stream.Position = offset;
        return ReadAsset() ?? throw new EndOfStreamException();
    }

    // Reads a record at its stored byte position.
    public JsonDocument ReadAt(long offset)
    {
        stream.Position = offset;
        return Read() ?? throw new EndOfStreamException();
    }

    // Closes the index file.
    public void Dispose()
    {
        reader.Dispose();
    }
}
