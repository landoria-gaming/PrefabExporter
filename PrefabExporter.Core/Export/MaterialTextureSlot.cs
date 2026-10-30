namespace PrefabExporter.Core;

// Stores a material texture property's name and UV transform.
internal sealed record MaterialTextureSlot(string Name, float[] Scale, float[] Offset);
