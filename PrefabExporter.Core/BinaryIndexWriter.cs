using System.Text;
using System.Text.Json;

namespace PrefabExporter.Core;

// Writes length-prefixed binary records from JSON values.
internal sealed class BinaryIndexWriter : TextWriter
{
    private readonly FileStream file;
    private readonly BinaryWriter writer;
    private readonly MemoryStream buffer = new();
    private readonly BinaryWriter record;
    public override Encoding Encoding => Encoding.UTF8;

    // Creates a versioned binary index file.
    public BinaryIndexWriter(string path)
    {
        file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
        writer = new BinaryWriter(file);
        record = new BinaryWriter(buffer);
        writer.Write(BinaryIndexReader.Magic);
        writer.Write(BinaryIndexReader.Version);
    }

    // Encodes and appends one JSON record.
    public override void WriteLine(string? value)
    {
        using var document = JsonDocument.Parse(value ?? throw new ArgumentNullException(nameof(value)));
        buffer.SetLength(0);
        BinaryJson.Write(record, document.RootElement);
        writer.Write(checked((int)buffer.Length));
        writer.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // Closes all streams for this index.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            record.Dispose();
            buffer.Dispose();
            writer.Dispose();
            file.Dispose();
        }
        base.Dispose(disposing);
    }
}
