using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

/// <summary>tcs と一緒に配る library (TinySystem のうち source で配る部分) と、Roslyn で C# として読むときの参照アセンブリ。</summary>
public static class TcsLibrary
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
            typeof(TcsLibrary).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("missing resource: " + name));

    // TinySystem のうち source で配る部分 (TinySystem.dll には入らない)。user のコードと
    // 一緒にコンパイルして変換する
    private static readonly string[] LibraryPaths = ["TinySystem/Linq.cs", ValueTupleLibraryPath];

    /// <summary>tcs の経路だけが読む System.ValueTuple の宣言。.NET は BCL の ValueTuple を使う。</summary>
    public const string ValueTupleLibraryPath = "TinySystem/ValueTuple.cs";

    private static readonly Lazy<SyntaxTree[]> LibraryTreesCache = new(() =>
        [.. LibraryPaths.Select(path =>
        {
            using var stream = typeof(TcsLibrary).Assembly.GetManifestResourceStream("lib/" + path)
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

    private static readonly Lazy<(string Path, string Text)[]> LibrarySources = new(() =>
        [.. LibraryPaths.Select(path =>
        {
            using var stream = typeof(TcsLibrary).Assembly.GetManifestResourceStream("lib/" + path)
                ?? throw new InvalidOperationException("missing resource: lib/" + path);
            using var reader = new StreamReader(stream);
            return (path, reader.ReadToEnd());
        })]);

    /// <summary>tcs と一緒に配る library の (path, 内容)。</summary>
    public static (string Path, string Text)[] LibrarySourcesFor() => LibrarySources.Value;
}
