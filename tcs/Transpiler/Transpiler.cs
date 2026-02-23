using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs;

public class TranspileResult
{
    public string Lua { get; init; } = "";
    public SourceMap? SourceMap { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool Success => Errors.Count == 0;
}

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
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Console.dll")),
        ];
    }

    public static string Transpile(string csharpSource) => Transpile([csharpSource]);

    public static string Transpile(string[] csharpSources)
    {
        var result = TranspileWithDiagnostics(csharpSources);
        if (!result.Success)
            throw new InvalidOperationException(
                string.Join("\n", result.Errors));
        return result.Lua;
    }

    public static TranspileResult TranspileWithDiagnostics(string[] csharpSources,
        string[]? filePaths = null, string[]? referenceSources = null)
    {
        var trees = csharpSources.Select((s, i) =>
            CSharpSyntaxTree.ParseText(s, path: filePaths != null && i < filePaths.Length
                ? filePaths[i] : "")).ToArray();
        var refTrees = referenceSources?.Select(s =>
            CSharpSyntaxTree.ParseText(s)).ToArray() ?? [];
        var allTrees = trees.Concat(refTrees).ToArray();
        var compilation = CSharpCompilation.Create("TinyCs",
            allTrees,
            DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = new List<string>();
        var warnings = new List<string>();

        // Check for C# compilation errors
        // Skip enum/int conversion errors (CS0266, CS0029) — TinyC# erases enums to ints
        // CS0266/CS0029: implicit conversion (enum↔int)
        // CS0019: operator mismatch (enum==int)
        // CS0535: interface member not implemented (fields as properties)
        var ignoredIds = new HashSet<string> { "CS0266", "CS0029", "CS0019", "CS0535" };
        foreach (var diag in compilation.GetDiagnostics())
        {
            if (diag.Severity == DiagnosticSeverity.Error && !ignoredIds.Contains(diag.Id))
            {
                var loc = diag.Location;
                var span = loc.GetLineSpan();
                var line = span.StartLinePosition.Line + 1;
                var col = span.StartLinePosition.Character + 1;
                var file = span.Path ?? "<source>";
                errors.Add($"{file}({line},{col}): error {diag.Id}: {diag.GetMessage()}");
            }
        }

        if (errors.Count > 0)
            return new TranspileResult { Errors = errors };

        // Naming convention analysis
        foreach (var tree in trees)
            warnings.AddRange(NamingAnalyzer.Analyze(tree));

        var emitter = new LuaEmitter();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            emitter.Visit(compilation, model, tree);
        }

        warnings.AddRange(emitter.Warnings);

        return new TranspileResult
        {
            Lua = emitter.ToString(),
            SourceMap = emitter.SourceMap,
            Warnings = warnings
        };
    }
}
