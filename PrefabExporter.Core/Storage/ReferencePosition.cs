namespace PrefabExporter.Core;

// Stores the hash and byte range of one reference record.
internal readonly record struct ReferencePosition(long Hash, long Offset, int Length);
