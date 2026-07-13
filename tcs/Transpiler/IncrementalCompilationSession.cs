using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TinyCs;

// doc/incremental-module-compilation-design.md §7-§9 の M1 core。
// 常駐 Roslyn session を保持し、method body だけの編集では変更 tree のみを
// 診断・emit する。次のいずれかに該当する編集は全 editable module の
// slow path に落とす (正しさ優先、§8.1):
//   - 変更 span が body に完全包含されない (surface hash 不変でも)
//   - body を除いた surface hash が変化した
//   - 直前の revision にエラーが残っている (復帰は常に authoritative full)
//   - top-level statement を含む入力
// slow path が全 module を再 emit するのは、surface 変更が他 module の
// binding (extension/overload 解決など) を変え、ソース不変の module の
// emit 結果を変え得るため。source hash による絞り込みでは不足する。
//
// 診断・compliance は full path (Transpiler.TranspileWithDiagnostics) と同じ
// 実装 (CompilationDiagnosticPolicy / NamingAnalyzer / TinyCsComplianceFacts /
// LuaEmitter) を呼ぶ。別実装を持たない (§9)。

public sealed class SessionModuleArtifact
{
    public required string ModuleId { get; init; }
    public required string SourceHash { get; init; }
    public required string SurfaceHash { get; init; }
    public required string Lua { get; init; }
    public required List<string> Warnings { get; init; }

    // method 単位 splice 用の内部状態 (Lua は RawLua の TrimEnd + "\n")
    internal string RawLua { get; init; } = "";
    internal List<(string Key, int Start, int Length)> MethodRanges { get; init; } = [];
}

public sealed class SessionUpdateResult
{
    public required bool Success { get; init; }
    public required bool FastPath { get; init; }
    public List<string> Errors { get; init; } = [];
    // 今回の revision で再 emit した module (error 時は空 = last-good 維持)
    public List<SessionModuleArtifact> ChangedArtifacts { get; init; } = [];
    public long ParseUpdateMs { get; init; }
    public long DiagnosticsMs { get; init; }
    public long ComplianceMs { get; init; }
    public long EmitMs { get; init; }
    public int ParsedTreeCount { get; init; }
    public int EmittedModuleCount { get; init; }
}

public sealed class IncrementalCompilationSession
{
    private readonly bool _checkNaming;
    private readonly SyntaxTree[] _refTrees;
    private readonly List<string> _refFixedDiagnosticErrors = [];

    // editable module 状態 (module ID = 与えられた path、open 順を保持)
    private readonly List<string> _moduleOrder = [];
    private readonly Dictionary<string, SourceText> _texts = [];
    private readonly Dictionary<string, SyntaxTree> _trees = [];
    private readonly Dictionary<string, SessionModuleArtifact> _lastGood = [];
    private readonly Dictionary<string, List<string>> _diagBuckets = [];

    private CSharpCompilation? _compilation;
    private bool _hasTopLevelStatements;
    private bool _hasErrors;

    public IncrementalCompilationSession(string[]? referenceSources = null,
        bool checkNaming = true)
    {
        _checkNaming = checkNaming;
        _refTrees = referenceSources?.Select(s => CSharpSyntaxTree.ParseText(s))
            .ToArray() ?? [];
    }

    public void OpenProject((string Path, string Text)[] files)
    {
        _moduleOrder.Clear();
        _texts.Clear();
        _trees.Clear();
        _lastGood.Clear();
        _diagBuckets.Clear();
        foreach (var (path, text) in files)
        {
            _moduleOrder.Add(path);
            var st = SourceText.From(text);
            _texts[path] = st;
            _trees[path] = CSharpSyntaxTree.ParseText(st, path: path);
        }
        RebuildCompilation();
        var errors = RecomputeDiagnostics();
        _hasErrors = errors.Count > 0;
        if (!_hasErrors)
            EmitAll();
    }

    // ------------------------------------------------------------------
    public SessionUpdateResult Update(string path, string newContent)
    {
        if (!_texts.ContainsKey(path))
            throw new ArgumentException($"unknown module: {path}");

        var swParse = Stopwatch.StartNew();
        var oldText = _texts[path];
        var oldTree = _trees[path];
        var change = MinimalChange(oldText.ToString(), newContent);
        var newText = oldText.WithChanges(change);
        var newTree = oldTree.WithChangedText(newText);
        _texts[path] = newText;
        _trees[path] = newTree;
        _compilation = _compilation!.ReplaceSyntaxTree(oldTree, newTree);
        swParse.Stop();

        var fastEligible = !_hasTopLevelStatements
            && !_hasErrors
            && ChangeInsideBody(oldTree, change.Span)
            && SurfaceHash(newTree) == _lastGood.GetValueOrDefault(path)?.SurfaceHash;

        if (!fastEligible)
            return SlowUpdate(swParse.ElapsedMilliseconds);
        // 変更を含む body の span (新 tree 座標)。body-only edit の新エラーは
        // 編集した body 内にしか現れないため、semantic 診断をここに限定できる。
        var newPos = Math.Min(change.Span.Start, newText.Length);
        var bodySpan = BodySpans(newTree)
            .FirstOrDefault(bs => bs.Contains(newPos)
                || bs.Contains(Math.Max(0, newPos - 1)));
        return FastUpdate(path, newTree, bodySpan, swParse.ElapsedMilliseconds);
    }

    // authoritative full build。診断・出力とも legacy full path と同一実装を使う。
    public TranspileResult BuildFull(string? entryClass = null)
    {
        var sources = _moduleOrder.Select(p => _texts[p].ToString()).ToArray();
        var paths = _moduleOrder.ToArray();
        var refs = _refTrees.Select(t => t.GetText().ToString()).ToArray();
        return Transpiler.TranspileWithDiagnostics(sources, paths,
            refs.Length > 0 ? refs : null, entryClass, _checkNaming);
    }

    public IReadOnlyList<SessionModuleArtifact> Artifacts =>
        _moduleOrder.Where(_lastGood.ContainsKey).Select(p => _lastGood[p]).ToList();

    // 全 bucket を file 順で並べた現行診断 (differential test 用)
    public (List<string> Errors, List<string> Warnings) CollectDiagnostics()
    {
        var errors = new List<string>(_refFixedDiagnosticErrors);
        var warnings = new List<string>();
        foreach (var p in _moduleOrder)
        {
            foreach (var d in _diagBuckets.GetValueOrDefault(p, []))
                (d.Contains("): error ") ? errors : warnings).Add(d);
        }
        return (errors, warnings);
    }

    // ------------------------------------------------------------------
    private SessionUpdateResult FastUpdate(string path, SyntaxTree tree,
        TextSpan bodySpan, long parseMs)
    {
        var comp = _compilation!;
        var swDiag = Stopwatch.StartNew();
        var bucket = new List<string>();
        var errors = new List<string>();
        var swCompliance = CollectTreeDiagnostics(comp, tree, bodySpan, bucket, errors);
        swDiag.Stop();

        _diagBuckets[path] = bucket;
        _hasErrors = errors.Count > 0;
        if (_hasErrors)
            return new SessionUpdateResult
            {
                Success = false,
                FastPath = true,
                Errors = errors,
                ParseUpdateMs = parseMs,
                DiagnosticsMs = swDiag.ElapsedMilliseconds - swCompliance,
                ComplianceMs = swCompliance,
                ParsedTreeCount = 1,
            };

        var swEmit = Stopwatch.StartNew();
        // 変更 method だけを emit して cache 済み出力へ splice する。
        // 対象外 (constructor/accessor/record/nested/警告持ち) は file 全体 emit。
        var artifact = TrySpliceMethodEmit(comp, path, tree, bodySpan)
            ?? EmitModule(comp, path, tree);
        _lastGood[path] = artifact;
        swEmit.Stop();
        return new SessionUpdateResult
        {
            Success = true,
            FastPath = true,
            ChangedArtifacts = [artifact],
            ParseUpdateMs = parseMs,
            DiagnosticsMs = swDiag.ElapsedMilliseconds - swCompliance,
            ComplianceMs = swCompliance,
            EmitMs = swEmit.ElapsedMilliseconds,
            ParsedTreeCount = 1,
            EmittedModuleCount = 1,
        };
    }

    private SessionUpdateResult SlowUpdate(long parseMs)
    {
        var swDiag = Stopwatch.StartNew();
        var errors = RecomputeDiagnostics();
        _hasErrors = errors.Count > 0;
        swDiag.Stop();
        if (_hasErrors)
            return new SessionUpdateResult
            {
                Success = false,
                FastPath = false,
                Errors = errors,
                ParseUpdateMs = parseMs,
                DiagnosticsMs = swDiag.ElapsedMilliseconds,
                ParsedTreeCount = _moduleOrder.Count,
            };

        var swEmit = Stopwatch.StartNew();
        var changed = EmitAll();
        swEmit.Stop();
        return new SessionUpdateResult
        {
            Success = true,
            FastPath = false,
            ChangedArtifacts = changed,
            ParseUpdateMs = parseMs,
            DiagnosticsMs = swDiag.ElapsedMilliseconds,
            EmitMs = swEmit.ElapsedMilliseconds,
            ParsedTreeCount = _moduleOrder.Count,
            EmittedModuleCount = changed.Count,
        };
    }

    // 全 editable module を再 emit する (slow path / open、§8.1)
    private List<SessionModuleArtifact> EmitAll()
    {
        var changed = new List<SessionModuleArtifact>();
        foreach (var p in _moduleOrder)
        {
            var artifact = EmitModule(_compilation!, p, _trees[p]);
            _lastGood[p] = artifact;
            changed.Add(artifact);
        }
        return changed;
    }

    // slow path / open 時の authoritative 診断。full GetDiagnostics を実行し
    // tree ごとの bucket を作り直す (§9)。戻り値は fatal error 一覧。
    private List<string> RecomputeDiagnostics()
    {
        var comp = _compilation!;
        _refFixedDiagnosticErrors.Clear();
        foreach (var p in _moduleOrder)
            _diagBuckets[p] = [];

        var errors = new List<string>();
        foreach (var diag in comp.GetDiagnostics())
        {
            if (diag.Severity != DiagnosticSeverity.Error
                || CompilationDiagnosticPolicy.IsAllowed(comp, diag))
                continue;
            var formatted = FormatError(diag);
            errors.Add(formatted);
            var treePath = diag.Location.SourceTree?.FilePath;
            if (treePath != null && _diagBuckets.ContainsKey(treePath))
                _diagBuckets[treePath].Add(formatted);
            else
                _refFixedDiagnosticErrors.Add(formatted);
        }
        if (errors.Count > 0)
            return errors;

        foreach (var p in _moduleOrder)
        {
            var bucket = _diagBuckets[p];
            var tree = _trees[p];
            var model = comp.GetSemanticModel(tree);
            if (_checkNaming)
                bucket.AddRange(NamingAnalyzer.Analyze(tree));
            bucket.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedSyntaxes(tree, model));
            bucket.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedCollectionNulls(tree, model));
            bucket.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedApis(tree, model));
        }
        return errors;
    }

    // fast path: 変更 tree の syntax 全域 + 変更 body 限定の semantic +
    // naming + compliance (§9)。戻り値は compliance walk の所要 ms。
    private long CollectTreeDiagnostics(CSharpCompilation comp, SyntaxTree tree,
        TextSpan bodySpan, List<string> bucket, List<string> errors)
    {
        var model = comp.GetSemanticModel(tree);
        var semantic = bodySpan.Length > 0
            ? model.GetDiagnostics(bodySpan) : model.GetDiagnostics();
        foreach (var diag in tree.GetDiagnostics().Concat(semantic))
        {
            if (diag.Severity != DiagnosticSeverity.Error
                || CompilationDiagnosticPolicy.IsAllowed(comp, diag))
                continue;
            var formatted = FormatError(diag);
            errors.Add(formatted);
            bucket.Add(formatted);
        }
        if (errors.Count > 0)
            return 0;
        var sw = Stopwatch.StartNew();
        if (_checkNaming)
            bucket.AddRange(NamingAnalyzer.Analyze(tree));
        bucket.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedSyntaxes(tree, model));
        bucket.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedCollectionNulls(tree, model));
        bucket.AddRange(TinyCsComplianceFacts.AnalyzeUnsupportedApis(tree, model));
        return sw.ElapsedMilliseconds;
    }

    private SessionModuleArtifact EmitModule(CSharpCompilation comp, string path,
        SyntaxTree tree)
    {
        var emitter = new LuaEmitter();
        foreach (var rt in _refTrees)
            emitter.ReferenceTrees.Add(rt);
        var model = comp.GetSemanticModel(tree);
        emitter.Visit(comp, model, tree);
        return new SessionModuleArtifact
        {
            ModuleId = path,
            SourceHash = Sha256(_texts[path].ToString()),
            SurfaceHash = SurfaceHash(tree),
            Lua = emitter.ToString(),
            Warnings = [.. emitter.Warnings],
            RawLua = emitter.RawOutput,
            MethodRanges = emitter.MethodRanges,
        };
    }

    // fast path 専用: 変更 body を含む method だけを emit し、cache 済み
    // RawLua の該当範囲を差し替える。適用できない形は null (full emit へ)。
    private SessionModuleArtifact? TrySpliceMethodEmit(CSharpCompilation comp,
        string path, SyntaxTree tree, TextSpan bodySpan)
    {
        if (bodySpan.Length == 0)
            return null;
        if (!_lastGood.TryGetValue(path, out var prev) || prev.Warnings.Count > 0)
            return null;

        var node = tree.GetCompilationUnitRoot().FindNode(
            new TextSpan(bodySpan.Start, 0));
        var method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method == null)
            return null;
        var cls = method.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (cls == null || cls.FirstAncestorOrSelf<ClassDeclarationSyntax>(c => c != cls) != null)
            return null;

        var key = LuaEmitter.MethodKey(cls.Identifier.ValueText, method);
        var idx = prev.MethodRanges.FindIndex(r => r.Key == key);
        if (idx < 0)
            return null;
        var (_, start, length) = prev.MethodRanges[idx];

        var emitter = new LuaEmitter();
        foreach (var rt in _refTrees)
            emitter.ReferenceTrees.Add(rt);
        var model = comp.GetSemanticModel(tree);
        var text = emitter.EmitSingleMethod(model, cls.Identifier.ValueText, method);
        if (emitter.Warnings.Count > 0)
            return null;

        var raw = prev.RawLua[..start] + text + prev.RawLua[(start + length)..];
        var delta = text.Length - length;
        var ranges = prev.MethodRanges
            .Select((r, i) => i == idx
                ? (r.Key, r.Start, text.Length)
                : r.Start > start ? (r.Key, r.Start + delta, r.Length) : r)
            .ToList();
        return new SessionModuleArtifact
        {
            ModuleId = path,
            SourceHash = Sha256(_texts[path].ToString()),
            SurfaceHash = prev.SurfaceHash,
            Lua = raw.TrimEnd() + "\n",
            Warnings = [],
            RawLua = raw,
            MethodRanges = ranges,
        };
    }

    private void RebuildCompilation()
    {
        var allTrees = _moduleOrder.Select(p => _trees[p]).Concat(_refTrees).ToArray();
        _hasTopLevelStatements = allTrees.Any(t =>
            t.GetCompilationUnitRoot().Members.OfType<GlobalStatementSyntax>().Any());
        _compilation = CSharpCompilation.Create("TinyCs",
            allTrees,
            Transpiler.References,
            new CSharpCompilationOptions(_hasTopLevelStatements
                    ? OutputKind.ConsoleApplication
                    : OutputKind.DynamicallyLinkedLibrary,
                concurrentBuild: false));
    }

    // ------------------------------------------------------------------
    private static string FormatError(Diagnostic diag)
    {
        var span = diag.Location.GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var col = span.StartLinePosition.Character + 1;
        var file = string.IsNullOrEmpty(span.Path) ? "<source>" : span.Path;
        return $"{file}({line},{col}): error {diag.Id}: {diag.GetMessage()}";
    }

    // 旧/new の full text から最小の単一 TextChange を得る (共通 prefix/suffix)。
    public static TextChange MinimalChange(string oldStr, string newStr)
    {
        int prefix = 0;
        var maxPrefix = Math.Min(oldStr.Length, newStr.Length);
        while (prefix < maxPrefix && oldStr[prefix] == newStr[prefix])
            prefix++;
        int suffix = 0;
        var maxSuffix = Math.Min(oldStr.Length, newStr.Length) - prefix;
        while (suffix < maxSuffix
               && oldStr[oldStr.Length - 1 - suffix] == newStr[newStr.Length - 1 - suffix])
            suffix++;
        var span = new TextSpan(prefix, oldStr.Length - prefix - suffix);
        return new TextChange(span, newStr.Substring(prefix, newStr.Length - prefix - suffix));
    }

    // 変更 span が method/constructor/accessor の body (block の brace 内側、
    // または arrow 式) に完全包含されるか。
    public static bool ChangeInsideBody(SyntaxTree oldTree, TextSpan span)
    {
        foreach (var bodySpan in BodySpans(oldTree))
            if (bodySpan.Contains(span))
                return true;
        return false;
    }

    private static IEnumerable<TextSpan> BodySpans(SyntaxTree tree)
    {
        foreach (var node in tree.GetCompilationUnitRoot().DescendantNodes())
        {
            var (block, arrow) = node switch
            {
                BaseMethodDeclarationSyntax m => (m.Body, m.ExpressionBody),
                AccessorDeclarationSyntax a => (a.Body, a.ExpressionBody),
                _ => ((BlockSyntax?)null, (ArrowExpressionClauseSyntax?)null),
            };
            // block body は brace の内側だけ (brace 自体の削除は surface 扱い)
            if (block != null && block.Span.Length >= 2)
                yield return TextSpan.FromBounds(block.SpanStart + 1, block.Span.End - 1);
            if (arrow != null)
                yield return arrow.Expression.Span;
        }
    }

    // body を除いた宣言 token 列の hash。trivia (コメント/空白) は含めない。
    // parameter 名・field/property 型・modifier・attribute・const/enum 値・
    // using など body 外の全 token が対象になる (§8 の conservative 近似)。
    public static string SurfaceHash(SyntaxTree tree)
    {
        var bodySpans = BodySpans(tree).ToArray();
        var sb = new StringBuilder();
        foreach (var token in tree.GetCompilationUnitRoot().DescendantTokens())
        {
            var inBody = false;
            foreach (var bs in bodySpans)
                if (bs.Contains(token.Span)) { inBody = true; break; }
            if (!inBody)
                sb.Append(token.Text).Append('\n');
        }
        return Sha256(sb.ToString());
    }

    private static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
