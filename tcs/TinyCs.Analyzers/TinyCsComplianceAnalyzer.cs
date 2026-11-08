using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace TinyCs.Analyzers;

/// <summary>
/// TinyC# の準拠の診断。TCS1001〜1003 は tcs の compiler (SelfHost/Compiler、`tcs check` と同じ実装) を
/// compilation のソースで動かして出す。規則は compiler の 1 か所にあり、analyzer は位置を Roslyn の Location に写すだけ。
/// TCS1004 (System.Math の超越関数が .NET の実装で動く) は .NET で実行するときだけの話なので、analyzer が Roslyn で見る。
/// `--ref` の stub (host の API の宣言) は、AdditionalFiles か Compile の項目に `TinyCsRef="true"` を付けて渡す
/// (`<CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="TinyCsRef" />`、Compile なら Include="Compile" も要る)。
/// Compile の stub は Roslyn が型の検査に使い、compiler は宣言だけを読む (診断は出さない)。
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
        "'{0}' runs the .NET implementation and can differ from tcs c / tcs vm; call TinySystem.Math (TinySystem.props maps Math to it)",
        "TinyCSharp",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reports System.Math transcendental functions, whose results on .NET differ from the TinyC# runtime/math implementation.");

    // 結果が実装で変わる関数。Abs / Min / Max / Floor / Sqrt / Round などは
    // IEEE-754 で結果が決まるので対象外
    private static readonly ImmutableHashSet<string> DotnetMathNames =
        ImmutableHashSet.Create("Sin", "Cos", "Tan", "Exp", "Log", "Atan2", "Pow");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UnsupportedSyntaxRule, UnsupportedApiRule,
            UnsupportedCollectionNullRule, DotnetMathRule);

    public override void Initialize(AnalysisContext context)
    {
        // 生成されたコードも transpiler は変換するので診断する
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
            // compilation の全部のソースを 1 回だけ compiler にかけ、各ソースの診断はその tree の解析で報告する
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
        // MathF は TCS1002 (サブセット外) が出る
        if (type != "System.Math" || !DotnetMathNames.Contains(method.Name))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(DotnetMathRule,
            context.Operation.Syntax.GetLocation(), $"{type}.{method.Name}"));
    }

    // TinySystem.props が .NET の project に source で入れる TinySystem の file。Linq / ValueTuple は tcs の library
    // (下で library として足す)、Collections は .NET の実装 (tcs は読まない) なので、どれも user のソースとして渡さない
    private static readonly string[] TinySystemFiles = ["Linq.cs", "ValueTuple.cs", "Collections.cs"];

    private static bool IsTinySystemFile(string path)
    {
        if (path == "") return false;
        var dir = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return dir == "TinySystem" && TinySystemFiles.Contains(Path.GetFileName(path));
    }

    // SDK が obj/ に作る file。GlobalUsings (ImplicitUsings) は名前の解決に要るので compiler に渡すが、
    // tcs (lub など) の入力には入らない file なので診断は出さない。assembly の属性の file は渡さない
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

    // compiler の状態は static を含むので、同じ process の compile は 1 つずつ動かす
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
            var session = new TcsSelf.Session("") { CheckOnly = true };
            // compiler の診断の path は tree の番号 (path の括弧と重ならない)
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

    // compiler の文 (`unsupported syntax: X`) から、analyzer の文の穴に入れる部分
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

    // `tree3.cs(12,5): error TCS1001: message` (列は UTF-8 の byte で数える)
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

    // 行 (1 始まり) と UTF-8 の byte の列 (1 始まり) の token
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
