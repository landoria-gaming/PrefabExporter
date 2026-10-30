using AngleSharp.Html.Parser;

namespace PrefabExporter.Core;

// Locates indexed objects in the AssetRipper search results.
internal sealed class AssetRipperSearch
{
    private readonly AssetRipperSession session;
    private readonly Dictionary<string, List<AssetRipperMatch>> cache = new(StringComparer.OrdinalIgnoreCase);

    // Stores the active local server session.
    public AssetRipperSearch(AssetRipperSession session)
    {
        this.session = session;
    }

    // Finds the exact collection, type, and object ID without relying on names alone.
    public async Task<string> Find(IndexedAsset asset, CancellationToken cancellation)
    {
        string query = string.IsNullOrWhiteSpace(asset.Name) ? asset.Type ?? "" : asset.Name;
        var matches = await Search(query, cancellation);
        var exact = Match(matches, asset);
        if (exact.Count == 0 && !query.Equals(asset.Type, StringComparison.OrdinalIgnoreCase))
        {
            exact = Match(await Search(asset.Type ?? "", cancellation), asset);
        }
        if (exact.Count != 1)
        {
            throw new InvalidDataException(exact.Count == 0 ? "Asset not found in AssetRipper." :
                "AssetRipper returned multiple matching assets.");
        }
        return exact[0].ViewPath;
    }

    // Filters search results using the complete indexed identity.
    private static List<AssetRipperMatch> Match(List<AssetRipperMatch> results, IndexedAsset asset)
    {
        string collection = Path.GetFileName((asset.Collection ?? "").Replace('\\', '/'));
        return results.Where(item => item.PathId == asset.PathId &&
            item.Type.Equals(asset.Type, StringComparison.OrdinalIgnoreCase) &&
            item.Collection.Equals(collection, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // Searches the loaded collections and caches the parsed result list.
    private async Task<List<AssetRipperMatch>> Search(string query, CancellationToken cancellation)
    {
        if (cache.TryGetValue(query, out var cached))
        {
            return cached;
        }
        string html = await session.GetText("/Search/View?q=" + Uri.EscapeDataString(query), cancellation);
        var document = await new HtmlParser().ParseDocumentAsync(html, cancellation);
        var results = new List<AssetRipperMatch>();
        foreach (var row in document.QuerySelectorAll("#resultsTable tr[data-class]"))
        {
            var cells = row.QuerySelectorAll("td");
            string? url = cells.Length >= 4 ? cells[2].QuerySelector("a")?.GetAttribute("href") : null;
            if (cells.Length >= 4 && long.TryParse(cells[0].TextContent, out long id) &&
                url != null && url.StartsWith("/Assets/View?", StringComparison.Ordinal))
            {
                results.Add(new AssetRipperMatch(id, cells[1].TextContent, cells[3].TextContent, url));
            }
        }
        cache[query] = results;
        return results;
    }
}
