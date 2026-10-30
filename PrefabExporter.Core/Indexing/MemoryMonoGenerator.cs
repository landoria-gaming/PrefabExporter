using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace PrefabExporter.Core;

// Builds script type trees from in-memory copies of the game assemblies.
internal sealed class MemoryMonoGenerator : IMonoBehaviourTemplateGenerator
{
    private readonly string directory;
    private readonly MemoryAssemblyResolver resolver;
    private readonly MonoCecilTempGenerator generator;

    // Creates a generator for the game managed directory.
    public MemoryMonoGenerator(string directory)
    {
        this.directory = directory;
        resolver = new MemoryAssemblyResolver(directory);
        generator = new MonoCecilTempGenerator(directory);
    }

    // Adds serialized script fields to the base MonoBehaviour template.
    public AssetTypeTemplateField GetTemplateField(AssetTypeTemplateField baseField, string assemblyName,
        string nameSpace, string className, UnityVersion unityVersion)
    {
        string name = assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? assemblyName : assemblyName + ".dll";
        string path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            return null!;
        }
        var fields = generator.Read(resolver.Load(path), nameSpace, className, unityVersion);
        var result = baseField.Clone();
        result.Children.AddRange(fields);
        return result;
    }

    // Releases temporary type-tree and assembly data.
    public void Dispose()
    {
        generator.Dispose();
        resolver.Dispose();
    }
}
