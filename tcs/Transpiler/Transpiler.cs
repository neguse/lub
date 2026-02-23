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

    public static string Transpile(string csharpSource)
    {
        var tree = CSharpSyntaxTree.ParseText(csharpSource);
        var compilation = CSharpCompilation.Create("TinyCs",
            [tree],
            DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree);
        var emitter = new LuaEmitter();
        emitter.Visit(compilation, model, tree);
        return emitter.ToString();
    }
}
