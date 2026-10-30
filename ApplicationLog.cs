using System.IO;
using System.Text;

namespace PrefabExporter;

// Writes application diagnostics to one file beside the executable.
internal static class ApplicationLog
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "log.txt");
    private static readonly TextWriter writer = TextWriter.Synchronized(new StreamWriter(
        new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
    {
        AutoFlush = true
    });

    public static TextWriter Writer => writer;
    public static long Length => new FileInfo(Path).Length;

    // Records an application error with its full diagnostic details.
    public static void Error(Exception error)
    {
        Writer.WriteLine($"ERROR {DateTimeOffset.Now:O}: {error}");
    }

    // Reads diagnostics appended since an indexing operation began.
    public static string ReadSince(long position)
    {
        using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        file.Seek(Math.Min(position, file.Length), SeekOrigin.Begin);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
