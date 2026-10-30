using System.Text.Json;

namespace PrefabExporter.Core;

// Summarizes the visual resources of a prefab and its children.
internal static class VisualSummary
{
    // Follows visual component links and lists models, materials, shaders and textures.
    public static async Task<string> Build(string temporary, HashSet<string> roots,
        Dictionary<string, IndexedAsset> names, CancellationToken cancellation)
    {
        var reachable = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        var lines = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new Dictionary<string, IReadOnlyDictionary<int, MaterialTextureSlot>>(StringComparer.OrdinalIgnoreCase);
        using var reader = File.OpenText(temporary);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellation)) != null)
        {
            var edge = JsonSerializer.Deserialize<ReferenceEdge>(line)!;
            if (!reachable.Contains(edge.From) || !names.TryGetValue(edge.From, out var from) ||
                !IsVisualLink(from.Type, edge.Field))
            {
                continue;
            }
            reachable.Add(edge.To);
            if (names.TryGetValue(edge.To, out var target))
            {
                AddResource(lines, slots, from, target, edge);
            }
            else
            {
                lines.Add($"Unresolved resource referenced by {from.Name ?? from.Type}");
            }
        }
        if (lines.Count == 0)
        {
            return "\nNo visual resources or icons found.\n";
        }
        return "\nVisual resources:\n" + string.Join(Environment.NewLine, lines) + "\n";
    }

    // Restricts the summary to the prefab hierarchy and rendering components.
    internal static bool IsVisualLink(string? type, string field)
    {
        return type switch
        {
            "GameObject" => field.StartsWith("/m_Component[", StringComparison.Ordinal),
            "Transform" or "RectTransform" => field.StartsWith("/m_Children[", StringComparison.Ordinal) ||
                field.StartsWith("/m_GameObject[", StringComparison.Ordinal),
            "MeshFilter" or "SkinnedMeshRenderer" => field.StartsWith("/m_Mesh[", StringComparison.Ordinal) ||
                field.StartsWith("/m_Materials[", StringComparison.Ordinal),
            "MeshRenderer" => field.StartsWith("/m_Materials[", StringComparison.Ordinal),
            "LODGroup" => field.StartsWith("/m_LODs[", StringComparison.Ordinal),
            "MonoBehaviour" => field.StartsWith("/m_icon[", StringComparison.Ordinal) ||
                field.Contains("/m_icons[", StringComparison.Ordinal),
            "Material" => field.StartsWith("/m_Shader[", StringComparison.Ordinal) ||
                field.Contains("/m_TexEnvs[", StringComparison.Ordinal),
            _ => false
        };
    }

    // Adds a readable description of a visual resource and its material slot.
    private static void AddResource(SortedSet<string> lines,
        Dictionary<string, IReadOnlyDictionary<int, MaterialTextureSlot>> slots, IndexedAsset from, IndexedAsset target,
        ReferenceEdge edge)
    {
        if (target.Type is not ("Mesh" or "Material" or "Texture2D" or "Shader" or "Sprite"))
        {
            return;
        }
        string label = target.Type == "Sprite" ? "Icon" : target.Type!;
        if (from.Type == "Material" && target.Type == "Texture2D")
        {
            if (!slots.TryGetValue(edge.From, out var properties))
            {
                properties = MaterialTextureSlots.Read(from);
                slots[edge.From] = properties;
            }
            int index = TextureIndex(edge.Field);
            string property = properties.TryGetValue(index, out var found) ? found.Name : "unknown slot";
            label = $"Texture ({SlotLabel(property)}) for {from.Name}";
        }
        string name = string.IsNullOrEmpty(target.Name) ? target.Type! : target.Name;
        string path = string.IsNullOrEmpty(target.AssetPath) ? "" : " | " + target.AssetPath;
        lines.Add($"{label}: {name}{path}");
    }

    // Extracts the texture array index from a serialized field path.
    internal static int TextureIndex(string field)
    {
        int start = field.IndexOf("/data[", StringComparison.Ordinal);
        if (start < 0)
        {
            return -1;
        }
        start += 6;
        int end = field.IndexOf(']', start);
        return end > start && int.TryParse(field.AsSpan(start, end - start), out int index) ? index : -1;
    }

    // Gives common shader texture properties readable names.
    private static string SlotLabel(string property) => property switch
    {
        "_MainTex" or "_BaseMap" or "_BaseColorMap" => $"Albedo {property}",
        "_BumpMap" or "_NormalMap" => $"Normal map {property}",
        _ => property
    };
}
