using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs;

// 増分 session の本体は SelfHost/Tool/Modules.cs の ModuleSession (TinyC#)。
// ここは .NET の呼び出し側 (WasmCompiler とテスト) のための入口で、時間の計測と、
// エディタの補完と hover のための Roslyn の compilation (ForkWithContent) を足す

public sealed class SessionModuleArtifact
{
    internal SessionModuleArtifact(TcsSelf.ModuleArtifact port) => Port = port;

    public TcsSelf.ModuleArtifact Port { get; }
    public string ModuleId => Port.ModuleId;
    public string SourceHash => Port.SourceHash;
    public string SurfaceHash => Port.SurfaceHash;
    // RawLua の末尾の空白を除いて改行を 1 つ足したもの
    public string Lua => Port.Lua;
    // 出力の内容の hash (registry は hot apply で変わらない module を飛ばす)
    public string LuaHash => Port.LuaHash;
    // descriptor の分割に使う型ごとの記録。範囲は RawLua の UTF-8 の byte の位置
    public List<TcsSelf.ModuleType> Types => Port.Types;
    public string RawLua => Port.RawLua;
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
    private readonly TcsSelf.ModuleSession _session;

    // artifact を更新した revision (成功した Open/Update ごとに増える)。
    // bridge snapshot と registry の stale reject が参照する。
    public int Revision => _session.Revision;

    public IncrementalCompilationSession(string[]? referenceSources = null)
    {
        _refSources = referenceSources ?? [];
        _session = new TcsSelf.ModuleSession([.. _refSources]);
    }

    public void OpenProject((string Path, string Text)[] files)
    {
        _session.Open([.. files.Select(f => f.Path)], [.. files.Select(f => f.Text)]);
        if (_session.OpenError != "")
            throw new ArgumentException(_session.OpenError);
    }

    public SessionUpdateResult Update(string path, string newContent)
    {
        if (!_session.Has(path))
            throw new ArgumentException($"unknown module: {path}");
        var sw = Stopwatch.StartNew();
        var u = _session.Update(path, newContent);
        sw.Stop();
        return new SessionUpdateResult
        {
            Success = u.Success,
            FastPath = u.FastPath,
            RequiresRestart = u.RequiresRestart,
            RestartReasons = [.. u.RestartReasons],
            Errors = [.. u.Errors],
            ChangedArtifacts = [.. u.ChangedArtifacts.Select(a => new SessionModuleArtifact(a))],
            DiagnosticsMs = u.Success ? 0 : sw.ElapsedMilliseconds,
            EmitMs = u.Success ? sw.ElapsedMilliseconds : 0,
            ParsedTreeCount = u.ParsedTreeCount,
            EmittedModuleCount = u.EmittedModuleCount,
        };
    }

    // 補完/hover (SemanticQueries) 用の Roslyn の compilation。エディタの現在の
    // バッファ content で path の tree を差し替えるが、session の状態は変えない。
    // Roslyn はエディタの道具としてだけ使い、compile の結果には関わらない。
    public (CSharpCompilation Compilation, SyntaxTree Tree)? ForkWithContent(
        string path, string content)
    {
        if (!_session.Has(path))
            return null;
        SyntaxTree? edited = null;
        var trees = new List<SyntaxTree>();
        foreach (var p in _session.Paths())
        {
            var tree = CSharpSyntaxTree.ParseText(p == path ? content : _session.TextOf(p), path: p);
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
        var paths = _session.Paths();
        return Transpiler.TranspileWithDiagnostics([.. paths.Select(_session.TextOf)], [.. paths],
            _refSources.Length > 0 ? _refSources : null, entryClass);
    }

    public IReadOnlyList<SessionModuleArtifact> Artifacts =>
        [.. _session.Artifacts().Select(a => new SessionModuleArtifact(a))];

    // 現行の診断 (differential test 用)
    public (List<string> Errors, List<string> Warnings) CollectDiagnostics() =>
        ([.. _session.Errors()], [.. _session.Warnings()]);

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

    // 本文を除いた宣言の token の列の hash。空白と comment は含めない
    public static string SurfaceHash(string text) => TcsSelf.ModuleSession.SurfaceHashOf(text);
}
