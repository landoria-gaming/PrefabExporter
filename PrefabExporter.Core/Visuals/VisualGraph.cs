using System.Text.Json.Nodes;

namespace PrefabExporter.Core;

// Traverses visual resources referenced by a prefab.
internal static class VisualGraph
{
    // Reads the visual object graph without a persistent index.
    public static async Task<Dictionary<string, JsonObject>> Read(AssetReader reader, string root, IProgress<string>? progress, CancellationToken token)
    {
        var result = new Dictionary<string, JsonObject>();
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out string? key))
        {
            if (result.ContainsKey(key)) { continue; }
            JsonObject data;
            try { data = await reader.Read(key, token); }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException)
            {
                reader.Warnings.Add($"Unable to read asset: {e.Message}");
                continue;
            }
            result[key] = data;
            foreach (var (value, icon) in Fields(data))
            {
                foreach (var pointer in Pointers(value))
                {
                    string? target = await reader.Resolve(key, pointer, icon, token);
                    if (target != null && !result.ContainsKey(target)) { pending.Enqueue(target); }
                }
            }
            if (result.Count % 50 == 0) { progress?.Report($"Reading prefab resources: {result.Count} objects..."); }
        }
        return result;
    }

    // Selects fields relevant to visuals and icons.
    private static IEnumerable<(JsonNode?, bool)> Fields(JsonObject data)
    {
        string[] fields = data.ContainsKey("m_Components") ? ["m_Components"] :
            data.ContainsKey("m_Children") && data.ContainsKey("m_LocalPosition") ? ["m_Children", "m_GameObject"] :
            data.ContainsKey("m_Materials") ? ["m_Materials", "m_Mesh"] :
            data.ContainsKey("m_Mesh") ? ["m_Mesh"] : data.ContainsKey("m_LODs") ? ["m_LODs"] :
            data.ContainsKey("m_SavedProperties") ? ["m_SavedProperties", "m_Shader"] : ["m_icon", "m_icons"];
        foreach (string field in fields) { yield return (data[field], field is "m_icon" or "m_icons"); }
        foreach (var icon in Icons(data["m_Structure"])) { yield return (icon, true); }
    }

    // Finds icon fields inside serialized script data.
    private static IEnumerable<JsonNode?> Icons(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var entry in obj)
            {
                if (entry.Key is "m_icon" or "m_icons") { yield return entry.Value; }
                else { foreach (var child in Icons(entry.Value)) { yield return child; } }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) { foreach (var child in Icons(item)) { yield return child; } }
        }
    }

    // Enumerates pointers nested inside a serialized value.
    private static IEnumerable<JsonObject> Pointers(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("m_FileID") && obj.ContainsKey("m_PathID")) { yield return obj; }
            else { foreach (var entry in obj) { foreach (var pointer in Pointers(entry.Value)) { yield return pointer; } } }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) { foreach (var pointer in Pointers(item)) { yield return pointer; } }
        }
    }

    // Lists the names and types of discovered visual resources.
    public static IEnumerable<string> Summary(IEnumerable<JsonObject> objects)
    {
        var entries = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var data in objects)
        {
            string? type = data.ContainsKey("m_VertexData") ? "Mesh" : data.ContainsKey("m_SavedProperties") ? "Material" :
                data.ContainsKey("m_Depth") && data.ContainsKey("m_Width") ? "3D texture" :
                data.ContainsKey("m_Width") && data.ContainsKey("m_Height") ? "Texture" :
                data.ContainsKey("m_RD") && data.ContainsKey("m_Rect") ? "Icon" : data.ContainsKey("m_ParsedForm") ? "Shader" : null;
            if (type != null) { entries.Add($"{type}: {data["m_Name"]?.GetValue<string>() ?? "unnamed"}"); }
        }
        return entries;
    }
}
