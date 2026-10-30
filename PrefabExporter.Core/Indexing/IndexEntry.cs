namespace PrefabExporter.Core;

// Describes one completed index shown in the application.
public sealed record IndexEntry(string Directory, string Version, DateTime CompletedUtc);
