using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PrefabExporter.Core;

// Reads game metadata and starts an indexing job.
public static class Indexer
{
    // Reads the game version from its managed assembly.
    public static string Version(string game)
    {
        string data = Directory.EnumerateDirectories(game, "*_Data", SearchOption.TopDirectoryOnly).FirstOrDefault()
            ?? throw new DirectoryNotFoundException("Game data directory not found.");
        string path = Path.Combine(data, "Managed", "assembly_valheim.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("assembly_valheim.dll not found", path);
        }
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var type = assembly.MainModule.Types.First(item => item.Name == "Version");
        var code = type.Methods.First(item => item.Name == ".cctor").Body.Instructions;
        for (int i = 3; i < code.Count; i++)
        {
            bool constructor = code[i].OpCode.Code == Code.Newobj &&
                code[i].Operand.ToString()!.Contains("GameVersion::.ctor");
            if (constructor && code.Skip(i + 1).Take(2).Any(item =>
                item.Operand?.ToString()?.Contains("CurrentVersion") == true))
            {
                return string.Join(".", code.Skip(i - 3).Take(3).Select(ReadInt));
            }
        }
        throw new InvalidDataException("Game version not found in the assembly.");
    }

    // Converts an integer instruction to its value.
    private static int ReadInt(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2,
        Code.Ldc_I4_3 => 3, Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5,
        Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8,
        Code.Ldc_I4 or Code.Ldc_I4_S => Convert.ToInt32(instruction.Operand),
        _ => throw new InvalidDataException("Unable to read game version")
    };

    // Runs a complete indexing job.
    public static void Run(Settings settings, IProgress<string> progress, CancellationToken cancellation,
        IProgress<IndexProgress>? indexingProgress = null)
    {
        new IndexJob(settings, progress, cancellation, indexingProgress).Run();
    }
}
