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
    private static readonly string[] LibraryPaths = ["TinySystem/Linq.cs", ValueTupleLibraryPath];

    /// <summary>tcs の経路だけが読む System.ValueTuple の宣言。.NET は BCL の ValueTuple を使う。</summary>
    public const string ValueTupleLibraryPath = "TinySystem/ValueTuple.cs";

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
        userTrees.Any(DeclaresLibraryNamespace)
            ? [.. LibraryTrees.Where(t => t.FilePath == ValueTupleLibraryPath)]
            : LibraryTrees;

    // namespace の宣言だけをたどる (入れ子の `namespace TinySystem { namespace Linq { } }` も)
    public static bool DeclaresLibraryNamespace(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Any(ns => string.Join(".", ns.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse().Select(n => n.Name.ToString())) == "TinySystem.Linq");

    /// <summary>C# を Lua に変換する。compiler はセルフホストの実装 (SelfHost/Compiler)。</summary>
    /// <param name="instanceRegistry">hot reload 用 weak instance registry (`__tcs_instances`) を出す</param>
    /// <param name="tracePrefixes">`--trace`: filePaths がこの prefix に path の
    /// segment 単位で一致するファイルの文に実行の記録を出す</param>
    public static TranspileResult TranspileWithDiagnostics(string[] csharpSources,
        string[]? filePaths = null, string[]? referenceSources = null,
        string? entryClass = null, bool module = false,
        bool instanceRegistry = false, bool cacheTypeLocals = true,
        IReadOnlyList<string>? tracePrefixes = null) =>
        TranspileSelfHosted(csharpSources, filePaths, referenceSources, entryClass, module,
            instanceRegistry, cacheTypeLocals, tracePrefixes);

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

    private static readonly Lazy<(string Path, string Text)[]> LibrarySources = new(() =>
        [.. LibraryPaths.Select(path =>
        {
            using var stream = typeof(Transpiler).Assembly.GetManifestResourceStream("lib/" + path)
                ?? throw new InvalidOperationException("missing resource: lib/" + path);
            using var reader = new StreamReader(stream);
            return (path, reader.ReadToEnd());
        })]);

    /// <summary>tcs と一緒に配る library の (path, 内容)。</summary>
    public static (string Path, string Text)[] LibrarySourcesFor() => LibrarySources.Value;

    private static TranspileResult TranspileSelfHosted(string[] csharpSources, string[]? filePaths,
        string[]? referenceSources, string? entryClass, bool module, bool instanceRegistry, bool cacheTypeLocals,
        IReadOnlyList<string>? tracePrefixes)
    {
        var session = new TcsSelf.Session(entryClass ?? "")
        {
            InstanceRegistry = instanceRegistry,
            CacheTypeLocals = cacheTypeLocals,
            Module = module,
            TracePrefixes = [.. tracePrefixes ?? []],
        };
        for (var i = 0; i < csharpSources.Length; i++)
            session.Add(filePaths != null && i < filePaths.Length ? filePaths[i] : "", csharpSources[i], 0);
        foreach (var source in referenceSources ?? [])
            session.Add("", source, 1);
        foreach (var (path, text) in LibrarySources.Value)
            session.Add(path, text, 2);
        var result = session.Compile();
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            if (d.Contains(": warning ") || d.StartsWith("warning ", StringComparison.Ordinal)) warnings.Add(d);
            else errors.Add(d);
        }
        if (!result.Ok || errors.Count > 0)
            return new TranspileResult { Errors = errors.Count > 0 ? errors : ["compile failed"], Warnings = warnings };
        var map = new SourceMap();
        foreach (var e in result.SourceMap) map.Add(e.LuaLine, e.File, e.Line);
        return new TranspileResult { Lua = result.Lua, SourceMap = map, Warnings = warnings };
    }
}
