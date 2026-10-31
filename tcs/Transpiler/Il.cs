using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace TinyCs;

// TinyC# IL (doc/il-spec.md)。method body の中間表現。
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
public sealed record IlLit(string LuaText, string? Type = null) : IlExpr;

/// <summary>local / parameter / 型名参照。</summary>
public sealed record IlVar(string Name) : IlExpr;

/// <summary>フィールド読み書き place: recv.Name。</summary>
public sealed record IlField(IlExpr Recv, string Name) : IlExpr;

/// <summary>要素 place: recv[idx]。PlusOne は 0-based→1-based 変換 (List/array)。
/// Lua backend は List / 配列の範囲外を fault にするが、要素が nil になりうる配列 (参照型 /
/// T? / 型引数の要素の `new T[n]` は穴を持つ。TCS1003) は長さが分からないので Unchecked。
/// BoolElements は要素が false になりうる (Lua は `t[i] or fault` の形で読めない)。
/// NilElements は List の要素が null になりうる (Lua は nil を読んだら長さと比べる)。</summary>
public sealed record IlIndex(IlExpr Recv, IlExpr Idx, bool PlusOne, bool Unchecked = false,
    bool BoolElements = false, bool NilElements = false) : IlExpr;

/// <summary>長さ: #e (List.Count / string.Length / array.Length)。NilElements は List の要素が
/// null になりうる (Lua はその List の長さを field `n` に持つ。il-spec 付録 A)。</summary>
public sealed record IlLen(IlExpr E, bool NilElements = false) : IlExpr;

public enum IlBinOp
{
    AddNum, Concat, Sub, Mul, DivNum, RemNum,
    Eq, Ne, Lt, Le, Gt, Ge,
    And, Or,
    BitAnd, BitOr, BitXor, Shl, Shr,
}

public sealed record IlBin(IlBinOp Op, IlExpr L, IlExpr R) : IlExpr;

public enum IlUnOp { Neg, Not, BitNot, ToFloat }

public sealed record IlUn(IlUnOp Op, IlExpr E) : IlExpr;

public sealed record IlParen(IlExpr E) : IlExpr;

/// <summary>return の複数の値 (Lua の multi-return: `return a, b`)。Lua backend の emit だけが作る。</summary>
public sealed record IlValues(ImmutableArray<IlExpr> Items) : IlExpr;

/// <summary>条件式。現行出力互換の IIFE で render される。</summary>
public sealed record IlTernary(IlExpr Cond, IlExpr T, IlExpr F) : IlExpr;

/// <summary>解決済み callee 名での呼び出し: Callee(args)。
/// callee は "print" / "Math.Min" / "__tcs_idiv" / "table.insert" 等。
/// NilElements は table.insert / table.remove の受け手の List の要素が null になりうる
/// (Lua はその List の長さを field `n` に持つので、素の table 操作にしない)。</summary>
public sealed record IlCall(string Callee, ImmutableArray<IlExpr> Args, bool NilElements = false) : IlExpr;

/// <summary>式 callee の呼び出し: callee(args) (静的 user method 等)。</summary>
public sealed record IlDynCall(IlExpr Callee, ImmutableArray<IlExpr> Args) : IlExpr;

/// <summary>インスタンスメソッド呼び出し: recv:Method(args)。</summary>
public sealed record IlInvoke(IlExpr Recv, string Method, ImmutableArray<IlExpr> Args)
    : IlExpr;

/// <summary>クラス生成: TypeName.Ctor(args)。Ctor は constructor の名前 (LuaNaming.CtorName)。</summary>
public sealed record IlNewObj(string TypeName, ImmutableArray<IlExpr> Args, string Ctor = "new") : IlExpr;

/// <summary>table 構築 (List / Dict リテラル / ref-type option table)。
/// Key があれば [k]=v、NameKey があれば name=v、どちらも無ければ配列項。
/// ElementType は配列/List リテラルの要素型 (C backend 用 metadata)。
/// NilElements は List の要素が null になりうる (Lua は項の数も field `n` に持つ)。</summary>
public sealed record IlTable(ImmutableArray<IlTableEntry> Entries,
    string? ElementType = null, string? KeyType = null, bool IsArray = false,
    string? ObjectType = null, bool NilElements = false) : IlExpr;

/// <summary>固定長配列の生成: new T[n] (il-spec §11)。dev backend は
/// 空 table (要素は使用時に埋まる)、release backend は連続バッファ確保。</summary>
/// <summary>固定長配列 `new T[n]`。要素は T の default 値 (Default: 値型は
/// その IL、struct は IlNewObj の zero 値、参照型は null)。C は zero 初期化、
/// Lua は値型のとき default を n 個詰める (参照型は空 table)。</summary>
public sealed record IlNewArray(string ElementType, IlExpr Length,
    IlExpr? Default = null) : IlExpr;

/// <summary>空の Queue / HashSet の生成。Type は型の display 名 (C backend の要素型)。</summary>
public sealed record IlNewQueue(string Type) : IlExpr;

public sealed record IlNewHashSet(string Type) : IlExpr;

// LuaCheck: object / 型引数からの unbox で Lua が確かめる math.type の値
public sealed record IlNumericConvert(IlExpr Value, string TargetType, string? LuaCheck = null) : IlExpr;
// LuaCheck: 実行時に型を確かめる Lua の関数と、その第 2 引数 (C は TargetType から型の検査を作る)
public sealed record IlRefCast(IlExpr Value, string TargetType, string? LuaCheck = null, IlExpr? LuaCheckArg = null) : IlExpr;

public readonly record struct IlTableEntry(
    IlExpr? Key, IlExpr Value, string? NameKey = null);

/// <summary>class 型 test: T またはその派生 (il-spec §9)。</summary>
public sealed record IlIsType(IlExpr E, string TypeRef, bool IsInterface = false) : IlExpr;

/// <summary>構造型 (List / 配列 / Dictionary) の型 test。TargetType は
/// IlRefCast と同じ型表記。Lua の値は構造型の tag を持たないので、実行時型を
/// 持つ backend 向けの IL (IlExport) だけが作る。</summary>

/// <summary>プリミティブ型 test: type(e) == "number" 等。</summary>
public sealed record IlIsLuaType(IlExpr E, string LuaType) : IlExpr;

/// <summary>式位置の逐次実行: (function() ... end)()。中の文は inline
/// render 可能なもの (local/代入/呼び出し/return/if/for-pairs) に限る。</summary>
public sealed record IlIife(ImmutableArray<IlStat> Stats) : IlExpr;

/// <summary>closure。ExprBody か Body のどちらか。PatternLocals は
/// 式本体内 is-pattern designation の関数冒頭宣言 (式本体を struct parameter
/// の copy と合わせて Body にしたときも残る)。</summary>
public sealed record IlClosure(
    ImmutableArray<string> Params, IlBlock? Body, IlExpr? ExprBody,
    ImmutableArray<string> PatternLocals) : IlExpr;

/// <summary>static method の method group (delegate への変換)。
/// CopyTypes は parameter ごとの struct の copy 関数 (Lua の式) で、copy の要らない parameter は null (il-spec §10)。
/// Lua backend は copy する wrapper を関数ごとに 1 つ作って返し、C backend は値渡しの thunk にする。</summary>
public sealed record IlMethodGroup(string TypeRef, string Method,
    ImmutableArray<string?> CopyTypes) : IlExpr;

/// <summary>struct 値の copy 地点 (il-spec §10)。Lua backend は型別 copy
/// 関数 {TypeName}.__copy (struct-in-struct を再帰 copy)、C backend は
/// 素の値代入として扱う。Nullable は E が struct の T? (Lua は nil または値) で、
/// 値があるときだけ中の struct を copy する。Copiers は generic struct の型引数の値を写す
/// 関数 (Lua の式、`, M.__copy` の形。Lua は型を消すので型引数の copy を呼び出し側が渡す)。</summary>
public sealed record IlStructCopy(IlExpr E, string TypeName, bool Nullable = false,
    string Copiers = "") : IlExpr;

// ---- Nullable<T> (il-spec §3 / §13)。T? は IL 上の独立した型で、backend は
// Lua = nil または値、C = { has, v } 構造体で表現する。nil 比較や `or` の
// Lua 方言を IL に残さず、以下のノードで操作を明示する ----

/// <summary>T → T? の暗黙変換。Type は T の display 名 (backend の型付け用)。</summary>
public sealed record IlNullableWrap(IlExpr E, string Type) : IlExpr;

/// <summary>HasValue / `!= null` / `is not null` (bool)。</summary>
public sealed record IlNullableHasValue(IlExpr E) : IlExpr;

/// <summary>.Value。値なしは fault (C# の InvalidOperationException 相当)。</summary>
public sealed record IlNullableValue(IlExpr E) : IlExpr;

/// <summary>`e ?? d` / GetValueOrDefault(d)。d は T (結果 T) か T? (結果 T?)。
/// d は e が値なしのときだけ評価する。</summary>
public sealed record IlNullableGetOrDefault(IlExpr E, IlExpr Default) : IlExpr;

/// <summary>lifted 演算子 (C# §12.4.8)。両 operand は T? (builder が
/// IlNullableWrap で揃える)。算術 / bit / shift は片方でも値なしなら値なし、
/// 比較は false、Eq / Ne は両方値なしで等しい、And / Or は bool? の三値論理。</summary>
public enum IlLiftedOp
{
    Add, Sub, Mul, DivFloat, DivInt, RemFloat, RemInt,
    BitAnd, BitOr, BitXor, Shl, Shr,
    Eq, Ne, Lt, Le, Gt, Ge,
    And, Or,
}

public sealed record IlLiftedBin(IlLiftedOp Op, IlExpr L, IlExpr R) : IlExpr;

/// <summary>lifted 単項 (Neg / Not / BitNot)。値なしは値なし。</summary>
public sealed record IlLiftedUn(IlUnOp Op, IlExpr E) : IlExpr;

/// <summary>class 参照の明示 downcast `(T)e` (il-spec §9)。Lua backend は透過
/// (型消去)、C backend は実行時型が T 系でなければ fault。upcast は IL に
/// 現れない (暗黙変換)。</summary>
public sealed record IlCast(IlExpr E, string TypeRef, string LuaRef) : IlExpr;

/// <summary>struct を入れた型引数の既定値として渡す、zero 値と copy の作り方 (Lua の型消去用)。
/// HasToString は ToString の override を文字列化に使う。
/// 引数も copier も無い struct は型の table の `__tcs_td`。</summary>
public sealed record IlStructDescriptor(string TypeName, ImmutableArray<IlExpr> NewArgs,
    string Copiers, bool HasToString = false) : IlExpr;

/// <summary>record with 式 (shallow copy + 上書き)。Src が IlStructCopy なら copy 済み
/// (struct の with)。</summary>
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

/// <summary>foreach (List/array): for _, v in ipairs(coll)。Counted は要素が null になりうる列で、
/// Lua は長さまで数える (ipairs は nil で止まる)。</summary>
public sealed record IlForeachList(string Var, IlExpr Coll, IlBlock Body, bool Counted = false) : IlStat;

/// <summary>foreach (Dictionary): pairs + KeyValuePair table 合成。</summary>
public sealed record IlForeachDict(string Var, IlExpr Coll, IlBlock Body) : IlStat;

/// <summary>string.EnumerateRunes() の foreach: for _, Var in tcs_utf8.codes(Str) do。
/// Var は codepoint 整数で、Rune.Value はその値そのもの。</summary>
public sealed record IlForeachRunes(string Var, IlExpr Str, IlBlock Body) : IlStat;

public sealed record IlBreak : IlStat;

public sealed record IlContinue : IlStat;

public sealed record IlReturn(IlExpr? Value) : IlStat;

/// <summary>do ... end スコープ (temp local の隔離)。</summary>
public sealed record IlDo(IlBlock Body) : IlStat;

/// <summary>break スコープ: 本文内の (ループに束縛されない) IlBreak はここを
/// 抜ける (switch 文の早期 break)。本文内の IlContinue は外側ループに束縛された
/// まま。Lua は repeat ... until true、C は block + goto label。</summary>
public sealed record IlBreakScope(IlBlock Body) : IlStat;

/// <summary>多重代入: [local ]t1, t2 = v1, v2 (分解・out 引数 multi-return)。</summary>
public sealed record IlMultiAssign(
    ImmutableArray<IlExpr> Targets, ImmutableArray<IlExpr> Values, bool Declare)
    : IlStat;

/// <summary>汎用 pairs ループ: for k[, v] in pairs(coll) do ... end。</summary>
public sealed record IlForPairs(
    string KVar, string? VVar, IlExpr Coll, IlBlock Body) : IlStat;
