using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs;

// doc/incremental-module-compilation-design.md §7-§9 の core。
// compile はセルフホストの compiler (TcsSelf.Session) が行い、parse は内容の変わったファイルだけをやり直す。
// 宣言、束縛、出力は毎回全体で行い、ファイルごとの module (artifact) を作る。
// 次の全てを満たす編集は fast path (編集したファイルの module だけを差し替える):
//   - 変更の範囲が method / constructor / accessor の本文に収まる
//   - 本文を除いた宣言の面 (surface hash) が変わらない
//   - 直前の revision に error が無い (復帰は常に全体)
//   - top-level の文を含まない
// それ以外 (slow path) は全 module を差し替え、hot apply できない変更 (ReloadRules) を判定する。
// 診断は full build (Transpiler.TranspileWithDiagnostics) と同じ compiler なので一致する。

public sealed class SessionModuleArtifact
{
    public required string ModuleId { get; init; }
    public required string SourceHash { get; init; }
    public required string SurfaceHash { get; init; }
    public required string Lua { get; init; }

    // module 出力内容の hash。registry が hot apply 時に unchanged module を
    // skip する判定に使う (source が同じでも他 module の surface 変更で
    // 出力が変わり得るため、source hash ではなく出力 hash)。
    public string LuaHash => _luaHash ??= ModuleArtifactText.Sha256(Lua);
    private string? _luaHash;

    // descriptor 分割用の type 単位記録 (ModuleArtifacts.cs)。範囲は RawLua 座標。
    public List<EmittedTypeInfo> Types { get; init; } = [];

    // 未 trim の出力 (Lua は RawLua の TrimEnd + "\n")。Types の範囲座標はこちら。
    public string RawLua { get; init; } = "";
}

public sealed class SessionUpdateResult
{
    public required bool Success { get; init; }
    public required bool FastPath { get; init; }
    // live apply できない変更 (§11.3)。host は snapshot を hotswap ではなく
    // fresh VM restart で適用する。fast path (body edit) では常に false。
    public bool RequiresRestart { get; init; }
    public List<string> RestartReasons { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    // 今回の revision で差し替えた module (error 時は空 = last-good 維持)
    public List<SessionModuleArtifact> ChangedArtifacts { get; init; } = [];
    public long ParseUpdateMs { get; init; }
    public long DiagnosticsMs { get; init; }
    public long ComplianceMs { get; init; }
    public long EmitMs { get; init; }
    public int ParsedTreeCount { get; init; }
    public int EmittedModuleCount { get; init; }
}

// 旧い文字列から新しい文字列への最小の置き換え (共通の prefix と suffix を除いた部分)
public readonly record struct TextEdit(int Start, int Length, string NewText);

public sealed class IncrementalCompilationSession
{
    private readonly string[] _refSources;
    private readonly TcsSelf.Session _compiler = new("") { ModuleArtifacts = true };

    // editable module (module ID = 与えられた path、open 順を保持)
    private readonly List<string> _moduleOrder = [];
    private readonly Dictionary<string, string> _texts = [];
    private readonly Dictionary<string, SessionModuleArtifact> _lastGood = [];
    // module の並び (library を含む。最後に成功した compile の出力の順)
    private List<string> _artifactOrder = [];
    private List<string> _errors = [];
    private List<string> _warnings = [];
    private bool _hasErrors;
    private bool _hasTopLevelStatements;

    // artifact を更新した revision (成功した Open/Update ごとに増える)。
    // bridge snapshot と registry の stale reject が参照する。
    public int Revision { get; private set; }

    public IncrementalCompilationSession(string[]? referenceSources = null)
    {
        _refSources = referenceSources ?? [];
        for (var i = 0; i < _refSources.Length; i++)
            _compiler.SetReference("<ref" + i + ">", _refSources[i]);
        foreach (var (path, text) in Transpiler.LibrarySourcesFor())
            _compiler.SetLibrary(path, text);
    }

    public void OpenProject((string Path, string Text)[] files)
    {
        foreach (var path in _moduleOrder)
            _compiler.Remove(path);
        _moduleOrder.Clear();
        _texts.Clear();
        _lastGood.Clear();
        _artifactOrder = [];
        foreach (var (path, text) in files)
        {
            if (IsReservedLibraryPath(path, text))
                throw new ArgumentException($"module path is reserved for the bundled library: {path}");
            _moduleOrder.Add(path);
            _texts[path] = text;
            _compiler.SetSource(path, text);
        }
        var result = Compile();
        if (_hasErrors) return;
        foreach (var a in BuildArtifacts(result)) _lastGood[a.ModuleId] = a;
        _artifactOrder = [.. result.Modules.Select(m => m.Path)];
        Revision++;
    }

    // ------------------------------------------------------------------
    public SessionUpdateResult Update(string path, string newContent)
    {
        if (!_texts.ContainsKey(path))
            throw new ArgumentException($"unknown module: {path}");

        var swParse = Stopwatch.StartNew();
        var oldText = _texts[path];
        var oldUnit = _compiler.ParsedUnit(path);
        var edit = MinimalByteChange(oldText, newContent);
        _texts[path] = newContent;
        _compiler.SetSource(path, newContent);
        var newUnit = _compiler.ParsedUnit(path);
        swParse.Stop();

        var fastEligible = !_hasTopLevelStatements
            && !_hasErrors
            && oldUnit != null && newUnit != null
            && TcsSelf.Surface.InsideBody(oldUnit, edit.Start, edit.Start + edit.Length)
            && _lastGood.TryGetValue(path, out var prev)
            && SurfaceHashOf(newUnit) == prev.SurfaceHash;

        var swCompile = Stopwatch.StartNew();
        var result = Compile();
        swCompile.Stop();
        if (_hasErrors)
            return new SessionUpdateResult
            {
                Success = false,
                FastPath = fastEligible,
                Errors = [.. _errors],
                ParseUpdateMs = swParse.ElapsedMilliseconds,
                DiagnosticsMs = swCompile.ElapsedMilliseconds,
                ParsedTreeCount = 1,
            };

        var next = BuildArtifacts(result);
        var prevArtifacts = new Dictionary<string, SessionModuleArtifact>(_lastGood);
        var libraryChanged = !result.Modules.Select(m => m.Path).SequenceEqual(_artifactOrder);
        _lastGood.Clear();
        foreach (var a in next) _lastGood[a.ModuleId] = a;
        _artifactOrder = [.. result.Modules.Select(m => m.Path)];
        Revision++;

        // 本文だけの編集は、宣言の面が同じなので他の module の出力を変えない。編集した module だけを差し替える
        if (fastEligible && !libraryChanged)
        {
            var changed = next.Where(a => a.ModuleId == path).ToList();
            return new SessionUpdateResult
            {
                Success = true,
                FastPath = true,
                ChangedArtifacts = changed,
                ParseUpdateMs = swParse.ElapsedMilliseconds,
                EmitMs = swCompile.ElapsedMilliseconds,
                ParsedTreeCount = 1,
                EmittedModuleCount = changed.Count,
            };
        }

        var reasons = new List<string>();
        foreach (var artifact in next)
            ClassifyRestart(prevArtifacts.GetValueOrDefault(artifact.ModuleId), artifact, reasons);
        return new SessionUpdateResult
        {
            Success = true,
            FastPath = false,
            RequiresRestart = reasons.Count > 0,
            RestartReasons = reasons,
            ChangedArtifacts = next,
            ParseUpdateMs = swParse.ElapsedMilliseconds,
            EmitMs = swCompile.ElapsedMilliseconds,
            ParsedTreeCount = 1,
            EmittedModuleCount = next.Count,
        };
    }

    // 補完/hover (SemanticQueries) 用の Roslyn の compilation。エディタの現在の
    // バッファ content で path の tree を差し替えるが、session の状態は変えない。
    // Roslyn はエディタの道具としてだけ使い、compile の結果には関わらない。
    public (CSharpCompilation Compilation, SyntaxTree Tree)? ForkWithContent(
        string path, string content)
    {
        if (!_texts.ContainsKey(path))
            return null;
        SyntaxTree? edited = null;
        var trees = new List<SyntaxTree>();
        foreach (var p in _moduleOrder)
        {
            var tree = CSharpSyntaxTree.ParseText(p == path ? content : _texts[p], path: p);
            if (p == path) edited = tree;
            trees.Add(tree);
        }
        trees.AddRange(Transpiler.LibraryTreesFor(trees));
        trees.AddRange(_refSources.Select(s => CSharpSyntaxTree.ParseText(s)));
        var hasTopLevel = trees.Any(t => t.GetCompilationUnitRoot().Members
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax>().Any());
        var compilation = CSharpCompilation.Create("TinyCs", trees, Transpiler.References,
            new CSharpCompilationOptions(hasTopLevel ? OutputKind.ConsoleApplication
                : OutputKind.DynamicallyLinkedLibrary, concurrentBuild: false));
        return (compilation, edited!);
    }

    // authoritative full build (CLI の変換と同じ入口)
    public TranspileResult BuildFull(string? entryClass = null)
    {
        var sources = _moduleOrder.Select(p => _texts[p]).ToArray();
        return Transpiler.TranspileWithDiagnostics(sources, [.. _moduleOrder],
            _refSources.Length > 0 ? _refSources : null, entryClass);
    }

    public IReadOnlyList<SessionModuleArtifact> Artifacts =>
        _artifactOrder.Where(_lastGood.ContainsKey).Select(p => _lastGood[p]).ToList();

    // 現行の診断 (differential test 用)
    public (List<string> Errors, List<string> Warnings) CollectDiagnostics() => ([.. _errors], [.. _warnings]);

    // ------------------------------------------------------------------

    private TcsSelf.CompileResult Compile()
    {
        var result = _compiler.Compile();
        _errors = [];
        _warnings = [];
        foreach (var d in result.Diagnostics)
        {
            if (d.Contains(": warning ") || d.StartsWith("warning ", StringComparison.Ordinal)) _warnings.Add(d);
            else _errors.Add(d);
        }
        _hasErrors = !result.Ok || _errors.Count > 0;
        if (_hasErrors && _errors.Count == 0) _errors.Add("compile failed");
        _hasTopLevelStatements = _moduleOrder.Any(p => _compiler.ParsedUnit(p) is { Count2: > 0 });
        return result;
    }

    private List<SessionModuleArtifact> BuildArtifacts(TcsSelf.CompileResult result)
    {
        var artifacts = new List<SessionModuleArtifact>();
        foreach (var m in result.Modules)
        {
            // chunk の番号 → RawLua の文字の位置
            var offsets = new int[m.Chunks.Count + 1];
            for (var i = 0; i < m.Chunks.Count; i++) offsets[i + 1] = offsets[i] + m.Chunks[i].Length;
            var raw = string.Concat(m.Chunks);
            var unit = _compiler.ParsedUnit(m.Path);
            artifacts.Add(new SessionModuleArtifact
            {
                ModuleId = m.Path,
                SourceHash = Sha256(_texts.TryGetValue(m.Path, out var text) ? text : m.Path),
                SurfaceHash = unit != null ? SurfaceHashOf(unit) : "",
                Lua = raw.TrimEnd() + "\n",
                RawLua = raw,
                Types = [.. m.Types.Select(t => ConvertType(t, offsets))],
            });
        }
        return artifacts;
    }

    private static EmittedTypeInfo ConvertType(TcsSelf.EmittedTypeInfo t, int[] offsets)
    {
        var info = new EmittedTypeInfo
        {
            Name = t.Name,
            Kind = t.Kind,
            CSharpName = t.CSharpName,
            BaseName = t.BaseName == "" ? null : t.BaseName,
            InstanceShape = t.InstanceShape,
            InitDependencies = [.. t.InitDependencies],
            StaticFields = [.. t.StaticFields.Select(s => new StaticFieldMeta(s.Key, s.Type, s.DefaultLua,
                s.InitHash, s.Pure, offsets[s.Start], offsets[s.End] - offsets[s.Start]))],
        };
        for (var i = 0; i < t.DeclStarts.Count; i++)
            info.DeclRanges.Add((offsets[t.DeclStarts[i]], offsets[t.DeclEnds[i]] - offsets[t.DeclStarts[i]]));
        info.DefinitionKeys.AddRange(t.DefinitionKeys);
        for (var i = 0; i < t.EnumMemberNames.Count; i++)
            info.EnumMembers.Add((t.EnumMemberNames[i], t.EnumMemberValues[i]));
        return info;
    }

    // hot apply できない変更を検出する (§11.3 state policy、規則は ReloadRules)。
    // module 追加は live-safe (新 type の declare/init のみ)
    private static void ClassifyRestart(SessionModuleArtifact? prev,
        SessionModuleArtifact next, List<string> reasons)
    {
        if (prev == null)
            return;
        reasons.AddRange(ReloadRules.RestartReasons(prev.Types.Select(ReloadTypeOf),
                next.Types.Select(ReloadTypeOf), migratesInstances: false)
            .Select(r => $"{next.ModuleId}: {r}"));
    }

    private static ReloadType ReloadTypeOf(EmittedTypeInfo t) =>
        new(t.Name, t.Kind, t.BaseName, t.InstanceShape,
            [.. t.StaticFields.Select(s => new ReloadStatic(s.Key, s.Type, s.InitHash, s.Pure))],
            t.EnumMembers);

    // 同梱の library の path は、その library の namespace を宣言する手元の版にだけ使える
    private static bool IsReservedLibraryPath(string path, string text)
    {
        if (!Transpiler.LibrarySourcesFor().Any(l => l.Path == path)) return false;
        if (path == Transpiler.ValueTupleLibraryPath) return true;
        return !Transpiler.DeclaresLibraryNamespace(CSharpSyntaxTree.ParseText(text));
    }

    private string SurfaceHashOf(TcsSelf.Node unit) => TcsSelf.Surface.Hash(unit.File, unit);

    // 旧/new の full text から最小の単一の置き換え (共通 prefix/suffix を除く)。位置は文字
    public static TextEdit MinimalChange(string oldStr, string newStr)
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
        return new TextEdit(prefix, oldStr.Length - prefix - suffix,
            newStr.Substring(prefix, newStr.Length - prefix - suffix));
    }

    // セルフホストの木の位置 (UTF-8 の byte) での最小の置き換え
    private static TextEdit MinimalByteChange(string oldStr, string newStr)
    {
        var edit = MinimalChange(oldStr, newStr);
        var start = Encoding.UTF8.GetByteCount(oldStr.AsSpan(0, edit.Start));
        var length = Encoding.UTF8.GetByteCount(oldStr.AsSpan(edit.Start, edit.Length));
        return new TextEdit(start, length, edit.NewText);
    }

    // 本文を除いた宣言の token の列の hash。空白と comment は含めない
    public static string SurfaceHash(string text)
    {
        var file = new TcsSelf.SourceFile(0, "", text, false);
        var unit = TcsSelf.Parser.ParseFile(file, new TcsSelf.Diagnostics());
        return TcsSelf.Surface.Hash(file, unit);
    }

    private static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
