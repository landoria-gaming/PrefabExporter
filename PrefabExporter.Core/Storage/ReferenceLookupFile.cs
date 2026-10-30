using System.Security.Cryptography;

namespace PrefabExporter.Core;

// Reads and writes validated reference offset tables.
internal static class ReferenceLookupFile
{
    private const int Magic = 0x58464552;
    private const int Version = 1;
    private const int HeaderSize = 28;
    private const int EntrySize = 20;
    private const int DigestSize = 32;

    // Saves offsets with source metadata and an integrity digest.
    public static void Save(string path, string source, List<ReferencePosition> positions, CancellationToken cancellation)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(new FileInfo(source).Length);
            writer.Write(File.GetLastWriteTimeUtc(source).Ticks);
            writer.Write(positions.Count);
            foreach (var item in positions)
            {
                cancellation.ThrowIfCancellationRequested();
                writer.Write(item.Hash);
                writer.Write(item.Offset);
                writer.Write(item.Length);
            }
        }
        using var output = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1024 * 1024);
        byte[] digest = ComputeDigest(output, output.Length, cancellation);
        output.Position = output.Length;
        output.Write(digest);
    }

    // Loads an intact table only when it matches the current references file.
    public static bool TryLoad(string path, string source, List<ReferencePosition> positions, CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
            if (stream.Length < HeaderSize + DigestSize || !HasValidDigest(stream, cancellation))
            {
                return false;
            }
            stream.Position = 0;
            using var reader = new BinaryReader(stream);
            long sourceLength = new FileInfo(source).Length;
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version || reader.ReadInt64() != sourceLength ||
                reader.ReadInt64() != File.GetLastWriteTimeUtc(source).Ticks)
            {
                return false;
            }
            int count = reader.ReadInt32();
            if (count < 0 || HeaderSize + (long)count * EntrySize + DigestSize != stream.Length)
            {
                return false;
            }
            return ReadEntries(reader, count, sourceLength, positions, cancellation);
        }
        catch (IOException)
        {
            return false;
        }
    }

    // Checks ordering and byte ranges before accepting an offset table.
    private static bool ReadEntries(BinaryReader reader, int count, long sourceLength,
        List<ReferencePosition> positions, CancellationToken cancellation)
    {
        positions.EnsureCapacity(count);
        long previous = long.MinValue;
        for (int i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            long hash = reader.ReadInt64();
            long offset = reader.ReadInt64();
            int length = reader.ReadInt32();
            if (hash < previous || offset < 0 || length <= 0 || offset > sourceLength - length)
            {
                return false;
            }
            positions.Add(new ReferencePosition(hash, offset, length));
            previous = hash;
        }
        return true;
    }

    // Verifies the digest stored at the end of the table.
    private static bool HasValidDigest(FileStream stream, CancellationToken cancellation)
    {
        long length = stream.Length - DigestSize;
        byte[] actual = ComputeDigest(stream, length, cancellation);
        byte[] expected = new byte[DigestSize];
        stream.ReadExactly(expected);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // Hashes a bounded part of a stream without loading it all into memory.
    private static byte[] ComputeDigest(FileStream stream, long length, CancellationToken cancellation)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        while (length > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (count == 0)
            {
                throw new EndOfStreamException();
            }
            hash.AppendData(buffer, 0, count);
            length -= count;
        }
        return hash.GetHashAndReset();
    }
}
