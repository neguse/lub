using System.Collections.Generic;

namespace TcsSelf;

// TinyC# IL (doc/il-spec.md)。method body の中間表現。
// builder が意味の決定をすべて済ませ、emitter (LuaGen) は IL だけから出力する。
// 演算ノードは型解決済みで単型 (il-spec §4)。
// Origin は source map と --trace のための位置で、意味には関わらない。
// 継承の代わりに interface (IlExpr / IlStat) とそれを実装する record で表し、型による分岐で読む。

// C# の位置 (ファイルと行)。同じ構文から作った IL は同じ IlPos を共有し、--trace は同一性で比べる (class なので == は同一性)
public sealed class IlPos
{
    public string File;
    public int Line;
    // 1 始まりの列 (UTF-8 の byte。分からなければ 0)
    public int Col;

    public IlPos(string file, int line)
    {
        File = file;
        Line = line;
    }
}

public interface IlExpr
{
    IlPos? Origin { get; }
}

public interface IlStat
{
    IlPos? Origin { get; }
}

// ---- 式 ----

// 変換済みの Lua の literal のテキスト (数値と文字列の escape は解決済み)
public record IlLit(string LuaText, string? Type, IlPos? Origin) : IlExpr;

// local / parameter / 型名の参照
public record IlVar(string Name, IlPos? Origin) : IlExpr;

// field の読み書きの place: recv.Name
public record IlField(IlExpr Recv, string Name, IlPos? Origin) : IlExpr;

// 要素の place: recv[idx]。PlusOne は 0 始まりから 1 始まりへの変換 (List / 配列)。
// Lua は List / 配列の範囲外を fault にするが、要素が nil になりうる配列 (TCS1003) は長さが分からないので Unchecked。
// BoolElements は要素が false になりうる (`t[i] or fault` の形で読めない)。
// NilElements は List の要素が null になりうる (nil を読んだら長さと比べる)
public record IlIndex(IlExpr Recv, IlExpr Idx, bool PlusOne, bool Unchecked, bool BoolElements, bool NilElements, IlPos? Origin) : IlExpr;

// 長さ: #e (List.Count / string.Length / array.Length)。NilElements は List の要素が null になりうる (長さを field `n` に持つ)
public record IlLen(IlExpr E, bool NilElements, IlPos? Origin) : IlExpr;

public enum IlBinOp
{
    AddNum,
    Concat,
    Sub,
    Mul,
    DivNum,
    RemNum,
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    And,
    Or,
    BitAnd,
    BitOr,
    BitXor,
    Shl,
    Shr,
}

public record IlBin(IlBinOp Op, IlExpr L, IlExpr R, IlPos? Origin) : IlExpr;

public enum IlUnOp
{
    Neg,
    Not,
    BitNot,
    ToFloat,
}

public record IlUn(IlUnOp Op, IlExpr E, IlPos? Origin) : IlExpr;

public record IlParen(IlExpr E, IlPos? Origin) : IlExpr;

// return の複数の値 (Lua の multi-return: `return a, b`)。Lua の emit だけが作る
public record IlValues(List<IlExpr> Items, IlPos? Origin) : IlExpr;

// 条件式。式のまま描くときは IIFE
public record IlTernary(IlExpr Cond, IlExpr T, IlExpr F, IlPos? Origin) : IlExpr;

// 解決済みの callee 名での呼び出し: Callee(args)。
// callee は "print" / "Math.Min" / "__tcs_idiv" / "table.insert" など。
// NilElements は table.insert / table.remove の受け手の List の要素が null になりうる (素の table 操作にしない)
public record IlCall(string Callee, List<IlExpr> Args, bool NilElements, IlPos? Origin) : IlExpr;

// 式の callee の呼び出し: callee(args)
public record IlDynCall(IlExpr Callee, List<IlExpr> Args, IlPos? Origin) : IlExpr;

// instance method の呼び出し: recv:Method(args)
public record IlInvoke(IlExpr Recv, string Method, List<IlExpr> Args, IlPos? Origin) : IlExpr;

// class の生成: TypeName.Ctor(args)
public record IlNewObj(string TypeName, List<IlExpr> Args, string Ctor, IlPos? Origin) : IlExpr;

// table の構築の項。Key があれば [k]=v、NameKey があれば name=v、どちらも無ければ配列の項
public record IlTableEntry(IlExpr? Key, IlExpr Value, string? NameKey);

// table の構築 (List / Dictionary の literal、参照型の option の table)。
// ElementType は配列 / List の literal の要素型 (C backend 用)。NilElements は List の要素が null になりうる (項の数も `n` に持つ)
public record IlTable(List<IlTableEntry> Entries, string? ElementType, string? KeyType, bool IsArray, string? ObjectType, bool NilElements, IlPos? Origin) : IlExpr;

// 固定長配列 `new T[n]`。要素は T の default 値 (Default: 値型はその IL、struct は IlNewObj の zero 値、参照型は null)
public record IlNewArray(string ElementType, IlExpr Length, IlExpr? Default, IlPos? Origin) : IlExpr;

// 空の Queue / HashSet の生成。Type は型の表示名 (C backend の要素型)
public record IlNewQueue(string Type, IlPos? Origin) : IlExpr;

public record IlNewHashSet(string Type, IlPos? Origin) : IlExpr;

// LuaCheck: object / 型引数からの unbox で Lua が確かめる math.type の値
public record IlNumericConvert(IlExpr Value, string TargetType, string? LuaCheck, IlPos? Origin) : IlExpr;

// LuaCheck: 実行時に型を確かめる Lua の関数と、その第 2 引数 (C は TargetType から型の検査を作る)
public record IlRefCast(IlExpr Value, string TargetType, string? LuaCheck, IlExpr? LuaCheckArg, IlPos? Origin) : IlExpr;

// class の型 test: T またはその派生 (il-spec §9)
public record IlIsType(IlExpr E, string TypeRef, bool IsInterface, IlPos? Origin) : IlExpr;

// プリミティブの型 test: type(e) == "number" など
public record IlIsLuaType(IlExpr E, string LuaType, IlPos? Origin) : IlExpr;

// 式の位置での逐次実行: (function() ... end)()。中の文は 1 行に描けるもの (local / 代入 / 呼び出し / return / if / for-pairs) に限る
public record IlIife(List<IlStat> Stats, IlPos? Origin) : IlExpr;

// closure。ExprBody か Body のどちらか。PatternLocals は式本体の中の is-pattern の designation を関数の冒頭で宣言する
public record IlClosure(List<string> Params, IlBlock? Body, IlExpr? ExprBody, List<string> PatternLocals, IlPos? Origin) : IlExpr;

// static method の method group (delegate への変換)。
// CopyTypes は parameter ごとの struct の copy 関数 (Lua の式) で、copy の要らない parameter は null (il-spec §10)
public record IlMethodGroup(string TypeRef, string Method, List<string?> CopyTypes, IlPos? Origin) : IlExpr;

// struct の値の copy 地点 (il-spec §10)。Lua は型別の copy 関数 {TypeName}.__copy。
// Nullable は E が struct の T? で、値があるときだけ中の struct を copy する。
// Copiers は generic な struct の型引数の値を写す関数 (Lua の式、`, M.__copy` の形)
public record IlStructCopy(IlExpr E, string TypeName, bool Nullable, string Copiers, IlPos? Origin) : IlExpr;

// ---- Nullable<T> (il-spec §3 / §13)。Lua は nil または値、C は { has, v } の構造体 ----

// T → T? の暗黙変換。Type は T の表示名
public record IlNullableWrap(IlExpr E, string Type, IlPos? Origin) : IlExpr;

// HasValue / `!= null` / `is not null` (bool)
public record IlNullableHasValue(IlExpr E, IlPos? Origin) : IlExpr;

// .Value。値が無ければ fault
public record IlNullableValue(IlExpr E, IlPos? Origin) : IlExpr;

// `e ?? d` / GetValueOrDefault(d)。d は e に値が無いときだけ評価する
public record IlNullableGetOrDefault(IlExpr E, IlExpr Default, IlPos? Origin) : IlExpr;

// lifted 演算子 (C# §12.4.8)。両 operand は T?
public enum IlLiftedOp
{
    Add,
    Sub,
    Mul,
    DivFloat,
    DivInt,
    RemFloat,
    RemInt,
    BitAnd,
    BitOr,
    BitXor,
    Shl,
    Shr,
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    And,
    Or,
}

public record IlLiftedBin(IlLiftedOp Op, IlExpr L, IlExpr R, IlPos? Origin) : IlExpr;

// lifted の単項 (Neg / Not / BitNot / ToFloat)。値が無ければ値が無い
public record IlLiftedUn(IlUnOp Op, IlExpr E, IlPos? Origin) : IlExpr;

// class の参照の明示の downcast `(T)e` (il-spec §9)
public record IlCast(IlExpr E, string TypeRef, string LuaRef, IlPos? Origin) : IlExpr;

// struct を入れた型引数の既定値として渡す、zero 値と copy の作り方 (Lua の型消去用)。HasToString は ToString の override を文字列化に使う
public record IlStructDescriptor(string TypeName, List<IlExpr> NewArgs, string Copiers, bool HasToString, IlPos? Origin) : IlExpr;

// with 式の上書きの項
public record IlOverride(string Name, IlExpr Value);

// record の with 式 (浅い copy と上書き)。Src が IlStructCopy なら copy 済み (struct の with)
public record IlWith(IlExpr Src, List<IlOverride> Overrides, IlPos? Origin) : IlExpr;

// ---- 文 ----

public record IlBlock(List<IlStat> Stats);

// 変数の導入。Type は宣言型 (表示の文字列。Init があっても設定される)
public record IlLocal(string Name, IlExpr? Init, string? Type, IlPos? Origin) : IlStat;

public record IlAssign(IlExpr Target, IlExpr Value, IlPos? Origin) : IlStat;

public record IlCallStat(IlExpr Call, IlPos? Origin) : IlStat;

// if の 1 つの腕
public record IlArm(IlExpr Cond, IlBlock Body);

public record IlIf(List<IlArm> Arms, IlBlock? Else, IlPos? Origin) : IlStat;

// while。Trailer は for の脱糖の増分 (continue label の後)。
// ScopeBody は本体と label を do..end で囲う (continue の goto が後の local の scope へ飛び込まないように)
public record IlWhile(IlExpr Cond, IlBlock Body, IlBlock? Trailer, bool ScopeBody, IlPos? Origin) : IlStat;

// do-while → repeat / until not (cond)
public record IlRepeat(IlBlock Body, IlExpr Cond, IlPos? Origin) : IlStat;

// Lua の数値 for。制御変数が捕捉されず範囲が変わらないと分かるときだけ builder が作る
public record IlNumericFor(string Var, IlExpr Start, IlExpr Limit, IlBlock Body, IlPos? Origin) : IlStat;

// foreach (List / 配列): for _, v in ipairs(coll)。Counted は要素が null になりうる列で、長さまで数える
public record IlForeachList(string Var, IlExpr Coll, IlBlock Body, bool Counted, IlPos? Origin) : IlStat;

// foreach (Dictionary): pairs と KeyValuePair の table
public record IlForeachDict(string Var, IlExpr Coll, IlBlock Body, IlPos? Origin) : IlStat;

// string.EnumerateRunes() の foreach: for _, Var in tcs_utf8.codes(Str) do
public record IlForeachRunes(string Var, IlExpr Str, IlBlock Body, IlPos? Origin) : IlStat;

public record IlBreak(IlPos? Origin) : IlStat;

public record IlContinue(IlPos? Origin) : IlStat;

public record IlReturn(IlExpr? Value, IlPos? Origin) : IlStat;

// do ... end の scope (temp の local を閉じる)
public record IlDo(IlBlock Body, IlPos? Origin) : IlStat;

// break の scope: 本文の (ループに束縛されない) IlBreak はここを抜ける (switch 文の早い break)。
// Lua は repeat ... until true
public record IlBreakScope(IlBlock Body, IlPos? Origin) : IlStat;

// 多重代入: [local ]t1, t2 = v1, v2 (分解、out 引数の multi-return)
public record IlMultiAssign(List<IlExpr> Targets, List<IlExpr> Values, bool Declare, IlPos? Origin) : IlStat;

// 汎用の pairs のループ: for k[, v] in pairs(coll) do ... end
public record IlForPairs(string KVar, string? VVar, IlExpr Coll, IlBlock Body, IlPos? Origin) : IlStat;
