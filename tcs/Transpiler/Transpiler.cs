using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs;

public static class Transpiler
{
    private static readonly MetadataReference[] DefaultReferences = GetDefaultReferences();

    private static MetadataReference[] GetDefaultReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Linq.dll")),
        ];
    }

    public static string Transpile(string csharpSource) => Transpile([csharpSource]);

    public static string Transpile(string[] csharpSources)
    {
        var trees = csharpSources.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
        var compilation = CSharpCompilation.Create("TinyCs",
            trees,
            DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var emitter = new LuaEmitter();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            emitter.Visit(compilation, model, tree);
        }
        return emitter.ToString();
    }
}
