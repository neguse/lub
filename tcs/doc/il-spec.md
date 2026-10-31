# TinyC# IL 仕様 v0 (T211)

> Status: draft v0 (2026-07-18)。**意味論の正本**。位置づけと経緯は
> `doc/il-design.md`、抽象度の決定根拠は `doc/il-lowering-examples.md`。
> 本文 §1-§15 は backend に依存せず規範を定める。各 backend の義務は付録。

## 1. スコープと適合

- IL は Roslyn で検査・診断済みの TinyC# プログラムの表現。IL は自前の
  型検査を持たず、型不整合な IL の挙動は未定義
- TinyC# プログラムの実行意味 = 本仕様が IL に与える意味。backend は
  **観測等価**な実行を提供する義務を負う。観測とは: Console 出力の列、
  エントリポイントの完了または fault (§12)、interop 境界 (§15) を通過する値
- backend は観測等価を保つ任意の変形を行ってよい。ただし §6 の f32 制約は
  観測等価の定義に含まれる（禁止事項は変形で導入できない）
- v0 の正規形は in-memory モデル（Transpiler 内の .NET オブジェクト）。
  テキスト表記は診断用で非規範。シリアライズ形式は M2 (T217) で決定

## 2. プログラム構造

module / class / record class / enum / interface / method / field。

- property・indexer・event・ループ構文・パターン・式位置の制御フロー・
  文字列補間は IL に存在しない（構築時 lowering 済み。examples 決定 1-2）
- enum は i32 定数の集合。実行時表現は i32
- 入れ子の型は外側の型と独立した型で、名前に外側の型の名前を `_` でつなぐ（`Outer_Inner`）
- interface は値を作らない型。class / record class は実装する interface の一覧を実行時に持ち、
  interface を対象とする type test（IlIsType）はこの一覧で判定する。
  一覧に載るのは source で宣言した generic でない interface で、入力は参照型に限る（それ以外は診断）
- エントリポイントは static Main 相当の単一 method

## 3. 型

`i32` / `f32` / `bool` / `string` / `ref C`（class / record class）/
`V`（struct / record struct、M5 で有効化）/ `T?`（Nullable、T は i32 /
f32 / bool / enum / V）/ `T[]` / `List<T>` / `Dictionary<K,V>` /
関数型（closure）/ enum (= i32) / char (= i32 の code unit。string は
UTF-8 byte 列なので `s[i]` は byte 値、文字列化は `string.char(c)` で 1
byte。literal は ASCII のみ、T251)。

- 型引数は消去済み。IL ノードはすべて単型（examples 決定 3）
- null は ref・string・List・Dict・関数型の値。i32 / f32 / bool / V は
  非 null
- `T?` は「値なし」を持つ独立した型（T244）。IL は Lua の nil 方言を使わず
  明示ノードで操作する: `IlNullableWrap` (T → T?)、`IlNullableHasValue`、
  `IlNullableValue` (値なしは fault §12)、`IlNullableGetOrDefault`
  (`??` / GetValueOrDefault。既定値は値なしのときだけ評価)、
  `IlLiftedBin` / `IlLiftedUn` (lifted 演算子: 片方でも値なしなら値なし、
  比較は false、Eq / Ne は両方値なしで等しい、`bool?` の `&` `|` は三値論理)。
  `x?.M` は receiver を local に 1 回評価する IIFE で、`T?` receiver は
  `IlNullableHasValue` で分岐し `IlNullableValue` (copy) に対して member を
  参照する。結果が非 nullable 値型なら `IlNullableWrap` で `T?` に揃える
  (値なし側は IIFE の fall-through = 値なし)。`??=` も `T?` では
  `not IlNullableHasValue` で判定する (T247)。式位置の `a ??= b` の値は
  代入後の a で、a が `T?` なら式の型 `T` に合わせて `IlNullableValue` で
  中の値を取り出す (代入後は必ず値がある)。
  意味論の規範は C# §12.4.8 で、dotnet differential がそれを検証する。
  文字列化 (`__tcs_nstr`) は値なしを空文字列にする (C# の
  `string.Concat(null)` / 補間と同じ)。backend 表現は dev = nil または値、
  release = `{ bool has; T v; }`。collection 要素の値なしは TCS1003 のまま
  (Lua table に nil を置けない)
- double / long はサブセット外（M4 で診断化。il-design §4）

## 4. 評価モデル

- 式の operand は**厳密に左→右**で評価する。副作用は逐次で、backend が
  観測可能な並べ替えをしてはならない
- 演算ノードは型解決済み・単型。overload 解決・暗黙変換の挿入・定数畳み込みの
  可否判断は Roslyn 層で完了している
- 短絡 `and` / `or` は bool 専用ノード（右 operand は必要時のみ評価）。
  条件式 `?:` は IL に無い（temp + if へ statement 化済み）

## 5. i32 意味論

- 32bit 2 の補数。add / sub / mul は wrap（C# unchecked と同一）。checked 文脈の add / sub / mul と単項 neg は `IlCall("__tcs_cadd" | "__tcs_csub" | "__tcs_cmul" | "__tcs_cneg")` で、結果が i32 の範囲外なら fault（C# の OverflowException 相当）
- div / rem: 0 方向切り捨て、rem の符号は被除数。除数 0 は fault。
  `INT_MIN / -1` および `INT_MIN % -1` は fault（C# の OverflowException 相当）
- 比較・ビット演算・シフトはサポート演算子集合（support-matrix §4.2 が正）
  について C# unchecked と同一の意味
- i32 ↔ f32 変換は明示ノード。f32 → i32 は 0 方向丸め。変換元が NaN または
  i32 で表現できない値のときは fault（C# は unchecked では値未規定のため、
  決定的な定義を IL が与えても C# サブセットと矛盾しない。digest workload と
  fuzz はこの入力を生成しない）

## 6. f32 意味論

- IEEE 754 binary32。各演算ノードは single rounding（演算ごとに f32 へ丸める）
- **再結合・縮約（fma 化）・精度の昇格/降格は観測等価とみなさない**。
  backend は式の演算列を IL のまま保存する
- NaN / ±Inf / −0 は IEEE 準拠。比較は IEEE（NaN との順序比較・`==` は
  false、`!=` は true。−0 == +0）
- **fp モード**: 上記 strict が既定。出荷ビルド単位の明示 opt-in で
  **relaxed-fp** を選択でき、縮約（fma 化）・再結合・中間精度の昇格を許可
  する。relaxed でも IEEE special values の意味論（NaN / Inf / −0 の比較・
  伝播）は維持する — finite-math 系（NaN 不存在仮定）は許可しない。
  relaxed 下では f32 の丸め列が実装定義になるため、f32 値に依存する観測
  （出力値、値依存の fault 位置）の backend 間一致は保証されず、digest
  一致ゲートは strict モードでのみ適用される
- リテラルは 10 進表記から最近接丸めで f32 へ写す。この丸めは IL 構築時に
  **一度だけ**起こり、IL はリテラルを f32 bit 値として保持する（10 進
  テキストを backend へ渡さない。double 経由の二重丸めは 1 bit の差を生む）

## 7. 変数・スコープ・capture

- 変数は名前ではなく identity を持つ実体。scope（block 構造）が寿命を定める
- closure は**変数を捕捉**する（値ではない。examples 決定 5）。同じ変数を
  捕捉する closure 同士・外側 scope は、その変数の読み書きを共有する
- ループは IL に来る前に while + block へ脱糖済み。「`for` の制御変数は
  ループ全体で 1 個」「`foreach` の反復変数は反復ごと」という C# の規定は、
  脱糖結果の scope 構造として表現される（現行バグ T221 はこの規範で修正）

## 8. 制御フロー

block / if / while / break / continue / return のみ（examples 決定 6）。
goto は無い。例外機構は無い — try / throw はサブセット外（TCS1001 維持）で、
実行時の異常は fault (§12) として扱う。

## 9. 参照型

- class インスタンスは identity を持つ heap 値。フィールドは place (§10)
- new: 全フィールドを default 値（i32=0, f32=0.0, bool=false,
  ref/string/List/Dict/関数型=null）で初期化した後、constructor 本体を実行
- type test（`is T`、T は class か interface — §2）: 実行時型が T またはその派生、
  T を実装する class なら真。null は常に偽（現行バグ T222 はこの規範で修正）
- cast: Roslyn が安全と検査済みの位置では無検査。明示 cast の失敗は fault
- class / record class の継承は無い（q.md Q15。基底 class、virtual /
  abstract / override / sealed / protected / new による隠蔽、base は build を
  止める TCS1001）。多態は interface の実装と type test で書く
- class の参照比較（operator 定義が無い `==`）は identity 比較

## 10. place と値型（struct / record struct。field + instance member +
ctor。static member / operator / override はサブセット外、T246 で契約完成）

place = 格納場所。変数、フィールド path、配列/List 要素 path の 3 種。

- place への store は in-place 更新であり copy を伴わない
  （`s.F = v`、`a[i].X = v`）
- 値型 V の **copy 地点**（この列挙が規範。examples 決定 7）:
  1. 代入（`var p = q` / place への V 値の store）
  2. 引数渡し（値渡し）
  3. return
  4. 値文脈での place 読み出し（`var p = a[i]`、`var q = s.Inner`）

  1 には field / property / object / collection / 配列の initializer、
  `with` の上書き値、分解の成分、foreach の反復変数、out への書き出し、
  `??=` の右辺を含む。2 には property setter の `value`、`base(...)` の引数、
  `?.` 経由の呼び出しの引数、class の user operator の operand (複合代入を含む) を含む。3 には式本体 (`=>`) の lambda / method / property / accessor / operator の値を含む (void の本体は式文なので含まない)
- 式位置の代入 (`F(a[i] = v)`) の値は代入した値の copy で、格納先とは
  独立した値（受けた側の変更は格納先に残らない）。custom property への代入は
  setter に copy を渡し、setter が `value` を変えても式の値は変わらない
- 分岐で値を選ぶ式 (switch 式 / `?:` / `??` / `GetValueOrDefault(x)`) の値は
  選んだ値そのもの (格納先の値でありうる) で、copy 地点で copy する。IIFE に
  降りる式でも新しい値とは限らない。switch 式は arm の値を arm ごとに copy して
  返す (新しく作った値の arm は copy しない)
- struct の `T?` の値 (`c ? h.F : null`、`h?.F`) も格納先の値でありうるので、
  copy 地点で値があれば中の struct を copy する (IlStructCopy の Nullable)。
  `T?` の値は `.Value` も含めて書き換えられない (CS1612) ので、`T?` の格納先
  どうしの共有は観測できず、foreach の反復変数 / lambda の parameter /
  copy constructor の要素の `T?` は copy しない
- lambda の struct parameter は入口で copy する（runtime の関数は要素を
  copy せずに渡すため、呼び出し側では copy できない）。copy constructor
  (`new List<T>(source)`) は新しい List に要素の copy を入れる（IL は末尾引数に
  `S.__copy` 参照を付け、要素を値で写す backend は読まなくてよい）
- static method の method group (IlMethodGroup) の struct parameter も、lambda と同じく受ける側で copy する。
  readonly struct と `T?` の parameter は copy しない。
  Lua は copy する wrapper を関数ごとに 1 つ作って返す。
  C は値渡しの thunk で、copy は要らない
- out parameter は呼び出しごとの新しい変数で、呼び出しの正常な完了で値が渡る。自作メソッドの受け手は `out var` / `out _` だけなので (TCS1001)、out を別名で観測する手段は無く、Lua は multi-return (`return v, out...`)、C はポインタで写す
- struct 配列の要素は互いに独立した place。連続メモリ配置は backend 表現の
  自由であり IL の意味論ではない
- struct の instance member は静的ディスパッチの自由関数
  `IlCall("S.M", [receiver, args...])`。receiver が C# の「変数」(local /
  parameter / field / 配列要素 / this) なら place をそのまま渡し、変異は
  その場に残る。rvalue (property / List indexer / 呼び出し結果) は copy を
  渡し、変異は捨てられる (C# と同じ)。backend は place のアドレス渡し
  (C: `Tcs_S *self`) で copy を省いてよい
- explicit ctor は `IlCall("S.ctor", args)`: zero 値 → field initializer →
  本文の順。`new S()` は ctor を通らない zero 値 (IlNewObj、initializer も
  走らない)。record struct の positional ctor は宣言順の代入
- タプルは struct (`Item1` からの field)。IL に専用の型は無い。代入や引数渡しなどの copy 地点も struct と同じで、literal の要素と分解の成分は要素ごとに copy 地点になる。
  分解は左辺の受け手と添字、右辺、左から順の代入の順に評価する (`(a[0], a[0]) = (1, 2)` は 2 が残る)。
  Lua backend は戻り値の宣言がタプルの method を multi-return にし、値として保持する位置で table に詰める (観測等価の変形)。型引数 T の値、delegate の戻り値、out を持つ method の戻り値、property の値は 1 つの table のまま
- 値等価は memberwise (ネスト struct は推移的)。record struct の `==` / `!=`
  は `IlCall("S.op_Equality", [a, b])`、plain struct も List.Contains /
  IndexOf / Remove (C# の EqualityComparer<T>.Default) で同じ等価を使う
  (IL は末尾引数に `S.op_Equality` 参照を付け、要素型から判る backend は
  読まなくてよい)。record struct の `with` は IlWith (値 copy + 上書き)
- `new S[n]` / `new int[n]` の要素は default 値 (IlNewArray.Default。struct
  は zero 値)。C は zero 初期化、Lua は値型のとき n 個詰める

## 11. 配列・List・Dictionary・string

- `T[]`: 固定長、**0-based**。範囲外 index は fault。`new T[n]` は
  default 値で初期化
- List / Dict: IL は runtime 型として扱い、メンバー呼び出しは intrinsic
  call (§13)。意味論の規範は TinySystem dotnet facade（T209 で System 委譲
  実装済み）+ `known-differences.json`。null 保存制約は TCS1003 の診断が守る
- string: **不変の octet 列**。Length・index・IndexOf 等はバイト単位。
  source literal は UTF-8 符号化で octet 列へ写す。孤立 surrogate を含む
  literal はこの写像が存在しないためサブセット外（診断）。octet 列は
  valid UTF-8 を不変条件としない（バイト単位の Substring 等が非 UTF-8 列を
  生成しうる）。C# frontend（UTF-16 code unit）との差は known-differences が
  正本（support-matrix §27）。
  判断: 出荷ターゲットの表現（octet 列）を規範に置き、開発時 .NET 実行を
  「非 ASCII で長さ系が異なる既知差」とする。逆にすると release backend が
  UTF-16 文字列を背負う
- string の `==` は内容比較。参照等価は観測に含まれない（intern の自由）
- string の順序 (比較なしの Sort、`string.CompareOrdinal` の符号。TinySystem.Linq の OrderBy / Min / Max はこれを使う) は .NET の ordinal (UTF-16 code unit の順) で、culture にも Lua の locale (strcoll) にも依らない。
  octet 列の byte の順とは、U+10000 以上 (UTF-16 では surrogate D800–DBFF) と U+E000–U+FFFF の間だけ逆になる。
  .NET 実行は InvariantGlobalization (TinySystem.props) で既定の比較が ordinal になることを前提にする

## 12. fault

fault = 決定的に検出される実行時異常。発生した fault はプログラムを即時
停止し、回復手段は無い。

- 種別: 除数 0 (§5)、i32 除算 overflow (§5)、f32→i32 変換不能 (§5)、
  null 参照への member アクセス、配列/List/string の範囲外 index、
  明示 cast の失敗 (§9)、switch 式の no-match（discard の無い非網羅
  switch 式で一致 arm が無い場合。C# の SwitchExpressionException 相当 —
  例外がサブセット外のため fault として定義）、
  runtime intrinsic が定義する error
- 同一入力・同一プログラムなら、どの backend・どの lowering モードでも
  同一の観測列の後に fault する（fault の位置も観測の一部）
- fault 時の診断情報（メッセージ、source 位置）は品質事項であり適合条件では
  ない
- 実装は上記に加えて実装定義の資源 fault（allocation 失敗等）を持ってよい。
  資源 fault の発生位置は backend 間一致の対象外

## 13. intrinsic

- TinySystem runtime 表面（Math / String / List / Dict / Random / Console）
  の member 集合が intrinsic 集合。member 一覧は support-matrix Part II が正。
  LINQ (TinySystem.Linq) は intrinsic ではなく、`TinySystem/Linq.cs` を user のコードと同じく IL にする
- 意味論の規範 = TinySystem dotnet facade + known-differences.json。
  IL 仕様はこれを重複記述しない（conformance sweep / differential が
  実行可能な正本として既に担保している）
- 数値→文字列（連結・補間・Console 出力・ToString・`string.Join` の要素）:
  i32 は 10 進最短表記。
  f32 は .NET の `float.ToString()`（InvariantCulture）と同じ文字列で、round-trip する最短の桁を、科学表記の指数 X が -5 < X < 9 なら固定小数（`1000000`、`0.0001`）、それ以外は `d.dddE+XX` / `d.dddE-XX`（指数は 2 桁以上。`1E+09`、`1E-05`）で書く。
  -0 は `-0`、NaN は `NaN`、±∞ は `Infinity` / `-Infinity`。
  bool は `True` / `False`（`IlCall("__tcs_bstr")`、C# の `bool.ToString()` と同じ）。
  静的型が object の値は中の値で文字列にし (float は f32 の規則)、null は空文字列。C backend は object の文字列化を emit error にする。
  runtime/fmt の 1 つの実装（C。Lua は tcs_rt module）が文字列を作る。
- 補間の f32（と f32 の `T?`）の書式 `G` / `E` / `F` / `N`（小文字と精度つきも。`{f:G3}`、`{f:e2}`、`{f:N}`）は `IlCall("String.FormatFloat", [v, spec])` で、.NET の `float.ToString(format)`（InvariantCulture）と同じ文字列。
  spec は書式の文字と精度（9 桁までの数。`G03` は `G3` に直す）の string literal。
  `G` と `G0` は上の既定の文字列化と同じ。
  精度 p の `G` は正確な値を有効数字 p 桁に丸め（ちょうど中間は偶数へ）、指数 X が -5 < X < p なら固定小数、それ以外は `d.dddE+XX` で書く（末尾の 0 は書かない）。
  `E` は正確な値を小数部 p 桁（精度が無ければ 6）に同じく丸めた `d.ddddddE+XXX`（指数は 3 桁以上）。
  `F` / `N` は正確な値を小数部 p 桁（精度が無ければ 2）に同じく丸めた固定小数で、`N` は整数部を 3 桁ごとに `,` で区切る。-0 と、丸めて 0 になる負の値は `-0.00` のように符号を残す。
  `R` / `r` は精度を見ず、精度なしの `G` / `g` と同じ。
  小文字の書式は指数の文字も小文字。NaN と ±∞ は既定の文字列化と同じ、`T?` の値なしは空文字列。
- 補間の i32（と i32 の `T?`）の書式 `G` / `E` / `F` / `N` / `D` / `X` / `R`（小文字と精度つきも）は `IlCall("String.FormatInt", [v, spec])` で、.NET の `int.ToString(format)`（InvariantCulture）と同じ文字列。
  `G`、`G0`、`R` は全部の桁。精度 p の `G` は有効数字 p 桁に丸めて、指数 X が X < p なら固定小数、それ以外は `d.dddE+XX` で書く（末尾の 0 は書かない）。`E`、`F`、`N` の形は f32 と同じで、`E` と `G` の丸めのちょうど中間は絶対値の大きい方へ。
  `D` は符号を除いて p 桁以上。`X` は 32 bit の 2 の補数の 16 進で p 桁以上。`T?` の値なしは空文字列。
  値は i32 の範囲だけで、64 bit の Lua に範囲外の値が届いたら実行時 error で止まる。
  runtime/fmt の同じ実装が文字列を作る。
  それ以外の書式は printf の書式に写す（`string.format`）
- enum (と enum の `T?`) の文字列化は `IlCall("String.FormatEnum", [v, spec])`。
  spec は string literal で、`名前:値` を `,` で並べる。`[Flags]` (と書式 "F")
  は先頭に `*` を付けて全 member を .NET の EnumInfo と同じ並び (値の符号なし
  昇順) で持ち、それ以外は値ごとに .NET が選ぶ名前を 1 つ持つ。値は実行時の
  32 bit の 2 の補数 (uint の 0x80000000 は int.MinValue)。`T?` の値なしは
  空文字列。値が数でない (host の enum が Lua の文字列等で持つ) ときはその値
- List.Sort の結果と比較関数の呼び出し列 (回数・順・引数) は .NET の introsort (dotnet/runtime の ArraySortHelper<T>。比較なしの float は NaN を先頭に寄せてから残りを並べる) と同じで、同値の要素の並びも一致する。
  比較関数が不整合で走査が区間の外に出る位置で、.NET は例外、tcs は fault になる
- 数学関数は全 backend とプラットフォームでビット一致する。Sqrt / Floor /
  Ceil / Abs / Round は IEEE-754 で値が決まる。超越関数 (Sin / Cos / Tan /
  Exp / Log / Atan2 / Pow) は runtime/math の 1 つの実装 (C。Lua は
  tcs_math module、.NET は TinySystem.Math の同じ手順の C# 版) が値を決める。
  digest workload は数学関数を使わない（perf の libm 排除方針と同一）
- Random の合意 PRNG は Lua 5.5 の `math.random` (xoshiro256**、LUA_32BITS
  構成の 32bit 射影と FIGS=24 の float 化) で、C backend は同じ実装を持つ
  (T245)。`Random` は System.Random と同じ形 (T248): `Random.Shared` は
  VM の `math.random` 状態 (`Random.Seed(n)` = `math.randomseed(n)`)、
  `new Random(seed)` は同じアルゴリズムの独立 instance (Lua は 32bit 対の
  pure-Lua 実装、C は GC object `TcsRandom`)。IL は `IlNewObj
  ("TinySystem.Random", [seed?])` と `IlInvoke(r, Next | NextFloat |
  NextSingle | Range, args)`、`IlField(TinySystem.Random, Shared)`。同じ seed
  なら instance と Shared、Lua と C の列が bit 一致する。
  seed 未指定時は両 backend とも起動ごと・instance ごとに異なるが、時刻などから作る 32bit の seed なので予測できる。敵対的な用途 (サーバ権威のロールやシャッフルなど) には向かない。
  dotnet 側 facade は System.Random に委譲するので列は一致しない
  (乱数は dotnet differential の対象外のまま)

## 14. migration metadata（v0 はスキーマのみ。実装は T220）

class ごとに: layout version hash / field 列（名前、IL 型、initializer）/
rename 注釈（`[RenamedFrom]` 相当）/ ユーザーフック（`OnReload` 相当）。
プロトコルの語彙と適用規則は il-design §6（CLOS 型 eager migration）。

## 15. interop 境界

- 境界面 = class の public メンバー（method・field・lowering 済み accessor）
- 外部コードとの相互作用は境界面の呼び出し・読み書きのみが規約内。
  インスタンス表現への直接操作は規約外（backend は表現を自由に選べる）
- release 実行では境界面のうち到達可能な export のみが保証対象（M3 で精密化）

## 付録 A: Lua backend の義務（dev-lowering）

- 0-based → 1-based の index 変換
- idiv / irem 補正（実装済み）+ 除算 overflow / 変換 fault の検出 (§5, §12)
- capture を保存するループ生成 — numeric for は制御変数が捕捉されていない
  場合のみ使用可（T221）
- 式位置制御フローの statement 化（IIFE 廃止。examples 決定 2）
- type test の継承対応 helper（metatable chain 走査。T222）
- f32 shortest round-trip の fmt helper（`tostring` の `%.14g` 系は不適合）
- f32 リテラルは bit 値を正確に表す表記（16 進浮動小数リテラル等）で出力
  （10 進経由の二重丸め禁止 — §6）
- 値型の copy helper（M5 v1 実装済み。連続表現への最適化は perf 実測駆動）
- List / 配列 / string の添字の範囲外の検出 (§12)。Lua の table は範囲外の key を nil で読み、範囲外の key にも書けるので、要素の読み書きごとに範囲を見る。要素は nil を持たない (TCS1003) ので、範囲内の key は必ず値を持つ。要素が nil になりうる配列 (参照型 / T? / 型引数の要素の `new T[n]`) は穴を持つので検査しない
- 型引数の既定値 (`default(T)`、generic な型の initializer の無い field) は、呼び出し側が型引数ごとの既定値を隠れ引数で渡す (Lua は型を消す)。generic method は自分の型引数、generic な class / record / struct は `new` (struct は zero 値の `new` と `ctor`) と static member が型の型引数を受け、instance は `new` が self に持つ。generic method の method group は既定値を足して呼ぶ closure にする。generic struct の `__copy` は型引数の値を写す関数も受け、copy 地点が型引数の struct の `__copy` を渡す。C backend は型を特殊化するので持たない
- `LUA_32BITS` ビルドでの実行（M4）

## 付録 B: C backend の義務（release-lowering、`../luo`）

- signed wrap の保証（`-fwrapv` 相当）
- strict（既定）: `-ffp-contract=off`、fast-math 系最適化の禁止、
  excess precision の排除（`FLT_EVAL_METHOD=0` 相当）(§6)
- relaxed-fp（opt-in）: `-ffp-contract=fast` と再結合を許可。
  `-ffinite-math-only` / `-ffast-math` 一括指定は不可（NaN/Inf 意味論を
  壊すため）(§6)
- bounds / null / 除算 check の生成。省略は観測等価を証明できる場合のみ
- object model（class 表現・struct 配列表現）は perf 実測の決着 (IL-native) に従う
- fault の trap 実装（§12 の決定性を満たすこと）

## 付録 C: 未決事項

- relaxed-fp (§6) の粒度を module 単位まで細分するか（mixed-mode ABI と
  絡むため v0 は出荷ビルド単位のみ）
- シリアライズ形式（M2 / T217）
- Lua 側 struct 配列の連続表現最適化（perf 実測駆動。release 側 class 表現は native に決着済み）
- mixed-mode の module 境界 ABI（il-design §5）
