using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PrefabExporter.Core;

// Runs a private AssetRipper process and communicates with its local API.
internal sealed class AssetRipperSession : IDisposable
{
    private readonly Process process;
    private readonly HttpClient client;

    // Starts a headless process on an available local port.
    public AssetRipperSession(string executable)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--headless");
        start.ArgumentList.Add("--log=false");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start AssetRipper.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMinutes(10) };
    }

    // Waits for the local server to become ready.
    public async Task WaitUntilReady(CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException("AssetRipper exited before starting.");
            }
            try
            {
                using var response = await client.GetAsync("/", cancellation);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(200, cancellation);
        }
        throw new TimeoutException("AssetRipper did not start.");
    }

    // Loads all required bundle or asset files in one request.
    public async Task Load(IEnumerable<string> paths, CancellationToken cancellation)
    {
        using var form = new FormUrlEncodedContent(paths.Select(path => new KeyValuePair<string, string>("Path", path)));
        using var response = await client.PostAsync("/LoadFile", form, cancellation);
        response.EnsureSuccessStatusCode();
    }

    // Fetches text from the local AssetRipper server.
    public async Task<string> GetText(string path, CancellationToken cancellation)
    {
        return await client.GetStringAsync(path, cancellation);
    }

    // Fetches one asset from the local AssetRipper server.
    public async Task<byte[]> GetBytes(string path, CancellationToken cancellation)
    {
        return await client.GetByteArrayAsync(path, cancellation);
    }

    // Stops only the process started by this session.
    public void Dispose()
    {
        client.Dispose();
        if (!process.HasExited)
        {
            process.Kill(true);
            process.WaitForExit();
        }
        process.Dispose();
    }
}
