using System.Text.Json.Nodes;

namespace PrefabExporter.Core;

// Builds the active prefab hierarchy and level-of-detail groups.
internal static class VisualHierarchy
{
    // Resolves active game objects and their attached components.
    public static async Task<List<VisualNode>> Build(AssetReader reader, Dictionary<string, JsonObject> objects, string root, CancellationToken token)
    {
        var nodes = new List<VisualNode>();
        var pending = new Stack<(string Key, int Parent)>();
        var seen = new HashSet<string>();
        pending.Push((root, -1));
        while (pending.TryPop(out var next))
        {
            if (!seen.Add(next.Key) || !objects.TryGetValue(next.Key, out var game)) { continue; }
            if (next.Key != root && game["m_IsActive"]?.GetValue<bool>() == false) { continue; }
            var components = new List<string>();
            foreach (var item in game["m_Components"]?.AsArray() ?? [])
            {
                string? key = await reader.Resolve(next.Key, item?["m_Component"], false, token);
                if (key != null && objects.ContainsKey(key)) { components.Add(key); }
            }
            string transformKey = components.FirstOrDefault(k => objects[k].ContainsKey("m_Children") && objects[k].ContainsKey("m_LocalPosition"))
                ?? throw new InvalidDataException("A prefab transform is missing.");
            var transform = objects[transformKey];
            int parent = nodes.Count;
            nodes.Add(new VisualNode(next.Parent, game, transform, components));
            foreach (var child in (transform["m_Children"]?.AsArray() ?? []).Reverse())
            {
                string? childKey = await reader.Resolve(transformKey, child, false, token);
                if (childKey == null || !objects.TryGetValue(childKey, out var childData)) { continue; }
                string? childGame = await reader.Resolve(childKey, childData["m_GameObject"], false, token);
                if (childGame != null) { pending.Push((childGame, parent)); }
            }
        }
        return nodes;
    }

    // Collects renderers used by each level of each LOD group.
    public static async Task<List<List<HashSet<string>>>> Groups(AssetReader reader, Dictionary<string, JsonObject> objects, List<VisualNode> nodes, CancellationToken token)
    {
        var groups = new List<List<HashSet<string>>>();
        foreach (string key in nodes.SelectMany(node => node.Components))
        {
            var data = objects[key];
            if (data["m_LODs"] is not JsonArray lods || data["m_Enabled"]?.GetValue<bool>() == false) { continue; }
            var levels = new List<HashSet<string>>();
            foreach (var lod in lods)
            {
                var level = new HashSet<string>();
                foreach (var entry in lod?["m_Renderers"]?.AsArray() ?? [])
                {
                    string? renderer = await reader.Resolve(key, entry?["m_Renderer"], false, token);
                    if (renderer != null) { level.Add(renderer); }
                }
                levels.Add(level);
            }
            if (levels.Count > 0) { groups.Add(levels); }
        }
        return groups;
    }

    // Excludes renderers belonging to other detail levels.
    public static HashSet<string> Excluded(List<List<HashSet<string>>> groups, int level)
    {
        var included = new HashSet<string>();
        var excluded = new HashSet<string>();
        foreach (var group in groups)
        {
            int choice = Math.Min(level, group.Count - 1);
            for (int index = 0; index < group.Count; index++)
            { (index == choice ? included : excluded).UnionWith(group[index]); }
        }
        excluded.ExceptWith(included);
        return excluded;
    }
}
