# IL リファレンス — backend (tcs VM と C) の入力契約 (T217)

> 意味論の規範は `doc/il-spec.md`。本書は in-memory モデル（正規形）のノードカタログと取得 API。
> tcs VM の backend (`SelfHost/VmBackend`) と C の backend (`SelfHost/CBackend`) は本書 + il-spec の IL を読む。

## 取得 API (`Transpiler` assembly を参照)

```csharp
var result = TinyCs.IlExport.Export(csharpSources);   // TcsSelf.ExportResult (SelfHost/Compiler/IlExport.cs)
// result.Diagnostics: TCS 診断 (非空なら対象プログラムはサブセット外を含む)
// result.Classes: List<ExClass>
//   .Name / .BaseName            — 基底 class。継承は TCS1001 (il-spec §9) なので
//                                  診断の無いプログラムでは null
//   .Fields: (Name, Type, IsStatic, Init)  auto property は field として
//            現れる。Init は initializer の IL (無ければ null → default 値)
//   .LayoutHash                  — migration metadata (il-spec §14)。
//                                  instance field の (名前:型;) 列の FNV-1a。
//                                  struct 型 field は内部レイアウトへ再帰展開
//                                  (struct 内部変更が owner の hash に伝播)
//   .Methods: (Name, IsStatic, Parameters, Body, ReturnType, ParameterTypes)
//     Body == null は IL 未対応 method (診断構文等)。backend は拒否してよい。
//     custom property accessor は get_X/set_X 名でここに現れる (T224)
//     interface の property を自動 property で実装する型の accessor も同じ名前で、本体は field の読み書き。
//     interface は継承した member も自分の Methods に持つ
//   .Ctors: explicit constructor (Name/Parameters/ParameterTypes/Body)。空なら
//     default 初期化のみ。Body は field default + initializer 適用後に実行。
//     Name は呼び出しの IlNewObj.Ctor / IlCall("S.Name") と同じ (`new`、`new__int`)
//   .IsRecord: record class (T243)。positional parameter が Fields の先頭に
//     並び (base へ渡すだけの parameter は C# 同様に合成しない)、Ctors の 1 つは
//     それらへの代入 + base 引数。==/!= は構造等価 (backend が field 比較を
//     生成、型が違えば false)、with は IlWith (実行時型の shallow copy)
// result.Structs: List<ExStruct> — struct / record struct (il-spec §10)。
//   Fields (auto property / record struct の positional parameter 込み、
//   Init は explicit ctor 経由でのみ適用) / LayoutHash (migration metadata、
//   struct 値は reload 時に struct の計画で移す — object-model §6)
//   / Methods (method + custom property accessor + operator、呼び出しは
//   IlCall("S.M", [receiver, args])、IsStatic なら receiver なし) / StaticFields
//   (static field と static auto property。読み書きは IlField(IlVar("S"), name))
//   / Ctors (explicit または positional、IlCall("S.ctor", args)、複数なら `S.ctor__float` の形)
//   / IsRecord (==/!= は IlCall("S.op_Equality"))
// result.Enums: enum 名 (hot reload の added field default 判定)
// result.EnumTypes: List<ExEnum> — enum の定数表 (Name / MemberNames (user の enum は C# の名前、--ref の enum は host の名前) /
//   MemberValues (int 値))。IL 上の enum 参照は IlField(IlVar(enum 名), member 名) で、
//   C backend は i32 定数へ畳む (T241)
```

シリアライズ形式は定義しない（v0 決定 — il-spec §1）。backend は compiler と同じ process で in-memory の IL を読む (tcs VM の `.tcsb` は IL ではなく VM の bytecode)。`Transpiler/IlExport.cs` の `TinyCs.IlExport.Export` は .NET のテストのための入口で、`specializeGenerics` を真にすると tcs VM と C が読むのと同じ単相化した IL を返す。

## ノードカタログ (`SelfHost/Compiler/Il.cs`)

意味論は il-spec の該当節に従う。tcs VM と C の backend が読まない field はそう書く。

### 式 (IlExpr)

| ノード | 意味 |
|---|---|
| IlLit(Kind, I, F, S) | literal の値。Kind (`IlLitKind`: Null / Bool / Int / Float / String) が読む member を決める: Bool と Int は I (bool は 0 / 1、char は code unit、uint は同じ 32 bit の int、int.MinValue もそのまま)、Float は F (f32 の値。NaN と ±∞ も値のまま)、String は S (escape の無い値)。作り方と診断の表示は `IlLiterals` |
| IlVar(Name) | local / parameter / 型名参照 |
| IlField(Recv, Name) | field place 読み (il-spec §10)。static は `IlField(IlVar(型名), name)`、enum の member も同じ形 (C は i32 定数へ畳む、T241) |
| IlIndex(Recv, Idx, PlusOne, Unchecked) | 要素 place。PlusOne が真なら List / 配列の 0 始まりの添字、偽なら Dictionary の key。Unchecked (要素が null になりうる配列) は tcs VM と C が読まない |
| IlLen(E) | List.Count / 配列の Length |
| IlBin(Op, L, R) | 型解決済み二項演算 (§4-6)。op は AddNum / Concat / Sub / Mul / DivNum (float の除算) / RemNum (float の剰余) / Eq / Ne / Lt / Le / Gt / Ge / And / Or / BitAnd / BitOr / BitXor / Shl / Shr。整数の除算と剰余は IlCall("__tcs_idiv" / "__tcs_irem")、float→int cast は IlCall("__tcs_trunc") (0 方向 truncation)、`(char)x` は IlCall("__tcs_char") (0〜255 の外は fault。char / byte と範囲内の定数からの cast には付かない)。And / Or は bool の短絡で、参照型の operand の Or は null の合体 (左が null でなければ左、null なら右を評価する。文字列の連結の operand の null を `""` にする `x Or ""` など)。Shl / Shr は C# 意味論 (count & 31、Shr は算術。tcs VM は `SHL_I32` / `SHR_I32`、C は `tcs_shl` / `tcs_shr`) |
| IlUn(Op, E) | Neg / Not / BitNot / ToFloat (ToFloat は IlLiftedUn でだけ使う) |
| IlParen(E) | 括弧 (評価順は §4 で規定済み — 表示用) |
| IlTernary(Cond, T, F) | 条件式。tcs VM は分岐と結果の register、C は `?:` |
| IlCall(Callee, Args) | 解決済み callee 名の呼び出し。callee は "Class.Method" / intrinsic 名 (§13: Math.*, String.*, List.* (List.Add、List.RemoveAt は 0 始まりの添字), Dict.*, Char.*, TinySystem.*, \_\_tcs_writeline / \_\_tcs_write (Console.WriteLine / Write), \_\_tcs_getenv, \_\_tcs_iparse / \_\_tcs_fparse (int.Parse / float.Parse), \_\_tcs_format (printf の書式), \_\_tcs_cstr (char → 1 byte string), \_\_tcs_upper / \_\_tcs_lower, \_\_tcs_fmod (f32 の %), \_\_tcs_refeq (参照の比較), \_\_tcs_idiv, \_\_tcs_irem, \_\_tcs_str / \_\_tcs_fstr / \_\_tcs_bstr / \_\_tcs_nstr (文字列化) など) |
| IlDynCall(Callee, Args) | 式 callee の呼び出し (delegate 変数等) |
| IlInvoke(Recv, Method, Args) | インスタンスメソッド (interface の呼び出しは実行時型で解決 §9) |
| IlNewObj(TypeName, Args, Ctor) | class 生成 (§9: default 初期化→ctor)。Ctor は constructor の名前 (既定は `new`)。struct は引数の無い zero 値 |
| IlTable(Entries, ElementType, KeyType, IsArray, ObjectType) | List / 配列 / Dictionary の literal と object initializer の項の並び。entry = 配列項 / Key つきの項 / NameKey つきの項 (`name = v`)。ElementType は配列 / List の要素型、KeyType は Dictionary の key の型、IsArray は配列、ObjectType は NameKey の項で作る型 (`--ref` の data class か struct) |
| IlNewArray(ElementType, Length, Default) | 固定長配列生成 (§11)。Default は要素の default 値 (値型は IL、struct は IlNewObj の zero 値、参照型は null)。tcs VM と C は zero 初期化で、Default を読まない |
| IlNewQueue(Type) / IlNewHashSet(Type) | 空の Queue / HashSet の生成。Type は型の表示名 |
| IlNumericConvert(Value, TargetType, Unbox) | 数の変換 (TargetType は int / float)。object の値からなら unbox で、実行時の型が違えば fault。Unbox はその印で、tcs VM と C は読まない (Value の型と TargetType から検査を作る) |
| IlRefCast(Value, TargetType) | 参照の値の cast。tcs VM と C は TargetType から実行時の型の検査を作る |
| IlStructCopy(E, TypeName, Nullable) | 値型の copy 地点 (§10)。Nullable は E が struct の T? (値があるときだけ中を copy)。tcs VM は register の move か heap との間の葉の load / store、C は素の値代入 |
| IlIsType(E, TypeRef, IsInterface) | class 型 test (T またはその派生、null 偽 §9)。IsInterface なら実装の test |
| IlIsPrimType(E, Type) | object の中の値のプリミティブの型 test。Type (`IlPrimType`) は String / Bool / Int / Float / Number (int か float)。tcs VM は box の型を見る。C は未対応 (emit error) |
| IlIife(Stats) | 式位置の逐次実行 (switch 式・?. 等の lowering 産物)。tcs VM は結果の register を持つ block に、C は GNU statement expression に展開する |
| IlClosure(Params, Body / ExprBody, PatternLocals) | closure。capture は変数単位 (§7)。PatternLocals は式本体の中の is-pattern の変数 |
| IlMethodGroup(TypeRef, Method) | static method の method group (delegate への変換)。tcs VM は関数を指す closure、C は値渡しの thunk で、どちらも struct の parameter を値で渡す (§10) |
| IlWith(Src, Overrides) | record with (shallow copy + 上書き)。record class なら実行時型の layout で copy、record struct なら値 copy |
| IlStructDescriptor(TypeName, NewArgs, HasToString) | struct を入れた型引数の既定値の作り方。単相化した IL には現れない |
| IlNullableWrap(E, Type) | T → T? (Type = T の display 名) |
| IlNullableHasValue(E) | HasValue / `!= null` / `is not null` |
| IlNullableValue(E) | .Value (値なしは fault) |
| IlNullableGetOrDefault(E, Default) | `??` / GetValueOrDefault。Default は T か T?、値なしのときだけ評価 |
| IlLiftedBin(Op, L, R) / IlLiftedUn(Op, E) | lifted 演算子 (il-spec §3)。op は IlLiftedOp (DivInt / RemInt を含む) |
| IlCast(E, TypeRef) | user class / record への明示 downcast。upcast と同型は IL に現れない (透過)。実行時型が TypeRef 系でなければ fault |

### 文 (IlStat) — Origin (IlPos。C# のファイル、行、列) を持つ。`tcs vm --trace` と VM の行の情報が使い、意味論には関わらない

| ノード | 意味 |
|---|---|
| IlBlock(Stats) | 文列 (scope。IlIf 等の arm が持つ) |
| IlLocal(Name, Init?, Type?) | 変数導入 (identity は §7)。Type は宣言型の display 文字列 (var も推論型、is-pattern / out var の前宣言も symbol 型)。Init が null の literal / closure / 派生型のときは backend は Type を優先する |
| IlAssign(Target, Value) | place への store (§10) |
| IlMultiAssign(Targets, Values, Declare) | 多重代入 (分解 / out 引数)。自作メソッドの out の呼び出しは `Targets` = [戻り値 (void なら無し), out...]、`Values` = [out を除いた引数の call] で、out の順は `ExMethod.OutParameters` の順。Math.TryParse* と Dict.TryGet も同じ形 (Targets = [見つかったか, 値]) |
| IlBreakScope(Body) | break スコープ: 本文内のループに束縛されない IlBreak がここを抜ける (switch 文の早期 break)。continue は外側ループ束縛のまま。C は block + goto label |
| IlCallStat(Call) | 呼び出し文 |
| IlIf(Arms, Else?) | if/elseif 連鎖 |
| IlWhile(Cond, Body, Trailer?) | while。Trailer は for 脱糖の incrementors |
| IlRepeat(Body, Cond) | do-while |
| IlNumericFor(Var, Start, Limit, Body) | 数値 for 最適化形。「制御変数が捕捉されず bound 不変」と builder が証明済みの場合のみ現れる。backend は素の for にしてよい (while + 単一変数と観測等価。tcs VM は本文が制御変数を書き換えるときは隠れた counter を回す) |
| IlForeachList(Var, Coll, Body) | List/array の foreach (反復変数は反復ごと §7) |
| IlForeachDict(Var, Coll, Body) | Dictionary の foreach (KeyValuePair 相当) |
| IlForeachRunes(Var, Str, Body) | `string.EnumerateRunes()` の foreach (runtime/utf8 で code point を読む) |
| IlForPairs(KVar, VVar?, Coll, Body) | List.Clear / Dictionary.Clear の lowering 産物 (`IlIife` の中で、全ての key に null の literal を代入する形)。tcs VM と C はこの形を runtime の clear にする |
| IlBreak / IlContinue / IlReturn(Value?) | 制御 (§8) |
| IlDo(Body) | スコープ隔離 (temp local) |

## C backend が負う義務

il-spec 付録 B（signed wrap、-ffp-contract=off、excess precision 排除、bounds/null/zero check、fault の決定性）。C backend は GNU statement expression（gcc/clang 拡張）に依存する — 対象 toolchain (arm-none-eabi-gcc) では成立するが MSVC 非対応の移植性制約として明記する。object model は `doc/object-model.md`。digest 検証は `Transpiler.Tests/DigestKernels/` の kernel（期待値は `perf/CONTRACT.md` 末尾）。

## tcs VM が負う義務

il-spec 付録 A。

## 未カバー（backend 側で拒否してよい）

- Body == null の method（診断構文、method group 参照等 — tasks.md T224）
(2026-07-18 時点で契約は完備: top-level 文は Result.TopLevel、operator は
`IlNames.OperatorName` の名前 (`__add`、overload は `__mul_1` 等) の static ExMethod として Methods に現れ、呼び出し箇所は IlCall)
