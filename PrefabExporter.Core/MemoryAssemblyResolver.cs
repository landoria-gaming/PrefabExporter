using Mono.Cecil;

namespace PrefabExporter.Core;

// Resolves managed assemblies from memory so game files are not held open.
internal sealed class MemoryAssemblyResolver : IAssemblyResolver
{
    private readonly string directory;
    private readonly Dictionary<string, AssemblyDefinition> loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly DefaultAssemblyResolver fallback = new();

    // Stores the directory containing the game assemblies.
    public MemoryAssemblyResolver(string directory)
    {
        this.directory = directory;
    }

    // Resolves an assembly using default reader settings.
    public AssemblyDefinition Resolve(AssemblyNameReference name)
    {
        return Resolve(name, new ReaderParameters());
    }

    // Loads a game assembly in memory or uses the system resolver.
    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        string path = Path.Combine(directory, name.Name + ".dll");
        if (!File.Exists(path))
        {
            return fallback.Resolve(name, parameters);
        }
        return Load(path);
    }

    // Reads and caches one managed assembly without retaining a file handle.
    public AssemblyDefinition Load(string path)
    {
        string name = Path.GetFileName(path);
        if (!loaded.TryGetValue(name, out var assembly))
        {
            var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
            assembly = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters
            {
                AssemblyResolver = this,
                InMemory = true
            });
            loaded.Add(name, assembly);
        }
        return assembly;
    }

    // Releases cached assemblies and the fallback resolver.
    public void Dispose()
    {
        foreach (var assembly in loaded.Values)
        {
            assembly.Dispose();
        }
        loaded.Clear();
        fallback.Dispose();
    }
}
