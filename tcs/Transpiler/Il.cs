using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace TinyCs;

// TinyC# IL (doc/il-spec.md)。M1 スライス: method body の中間表現。
// builder (LuaEmitter.IlBuild) が全 SemanticModel 問い合わせを済ませ、
// emitter (LuaEmitter.IlEmit) は Roslyn に依存せず IL のみから出力する。
// 演算ノードは型解決済み・単型 (il-spec §4)。
// Origin は source map 用の位置情報で、意味論には関与しない。

public abstract record IlNode
{
    public SyntaxNode? Origin { get; init; }
}

// ---- 式 ----

public abstract record IlExpr : IlNode;

/// <summary>変換済み Lua リテラルテキスト (数値・文字列 escape 解決済み)。</summary>
public sealed record IlLit(string LuaText) : IlExpr;

/// <summary>local / parameter / 型名参照。</summary>
public sealed record IlVar(string Name) : IlExpr;

/// <summary>フィールド読み書き place: recv.Name。</summary>
public sealed record IlField(IlExpr Recv, string Name) : IlExpr;

/// <summary>要素 place: recv[idx]。PlusOne は 0-based→1-based 変換 (List/array)。</summary>
public sealed record IlIndex(IlExpr Recv, IlExpr Idx, bool PlusOne) : IlExpr;

/// <summary>長さ: #e (List.Count / string.Length / array.Length)。</summary>
public sealed record IlLen(IlExpr E) : IlExpr;

public enum IlBinOp
{
    AddNum, Concat, Sub, Mul, DivNum, RemNum,
    Eq, Ne, Lt, Le, Gt, Ge,
    And, Or,
    BitAnd, BitOr, BitXor, Shl, Shr,
}

public sealed record IlBin(IlBinOp Op, IlExpr L, IlExpr R) : IlExpr;

public enum IlUnOp { Neg, Not, BitNot }

public sealed record IlUn(IlUnOp Op, IlExpr E) : IlExpr;

public sealed record IlParen(IlExpr E) : IlExpr;

/// <summary>条件式。現行出力互換の IIFE で render される。</summary>
public sealed record IlTernary(IlExpr Cond, IlExpr T, IlExpr F) : IlExpr;

/// <summary>解決済み callee 名での呼び出し: Callee(args)。
/// callee は "print" / "Math.Min" / "__tcs_idiv" / "table.insert" 等。</summary>
public sealed record IlCall(string Callee, ImmutableArray<IlExpr> Args) : IlExpr;

/// <summary>式 callee の呼び出し: callee(args) (静的 user method 等)。</summary>
public sealed record IlDynCall(IlExpr Callee, ImmutableArray<IlExpr> Args) : IlExpr;

/// <summary>インスタンスメソッド呼び出し: recv:Method(args)。</summary>
public sealed record IlInvoke(IlExpr Recv, string Method, ImmutableArray<IlExpr> Args)
    : IlExpr;

/// <summary>クラス生成: TypeName.new(args)。</summary>
public sealed record IlNewObj(string TypeName, ImmutableArray<IlExpr> Args) : IlExpr;

/// <summary>table 構築 (List / Dict リテラル / ref-type option table)。
/// Key があれば [k]=v、NameKey があれば name=v、どちらも無ければ配列項。
/// ElementType は配列/List リテラルの要素型 (C backend 用 metadata、T228)。</summary>
public sealed record IlTable(ImmutableArray<IlTableEntry> Entries,
    string? ElementType = null, string? KeyType = null) : IlExpr;

/// <summary>固定長配列の生成: new T[n] (il-spec §11)。dev backend は
/// 空 table (要素は使用時に埋まる)、release backend は連続バッファ確保。</summary>
public sealed record IlNewArray(string ElementType, IlExpr Length) : IlExpr;

public readonly record struct IlTableEntry(
    IlExpr? Key, IlExpr Value, string? NameKey = null);

/// <summary>class 型 test: T またはその派生 (il-spec §9)。</summary>
public sealed record IlIsType(IlExpr E, string TypeRef) : IlExpr;

/// <summary>プリミティブ型 test: type(e) == "number" 等。</summary>
public sealed record IlIsLuaType(IlExpr E, string LuaType) : IlExpr;

/// <summary>式位置の逐次実行: (function() ... end)()。中の文は inline
/// render 可能なもの (local/代入/呼び出し/return/if/for-pairs) に限る。</summary>
public sealed record IlIife(ImmutableArray<IlStat> Stats) : IlExpr;

/// <summary>closure。ExprBody か Body のどちらか。PatternLocals は
/// 式本体内 is-pattern designation の関数冒頭宣言。</summary>
public sealed record IlClosure(
    ImmutableArray<string> Params, IlBlock? Body, IlExpr? ExprBody,
    ImmutableArray<string> PatternLocals) : IlExpr;

/// <summary>値型の copy 地点 (il-spec §10)。Lua backend は __tcs_scopy、
/// C backend は素の値代入 (native struct) として扱う。</summary>
public sealed record IlStructCopy(IlExpr E) : IlExpr;

/// <summary>record with 式 (shallow copy + 上書き)。</summary>
public sealed record IlWith(
    IlExpr Src, ImmutableArray<(string Name, IlExpr Value)> Overrides) : IlExpr;

// ---- 文 ----

public abstract record IlStat : IlNode;

public sealed record IlBlock(ImmutableArray<IlStat> Stats);

/// <summary>変数導入。Type は宣言型 (display 文字列、backend の型付け用。
/// Init 非 null でも設定される)。</summary>
public sealed record IlLocal(string Name, IlExpr? Init, string? Type = null)
    : IlStat;

public sealed record IlAssign(IlExpr Target, IlExpr Value) : IlStat;

public sealed record IlCallStat(IlExpr Call) : IlStat;

public sealed record IlIf(
    ImmutableArray<(IlExpr Cond, IlBlock Body)> Arms, IlBlock? Else) : IlStat;

/// <summary>while。Trailer は for 脱糖の incrementors (continue label の後)。
/// ScopeBody は body + label を do..end で囲う (continue の goto が後続
/// local のスコープへ飛び込むのを防ぐ。legacy VisitFor と同条件)。</summary>
public sealed record IlWhile(
    IlExpr Cond, IlBlock Body, IlBlock? Trailer, bool ScopeBody = false)
    : IlStat;

/// <summary>do-while → repeat/until not (cond)。</summary>
public sealed record IlRepeat(IlBlock Body, IlExpr Cond) : IlStat;

/// <summary>Lua numeric for。制御変数が捕捉されず bound が不変と証明済みの
/// ときだけ builder が生成する最適化形 (意味論上は while + 単一変数と観測等価)。</summary>
public sealed record IlNumericFor(
    string Var, IlExpr Start, IlExpr Limit, IlBlock Body) : IlStat;

/// <summary>foreach (List/array): for _, v in ipairs(coll)。</summary>
public sealed record IlForeachList(string Var, IlExpr Coll, IlBlock Body) : IlStat;

/// <summary>foreach (Dictionary): pairs + KeyValuePair table 合成。</summary>
public sealed record IlForeachDict(string Var, IlExpr Coll, IlBlock Body) : IlStat;

public sealed record IlBreak : IlStat;

public sealed record IlContinue : IlStat;

public sealed record IlReturn(IlExpr? Value) : IlStat;

/// <summary>do ... end スコープ (temp local の隔離)。</summary>
public sealed record IlDo(IlBlock Body) : IlStat;

/// <summary>多重代入: [local ]t1, t2 = v1, v2 (分解・out 引数 multi-return)。</summary>
public sealed record IlMultiAssign(
    ImmutableArray<IlExpr> Targets, ImmutableArray<IlExpr> Values, bool Declare)
    : IlStat;

/// <summary>汎用 pairs ループ: for k[, v] in pairs(coll) do ... end。</summary>
public sealed record IlForPairs(
    string KVar, string? VVar, IlExpr Coll, IlBlock Body) : IlStat;
