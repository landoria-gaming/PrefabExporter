using System.Diagnostics;
using System.Text.Json;

namespace PrefabExporter.Core;

// Uses a local Blender installation to assemble and export an FBX.
internal static class BlenderFbxExporter
{
    // Reconstructs the prefab scene and runs Blender in the background.
    public static async Task Export(ExtractionPlan plan, Dictionary<string, string> paths, string name,
        string stage, IProgress<string>? progress, CancellationToken cancellation)
    {
        string executable = FindBlender();
        string target = Path.Combine(stage, name + ".fbx");
        var scene = new PrefabSceneBuilder(plan, paths).Build(name, target, cancellation);
        string temp = Path.Combine(Path.GetTempPath(), "prefab-blender-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string script = Path.Combine(temp, "export.py");
            string manifest = Path.Combine(temp, "scene.json");
            using var source = typeof(BlenderFbxExporter).Assembly.GetManifestResourceStream("PrefabExporter.Core.BlenderExport.py")
                ?? throw new InvalidOperationException("FBX export script is missing.");
            await using (var output = File.Create(script))
            {
                await source.CopyToAsync(output, cancellation);
            }
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(scene), cancellation);
            progress?.Report("Assembling FBX with Blender...");
            await Run(executable, script, manifest, cancellation);
            ValidateFbx(target);
            if (!File.Exists(Path.ChangeExtension(target, ".blend")))
            {
                throw new InvalidDataException("Blender did not produce a Blender file.");
            }
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    // Checks that Blender produced a binary FBX file.
    private static void ValidateFbx(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException("Blender did not produce an FBX file.");
        }
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[18];
        if (file.Read(header) != header.Length || !header.SequenceEqual("Kaydara FBX Binary"u8))
        {
            throw new InvalidDataException("Blender produced an invalid FBX file.");
        }
    }

    // Finds Blender using an environment override, PATH, or standard Windows installations.
    private static string FindBlender()
    {
        string? configured = Environment.GetEnvironmentVariable("BLENDER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }
        string executable = OperatingSystem.IsWindows() ? "blender.exe" : "blender";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        if (OperatingSystem.IsWindows() && Directory.Exists(programs))
        {
            string? candidate = Directory.EnumerateDirectories(programs, "Blender*")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, executable)).FirstOrDefault(File.Exists);
            if (candidate != null)
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("Blender is required for FBX export. Install Blender or set BLENDER_PATH.");
    }

    // Runs Blender and reports a failed conversion without leaving it running.
    private static async Task Run(string executable, string script, string manifest, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "--background", "--factory-startup", "--python-exit-code", "1", "--python", script, "--", manifest })
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Blender.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            throw;
        }
        string output = await stdout;
        string error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Blender FBX export failed: " + (string.IsNullOrWhiteSpace(error) ? output : error));
        }
    }
}
