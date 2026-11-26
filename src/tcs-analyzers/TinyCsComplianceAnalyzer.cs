using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace TinyCs.Analyzers;

/// <summary>
/// TinyC# compliance diagnostics. TCS1001 to 1003 come from running the tcs compiler (Compiler, the same implementation as `tcs check`)
/// on the compilation's sources. The rules live in one place in the compiler, and the analyzer only maps positions to Roslyn Locations.
/// TCS1004 (System.Math transcendental functions running on the .NET implementation) only matters when running on .NET, so the analyzer checks it with Roslyn.
/// `--ref` stubs (declarations of the host API) are passed as AdditionalFiles or Compile items with `TinyCsRef="true"`
/// (`<CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="TinyCsRef" />`; for Compile, Include="Compile" is also needed).
/// Roslyn uses Compile stubs for type checking, and the compiler reads only the declarations (it reports no diagnostics).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TinyCsComplianceAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor UnsupportedSyntaxRule = new(
        TinyCsDiagnosticIds.UnsupportedSyntax,
        "Unsupported TinyC# syntax",
        "TinyC# does not support syntax '{0}'",
        "TinyCSharp",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reports C# syntax that is outside the TinyC# baseline.");

    private static readonly DiagnosticDescriptor UnsupportedApiRule = new(
        TinyCsDiagnosticIds.UnsupportedApi,
        "Unsupported TinyC# API",
        "TinyC# does not support API '{0}'",
        "TinyCSharp",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reports BCL APIs that are outside the TinyC# runtime baseline.");

    private static readonly DiagnosticDescriptor UnsupportedCollectionNullRule = new(
        TinyCsDiagnosticIds.UnsupportedCollectionNull,
        "Unsupported TinyC# collection null",
        "TinyC# does not support null storage here: {0}",
        "TinyCSharp",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reports null values stored where the TinyC# subset does not allow them (arrays and Dictionary values).");

    private static readonly DiagnosticDescriptor DotnetMathRule = new(
        TinyCsDiagnosticIds.DotnetMath,
        "System.Math transcendental runs the .NET implementation",
        "'{0}' runs the .NET implementation and can differ from tcs c / tcs vm; call TinySystem.Math (tcs-system.props maps Math to it)",
        "TinyCSharp",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reports System.Math transcendental functions, whose results on .NET differ from the TinyC# runtime/math implementation.");

    // Functions whose results depend on the implementation. Abs / Min / Max / Floor / Sqrt / Round, etc.
    // have results fixed by IEEE-754, so they are out of scope
    private static readonly ImmutableHashSet<string> DotnetMathNames =
        ImmutableHashSet.Create("Sin", "Cos", "Tan", "Exp", "Log", "Atan2", "Pow");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UnsupportedSyntaxRule, UnsupportedApiRule,
            UnsupportedCollectionNullRule, DotnetMathRule);

    public override void Initialize(AnalysisContext context)
    {
        // The transpiler also converts generated code, so it is diagnosed
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze
            | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var refs = new List<string>();
            foreach (var file in start.Options.AdditionalFiles)
            {
                var options = start.Options.AnalyzerConfigOptionsProvider.GetOptions(file);
                if (!options.TryGetValue("build_metadata.AdditionalFiles.TinyCsRef", out var value)
                    || !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                    continue;
                var text = file.GetText(start.CancellationToken);
                if (text != null) refs.Add(text.ToString());
            }
            var refTrees = new HashSet<SyntaxTree>();
            foreach (var tree in start.Compilation.SyntaxTrees)
            {
                var options = start.Options.AnalyzerConfigOptionsProvider.GetOptions(tree);
                if (options.TryGetValue("build_metadata.Compile.TinyCsRef", out var value)
                    && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                    refTrees.Add(tree);
            }
            // Runs the compiler only once over all sources of the compilation, and reports each source's diagnostics in the analysis of that tree
            var check = new Lazy<Dictionary<SyntaxTree, List<Diagnostic>>>(
                () => Check(start.Compilation, refs, refTrees), LazyThreadSafetyMode.ExecutionAndPublication);
            start.RegisterSemanticModelAction(model =>
            {
                if (!check.Value.TryGetValue(model.SemanticModel.SyntaxTree, out var diagnostics)) return;
                foreach (var d in diagnostics) model.ReportDiagnostic(d);
            });
        });
        context.RegisterOperationAction(AnalyzeDotnetMath, OperationKind.Invocation);
    }

    private static void AnalyzeDotnetMath(OperationAnalysisContext context)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        var type = method.ContainingType?.ToDisplayString();
        // MathF gets TCS1002 (outside the subset)
        if (type != "System.Math" || !DotnetMathNames.Contains(method.Name))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(DotnetMathRule,
            context.Operation.Syntax.GetLocation(), $"{type}.{method.Name}"));
    }

    // TinySystem files (in the directory tcs-system) that tcs-system.props adds as source to .NET projects. Linq / ValueTuple are tcs libraries
    // (added below as libraries), and Collections is a .NET implementation (tcs does not read it), so none of them is passed as user source
    private static readonly string[] TinySystemFiles = ["Linq.cs", "ValueTuple.cs", "Collections.cs"];

    private static bool IsTinySystemFile(string path)
    {
        if (path == "") return false;
        var dir = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return dir == "tcs-system" && TinySystemFiles.Contains(Path.GetFileName(path));
    }

    // Files the SDK generates in obj/. GlobalUsings (ImplicitUsings) is needed for name resolution, so it is passed to the compiler,
    // but it is not an input of tcs (lub, etc.), so no diagnostics are reported. Assembly attribute files are not passed
    private static bool IsSdkGlobalUsings(string path) => path.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsSdkAssemblyInfo(string path) =>
        path.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase);

    private static readonly Lazy<(string Path, string Text)[]> Libraries = new(() =>
    {
        var assembly = typeof(TinyCsComplianceAnalyzer).Assembly;
        var result = new List<(string, string)>();
        foreach (var name in new[] { "Linq.cs", "ValueTuple.cs" })
        {
            using var stream = assembly.GetManifestResourceStream("TinySystem/" + name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            result.Add(("TinySystem/" + name, reader.ReadToEnd()));
        }
        return result.ToArray();
    });

    // Compiler state includes statics, so compiles in the same process run one at a time
    private static readonly object Gate = new();

    private static Dictionary<SyntaxTree, List<Diagnostic>> Check(Compilation compilation, List<string> refs,
        HashSet<SyntaxTree> refTrees)
    {
        var byTree = new Dictionary<SyntaxTree, List<Diagnostic>>();
        refs = new List<string>(refs);
        var trees = new List<SyntaxTree>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (refTrees.Contains(tree)) refs.Add(tree.GetText().ToString());
            else if (!IsTinySystemFile(tree.FilePath) && !IsSdkAssemblyInfo(tree.FilePath)) trees.Add(tree);
        }
        if (trees.Count == 0) return byTree;
        List<string> diagnostics;
        lock (Gate)
        {
            var session = new Tcs.Session("") { CheckOnly = true };
            // Paths in compiler diagnostics are tree indexes (they do not clash with the parentheses of the path)
            for (int i = 0; i < trees.Count; i++)
                session.Add("tree" + i + ".cs", trees[i].GetText().ToString(), 0);
            foreach (var r in refs) session.Add("", r, 1);
            foreach (var (path, text) in Libraries.Value) session.Add(path, text, 2);
            diagnostics = session.Compile().Diagnostics;
        }
        foreach (var line in diagnostics)
        {
            if (!TryParse(line, out var index, out var row, out var column, out var code, out var message)) continue;
            if (index < 0 || index >= trees.Count) continue;
            var rule = code switch
            {
                TinyCsDiagnosticIds.UnsupportedSyntax => UnsupportedSyntaxRule,
                TinyCsDiagnosticIds.UnsupportedApi => UnsupportedApiRule,
                TinyCsDiagnosticIds.UnsupportedCollectionNull => UnsupportedCollectionNullRule,
                _ => null,
            };
            if (rule == null) continue;
            var tree = trees[index];
            if (IsSdkGlobalUsings(tree.FilePath)) continue;
            var location = LocationOf(tree, row, column);
            if (!byTree.TryGetValue(tree, out var list)) byTree[tree] = list = [];
            list.Add(Diagnostic.Create(rule, location, Detail(code, message)));
        }
        return byTree;
    }

    // The part of the compiler message (`unsupported syntax: X`) that goes into the hole of the analyzer message
    private static string Detail(string code, string message)
    {
        var prefix = code switch
        {
            TinyCsDiagnosticIds.UnsupportedSyntax => "unsupported syntax: ",
            TinyCsDiagnosticIds.UnsupportedApi => "unsupported API: ",
            _ => "unsupported collection null: ",
        };
        return message.StartsWith(prefix, StringComparison.Ordinal) ? message.Substring(prefix.Length) : message;
    }

    // `tree3.cs(12,5): error TCS1001: message` (columns are counted in UTF-8 bytes)
    private static bool TryParse(string line, out int index, out int row, out int column, out string code, out string message)
    {
        index = row = column = 0;
        code = message = "";
        if (!line.StartsWith("tree", StringComparison.Ordinal)) return false;
        int open = line.IndexOf(".cs(", StringComparison.Ordinal);
        int close = open < 0 ? -1 : line.IndexOf("): ", open, StringComparison.Ordinal);
        if (open < 0 || close < 0) return false;
        if (!int.TryParse(line.Substring(4, open - 4), out index)) return false;
        var pos = line.Substring(open + 4, close - open - 4).Split(',');
        if (pos.Length != 2 || !int.TryParse(pos[0], out row) || !int.TryParse(pos[1], out column)) return false;
        var rest = line.Substring(close + 3);
        int space = rest.IndexOf(' ');
        if (space < 0) return false;
        rest = rest.Substring(space + 1);
        int colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return false;
        code = rest.Substring(0, colon);
        message = rest.Substring(colon + 2);
        return true;
    }

    // Token of line (1-based) and UTF-8 byte column (1-based)
    private static Location LocationOf(SyntaxTree tree, int row, int column)
    {
        var text = tree.GetText();
        if (row < 1 || row > text.Lines.Count) return Location.Create(tree, new TextSpan(0, 0));
        var lineSpan = text.Lines[row - 1];
        var lineText = text.ToString(lineSpan.Span);
        int bytes = 0;
        int chars = 0;
        while (chars < lineText.Length && bytes < column - 1)
        {
            int cp = char.ConvertToUtf32(lineText, chars);
            bytes += cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4;
            chars += cp >= 0x10000 ? 2 : 1;
        }
        int at = lineSpan.Start + chars;
        var token = tree.GetRoot().FindToken(at);
        var span = token.Span.Contains(at) ? token.Span : new TextSpan(at, 0);
        return Location.Create(tree, span);
    }
}
