using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

/// <summary>The library distributed with tcs (the part of TinySystem distributed as source) and the reference assemblies used when Roslyn reads it as C#.</summary>
public static class TcsLibrary
{
    private static MetadataReference[]? _references;

    // Injection point for reference assemblies. If unset, they are built lazily from the reference assemblies
    // embedded at build time (EmbedReferences.targets).
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

    // The part of TinySystem distributed as source (not included in TinySystem.dll). It is compiled together with
    // user code and converted
    private static readonly string[] LibraryPaths = ["TinySystem/Linq.cs", ValueTupleLibraryPath];

    /// <summary>The System.ValueTuple declaration that only the tcs path reads. .NET uses the BCL ValueTuple.</summary>
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

    /// <summary>Library compiled together with the user trees. When the user passes a library namespace
    /// (a local version of TinySystem.Linq), that one is used and the bundled version is not added.</summary>
    public static SyntaxTree[] LibraryTreesFor(IEnumerable<SyntaxTree> userTrees) =>
        userTrees.Any(DeclaresLibraryNamespace)
            ? [.. LibraryTrees.Where(t => t.FilePath == ValueTupleLibraryPath)]
            : LibraryTrees;

    // Follows only namespace declarations (including nested `namespace TinySystem { namespace Linq { } }`)
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

    /// <summary>(path, content) of the library distributed with tcs.</summary>
    public static (string Path, string Text)[] LibrarySourcesFor() => LibrarySources.Value;
}
