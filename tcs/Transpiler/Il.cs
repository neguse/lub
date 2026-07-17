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

/// <summary>table 構築 (List / Dict リテラル)。Key が null なら配列項。</summary>
public sealed record IlTable(ImmutableArray<IlTableEntry> Entries) : IlExpr;

public readonly record struct IlTableEntry(IlExpr? Key, IlExpr Value);

// ---- 文 ----

public abstract record IlStat : IlNode;

public sealed record IlBlock(ImmutableArray<IlStat> Stats);

public sealed record IlLocal(string Name, IlExpr? Init) : IlStat;

public sealed record IlAssign(IlExpr Target, IlExpr Value) : IlStat;

public sealed record IlCallStat(IlExpr Call) : IlStat;

public sealed record IlIf(
    ImmutableArray<(IlExpr Cond, IlBlock Body)> Arms, IlBlock? Else) : IlStat;

/// <summary>while。Trailer は for 脱糖の incrementors (continue label の後)。</summary>
public sealed record IlWhile(IlExpr Cond, IlBlock Body, IlBlock? Trailer) : IlStat;

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
