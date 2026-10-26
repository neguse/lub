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

    /// <summary>IL 経由で emit した member body 数 (計測用)。</summary>
    public int IlBodies { get; init; }
}

public static class Transpiler
{
    private static MetadataReference[]? _references;

    // 参照アセンブリの注入口。未設定なら、build 時に埋め込んだ参照アセンブリ
    // (EmbedReferences.targets) から遅延構築する。
    public static MetadataReference[] References
    {
        get => _references ??= GetDefaultReferences();
        set => _references = value;
    }

    private static MetadataReference[] GetDefaultReferences() =>
    [
        EmbeddedReference("refs/System.Runtime.dll"),
        EmbeddedReference("refs/System.Collections.dll"),
        EmbeddedReference("refs/System.Linq.dll"),
        EmbeddedReference("refs/System.Console.dll"),
        EmbeddedReference("refs/TinySystem.dll"),
    ];

    private static MetadataReference EmbeddedReference(string name) =>
        MetadataReference.CreateFromStream(
            typeof(Transpiler).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("missing resource: " + name));

    // TinySystem のうち source で配る部分 (TinySystem.dll には入らない)。user のコードと
    // 一緒にコンパイルし、Lua / C にも変換する
    private static readonly string[] LibraryPaths = ["TinySystem/Linq.cs"];

    private static readonly Lazy<SyntaxTree[]> LibraryTreesCache = new(() =>
        [.. LibraryPaths.Select(path =>
        {
            using var stream = typeof(Transpiler).Assembly.GetManifestResourceStream("lib/" + path)
                ?? throw new InvalidOperationException("missing resource: lib/" + path);
            using var reader = new StreamReader(stream);
            return CSharpSyntaxTree.ParseText(reader.ReadToEnd(), path: path);
        })]);

    public static SyntaxTree[] LibraryTrees => LibraryTreesCache.Value;

    /// <summary>user の tree と一緒にコンパイルする library。user が library の namespace
    /// (手元の版の TinySystem.Linq) を渡したときはそちらを使い、同梱の版は入れない。</summary>
    public static SyntaxTree[] LibraryTreesFor(IEnumerable<SyntaxTree> userTrees) =>
        userTrees.Any(DeclaresLibraryNamespace) ? [] : LibraryTrees;

    // namespace の宣言だけをたどる (入れ子の `namespace TinySystem { namespace Linq { } }` も)
    public static bool DeclaresLibraryNamespace(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Any(ns => string.Join(".", ns.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse().Select(n => n.Name.ToString())) == "TinySystem.Linq");

    /// <summary>user の tree が library の namespace を using するか、library の型を参照するか。
    /// そのときだけ library も emit する (しないなら出力に含めない)。</summary>
    public static bool UsesLibrary(Compilation compilation, IEnumerable<SyntaxTree> userTrees,
        SemanticModelCache? models = null)
    {
        models ??= new(compilation);
        var library = LibraryTrees;
        foreach (var tree in userTrees)
        {
            if (tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
                    .Any(u => u.Name?.ToString() == "TinySystem.Linq"))
                return true;
            var model = models[tree];
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name).Symbol;
                var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                if (type != null && type.DeclaringSyntaxReferences
                        .Any(r => library.Contains(r.SyntaxTree)))
                    return true;
            }
        }
        return false;
    }

    public static string Transpile(string csharpSource) => Transpile([csharpSource]);

    public static string Transpile(string[] csharpSources,
        bool instanceRegistry = false, bool cacheTypeLocals = true)
    {
        var result = TranspileWithDiagnostics(csharpSources,
            instanceRegistry: instanceRegistry, cacheTypeLocals: cacheTypeLocals);
        if (!result.Success)
            throw new InvalidOperationException(
                string.Join("\n", result.Errors));
        return result.Lua;
    }

    /// <param name="instanceRegistry">hot reload 用 weak instance registry
    /// (`__tcs_instances`) を emit する (LuaEmitter.InstanceRegistry)</param>
    /// <param name="tracePrefixes">`--trace`: filePaths がこの prefix に path の
    /// segment 単位で一致するファイルの文に実行の記録を出す (LuaEmitter.TracePrefixes)</param>
    /// <param name="validated">入力は同じ入力の前の呼び出しで compile error と準拠の検査を
    /// 通っている。その検査 (と警告) を繰り返さない (hot reload の chunk の emit)</param>
    public static TranspileResult TranspileWithDiagnostics(string[] csharpSources,
        string[]? filePaths = null, string[]? referenceSources = null,
        string? entryClass = null,
        MetadataReference[]? references = null, bool module = false,
        bool instanceRegistry = false, bool cacheTypeLocals = true,
        IReadOnlyList<string>? tracePrefixes = null, bool validated = false)
    {
        var trees = csharpSources.Select((s, i) =>
            CSharpSyntaxTree.ParseText(s, path: filePaths != null && i < filePaths.Length
                ? filePaths[i] : "")).ToArray();
        var refTrees = referenceSources?.Select(s =>
            CSharpSyntaxTree.ParseText(s)).ToArray() ?? [];
        var libraryTrees = LibraryTreesFor(trees);
        var allTrees = trees.Concat(libraryTrees).Concat(refTrees).ToArray();
        var hasTopLevelStatements = allTrees.Any(HasTopLevelStatements);
        // concurrentBuild: false — Roslyn の並列解析はシングルスレッド WASM で
        // Monitor.Wait 不可により実行時クラッシュする。tcs の入力サイズでは
        // 性能影響なし。
        var compilation = CSharpCompilation.Create("TinyCs",
            allTrees,
            references ?? References,
            new CSharpCompilationOptions(hasTopLevelStatements
                    ? OutputKind.ConsoleApplication
                    : OutputKind.DynamicallyLinkedLibrary,
                concurrentBuild: false));

        var errors = new List<string>();
        var warnings = new List<string>();

        // TinyC# intentionally erases enums to integers and allows public
        // fields to stand in for interface properties. Suppress only those
        // exact semantic cases; ordinary C# type errors remain fatal.
        foreach (var diag in validated ? [] : compilation.GetDiagnostics())
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

        // 検査・library の判定・static の順・emit は同じ model で bind を共有する。
        // GetDiagnostics の bind は model の cache と別なので共有できない
        var models = new SemanticModelCache(compilation);
        foreach (var tree in validated ? [] : trees)
        {
            var model = models[tree];
            errors.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedSyntaxes(
                tree, model, refTrees.Contains));
            warnings.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedCollectionNulls(
                tree, model));
            errors.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedApis(
                tree, model));
        }

        if (errors.Count > 0)
            return new TranspileResult { Errors = errors, Warnings = warnings };

        // library は user の型より先に定義する (user の static initializer が使える)
        SyntaxTree[] emitted = UsesLibrary(compilation, trees, models) ? [.. libraryTrees, .. trees] : trees;
        var emitter = new LuaEmitter
        {
            InstanceRegistry = instanceRegistry,
            TracePrefixes = tracePrefixes ?? [],
        };
        if (cacheTypeLocals)
            emitter.TypeLocalBudget = LuaEmitter.DefaultTypeLocalBudget(emitted);
        foreach (var refTree in refTrees)
            emitter.ReferenceTrees.Add(refTree);
        emitter.StaticInitDependencies = StaticInitOrder.DependenciesByName(
            compilation, emitted, emitter.TypeName, models);
        if (hasTopLevelStatements)
        {
            foreach (var tree in emitted)
            {
                var model = models[tree];
                emitter.Visit(compilation, model, tree,
                    emitGlobalStatements: false);
            }

            foreach (var tree in emitted)
            {
                var model = models[tree];
                emitter.Visit(compilation, model, tree,
                    emitNonGlobalMembers: false);
            }
        }
        else
        {
            foreach (var tree in emitted)
            {
                var model = models[tree];
                emitter.Visit(compilation, model, tree);
            }
        }

        // static initializer は ToString で出す (LuaEmitter.StaticInit.cs) ので、
        // その診断も含めてから集める
        var lua = emitter.ToString();
        if (emitter.Errors.Count > 0)
            return new TranspileResult { Errors = emitter.Errors, Warnings = warnings };
        if (entryClass != null)
        {
            // metadata 名 (Ns.Type) → 一意な simple 名の順で解決する
            var entrySymbol = compilation.GetTypeByMetadataName(entryClass);
            if (entrySymbol == null)
            {
                var candidates = FindSourceTypesBySimpleName(compilation,
                    entryClass);
                if (candidates.Count > 1)
                    return new TranspileResult
                    {
                        Errors = [$"entry class is ambiguous: {entryClass} " +
                            $"({string.Join(", ", candidates.Select(c => c.ToDisplayString()))})"],
                        Warnings = warnings
                    };
                entrySymbol = candidates.FirstOrDefault();
            }
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
            if (entrySymbol.TypeKind != TypeKind.Class)
                return new TranspileResult
                {
                    Errors = [$"entry class must be a class or record class: " +
                        $"{entrySymbol.ToDisplayString()} ({entrySymbol.TypeKind})"],
                    Warnings = warnings
                };
            lua += $"return {emitter.TypeName(entrySymbol)}\n";
        }
        if (module)
        {
            // --module: 定義した型 (--ref と library を除く) の table を返す。raw Lua が
            // `local lubx = require("lubx")` で lubx.SpriteBatch と引ける形。
            // key は C# の simple 名 (namespace の中の型も)。simple 名が出力内で
            // 重複するときだけ global 名を key にする。global の定義はそのまま
            // (entry 出力と同じ chunk が動く)。
            var libraryTypes = libraryTrees.SelectMany(tree => DeclaredTypeNames(compilation, tree, emitter))
                .ToHashSet(StringComparer.Ordinal);
            var types = emitter.EmittedTypes
                .Where(t => !libraryTypes.Contains(t.Name))
                .GroupBy(t => t.Name, StringComparer.Ordinal)
                .Select(g => (Name: g.Key, Simple: SimpleName(g.First().CSharpName) ?? g.Key))
                .ToList();
            var simpleCounts = types.GroupBy(t => t.Simple, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder("return {\n");
            foreach (var (name, key) in types
                .Select(t => (t.Name, Key: simpleCounts[t.Simple] == 1 ? t.Simple : t.Name))
                .OrderBy(t => t.Key, StringComparer.Ordinal))
                sb.Append(IsLuaName(key) ? $"  {key} = {name},\n" : $"  [\"{key}\"] = {name},\n");
            sb.Append("}\n");
            lua += sb.ToString();
        }

        return new TranspileResult
        {
            Lua = lua,
            SourceMap = emitter.SourceMap,
            Warnings = warnings,
            IlBodies = emitter.IlBodies
        };
    }

    // generic の型 (`Box<int>`) は従来どおり global 名を key にする
    private static IEnumerable<string> DeclaredTypeNames(Compilation compilation, SyntaxTree tree,
        LuaEmitter emitter)
    {
        var model = compilation.GetSemanticModel(tree);
        return tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Select(d => model.GetDeclaredSymbol(d))
            .OfType<INamedTypeSymbol>()
            .Select(emitter.TypeName);
    }

    private static string? SimpleName(string csharpName) =>
        csharpName.Contains('<') ? null : csharpName[(csharpName.LastIndexOf('.') + 1)..];

    private static bool IsLuaName(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_')
        && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static bool HasTopLevelStatements(SyntaxTree tree) =>
        tree.GetCompilationUnitRoot().Members
            .OfType<GlobalStatementSyntax>()
            .Any();

    private static List<INamedTypeSymbol> FindSourceTypesBySimpleName(
        CSharpCompilation compilation, string name)
    {
        var results = new List<INamedTypeSymbol>();
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(compilation.Assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            foreach (var member in stack.Pop().GetMembers())
            {
                if (member is INamespaceSymbol child)
                    stack.Push(child);
                else if (member is INamedTypeSymbol type && type.Name == name)
                    results.Add(type);
            }
        }
        return results;
    }
}
