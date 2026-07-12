using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    private static MetadataReference[]? _references;

    // 参照アセンブリの注入口。browser-wasm など Assembly.Location が空で
    // runtime pack をファイルとして読めない host は、byte image
    // (MetadataReference.CreateFromStream) で構築した参照をここへ設定する。
    // 未設定なら runtime pack から遅延構築する。
    public static MetadataReference[] References
    {
        get => _references ??= GetDefaultReferences();
        set => _references = value;
    }

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
            MetadataReference.CreateFromFile(typeof(global::TinySystem.Random).Assembly.Location),
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
        string[]? filePaths = null, string[]? referenceSources = null,
        string? entryClass = null, bool checkNaming = true)
    {
        var trees = csharpSources.Select((s, i) =>
            CSharpSyntaxTree.ParseText(s, path: filePaths != null && i < filePaths.Length
                ? filePaths[i] : "")).ToArray();
        var refTrees = referenceSources?.Select(s =>
            CSharpSyntaxTree.ParseText(s)).ToArray() ?? [];
        var allTrees = trees.Concat(refTrees).ToArray();
        var hasTopLevelStatements = allTrees.Any(HasTopLevelStatements);
        // concurrentBuild: false — Roslyn の並列解析はシングルスレッド WASM で
        // Monitor.Wait 不可により実行時クラッシュする。tcs の入力サイズでは
        // 性能影響なし。
        var compilation = CSharpCompilation.Create("TinyCs",
            allTrees,
            References,
            new CSharpCompilationOptions(hasTopLevelStatements
                    ? OutputKind.ConsoleApplication
                    : OutputKind.DynamicallyLinkedLibrary,
                concurrentBuild: false));

        var errors = new List<string>();
        var warnings = new List<string>();

        // TinyC# intentionally erases enums to integers and allows public
        // fields to stand in for interface properties. Suppress only those
        // exact semantic cases; ordinary C# type errors remain fatal.
        foreach (var diag in compilation.GetDiagnostics())
        {
            if (diag.Severity == DiagnosticSeverity.Error
                && !CompilationDiagnosticPolicy.IsAllowed(compilation, diag))
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
        {
            var model = compilation.GetSemanticModel(tree);
            if (checkNaming)
                warnings.AddRange(NamingAnalyzer.Analyze(tree));
            warnings.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedSyntaxes(
                tree, model));
            warnings.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedCollectionNulls(
                tree, model));
            warnings.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedApis(
                tree, model));
        }

        var emitter = new LuaEmitter();
        foreach (var refTree in refTrees)
            emitter.ReferenceTrees.Add(refTree);
        if (hasTopLevelStatements)
        {
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                emitter.Visit(compilation, model, tree,
                    emitGlobalStatements: false);
            }

            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                emitter.Visit(compilation, model, tree,
                    emitNonGlobalMembers: false);
            }
        }
        else
        {
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                emitter.Visit(compilation, model, tree);
            }
        }

        warnings.AddRange(emitter.Warnings);

        var lua = emitter.ToString();
        if (entryClass != null)
        {
            var entrySymbol = compilation.GetTypeByMetadataName(entryClass);
            if (entrySymbol == null)
                return new TranspileResult
                {
                    Errors = [$"entry class not found: {entryClass}"],
                    Warnings = warnings
                };
            if (entrySymbol.DeclaringSyntaxReferences.Any(
                r => emitter.ReferenceTrees.Contains(r.SyntaxTree)))
                return new TranspileResult
                {
                    Errors = [$"entry class is reference-only (--ref): {entryClass}"],
                    Warnings = warnings
                };
            lua += $"return {entryClass}\n";
        }

        return new TranspileResult
        {
            Lua = lua,
            SourceMap = emitter.SourceMap,
            Warnings = warnings
        };
    }

    private static bool HasTopLevelStatements(SyntaxTree tree) =>
        tree.GetCompilationUnitRoot().Members
            .OfType<GlobalStatementSyntax>()
            .Any();
}
