# IL リファレンス — IL→C backend の入力契約 (M2 / T217)

> 意味論の規範は `doc/il-spec.md`。本書は in-memory モデル（正規形）の
> ノードカタログと取得 API。luo は本書 + il-spec のみで実装着手できる
> （LuaEmitter を読む必要はない）。

## 取得 API (`Transpiler` assembly を参照)

```csharp
var result = TinyCs.IlExport.Export(csharpSources);
// result.Diagnostics: TCS 診断 (非空なら対象プログラムはサブセット外を含む)
// result.Classes: IlClassInfo[]
//   .Name / .BaseName            — 単一継承 (il-spec §9)
//   .Fields: (Name, Type, IsStatic, Init)  auto property は field として
//            現れる。Init は initializer の IL (無ければ null → default 値)
//   .LayoutHash                  — migration metadata (il-spec §14)。
//                                  instance field の (名前:型;) 列の FNV-1a
//   .Methods: (Name, IsStatic, Parameters, Body, ReturnType, ParameterTypes)
//     Body == null は IL 未対応 method (診断構文等)。backend は拒否してよい。
//     custom property accessor は get_X/set_X 名でここに現れる (T224)
//   .Ctor: explicit constructor (Parameters/ParameterTypes/Body)。null なら
//     default 初期化のみ。Body は field default + initializer 適用後に実行
```

シリアライズ形式は定義しない（v0 決定 — il-spec §1）。luo は .NET から
assembly 参照で直接消費する。

## ノードカタログ (`Transpiler/Il.cs`)

意味論は il-spec の該当節に従う。「Lua render」列は dev backend の現行出力
（参考情報。C backend を拘束しない）。

### 式 (IlExpr)

| ノード | 意味 | Lua render (参考) |
|---|---|---|
| IlLit(text) | 変換済みリテラル (数値/文字列 escape/bool/nil 解決済み) | text |
| IlVar(name) | local / parameter / 型名参照 | name |
| IlField(recv, name) | field place 読み (il-spec §10) | recv.name |
| IlIndex(recv, idx, plusOne) | 要素 place。plusOne=0-based→1-based | recv[idx + 1] |
| IlLen(e) | List.Count / string.Length / array.Length | #e |
| IlBin(op, l, r) | 型解決済み二項演算 (§4-6)。op に DivInt/RemInt は無い — それらは IlCall("__tcs_idiv"/"__tcs_irem") | l op r |
| IlUn(op, e) | Neg / Not / BitNot | -e 等 |
| IlParen(e) | 括弧 (評価順は §4 で規定済み — 表示用) | (e) |
| IlTernary(c, t, f) | 条件式 | IIFE |
| IlCall(callee, args) | 解決済み callee 名の呼び出し。callee は "Class.Method" / intrinsic 名 (§13: print, Math.*, String.*, List.*, Dict.*, table.*, string.format, tostring, math.fmod, \_\_tcs_idiv, \_\_tcs_irem) | callee(args) |
| IlDynCall(callee, args) | 式 callee の呼び出し (delegate 変数等) | callee(args) |
| IlInvoke(recv, m, args) | インスタンスメソッド (仮想解決は実行時型 §9) | recv:m(args) |
| IlNewObj(type, args) | class 生成 (§9: default 初期化→ctor) | Type.new(args) |
| IlTable(entries, elemType?) | List/Dict/option table リテラル。entry = 配列項 / [k]=v / name=v。elemType は配列/List の要素型 metadata | {…} |
| IlNewArray(elemType, length) | 固定長配列生成 (§11)。release は連続バッファ確保 | {} |
| IlStructCopy(e) | 値型の copy 地点 (§10)。C backend は素の値代入で良い | \_\_tcs_scopy(e) |
| IlIsType(e, typeRef) | class 型 test (T またはその派生、null 偽 §9) | \_\_tcs_is(e, T) |
| IlIsLuaType(e, luaType) | プリミティブ型 test | type(e) == "…" |
| IlIife(stats) | 式位置の逐次実行 (switch 式・?. 等の lowering 産物) | (function() … end)() |
| IlClosure(params, body/exprBody, patternLocals) | closure。capture は変数単位 (§7) | function(…) … end |
| IlWith(src, overrides) | record with (shallow copy + 上書き) | IIFE |

### 文 (IlStat) — すべて Origin (SyntaxNode?) を持つ (source map 用・非意味論)

| ノード | 意味 |
|---|---|
| IlBlock(stats) | 文列 (scope。IlIf 等の arm が持つ) |
| IlLocal(name, init?) | 変数導入 (identity は §7) |
| IlAssign(target, value) | place への store (§10) |
| IlMultiAssign(targets, values, declare) | 多重代入 (分解 / out 引数 multi-return) |
| IlCallStat(call) | 呼び出し文 |
| IlIf(arms, else?) | if/elseif 連鎖 |
| IlWhile(cond, body, trailer?, scopeBody) | while。trailer は for 脱糖の incrementors。scopeBody は continue label のための body スコープ隔離 |
| IlRepeat(body, cond) | do-while |
| IlNumericFor(var, start, limit, body) | 数値 for 最適化形。「制御変数が捕捉されず bound 不変」と builder が証明済みの場合のみ現れる。C backend は素の for へ (while + 単一変数と観測等価) |
| IlForeachList(var, coll, body) | List/array の foreach (反復変数は反復ごと §7) |
| IlForeachDict(var, coll, body) | Dictionary の foreach (KeyValuePair 相当) |
| IlForPairs(k, v?, coll, body) | 汎用 pairs (List.Clear の lowering 産物) |
| IlBreak / IlContinue / IlReturn(value?) | 制御 (§8) |
| IlDo(body) | スコープ隔離 (temp local) |

## C backend が負う義務

il-spec 付録 B（signed wrap、-ffp-contract=off、excess precision 排除、
bounds/null/zero check、fault の決定性）。現行 luoc は GNU statement
expression（gcc/clang 拡張）に依存する — 対象 toolchain (arm-none-eabi-gcc)
では成立するが MSVC 非対応の移植性制約として明記する。object model は spike
(`spike/`) の合否解釈に従う。digest 検証は
`Transpiler.Tests/DigestKernels/` の 3 kernel（期待値は
`spike/CONTRACT.md` 末尾）。

## 未カバー（backend 側で拒否してよい）

- Body == null の method（診断構文、method group 参照等 — tasks.md T224）
(2026-07-18 時点で契約は完備: top-level 文は Result.TopLevel、operator は
metamethod 名 (__add 等) の static IlMethodInfo として Methods に現れる)
