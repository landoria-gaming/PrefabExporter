using System.IO;
using System.Text;

namespace PrefabExporter.UI;

// Writes application diagnostics to one file beside the executable.
internal static class ApplicationLog
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "log.txt");
    private static readonly TextWriter writer = TextWriter.Synchronized(new StreamWriter(
        new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
    {
        AutoFlush = true
    });

    // Records an application error with its full diagnostic details.
    public static void Error(Exception error)
    {
        writer.WriteLine($"ERROR {DateTimeOffset.Now:O}: {error}");
    }

    // Records warnings returned by an AssetRipper operation.
    public static void Warnings(string result)
    {
        foreach (string line in result.Split('\n'))
        {
            if (line.StartsWith("WARNING:", StringComparison.Ordinal))
            {
                writer.WriteLine($"{DateTimeOffset.Now:O} {line.TrimEnd('\r')}");
            }
        }
    }
}
