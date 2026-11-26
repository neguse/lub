using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs;

/// <summary>What the .NET oracle passes to Roslyn to read test input as C#: the reference assemblies, and the part of TinySystem shipped as source.</summary>
public static class TcsLibrary
{
    private static MetadataReference[]? _references;

    // The reference assemblies the build copied next to the tests (refs/), and tcs-system.dll
    public static MetadataReference[] References => _references ??=
    [
        Reference("System.Runtime.dll"),
        Reference("System.Collections.dll"),
        Reference("System.Linq.dll"),
        Reference("System.Console.dll"),
        MetadataReference.CreateFromFile(typeof(TinySystem.Math).Assembly.Location),
    ];

    private static MetadataReference Reference(string name) =>
        MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory, "refs", name));

    /// <summary>The System.ValueTuple declaration that only the tcs path reads. .NET uses the BCL ValueTuple.</summary>
    public const string ValueTupleLibraryPath = "TinySystem/ValueTuple.cs";

    private static readonly string[] LibraryPaths = ["TinySystem/Linq.cs", ValueTupleLibraryPath];

    private static readonly Lazy<SyntaxTree[]> LibraryTreesCache = new(() =>
        [.. LibraryPaths.Select(path => CSharpSyntaxTree.ParseText(TcsHost.ToolFile("lib/" + path), path: path))]);

    public static SyntaxTree[] LibraryTrees => LibraryTreesCache.Value;
}
