using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs;

// Session only for completion and hover. It does not compile; it only holds the current text of the files
// (the playground does the conversion with the tcs wasm, and the .NET wasm only answers Roslyn queries with this session)
public sealed class EditorSession
{
    private readonly List<string> _paths = [];
    private readonly Dictionary<string, string> _texts = [];
    private readonly string[] _refSources;

    public EditorSession((string Path, string Text)[] files, string[]? referenceSources = null)
    {
        _refSources = referenceSources ?? [];
        foreach (var (path, text) in files)
        {
            if (!_texts.ContainsKey(path)) _paths.Add(path);
            _texts[path] = text;
        }
    }

    public bool SetText(string path, string text)
    {
        if (!_texts.ContainsKey(path)) return false;
        _texts[path] = text;
        return true;
    }

    public (CSharpCompilation Compilation, SyntaxTree Tree)? ForkWithContent(string path, string content) =>
        Fork(_paths, p => _texts[p], _refSources, path, content);

    public static (CSharpCompilation Compilation, SyntaxTree Tree)? Fork(IEnumerable<string> paths,
        Func<string, string> textOf, string[] refSources, string path, string content)
    {
        SyntaxTree? edited = null;
        var trees = new List<SyntaxTree>();
        foreach (var p in paths)
        {
            var tree = CSharpSyntaxTree.ParseText(p == path ? content : textOf(p), path: p);
            if (p == path) edited = tree;
            trees.Add(tree);
        }
        if (edited == null)
            return null;
        trees.AddRange(TcsLibrary.LibraryTreesFor(trees));
        trees.AddRange(refSources.Select(s => CSharpSyntaxTree.ParseText(s)));
        var hasTopLevel = trees.Any(t => t.GetCompilationUnitRoot().Members
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax>().Any());
        var compilation = CSharpCompilation.Create("TinyCs", trees, TcsLibrary.References,
            new CSharpCompilationOptions(hasTopLevel ? OutputKind.ConsoleApplication
                : OutputKind.DynamicallyLinkedLibrary, concurrentBuild: false));
        return (compilation, edited);
    }
}
