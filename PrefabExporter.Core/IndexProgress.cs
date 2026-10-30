namespace PrefabExporter.Core;

// Describes indexing progress without storing it in the index.
public sealed record IndexProgress(int CompletedFiles, int TotalFiles, bool IsAuditing);
