using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace PrefabExporter.Core;

// Reads assets from a private asset reader process.
internal sealed class AssetReader : IDisposable
{
    private readonly Process process;
    private readonly HttpClient client;
    private readonly Dictionary<string, JsonObject> cache = [];
    private readonly Dictionary<string, List<string>> dependencies = [];
    private List<string>? sprites;
    public HashSet<string> Warnings { get; } = [];

    // Starts the reader on an available local port.
    public AssetReader(string executable)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMinutes(10) };
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "--headless", "--log=false", "--port", port.ToString() })
        {
            info.ArgumentList.Add(arg);
        }
        process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the asset reader.");
    }

    // Waits until the reader is ready and loads the selected bundles.
    public async Task Load(IEnumerable<string> bundles, CancellationToken token)
    {
        bool ready = false;
        for (int i = 0; i < 150; i++)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) { throw new InvalidOperationException("The asset reader exited before starting."); }
            try { using var response = await client.GetAsync("/", token); ready = response.IsSuccessStatusCode; }
            catch (HttpRequestException) { }
            if (ready) { break; }
            await Task.Delay(200, token);
        }
        if (!ready) { throw new TimeoutException("The asset reader did not start."); }
        using var body = new FormUrlEncodedContent(bundles.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new KeyValuePair<string, string>("Path", path)));
        using var result = await client.PostAsync("/LoadFile", body, token);
        result.EnsureSuccessStatusCode();
    }

    // Builds the URL for an asset endpoint.
    public static string Url(string key, string endpoint) => $"/Assets/{endpoint}?Path={Uri.EscapeDataString(key)}";

    // Downloads binary resource data.
    public async Task<byte[]> Get(string url, CancellationToken token) => await client.GetByteArrayAsync(url, token);

    // Reads and caches serialized asset data.
    public async Task<JsonObject> Read(string key, CancellationToken token)
    {
        if (!cache.TryGetValue(key, out var value))
        {
            string json = Encoding.UTF8.GetString(await Get(Url(key, "Json"), token));
            try { value = JsonNode.Parse(json)!.AsObject(); }
            catch (System.Text.Json.JsonException)
            {
                json = Regex.Replace(json, @"(?<=[\s\[:,])(?:-?Infinity|NaN)(?=[\s,}\]])", "null");
                value = JsonNode.Parse(json)!.AsObject();
            }
            cache[key] = value;
        }
        return value;
    }

    // Finds linked asset identities on an HTML page.
    private async Task<List<string>> Links(string url, CancellationToken token)
    {
        var page = await new HtmlParser().ParseDocumentAsync(await client.GetStringAsync(url, token), token);
        var result = new List<string>();
        foreach (var anchor in page.QuerySelectorAll("a[href^='/Assets/View?']"))
        {
            string href = anchor.GetAttribute("href")!;
            int start = href.IndexOf("Path=", StringComparison.Ordinal);
            if (start < 0) { continue; }
            string path = href[(start + 5)..].Split('&')[0];
            var data = JsonNode.Parse(Uri.UnescapeDataString(path))!;
            result.Add(data.ToJsonString());
        }
        return result;
    }

    // Finds the prefab root using its original game path.
    public async Task<string> Find(GamePrefab prefab, CancellationToken token)
    {
        var roots = new List<string>();
        foreach (string key in await Links("/Search/View?q=" + Uri.EscapeDataString(prefab.Name), token))
        {
            var data = await Read(key, token);
            if (data["m_Name"]?.GetValue<string>() != prefab.Name || data["m_Components"] is not JsonArray components) { continue; }
            foreach (var component in components)
            {
                string? target = await Resolve(key, component?["m_Component"], false, token);
                if (target == null) { continue; }
                var transform = await Read(target, token);
                if (transform["m_Father"] is not JsonObject father || father["m_PathID"]?.GetValue<long>() != 0) { continue; }
                var page = await new HtmlParser().ParseDocumentAsync(await client.GetStringAsync(Url(key, "View"), token), token);
                string? original = page.QuerySelectorAll("tr").FirstOrDefault(row => row.QuerySelector("th")?.TextContent == "Original Path")?.QuerySelector("td")?.TextContent;
                if (string.Equals(original?.Replace('\\', '/'), prefab.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) { roots.Add(key); }
            }
        }
        return roots.Count == 1 ? roots[0] : throw new InvalidDataException($"Expected one prefab root for {prefab.Path}; found {roots.Count}.");
    }

    // Resolves local and external Unity pointers.
    public async Task<string?> Resolve(string source, JsonNode? pointer, bool icon, CancellationToken token)
    {
        if (pointer is not JsonObject obj || obj["m_PathID"]?.GetValue<long>() is not long id || id == 0) { return null; }
        int file = obj["m_FileID"]?.GetValue<int>() ?? 0;
        if (file == 0)
        {
            var path = JsonNode.Parse(source)!.AsObject();
            path["D"] = id;
            return path.ToJsonString();
        }
        if (!dependencies.TryGetValue(source, out var links))
        {
            links = await Links(Url(source, "View"), token);
            dependencies[source] = links;
        }
        var matches = links.Where(link => JsonNode.Parse(link)?["D"]?.GetValue<long>() == id).Distinct().ToArray();
        if (matches.Length == 0 && icon)
        {
            sprites ??= await Links("/Search/View?q=Sprite", token);
            matches = sprites.Where(link => JsonNode.Parse(link)?["D"]?.GetValue<long>() == id).Distinct().ToArray();
        }
        if (matches.Length == 1) { return matches[0]; }
        Warnings.Add($"Unresolved external reference: file {file}, object {id}");
        return null;
    }

    // Stops the reader and releases its resources.
    public void Dispose()
    {
        if (!process.HasExited) { process.Kill(true); process.WaitForExit(); }
        process.Dispose();
        client.Dispose();
    }
}
