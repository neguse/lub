using Microsoft.CodeAnalysis;

namespace TinyCs;

// --trace: 実行した C# 行の記録 (開発用)。origin の path が TracePrefixes の
// どれかに一致する (IsTracedPath) 文の直前に、現在行を関数 local の __tcs_at に書き、global の
// __tcs_trace[__tcs_at] = true を立てる。host は __tcs_trace の key
// (`<file>:<line>`) を読んで in place で消し、各 Lua frame の local __tcs_at を
// debug.getlocal / lua_getlocal で読んで C# の呼び出し元を組み立てる。
// 対象外: inline に描く文 (IIFE / 式本体の lambda)、top-level 文、ctor 先頭の
// table constructor に畳まれる field 代入。
// 文としての代入 (複合代入と ++ / -- を含む) と初期化子付きの local の宣言は、文の後に
// 代入先を読み直した値を global の __tcs_vals["<file>:<line>:<col>:<代入先>"] に置く
// (代入先は C# の書き方。読み直しに呼び出しが要る代入先は置かない)
public partial class LuaEmitter
{
    // '/' 区切りに正規化した path の prefix。空なら trace しない (出力は不変)
    public IReadOnlyList<string> TracePrefixes { get; set; } = [];

    // emit 中の関数が trace 対象ファイルのものか (lambda は外側の関数に従う)
    private bool _traceFunction;

    public static string NormalizeTracePath(string path) => path.Replace('\\', '/');

    // prefix は path そのもの (ファイル) か、segment の境界で終わる directory に
    // 一致する (`samples/game` は `samples/gameover/` に一致しない)。先頭の `./` と
    // 末尾の `/` は比べる前に外す。空になった prefix (`./`) はすべてに一致する
    private bool IsTracedPath(string path)
    {
        var normalized = TraceMatchForm(path);
        return TracePrefixes.Any(raw =>
        {
            var prefix = TraceMatchForm(raw);
            return prefix.Length == 0 || normalized == prefix
                || normalized.StartsWith(prefix + "/", StringComparison.Ordinal);
        });
    }

    private static string TraceMatchForm(string path)
    {
        var p = NormalizeTracePath(path);
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.TrimEnd('/');
    }

    // 関数本体の先頭 (parameter default より前、どの goto / label より前) で
    // 呼ぶ。__tcs_at はその関数の local なので、frame ごとに現在行を持つ
    private void BeginTraceFunction(SemanticModel model)
    {
        _traceFunction = TracePrefixes.Count > 0 && IsTracedPath(model.SyntaxTree.FilePath);
        if (_traceFunction) AppendLine(TraceLocal);
    }

    private void EndTraceFunction() => _traceFunction = false;

    private const string TraceLocal = "local __tcs_at";

    // 対象ファイルの型 table (class / record / struct。enum は状態を持たない) を
    // global の __tcs_trace_classes に登録する。host はここから static field を
    // 辿って game の状態を snapshot / restore する。型 table を作った直後に出す
    private void RegisterTraceClass(string name, string kind, INamedTypeSymbol? symbol)
    {
        if (TracePrefixes.Count == 0 || kind == "enum") return;
        var path = symbol?.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
        if (path == null || !IsTracedPath(path)) return;
        AppendLine($"__tcs_trace_classes[{EscapeLuaString(name)}] = {name}");
    }

    // builder が 1 つの文を同じ Origin の文の列に分けたとき (前宣言、条件の hoist
    // など) は、列の先頭で 1 回だけ trace 行を出す。前の文が入れ子の文を持つ
    // (その中で __tcs_at が他の行に進み得る) ときは出し直す
    private static bool ContinuesTraceRun(IlStat prev, IlStat stat) =>
        stat.Origin != null && ReferenceEquals(prev.Origin, stat.Origin)
        && prev is IlLocal or IlAssign or IlCallStat or IlMultiAssign;

    // elseif の条件 (Origin は条件式) を評価する前に trace 行を置けるよう、
    // arm 以降を else の中の if 文にする。trace しない関数では null (elseif のまま)
    private IlIf? TracedElseIf(IlIf ifStat, int arm) =>
        _traceFunction && ifStat.Arms[arm].Cond.Origin is { } origin
            ? new IlIf(ifStat.Arms[arm..], ifStat.Else) { Origin = origin }
            : null;

    // while の条件は反復ごとに評価されるので、lowering と同じ
    // `while true do if not c then break end ... end` に開いて毎回 trace 行を置く。
    // 定数の条件 (lowering 後の while true も) は評価しないので開かない
    private bool TracesLoopCond(IlWhile w) =>
        _traceFunction && w.Origin != null && w.Cond is not IlLit;

    // 代入した値を __tcs_vals に置く文を acc に足す。origin は代入した文で、同じ文の
    // 続きとして trace 行を出し直さない (ContinuesTraceRun)
    private void AddTraceValue(Location target, string label, IlExpr read,
        SyntaxNode origin, List<IlStat> acc)
    {
        label = string.Join(" ", label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // host は key を後ろの ':' から分けるので、代入先に ':' があれば置かない (global::)
        if (!_traceFunction || !IsRereadable(read) || label.Contains(':')) return;
        var span = target.GetLineSpan();
        var key = $"{NormalizeTracePath(span.Path)}:{span.StartLinePosition.Line + 1}:"
            + $"{span.StartLinePosition.Character + 1}:{label}";
        acc.Add(new IlAssign(new IlIndex(new IlVar("__tcs_vals"),
            new IlLit(EscapeLuaString(key)), PlusOne: false), read) { Origin = origin });
    }

    // 代入した直後に読み直せる (呼び出しが無い)。要素は書けたので読んでも範囲外にならない
    private static bool IsRereadable(IlExpr e) => e switch
    {
        IlIndex ix => IsRereadable(ix.Recv) && IsRereadable(ix.Idx),
        IlField f => IsRereadable(f.Recv),
        _ => IsCallFree(e),
    };

    private void EmitTracePoint(SyntaxNode origin)
    {
        if (!_traceFunction) return;
        var span = origin.GetLocation().GetLineSpan();
        var key = $"{NormalizeTracePath(span.Path)}:{span.StartLinePosition.Line + 1}";
        AppendLine($"__tcs_at = {EscapeLuaString(key)}; __tcs_trace[__tcs_at] = true");
        // 文そのものも source map に載せる (trace 行が _currentSource を消費した)
        SetSource(origin);
    }
}
