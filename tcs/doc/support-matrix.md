# TinyC# Support Matrix

C# 14 言語機能および .NET System 名前空間の棚卸し表。

この表はフル C# / フル BCL 対応の TODO リストではない。
TinyC# は C# の開発体験を活かしつつ、Go のようにコンパクトで予測しやすいサブセットを目指す。
今後は対応状況に加えて Core / Useful / Out of scope の優先度を付け、Core だけを高優先度の実装対象にする。

## 凡例

- **Y** = 対応済み
- **P** = 部分対応
- **-** = 未対応
- **N/A** = 対象外 (意図的にスコープ外)
- **T** = トランスパイラ側の変換
- **R** = ランタイム (tinysystem.lua) の関数
- **Core** = モダン C# 開発体験に必要な最小ライン
- **Useful** = サンプルや実用上必要になったら追加
- **Out** = コンパクトさ維持のため原則スコープ外

---

## Compact C# Baseline

TinyC# の実装判断は「C# 14 の全機能対応」ではなく、次の baseline に従う。
詳細表の状態は実装状況、優先度は今後の投資判断を表す。

### 言語機能の baseline

| 領域 | 優先度 | 方針 |
|------|:------:|------|
| 基本型・nullable・リテラル | **Core** | 日常的な C# の型チェックと値表現を保つ |
| class / enum / interface / record class | **Core** | editor 補完、型チェック、データ表現に必要 |
| class / record の継承 | **Out** | Go と同じく、多態は interface の実装と型による分岐 (`switch (e) { case Hit h: ... }`)、組み合わせは field で書く (q.md Q15)。基底 class と、継承で意味を持つ member (`virtual` / `abstract` / `override` / `sealed` / `protected` / `new` による隠蔽 / `base`) は build を止める TCS1001 |
| struct / record struct | **Core** | 値セマンティクス対応済み (T219b)。instance member / 値等価 / readonly copy 省略。C backend は素の C struct |
| if / switch / loop / lambda / pattern | **Core** | ゲームロジックと小さな業務ロジックの表現力として必要 |
| 演算子オーバーロード (算術) | **Core** | ベクトル/行列など math 型の表現に必要。二項 `+ - * / %` と単項 `-` だけを class table 上の static 関数へ写像し (呼び出し箇所で overload を静的解決)、変換演算子と `==`/`!=`/比較系は対象外 |
| LINQ メソッドチェーン | **Core** | `TinySystem.Linq` の `List<T>` の拡張メソッド (`Where`/`Select`/`Any`/`All`/`Count`/`FirstOrDefault`/`OrderBy`/集計) を即時評価で提供 (§19) |
| ユーザー定義ジェネリクス | **Core** | class / record / struct / method と型制約。Lua は型消去、C は閉じた型へ特殊化 (§7、q.md Q20)。TinySystem.Linq もこれで書く |
| reflection / dynamic / expression tree | **Out** | Lua 5.5 backend と compact baseline に合わない |
| async / Task / threading | **Out** | ホットリロード可能なシングルスレッド Lua 実行を基本にする |
| unsafe / Span / pointer | **Out** | 組み込み backend の低レベル最適化フェーズまで扱わない |

### 標準ライブラリの baseline

| 領域 | 優先度 | 方針 |
|------|:------:|------|
| `String`, `Math`, `Random`, `Console` | **Core** | 小さなスクリプトで頻出する API を厳選して提供 |
| `List<T>`, `Dictionary<K,V>` | **Core** | sequence/hash table として明確に対応 |
| LINQ `Enumerable` | **Core** | `System.Linq` の代わりに `TinySystem.Linq` (§19)。クエリ構文と遅延評価は非目標 |
| `System.Numerics` 風のベクトル型 | **Useful** | engine / math facade として必要になった時点で追加 |
| `DateTime` / `TimeSpan` / 軽量 IO | **Useful** | サンプルが要求したら最小 API だけ追加 |
| `System.IO` 全面対応 | **Out** | Lua `io` や engine API で代替。BCL 再実装はしない |
| `System.Text.Encoding` / Regex | **Out** | runtime 規模が膨らむため標準 Core には含めない |

---

## サマリ

### 言語機能

| カテゴリ | 優先度 | Y | P | - | N/A | 計 |
|---------|:------:|---|---|---|-----|---|
| 組み込み型 | **Core** | 7 | 4 | 11 | 6 | 28 |
| ユーザー定義型 | **Core** | 2 | 2 | 3 | 1 | 8 |
| 名前空間・using | **Core** | 3 | 0 | 3 | 1 | 7 |
| 型宣言 | **Core** | 3 | 1 | 5 | 0 | 9 |
| メンバー宣言 | **Core** | 10 | 3 | 8 | 1 | 22 |
| ローカル宣言 | **Core** | 4 | 1 | 5 | 1 | 11 |
| 文 | **Core** | 12 | 0 | 6 | 4 | 22 |
| リテラル | **Core** | 12 | 0 | 0 | 1 | 13 |
| 演算子 | **Core** | 24 | 1 | 8 | 5 | 38 |
| 高度な式 | **Core** | 5 | 2 | 9 | 3 | 19 |
| パターンマッチング | **Core** | 9 | 0 | 8 | 0 | 17 |
| 修飾子 | **Useful** | 7 | 1 | 11 | 11 | 30 |
| ジェネリクス | **Core** | 5 | 1 | 1 | 0 | 7 |
| 継承 | **Out** | 1 | 0 | 0 | 5 | 6 |
| **小計** | | **100** | **16** | **82** | **39** | **237** |

### 標準ライブラリ

| 名前空間 | 優先度 | Y | P | - | N/A | 計 |
|---------|:------:|---|---|---|-----|---|
| System 基本型 | **Core/Useful** | 4 | 2 | 19 | 3 | 28 |
| String メンバー | **Core** | 15 | 0 | 17 | 0 | 32 |
| Math メンバー | **Core** | 17 | 0 | 15 | 0 | 32 |
| Random メンバー | **Core** | 3 | 0 | 6 | 0 | 9 |
| List\<T\> メンバー | **Core** | 14 | 0 | 17 | 0 | 31 |
| Dictionary\<K,V\> メンバー | **Core** | 10 | 0 | 3 | 0 | 13 |
| LINQ (TinySystem.Linq) | **Core/Useful** | 9 | 0 | 0 | 0 | 9 |
| Collections.Generic 型 | **Core/Useful** | 2 | 0 | 8 | 5 | 15 |
| System.Text | **Out** | 0 | 0 | 3 | 0 | 3 |
| System.IO | **Out** | 0 | 0 | 7 | 0 | 7 |
| System.Threading.Tasks | **Out** | 0 | 0 | 0 | 4 | 4 |
| System.Numerics | **Useful** | 0 | 0 | 6 | 0 | 6 |
| **小計** | | **80** | **2** | **132** | **12** | **226** |

---

# Part I: 言語機能

---

## 1. 型 (Types)

### 1.1 組み込み値型

| 型 | 状態 | Lua マッピング | 備考 |
|----|:----:|--------------|------|
| `int` (Int32) | **Y** | number | |
| `long` (Int64) | **Y** | number (integer) | |
| `float` (Single) | **Y** | number, suffix `f` 除去 | |
| `double` (Double) | **N/A** | | 数値は i32 / f32 だけ (il-spec §3)。`double` の型と、suffix の無い実数リテラル (`1.5`、`1e3`、`1d`) は TCS1001 `DoubleType` / `DoubleLiteral`。実数は `f` suffix の float で書く |
| `bool` (Boolean) | **Y** | boolean | 文字列化 (`ToString()`・連結・補間・`Console.WriteLine`・`string.Join` の要素、`bool?` と object に入れた値も) は .NET と同じ `True` / `False` (`__tcs_bstr`) |
| `decimal` (Decimal) | **-** | | |
| `char` (Char) | **Y** | 整数 code unit (byte)。literal は ASCII のみ (非 ASCII は TCS1001 `NonAsciiCharLiteral`、string literal で書く)。算術 / 比較 / switch / pattern は int と同じ、文字列化 (連結・補間・`ToString`・`WriteLine`) は 1 byte の string。`Char.*` は §15b |
| `byte` (Byte) | **-** | | |
| `sbyte` (SByte) | **-** | | |
| `short` (Int16) | **-** | | |
| `ushort` (UInt16) | **-** | | |
| `uint` (UInt32) | **P** | number | 外部 API facade 用 |
| `ulong` (UInt64) | **-** | | |
| `nint` (IntPtr) | **P** | number | 外部 API facade 用 |
| `nuint` (UIntPtr) | **-** | | |

### 1.2 組み込み参照型

| 型 | 状態 | 備考 |
|----|:----:|------|
| `string` (String) | **Y** | Lua string |
| `object` (Object) | **-** | |
| `dynamic` | **N/A** | |

### 1.3 特殊型

| 型 | 状態 | 備考 |
|----|:----:|------|
| `void` | **Y** | 戻り値型として |
| Nullable 値型 (`int?`) | **Y** | `null`/値/HasValue/Value/GetValueOrDefault/`??`/`??=`/`?.` (receiver が `S?` の member / method、結果の `T?` wrap)/null 比較/lifted 演算子 (算術・bit・比較・`bool?` の三値 `&` `\|`)。IL は明示ノード (il-spec §3)、C backend は `{ has, v }`。`.Value` の値なしは fault、文字列化は空文字列 |
| Nullable 参照型 (`string?`) | **N/A** | Lua は常に nil 可能 |
| タプル `(int, string)` | **-** | |
| 配列 `int[]` | **P** | 初期化子、index、Length。`new T[n]` の要素は値型 (数値 / bool / enum / char / struct) なら default で埋める。nil が default になる要素型 (参照型 / `T?` / 型パラメータ (`where T : struct` も含む)、`new int[n][]` を含む) の `new T[n]` と、初期化子・代入・`List<T>.Add` の null / 型パラメータの `default` 要素は TCS1003 (Lua の sequence に nil 穴は持てず Length / foreach が崩れる。`new T[0]` は可)。List\<T\> を推奨 |
| 匿名型 `new { }` | **-** | |
| `Span<T>` / `ReadOnlySpan<T>` | **N/A** | |
| ポインタ型 `int*` | **N/A** | |
| 関数ポインタ `delegate*` | **N/A** | |
| `ref struct` | **N/A** | |

### 1.4 ユーザー定義型

| 型 | 状態 | Lua マッピング | 備考 |
|----|:----:|--------------|------|
| `class` | **Y** | table + metatable | 基底 class は持てない (継承は対象外、§8)。interface の実装は可。object の member (ToString / Equals / GetHashCode) の override も TCS1001 (Lua は List.Contains を同一性に写し、override を呼ばない)。class の値の文字列化 (`ToString()`・連結・補間・`Console.WriteLine`・`string.Join` の要素) は TCS1001 `UnsupportedToString(T)` (C# は型名を書く) |
| `struct` | **Y** | plain table (metatable なし) + copy 地点で型別 `__copy` | 値意味論 (il-spec §10)。instance member は静的自由関数 (C backend は `Tcs_S *self` のアドレス渡し)。List.Contains / IndexOf / Remove は memberwise 等価。copy constructor `new List<T>(source)` と LINQ の lambda parameter、static method の method group の parameter、式本体 (`=>`) の lambda と method の戻り値、`?.` 呼び出しの引数、class の user operator の operand、式位置の代入の値、switch 式の arm の値、struct の `T?` の値 (値があれば中の struct) も値 copy。static member / operator / override はサブセット外 (TCS1001。static field と static auto property は Lua で nil を読むので build を止める error、const は使用箇所で値に畳むので可)。object / interface への変換 (boxing。`object o = s`、`Console.WriteLine(s)`、`(object)s`、struct の `T?` も) は TCS1001 `StructBoxing` (Lua の struct は型を持たない table で `is` と interface 経由の呼び出しが効かず、C は受け付けない)。`foreach (IShape s in structs)` も。interface の制約を持つ型引数 (`where T : IShape`) に struct を入れるのも TCS1001 `StructInterfaceConstraint(S)` (Lua の generic なコードは interface の method を method の呼び出しにする)。data を運ぶ struct は `readonly record struct` を既定にする: Lua は copy 地点ごとに table を作るので、readonly でない struct は copy の分だけ遅い (2000 フレーム × 1000 要素の Where で readonly record struct 0.089 s、readonly なし 0.715 s。C は 0.019 / 0.023 s。q.md Q18) |
| `record` / `record class` | **Y** | table + metatable (`__eq`)。IL 契約に IsRecord で収載、C backend は型ごとの構造等価関数と実行時 layout の with copy | positional record。record の継承 (`record D(...) : B(...)`) は対象外 (§8)。==/!= は実行時型の一致と、各 positional field の値等価 (struct field は memberwise、struct の `T?` field は値の有無と中の値、float / `float?` field と generic な record の型 parameter の field は float.Equals で NaN 同士も等しい。C# の合成 Equals と同じ。型 parameter に struct を入れた field は table の同一性で比べる)。static field (const / static readonly 込み) と static auto property は class と同じく pre-zero して型をまたぐ初期化順で初期化する。Equals / GetHashCode 呼びと override は対象外 (override は TCS1001)。文字列化は TCS1001 `UnsupportedToString(T)` (C# は member の一覧を書く)。C# の `(T)x` downcast は IlCast で C 側 fault 対象 |
| `record struct` | **Y** | plain table + positional ctor + 合成 `op_Equality` | 値等価 ==/!= (field ごとの比較は record class と同じく EqualityComparer\<T\>.Default、struct の List 検索の ValueType.Equals も同じ) と with 式。readonly (record) struct は copy 全省略。IL 契約 (IlStructInfo) と C backend、hot reload の migration も struct と同じ |
| `interface` | **P** | 出力なし | Roslyn 型チェックのみ |
| `enum` | **Y** | 定数テーブル | initializer 無しの field / auto property / `default(E)` の既定値は member 値に依らず 0。member の値は基底型 (byte / sbyte / short / ushort / int / uint) の定数を実行時の 32 bit の 2 の補数に写す (uint の 0x80000000 は int.MinValue)。文字列化 (`ToString()`・文字列連結・補間・`Console.WriteLine`、`E?` も) は Enum.ToString と同じく member 名、`[Flags]` は名前の `", "` 連結、表せない値は数値 (`String.FormatEnum`、名前の表は文字列化する地点にだけ出す。`--ref` の enum は stub の名前、host が数でない Lua の値で持つ enum (lub の `[LubLuaString]` 等) はその値)。書式は `ToString("G" / "F" / "D")` と補間の `{e:G}` / `{e:F}` (名前 / 名前の組み合わせ) と `{e:D}` (数値)。`ToString` の他の書式 (`"X"` 等、定数でない書式) は TCS1002、補間の他の書式は数値の書式に写す。generic の `T` に入れた enum (`__tcs_etd` が名前の表から作る型引数の記述子が名前を引く) と `string.Join` に渡した enum の List / 配列 (`String.JoinEnum`) も member 名。`object` に入れた enum は数値のまま (実行時の値が enum の型を持たない) |
| `delegate` 型定義 | **N/A** | | Action/Func で代替 |
| ネストされた型 | **-** | | |

---

## 2. 宣言 (Declarations)

### 2.1 名前空間・using

| 機能 | 状態 | 備考 |
|------|:----:|------|
| `namespace N { }` (ブロック) | **Y** | Lua ではフラット化。namespace の中の型は namespace 修飾名 (`A.Color` → `A_Color`、`Game.Gfx.Color` → `Game_Gfx_Color`) の global、namespace に属さない型は simple 名の global。型の Lua 名は他の型の有無に左右されない。`--ref` 型は namespace と入れ子の型名を全小文字で結合したパス (`Lub.Gfx` → `lub.gfx`)、enum はその型か namespace の下に平らに置く |
| `namespace N;` (ファイルスコープ, C# 10) | **Y** | |
| `using System;` | **Y** | Roslyn 解決 |
| `using static` (C# 6) | **-** | |
| `using Alias = Type` | **-** | |
| `global using` (C# 10) | **-** | |
| `extern alias` | **N/A** | |

### 2.2 型宣言

| 機能 | 状態 | 備考 |
|------|:----:|------|
| `class` 宣言 | **Y** | |
| `struct` 宣言 | **Y** | field / instance method / property / 単一のパラメータ付き ctor |
| `record` 宣言 (C# 9) | **P** | positional record |
| `record struct` 宣言 (C# 10) | **Y** | readonly 含む。Equals/GetHashCode 呼びは対象外、文字列化は TCS1001 `UnsupportedToString(T)` |
| `interface` 宣言 | **Y** | 出力なし |
| `enum` 宣言 | **Y** | |
| `delegate` 宣言 | **-** | |
| `partial` 型 (C# 2) | **-** | TCS1001 |
| `file` 型 (C# 11) | **-** | |

### 2.3 メンバー宣言

| 機能 | 状態 | Lua マッピング | 備考 |
|------|:----:|--------------|------|
| フィールド | **Y** | `self.Field` | initializer なしは型別 default |
| フィールド初期化子 | **Y** | コンストラクタ内 | |
| static フィールド / プロパティ初期化子 | **Y** | 全型の定義の後に型単位で実行 | 初期化子 (と、そこから呼ぶ method・constructor・accessor・interface 実装の本文) が読み書きする他の型の static を先に初期化する (ファイル順・宣言順に依存しない)。lambda の本文・method group の先 (delegate を作るだけで実行しない) と delegate 値の呼び出しの先は辿らない。依存は制御フローを見ない過大評価 (分岐の両側、interface 呼び出しは全実装) なので、実際には起きない循環ができることがある。循環は宣言順で先の型を先に触ったものとして解き、相手は初期化中の型の default を読む (実際の循環なら、宣言順に触ったときの C# と同じ。偽の循環では C# / C backend と違い得る)。Lua は Main より前に全型を初期化するので、初期化子の副作用 (log 等) の順は初回アクセス時に初期化する C# / C backend と違い得る |
| `const` フィールド | **Y** | `ClassName.CONST` | |
| オートプロパティ (`{ get; set; }`) | **Y** | フィールド直接 | initializer なしは型別 default |
| プロパティ初期化子 | **Y** | | |
| カスタム getter/setter | **Y** | `get_`/`set_` メソッド | |
| 式本体メンバー (`=>`, C# 6) | **Y** | | |
| インスタンスメソッド | **Y** | `obj:Method()` | |
| 静的メソッド | **Y** | `Class.Method()` | |
| メソッドの overload | **Y** | 名前に引数の型を付ける (`Sum__List_T__Func_T_int`、§28)。interface の method を実装する method は interface の method の名前 (interface 経由の呼び出しと同じ key) | 別の interface から同じ名前の method を実装して名前がぶつかる場合と、`--ref` の型の overload は TCS1001 `MethodOverload` |
| コンストラクタ | **Y** | `Class.new(args)` | |
| 静的コンストラクタ | **-** | | |
| デストラクタ / ファイナライザ | **N/A** | | |
| イベント | **-** | | |
| インデクサ (`this[int]`) | **-** | | |
| 演算子オーバーロード | **P** | static 関数 (`V.__add` / `V.__sub` / `V.__mul` / `V.__div` / `V.__mod` / `V.__unm`、同じ operator の複数 overload は `V.__mul_1` `V.__mul_2` …) | 二項 `+ - * / %` と単項 `-`。呼び出し箇所が Roslyn の選んだ overload を直呼びする (metatable 経由の実行時振り分けなし。基底 class の operator は派生 class の値でも動く)。`==`/`!=`/比較系は TCS1001 (record の `__eq` のみ) |
| 暗黙/明示変換演算子 | **-** | | TCS1001 |
| ローカル関数 (C# 7) | **-** | | unsupported 診断あり |
| 静的ローカル関数 (C# 8) | **-** | | unsupported 診断あり |
| 拡張メソッド (C# 3) | **P** | static method 呼び出しへ展開 | LINQ (TinySystem.Linq) も user の拡張メソッドと同じ |
| 拡張メンバー (C# 14) | **-** | | |
| primary constructor (C# 12) | **-** | | |
| デフォルト引数値 | **P** | Roslyn パース | 省略した末尾の引数は、呼び出しが静的に束縛した宣言 (interface) の既定値を呼び出し側で渡す。callee 冒頭でも nil を既定値で補うので、明示 null と省略は区別しない |

### 2.4 ローカル宣言

| 機能 | 状態 | Lua マッピング | 備考 |
|------|:----:|--------------|------|
| `var x = expr;` | **Y** | `local x = expr` | |
| `int x = expr;` | **Y** | `local x = expr` | |
| `ref` ローカル (C# 7) | **N/A** | | |
| ローカル定数 `const` | **-** | | |
| 分解宣言 `var (a, b) = ...` (C# 7) | **P** | `__tcs_dec` へ一回評価して展開 | record positional property 限定。既存変数への分解代入 `(a, b) = rhs` も対応 |
| 破棄 `_ = expr` (C# 7) | **Y** | `local _ = expr` | 評価して捨てる |
| トップレベル文 (C# 9) | **Y** | Lua chunk | 型定義を先に出力してから実行 |
| using 宣言 `using var` (C# 8) | **-** | | unsupported 診断あり |
| `scoped` ローカル (C# 11) | **-** | | |
| パターン変数 `if (x is int i)` (C# 7) | **Y** | statement 前 `local` 宣言 + IIFE 内代入 | 式文脈 (ternary / 複合条件 / lambda) も束縛 |
| `required` メンバー (C# 11) | **-** | | |

---

## 3. 文 (Statements)

| 文 | 状態 | Lua マッピング | 備考 |
|---|:----:|--------------|------|
| 変数宣言文 | **Y** | `local x = ...` | |
| 式文 (メソッド呼び出し等) | **Y** | | |
| `if` / `else if` / `else` | **Y** | `if`/`elseif`/`else`/`end` | |
| `switch` 文 | **Y** | if-elseif チェーン | 定数に加え関係 (`case > 5:`) / or / 型 / `when` パターンラベル対応 |
| `while` ループ | **Y** | `while cond do end` | |
| `do-while` ループ | **Y** | `repeat ... until not(cond)` | |
| `for` ループ (C 形式) | **Y** | `for i=s,e do` / while 展開 | 一般 incrementor 対応 |
| `foreach` ループ | **Y** | `ipairs`/`pairs` | |
| `break` | **Y** | `break` | |
| `continue` | **Y** | `goto _continue_N` | Lua 5.5 goto |
| `return` | **Y** | `return expr` | |
| `throw` | **-** | | unsupported 診断あり |
| `try` / `catch` / `finally` | **-** | | unsupported 診断あり |
| `using` 文 (リソース破棄) | **-** | | unsupported 診断あり |
| `lock` | **-** | | TCS1001 |
| `yield return` / `yield break` (C# 2) | **-** | | |
| `goto` / ラベル | **-** | | |
| `checked` / `unchecked` (文) | **-** | | TCS1001 `CheckedStatement` |
| `fixed` | **N/A** | | |
| `unsafe` | **N/A** | | |
| `await foreach` (C# 8) | **-** | | |
| 空文 (`;`) | **Y** | | Roslyn がパース |

---

## 4. 式 (Expressions)

### 4.1 リテラル

| リテラル | 状態 | Lua 出力 | 備考 |
|---------|:----:|---------|------|
| 整数 (`42`) | **Y** | そのまま | |
| 浮動小数点 (`3.14`, `1.0f`, `2.5d`) | **Y** | suffix 除去 | |
| `bool` (`true`/`false`) | **Y** | そのまま | |
| `null` | **Y** | `nil` | |
| 文字列 (`"hello"`) | **Y** | そのまま | |
| 補間文字列 (`$"...{x}..."`) | **Y** | `tostring` + `..` | object の hole は `__tcs_nstr` (値が float なら .NET と同じ文字列、null は `""`。C backend は object の補間を受けない)。float (と `float?`) の書式 `G` / `E` / `F` / `N` / `R`、int (と `int?`) の書式 `G` / `E` / `F` / `N` / `D` / `X` / `R` (小文字・精度つきも) は .NET の `ToString(format)` と同じ文字列 (`String.FormatFloat` / `String.FormatInt`、il-spec §13。`T?` の null は空文字列)。他の書式は printf の書式に写す。raw 補間 (`$$"""..."""`) の `$` の個数より短い brace の連続は文字のまま |
| 文字 (`'A'`) | **Y** | 1文字 string | |
| 2進数 (`0b1010`, C# 7) | **Y** | 10進数へ変換 | |
| 16進数 (`0xFF`) | **Y** | `_` 除去して出力 | |
| 桁区切り (`1_000_000`, C# 7) | **Y** | `_` 除去 | |
| verbatim 文字列 (`@"..."`) | **Y** | ValueText を Lua string escape | |
| raw 文字列 (`"""..."""`, C# 11) | **Y** | ValueText を Lua string escape | |
| UTF-8 文字列 (`"..."u8`, C# 11) | **N/A** | | |

### 4.2 演算子

| 演算子 | 状態 | Lua 出力 | 備考 |
|--------|:----:|---------|------|
| `+` `-` `*` `/` `%` (算術) | **Y** | `+ - *` はそのまま。整数 `/` `%` は `__tcs_idiv`/`__tcs_irem` (C# の 0 方向 truncation)、float `%` は `math.fmod` | |
| `+` (文字列連結) | **Y** | `..` | 型で自動判定。operand は float → `__tcs_fstr`、bool / bool? → `tostring` (null は `""`)、object → `__tcs_nstr` (値が float なら `__tcs_fstr` と同じ、null は `""`。C backend は object の連結を受けない)、null になり得る string → `or ""`。`+=` の右辺も同じ変換 |
| `==` `!=` | **Y** | `==` `~=` | |
| `<` `<=` `>` `>=` | **Y** | そのまま | |
| `&&` `\|\|` | **Y** | `and` `or` | |
| `!` (論理否定) | **Y** | `not` | |
| `-x` (単項マイナス) | **Y** | `-x` | |
| `++x` `x++` (インクリメント) | **Y** | 文位置は `x = x + 1`、式位置は IIFE (前置は更新後、後置は更新前の値) | custom property / 副作用 (getter 含む) receiver は 1 回評価。右側の ++ / 代入が書き換える local を左 operand / 代入先の添字が読むときは左を temp に退避 (C# の左→右評価) |
| `--x` `x--` (デクリメント) | **Y** | 同上 | |
| `=` (代入) | **Y** | そのまま | 受け手 / 添字は右辺より先に 1 回評価し、式位置の値は代入した値 (右辺が受け手を差し替えても読み直さない)。右辺が struct の格納先 (class の field、配列要素、static field) を差し替え得るときは、値を temp に置いてから格納先を辿り直して書く (`+=` 等 / `??=` も同じ) |
| `+=` `-=` `*=` `/=` `%=` `&=` `\|=` `^=` `<<=` `>>=` | **Y** | 展開 `x = x op y` | bool への `&=` `\|=` `^=` は未対応 |
| `? :` (三項) | **Y** | temp local + if 文 (式の途中・入れ子・条件・lambda 本体も。前置文を置けない IIFE 内 / field initializer のみ IIFE) | falsy 安全、C# の評価順を保つ |
| `??` (null 合体) | **Y** | `or` (bool? のみ nil 判定 IIFE — `or` だと false が fallback するため)。右辺が呼び出しを含むときは temp + `if nil` | 右辺は nil のときだけ評価 |
| `?.` (null 条件アクセス, C# 6) | **Y** | IIFE nil チェック | String/List/Dict mapping 対応。連鎖 (`x?.M().N()` / `x?.P.Q` / `x?[i].M()`) は一番内側を receiver に対する呼び出し / member にする。delegate の `f?.Invoke(...)` は null でなければ delegate 値を呼ぶ (戻り値は `T?` に wrap) |
| `(T)x` (キャスト) | **Y** | 数値変換は il-spec §5 / §6 の契約: `(int)f` は `__tcs_trunc` で 0 方向 truncation、整数 (int / uint / short / byte / char など) → float は `(x * 1.0)` で f32 へ丸める (暗黙変換の代入・引数・return・比較の昇格も同じ。定数は float literal に畳み、組み込みの float 算術の operand は Lua / C の演算自体が変換するので省く。この前提のため float の既定値と runtime が返す float は Lua でも float)。参照型・bool・`T?` への cast は透過 (型消去) | C は `(float)` / `tcs_trunc_f32` |
| `is null` / `is not null` | **Y** | `== nil` / `~= nil` | |
| `is Type` | **Y** | class は `getmetatable() ==`、値型/string は `type()` 判定。designation なしの binary 形も対応 | |
| `new T(args)` | **Y** | `T.new(args)` | |
| `??=` (null 合体代入, C# 8) | **Y** | nil check + assignment | 式位置の値は代入後の左辺。左辺が `T?` なら値は `T` (C# の型) |
| `?[]` (null 条件インデクサ, C# 6) | **Y** | IIFE nil チェック | List/array は 0→1 indexed |
| `?.Prop = v` (null条件代入, C# 14) | **-** | | |
| `as` (安全キャスト) | **-** | | |
| `typeof(T)` | **-** | | |
| `nameof(x)` (C# 6) | **Y** | 定数文字列 | C# の定数式として両 backend で畳む (T250)。同名ユーザー method は通常 call |
| `default` / `default(T)` (C# 7.1) | **Y** | 型別 default | |
| `sizeof(T)` | **N/A** | | |
| `~` (ビット反転) | **Y** | `~x` | 整数のみ (64bit 幅、下記注記) |
| `<<` `>>` (シフト) | **Y** | `__tcs_shl` / `__tcs_shr` | 整数のみ。C# 意味論 (count は 31 でマスク、`>>` は算術シフト) を helper で与える。`>>>` は未対応 |
| `&` `\|` `^` (ビット演算) | **Y** | `&` `\|` `~` (二項) | 整数と enum のみ。C# `^` は Lua 二項 `~`。bool operand は TCS1001 未対応 (Lua native が boolean を拒否し、and/or は非短絡の C# `&` `\|` と意味論が変わるため) |
| `..` (Range, C# 8) | **-** | | |
| `^x` (Index from end, C# 8) | **-** | | |
| `new T { ... }` (初期化子) | **Y** | List/Dict、class (IIFE + field 代入)、`--ref` 型 (plain table)。ネストした初期化子 (`Child = { A = 1 }` / `Items = { a, b }`) は C# と同じく既存 member への代入 / Add | |
| `new(args)` (ターゲット型, C# 9) | **Y** | 初期化子含む | |
| `x!` (null 許容抑制, C# 8) | **Y** | 型チェック専用、Lua へは透過 | |
| `stackalloc` | **N/A** | | |
| `&x` `*x` `->` (ポインタ) | **N/A** | | |
| `delegate { }` (匿名メソッド, C# 2) | **-** | | |
| `delegate*` (関数ポインタ, C# 9) | **N/A** | | |

ビット演算の幅意味論: 数値基準は LUA_32BITS 構成の Lua (`deps/lua/lua32`、
整数 32bit / float 32bit) で、`~x`、負数を含む `& \| ^`、`<<` の折り返しは
C# `int` と一致する。シフトは `__tcs_shl` / `__tcs_shr` (count を 31 で
マスク、`>>` は算術) で C# 意味論。64bit 整数構成の Lua で動かす場合は
上位 32bit の扱いが C# と異なる (そちらは基準外)。


### 4.3 高度な式

| 式 | 状態 | Lua マッピング | 備考 |
|---|:----:|--------------|------|
| ラムダ (式本体) `x => expr` | **Y** | `function(x) return expr end` | |
| ラムダ (文本体) `(x) => { }` | **Y** | `function(x) ... end` | |
| switch 式 (C# 8) | **Y** | IIFE if-elseif | |
| `this` | **Y** | `self` | |
| `base.Method()` | **N/A** | | TCS1001 `BaseMemberAccess` (継承は対象外、§8) |
| メソッドチェーン (LINQ) | **Y** | ネスト呼び出し | |
| `with` 式 (C# 9) | **P** | shallow copy + field override (struct は入れ子の struct も写す `__copy` の上に override) | record/class table 限定 |
| LINQ クエリ構文 (C# 3) | **-** | | メソッドチェーンで代替。TCS1001 `QueryExpression` |
| メソッドグループ変換 | **P** | `Class.method` (関数の参照) / `__tcs_mg(Class.method, S.__copy)` | static method (拡張 method を含む、型名で修飾してもしなくても) は呼び出しと同じ関数の参照。struct の parameter (readonly struct と `T?` を除く) を持つ method は、Lua はその parameter を copy してから呼ぶ wrapper になる。wrapper は method ごとに 1 つで、同じ method の delegate は `==` で等しい (C# と同じ)。hot reload 後に変換した delegate は新しい method の wrapper になる。instance method の method group は TCS1001 (`InstanceMethodGroup`) |
| `throw` 式 (C# 7) | **-** | | unsupported 診断あり |
| コレクション式 `[1,2,3]` (C# 12) | **-** | | |
| タプル式 `(a, b)` (C# 7) | **-** | | |
| 名前付き引数 `F(x: 1)` (C# 4) | **-** | | TCS1001 `NamedArgument`。位置渡しに落ちて別の意味になるため build を止める error (§24) |
| `await` 式 (C# 5) | **N/A** | | |
| 式ツリー `Expression<>` (C# 3) | **N/A** | | |
| 静的ラムダ `static =>` (C# 9) | **-** | | |
| ラムダのデフォルト引数 (C# 12) | **-** | | |
| ラムダの戻り値型 (C# 10) | **-** | | |
| ラムダに `params` (C# 12) | **-** | | |
| `checked(expr)` / `unchecked(expr)` | **P** | checked な i32 の `+ - *` と単項 `-` は overflow で fault する helper (`__tcs_cadd` 等、C は `tcs_checked_add` 等) | 除算と float → int の変換は checked でなくても範囲外を fault にするので同じ。それ以外の checked な演算 (明示の整数変換、`++` / `--`、複合代入、int 以外の型、lifted) は TCS1001 `CheckedOperation` |

---

## 5. パターンマッチング

| パターン | C# ver | 状態 | Lua マッピング | 備考 |
|---------|--------|:----:|--------------|------|
| 定数パターン `is 42`, `is null` | 7 | **Y** | `== 42`, `== nil` | |
| 型パターン `is MyClass` | 7 | **Y** | class は `getmetatable() ==`、値型/string は `type()` 判定 | struct `S` / `S?` の入力を同じ struct 型で照合するのは値の有無だけ。`object` に入れた `List<T>` / 配列 / `Dictionary<K,V>` への型 test は Lua では TCS1001 (table は構造型の tag を持たない。C backend は構造型 id で判定) |
| `not` パターン `is not null` | 9 | **Y** | `~= nil` | |
| 破棄パターン `_` | 7 | **Y** | switch 式の default | `_ when 条件` は条件付きの arm |
| 宣言パターン `is int i` | 7 | **Y** | 型判定 + local 束縛 | if / ternary / 複合条件 / lambda / switch で束縛。struct は入力 (`S?` は中の値) の copy を束縛 |
| `var` パターン `is var v` | 7 | **-** | | |
| プロパティパターン `is { Name: "x" }` | 8 | **Y** | null 判定 + property 比較展開 | 型を書かない `{ }` も null を除く。`T?` は値の有無を見て中の値の member を読む (`p is Pt { X: 2 }` のように型を書いたときも同じ) |
| 位置パターン `is (1, 2)` | 8 | **-** | | |
| タプルパターン `(a, b) is (1, 2)` | 8 | **-** | | |
| 関係パターン `is > 0` | 9 | **Y** | 比較式展開 | |
| `and` パターン `is > 0 and < 100` | 9 | **Y** | and 結合 | 入力が呼び出しを含む式なら temp に 1 回評価してから照合 (`or` / 複数条件の property パターンも同じ) |
| `or` パターン `is 1 or 2` | 9 | **Y** | or 結合 | |
| 括弧パターン `is not (A or B)` | 9 | **-** | | |
| 拡張プロパティパターン `{ A.B: v }` | 10 | **-** | | |
| リストパターン `[1, 2, ..]` | 11 | **-** | | |
| スライスパターン `[.., last]` | 11 | **-** | | |
| Span パターン | 11 | **-** | | |

---

## 6. 修飾子 (Modifiers)

### 6.1 アクセス修飾子

| 修飾子 | 状態 | 備考 |
|--------|:----:|------|
| `public` | **Y** | Roslyn 解決、Lua は全て公開 |
| `private` | **Y** | Roslyn 解決、Lua 区別なし |
| `protected` | **N/A** | TCS1001 `ProtectedMember` (継承は対象外、§8) |
| `internal` | **Y** | Roslyn 解決、Lua 区別なし |
| `protected internal` | **N/A** | TCS1001 `ProtectedMember` |
| `private protected` (C# 7.2) | **N/A** | TCS1001 `ProtectedMember` |
| `file` (C# 11) | **-** | |

### 6.2 型・メンバー修飾子

| 修飾子 | 状態 | 備考 |
|--------|:----:|------|
| `static` | **Y** | static メソッド/フィールド |
| `abstract` | **N/A** | TCS1001 `AbstractClass` / `AbstractMember` (継承は対象外、§8) |
| `virtual` | **N/A** | TCS1001 `VirtualMember` |
| `override` | **N/A** | TCS1001 `OverrideMember`。object の member (ToString / Equals / GetHashCode) の override も (Lua は override を呼ばない) |
| `sealed` | **P** | `sealed class` は可 (意味は変わらない)。member の `sealed` は TCS1001 `SealedMember` |
| `new` (メンバー隠蔽) | **N/A** | TCS1001 `NewMemberHiding` |
| `readonly` | **Y** | field (Roslyn が代入を検査、Lua は通常の field)、`readonly struct` / `readonly record struct` (copy 省略) |
| `const` | **Y** | 使用箇所で値に畳む (§2.3) |
| `volatile` | **N/A** | |
| `extern` | **N/A** | |
| `async` (C# 5) | **N/A** | |
| `partial` (C# 2) | **-** | 型宣言はTCS1001 |
| `required` (C# 11) | **-** | |
| `unsafe` | **N/A** | |

### 6.3 引数修飾子

| 修飾子 | 状態 | 備考 |
|--------|:----:|------|
| `ref` | **-** | ユーザー定義メソッドの宣言は TCS1001 (`RefParameter`) |
| `out` | **-** | ユーザー定義メソッドの宣言は TCS1001 (`OutParameter`)。`--ref` host メソッドの out multi-return のみ対応 |
| `in` (C# 7.2) | **-** | |
| `ref readonly` (C# 12) | **-** | |
| `params` | **-** | |
| `this` (拡張メソッド) | **Y** | user 定義の拡張メソッドは静的呼び出し (receiver が第 1 引数)。拡張の形 `x.M()`、static の形 `Ext.M(x)`、`x?.M()` のどれも同じ呼び出し。TinySystem.Linq の static の形 (`Enumerable.Count(l, f)`) も同じ |
| `scoped` (C# 11) | **-** | |

### 6.4 変換修飾子

| 修飾子 | 状態 | 備考 |
|--------|:----:|------|
| `implicit` (暗黙変換) | **-** | |
| `explicit` (明示変換) | **-** | |

---

## 7. ジェネリクス

| 機能 | 状態 | 備考 |
|------|:----:|------|
| 組み込み `List<T>`, `Dictionary<K,V>` | **Y** | 型消去 |
| `Action<T>` / `Func<T,R>` | **P** | 型消去して Lua function |
| ユーザー定義ジェネリック型 (class / record / struct / record struct / interface) | **Y** | Lua は型消去 (型引数の既定値と、struct の型引数の copy は隠れ引数で渡す。可変な struct を入れた型引数は zero 値と copy の作り方 (記述子) を渡し、`default(T)` は使うたびに新しい値、型引数の型の field / 配列の要素への代入は copy。il-spec 付録 A)、C は閉じた型の copy に特殊化 (top level の型だけ) | static field / static auto property は TCS1001 `GenericStaticField` (C# では閉じた型ごとに別の値だが、Lua は型を消すので共有してしまう。const は可)。外から `Box<int>.M()` のように static member を呼ぶ形は対象外 |
| ジェネリックメソッド | **Y** | Lua は型消去、C は閉じた method に特殊化。method group (`G.Id<int>`、型引数を推論させた `G.Id`) も。interface の generic method は、interface と実装する型に同じ名前の閉じた method を作る | |
| 型制約 (`where T : ...`) | **Y** | Roslyn が検査し、interface 制約の member 呼び出しは実装の method を呼ぶ | `IComparable<T>` 経由の比較などは API 側で TCS1002 (§11) |
| 共変性/反変性 (`in`/`out`, C# 4) | **-** | |
| 静的抽象/仮想インターフェースメンバー (C# 11) | **-** | interface の operator 宣言は TCS1001 `InterfaceOperatorDeclaration` (Lua は制約付き generic の演算を実装 class の metamethod に委ねるだけで overload / 継承を解決できない。tcs2c は TCS1001 で停止) |

---

## 8. 継承

class / record の継承は対象外 (q.md Q15)。多態は interface の実装と型による分岐、
組み合わせは field で書く。以下は TCS1001 で build を止める (transpiler は Lua を
書かない、`tcs check` は exit 1、tcs2c も止まる。analyzer は warning)。`--ref` の型 (host の宣言) は診断の対象にしない。

| 機能 | 状態 | Lua マッピング | 備考 |
|------|:----:|--------------|------|
| 基底 class (`class D : B`、`record D(...) : B(...)`) | **N/A** | | TCS1001 `ClassInheritance(B)`。`System.Attribute` などの BCL の class も同じ |
| `base(...)` コンストラクタ | **N/A** | | TCS1001 `BaseConstructorInitializer` |
| `base.Method()` | **N/A** | | TCS1001 `BaseMemberAccess` |
| 多重インターフェース実装 | **Y** | Roslyn 型チェックのみ | class / record / struct の `: IFoo, IBar`。interface 同士の継承も可 |
| 抽象クラス | **N/A** | | TCS1001 `AbstractClass` |
| 共変戻り値型 (C# 9) | **N/A** | | |

---

## 9. プリプロセッサ・属性・その他

| 機能 | 状態 | 備考 |
|------|:----:|------|
| `#if` / `#endif` | **-** | |
| `#define` / `#undef` | **-** | |
| `#region` / `#endregion` | **-** | Roslyn は処理 |
| `#nullable` (C# 8) | **-** | |
| `#pragma` | **-** | |
| `[Attribute]` | **-** | Roslyn はパース、トランスパイラ無視 |
| caller info 属性 (`[CallerArgumentExpression]` / `[CallerMemberName]` / `[CallerLineNumber]` / `[CallerFilePath]`, C# 5 / 10) | **-** | TCS1001 `CallerInfoAttribute(name)`。C# では呼び出し側でコンパイラが引数を埋めるが tcs は再現せず既定値が渡るため、parameter の属性を構文名で診断する (`Attribute` suffix と `System.Runtime.CompilerServices.` 修飾を許容) |
| `///` XML ドキュメント | **N/A** | |

---

## 10. C# バージョン別対応状況

| C# ver | 年 | 主要機能 | 状態 |
|--------|---|---------|:----:|
| 1.0 | 2002 | class, struct, interface, enum, delegate, 制御構文, 例外処理 | **P** |
| 2.0 | 2005 | generics, nullable, iterators, `??`, partial, anonymous methods | **P** |
| 3.0 | 2007 | LINQ, ラムダ, `var`, 拡張メソッド, 初期化子, 匿名型 | **P** |
| 4.0 | 2010 | `dynamic`, named/optional 引数, 共変性/反変性 | **-** |
| 5.0 | 2012 | `async`/`await`, caller info | **N/A** | caller info 属性は TCS1001 `CallerInfoAttribute` (§9) |
| 6.0 | 2015 | `?.`, `$""`, `nameof`, 式本体, `using static` | **P** |
| 7.0 | 2017 | タプル, パターンマッチング, ローカル関数, `out var`, throw 式 | **P** |
| 7.1 | 2017 | `default` リテラル, async Main | **-** |
| 7.2 | 2017 | `readonly struct`, `ref struct`, `in`, `Span` | **-** |
| 7.3 | 2018 | タプル `==`/`!=`, unmanaged 制約 | **-** |
| 8.0 | 2019 | switch 式, NRT, `??=`, using 宣言, Index/Range, デフォルトIF実装 | **P** |
| 9.0 | 2020 | record, `init`, トップレベル文, 関係/論理パターン, target-typed new | **P** |
| 10.0 | 2021 | record struct, global using, ファイルスコープNS, ラムダ自然型 | **P** |
| 11.0 | 2022 | raw 文字列, リストパターン, `required`, `file`, generic math | **-** |
| 12.0 | 2023 | primary constructor, コレクション式, inline array | **-** |
| 13.0 | 2024 | `params` コレクション, `Lock`, partial プロパティ | **-** |
| 14.0 | 2025 | extension members, `field` キーワード, null条件代入 | **-** |

---

## 11. 予約キーワード

### 対応済み

```
bool  break  case  class  continue  default(switch/default)  do  double  else  enum  false
float  for  foreach  if  int  interface  is(部分)  long  namespace
new  null  operator(部分)  private  public  internal  const  readonly  sealed(class のみ)  return  static  string
switch  this  true  var  void  while
```

### 未対応

```
abstract  as  byte  catch  char  checked  decimal
delegate  event  explicit  extern  finally  fixed  goto
implicit  in(foreach以外)  lock  object  out  override  protected
params  ref  sbyte  short  sizeof  stackalloc  struct
throw  try  typeof  uint(部分)  ulong  unchecked  unsafe  ushort
using(宣言)  virtual  volatile  yield
```

### コンテキストキーワード

| 状態 | キーワード |
|:----:|---------|
| **Y** | `and`(パターン), `get`, `not`(パターン), `or`(パターン), `record`(positional), `set`, `value`, `var`, `with` |
| **P** | `when`(switch内のみ), `nint`(外部 API facade 用) |
| **-** | `add`, `alias`, `allows`, `args`, `ascending`, `async`, `await`, `by`, `descending`, `dynamic`, `equals`, `extension`, `field`, `file`, `from`, `global`, `group`, `init`, `into`, `join`, `let`, `managed`, `nameof`, `notnull`, `nuint`, `on`, `orderby`, `partial`, `remove`, `required`, `scoped`, `select`, `unmanaged`, `where`, `yield` |

### 識別子

| 識別子 | 状態 | 備考 |
|--------|:----:|------|
| Lua 5.5 予約語と同名のローカル束縛 (local 変数 / parameter / lambda parameter / foreach 変数 / pattern designation の `local`, `end`, `nil` 等) | **Y** | `local` → `local_` に写す。写した先が同じ member body (lambda 連鎖を含む) に識別子として現れる場合は `_` を足して衝突を避ける (`local_` があれば `local__`)。宣言と全参照 (closure 内含む) を同じ symbol として写す (LuaLocalRenamer) |
| Lua 5.5 予約語と同名の member 宣言 (型 / method / property / field / enum メンバ / record positional parameter の `end`, `repeat`, `until`, `global` 等) | **-** | TCS1001 `LuaKeywordIdentifier(name)` |
| `self` / `__tcs_` prefix と同名の宣言 | **-** | TCS1001 `ReservedIdentifier(name)`。`self` は Lua method receiver、`__tcs_*` は generated temp を壊すため拒否 (ローカル束縛でも写さない) |
| runtime の global と同名の型宣言 (`TinySystem`, `List`, `Dict`, `Math`, `String`) | **-** | TCS1001 `RuntimeGlobalIdentifier(name)`。namespace に属さない型は simple name で global に emit され runtime の table を上書きし、以後の BCL 呼び出しが nil になるため build を止める error (§24)。namespace の中の型は修飾名になり上書きしないが、同じ error のまま。集合は `TinyCsComplianceFacts.ReservedRuntimeGlobals` (prelude の `_G` alias もここから生成)。interface と `Random` (facade は `TinySystem.Random.*` 経由) は対象外 |
| ユーザ型と同名のローカル束縛 (`static int Sum(V a, int V)` 等) | **Y** | 予約語と同じく `V` → `V_` に写す。emit は型を裸の型名で参照する (`V.new(...)` / operator の `V.__add(a, b)` / 型内 static member) ので、写さないと Lua で同名ローカルが型を隠す。写した先がユーザ型名と同じなら更に `_` を足す |
| verbatim 識別子 (`@float`, `@out` 等) | **Y** | ValueText (`@` なし) で emit。`@end` 等 Lua 予約語になるものはローカル束縛なら上記のとおり写し、member なら TCS1001 |

---

# Part II: 標準ライブラリ

---

## 12. System 名前空間 — 型一覧

| 型 | 状態 | 対応内容 | 備考 |
|----|:----:|---------|------|
| `Object` | **P** | `ToString()` → `__tcs_nstr()`。`Equals` / `GetHashCode` / `ReferenceEquals` / `GetType` / `MemberwiseClone` (record と数値と enum の合成 / 継承した member も) は TCS1002 (値の比較は `==`) | 値が float なら .NET と同じ文字列。class / record / struct / List / Dictionary の値は fault (静的型がそれらなら TCS1001 `UnsupportedToString(T)`)。型引数の値も同じ。C backend は object の ToString を受けない |
| `String` | **P** | 後述の個別メンバー表参照 | |
| `Math` | **Y** | 後述の個別メンバー表参照 | `math` ラッパー |
| `MathF` | **-** | | float 版 Math。TCS1002 (System.Math の float overload を使う) |
| `Console` | **Y** | `WriteLine` → `print`、1 引数の `Write` → `io.write` (文字列化は WriteLine と同じ) | object の引数は `__tcs_nstr` (値が float なら .NET と同じ文字列、null は空行。C backend は object の引数を受けない) |
| `int` / `float` の `Parse(string)` | **Y** | `math.tointeger(tonumber(s))` / `__tcs_tofloat(tonumber(s))` (float にする) | 不正な文字列は例外でなく nil |
| `int` / `float` の `TryParse(string, out v)` | **Y** | `Math.TryParseInt` / `Math.TryParseFloat` の (found, value) multi-return (TryGetValue と同形の IIFE) | 失敗時の out は 0。int は NumberStyles.Integer (前後の空白、先頭の符号、10 進数字だけ。小数・指数・16 進・空白だけは失敗) |
| `int` / `float` の `CompareTo(同じ型)` | **Y** | `Math.CompareTo(a, b)` | -1 / 0 / 1。float の NaN は他のどの値より小さく、NaN 同士は 0 (Single.CompareTo と同じ)。それ以外の CompareTo (object overload の `i.CompareTo(5L)`、enum、`IComparable<T>` 経由と generic の `T`、差を返す `char`、`string`) は TCS1002 |
| `Convert` | **-** | | |
| `Environment` | **P** | `GetEnvironmentVariable(s)` → `os.getenv(s)` | 他メンバーは未対応 |
| `Array` | **-** | | List で代替。`Array.Sort` などの member (配列の `Length` 以外) は TCS1002 |
| `Tuple` | **-** | | |
| `ValueTuple` | **-** | | |
| `Nullable<T>` | **Y** | `int?` 等 (Part I 参照) | collection 要素の null は TCS1003 |
| `Enum` (静的メソッド) | **-** | | |
| `Exception` (全サブクラス) | **-** | | try/catch 未対応 |
| `IDisposable` | **-** | | |
| `IComparable<T>` | **-** | | |
| `IEquatable<T>` | **-** | | |
| `ICloneable` | **-** | | |
| `Guid` | **-** | | |
| `DateTime` | **-** | | |
| `TimeSpan` | **-** | | |
| `Random` | **Y** | 後述 | `math.random` |
| `StringComparison` | **-** | | |
| `Type` | **N/A** | | リフレクション |
| `Attribute` | **N/A** | | |
| `GC` | **N/A** | | |
| `Action<>` / `Func<>` / `Predicate<>` | **Y** | 型消去 → Lua function | `f.Invoke(...)` / `f?.Invoke(...)` は `f(...)` と同じ呼び出し |
| `Lazy<T>` | **-** | | |
| `WeakReference<T>` | **-** | | |

---

## 13. String メンバー

| メンバー | 状態 | Lua マッピング | 区分 |
|---------|:----:|--------------|:----:|
| `.Length` | **Y** | `#str` | T |
| `.Contains(s)` | **Y** | `String.Contains(str, s)` | T+R |
| `.Replace(old, new)` | **Y** | `String.Replace(str, old, new)` (`old == ""`は即時error、`new == null` は削除) | T+R |
| `.StartsWith(s)` | **Y** | `String.StartsWith(str, s)` | T+R |
| `.EndsWith(s)` | **Y** | `String.EndsWith(str, s)` (空suffixはtrue) | T+R |
| `.Trim()` | **Y** | `String.Trim(str)` | T+R |
| `.Substring(i)` / `.Substring(i,n)` | **Y** | `String.Substring(str, i[, n])` | T+R |
| `.Split()` / `.Split(sep)` | **Y** | `String.Split(str[, sep])` (引数なしはwhitespace、空/null separatorは元文字列1要素。空要素保持) | T+R |
| `.ToUpper()` | **Y** | `string.upper(str)` | T |
| `.ToLower()` | **Y** | `string.lower(str)` | T |
| `.ToString()` | **Y** | `tostring(x)` | T |
| `string + string` | **Y** | `..` | T |
| `.IndexOf(c)` / `.IndexOf(s)` | **Y** | `String.IndexOf(str, value)` | T+R |
| `.LastIndexOf(c)` / `.LastIndexOf(s)` | **-** | | |
| `.IndexOfAny(chars)` | **-** | | |
| `.Insert(i, s)` | **-** | | |
| `.Remove(i)` / `.Remove(i, n)` | **-** | | |
| `.PadLeft(n)` / `.PadRight(n)` | **-** | | |
| `.TrimStart()` / `.TrimEnd()` | **-** | | |
| `.ToCharArray()` | **-** | | |
| `.Equals(s, comp)` | **-** | | |
| `.CompareTo(s)` | **-** | | |
| `String.IsNullOrEmpty(s)` (static) | **Y** | `String.IsNullOrEmpty(s)` (`nil or ""`) | T+R |
| `string.CompareOrdinal(a, b)` (static) | **Y** | `String.CompareOrdinal(a, b)` (-1 / 0 / 1) | T+R。順序は il-spec §11 の ordinal で、null は他のどの string より小さい。値は 0 との比較 (`< 0`、`== 0` など) の operand でだけ使える。.NET の契約は符号だけで、値そのものは 2 文字ずつ比べる位置で変わる (`("", "x")` は -120、`("abc", "ab")` は 1) ので、それ以外の使い方は TCS1001 `CompareOrdinalValue`。範囲つきの overload は TCS1002 |
| `String.IsNullOrWhiteSpace(s)` (static) | **-** | | |
| `String.Format(fmt, ...)` (static) | **-** | | 補間で代替 |
| `String.Join(sep, ...)` (static) | **Y** | `String.Join(sep, values)` | T+R。List / 配列の要素は連結と同じく文字列化する (float は .NET と同じ文字列)。C backend の要素は string / int / float / bool |
| `String.Concat(...)` (static) | **-** | | |
| `String.Compare(a, b)` (static) | **-** | | |
| `String.Equals(a, b)` (static) | **-** | | |
| `String.Empty` (static) | **-** | | |
| `.ReplaceLineEndings()` | **-** | | |
| `[int]` (char indexer) | **Y** | `string.byte(s, i + 1)` (整数 code unit = byte) | T |
| `(int)ch` / `(char)n` (char ↔ int) | **Y** | 恒等 (char は整数)。`(int)'a'` は定数畳み込み | T |
| `foreach (char c in s)` | **Y** | `for i = 1, #s do local c = string.byte(s, i)` (byte 単位。codepoint は `EnumerateRunes`) | T |
| `.IndexOf(char)` / `.Contains(char)` / `.StartsWith(char)` / `.EndsWith(char)` / `.Replace(char, char)` / `.Split(char)` | **Y** | char 引数を `string.char` で 1 文字 string にして同名 runtime へ | T+R |
| `.EnumerateRunes()` | **Y** | `foreach` の collection 位置限定: `for _, r in utf8.codes(s)`。`r.Value` (`System.Text.Rune`) は codepoint 整数そのもの。`r` の文字列化 (`ToString`・連結・補間・`WriteLine`) は `Char.ConvertFromUtf32(r)` | T+R |

引数なし`Split()`のwhitespace判定はLua `%s`によるbyte/locale単位であり、
.NETのUnicode `Char.IsWhiteSpace`とは一致しない。これはUTF-8 byte列を使う既知制約に含む。

---

## 13b. Char メンバー (static、整数 code unit の ASCII 判定 / 変換)

| メンバー | 状態 | Lua マッピング | 区分 |
|---------|:----:|--------------|:----:|
| `char.IsDigit(c)` / `IsLetter` / `IsLetterOrDigit` / `IsWhiteSpace` / `IsUpper` / `IsLower` | **Y** | `Char.IsDigit(c)` 等 (ASCII 範囲。C backend も同じ表) | R |
| `char.ToUpper(c)` / `char.ToLower(c)` | **Y** | `Char.ToUpper(c)` / `Char.ToLower(c)` (ASCII) | R |
| `c.ToString()` | **Y** | `string.char(c)` | T |
| `char.ConvertFromUtf32(n)` | **Y** | `Char.ConvertFromUtf32(n)`: codepoint の UTF-8 文字列 (Lua は `utf8.char`)。負の値、surrogate (0xD800〜0xDFFF)、0x10FFFF 超は Lua / C とも同じ文言で fault (.NET は ArgumentOutOfRangeException)。結果は byte 列なので `Length` は UTF-8 の byte 数 | R |
| `char.IsPunctuation` / `IsSymbol` / `IsControl` / `GetNumericValue` | **-** | | |

---

## 14. Math メンバー

| メンバー | 状態 | Lua マッピング | 区分 |
|---------|:----:|--------------|:----:|
| `Math.PI` | **Y** | `Math.PI` (`math.pi`) | R |
| `Math.Min(a, b)` | **Y** | `Math.Min(a, b)` (System.Math は chunk prelude の `__tcs_min`) | R。float は MathF.Min と同じく NaN を伝播し、-0 を +0 より小さいとみなす |
| `Math.Max(a, b)` | **Y** | `Math.Max(a, b)` (System.Math は chunk prelude の `__tcs_max`) | R。float は MathF.Max と同じく NaN を伝播し、+0 を -0 より大きいとみなす |
| `Math.Clamp(v, min, max)` | **Y** | `Math.Clamp(v, min, max)` | R |
| `Math.Abs(x)` | **Y** | `Math.Abs(x)` | R |
| `Math.Floor(x)` | **Y** | `Math.Floor(x)` (System.Math は `(x // 1)`) | R。結果は float のまま (-0 を保つ) |
| `Math.Ceiling(x)` | **Y** | `Math.Ceil(x)` (System.Math は `(-(-x // 1))`) | R。結果は float のまま (-0 を保つ) |
| `Math.Sqrt(x)` | **Y** | `Math.Sqrt(x)` | R |
| `Math.Sin(x)` | **Y** | `Math.Sin(x)` | R |
| `Math.Cos(x)` | **Y** | `Math.Cos(x)` | R |
| `Math.Atan2(y, x)` | **Y** | `Math.Atan2(y, x)` | R |
| `Math.E` | **-** | | |
| `Math.Tau` | **-** | | |
| `Math.Pow(x, y)` | **Y** | `Math.Pow(x, y)` | R |
| `Math.Exp(x)` | **Y** | `Math.Exp(x)` (`math.exp`) | R |
| `Math.Log(x)` / `Math.Log(x, base)` | **Y** | `math.log(x)` / `Math.Log(x, base)` | R。base が 1、または x が 1 でなく base が 0 / +∞ なら NaN (MathF.Log と同じ) |
| `Math.Log10(x)` / `Math.Log2(x)` | **-** | | `Math.Log(x, base)` で代替 |
| `Math.Round(x)` / `Math.Round(x, digits)` | **Y** | `Math.Round(x[, digits])` | R。C# と同じ偶数丸め (banker's rounding)。0 に丸めた結果は x の符号を持つ (`Round(-0.25)` は -0) |
| `Math.Truncate(x)` | **-** | | |
| `Math.Sign(x)` | **Y** | `Math.Sign(x)` | R |
| `Math.Tan(x)` | **Y** | `Math.Tan(x)` (`math.tan`) | R |
| `Math.Asin(x)` / `Math.Acos(x)` / `Math.Atan(x)` | **-** | | |
| `Math.Sinh(x)` / `Math.Cosh(x)` / `Math.Tanh(x)` | **-** | | |
| `Math.Cbrt(x)` | **-** | | |
| `Math.CopySign(x, y)` | **-** | | |
| `Math.FusedMultiplyAdd(x, y, z)` | **-** | | |
| `Math.IEEERemainder(x, y)` | **-** | | |
| `Math.DivRem(a, b)` | **-** | | |
| `Math.BigMul(a, b)` | **-** | | |
| `Math.ScaleB(x, n)` | **-** | | |
| `Math.MaxMagnitude(x, y)` / `Math.MinMagnitude(x, y)` | **-** | | |
| `Math.SinCos(x)` | **-** | | |

---

## 15. Random メンバー

| メンバー | 状態 | Lua マッピング | 区分 |
|---------|:----:|--------------|:----:|
| `new Random()` / `new Random(seed)` | **Y** | `Random.new(seed)` (pure-Lua xoshiro256**) | System.Random と同じ形。seed 固定時は Lua / C で列が bit 一致し、`Random.Seed(seed)` 後の `Shared` とも一致 (il-spec §13)。seed 未指定は時刻などから作る seed で予測できるので、敵対的な用途には向かない。C backend は GC object |
| `Random.Shared` (static) | **Y** | `Random.Shared` (VM の `math.random` 状態) | |
| `Random.Seed(seed)` | **Y** | `math.randomseed(seed)` | `Shared` の列の固定 (tcs 独自。System.Random に無い) |
| `r.Next()` / `r.Next(max)` / `r.Next(min, max)` | **Y** | `r:Next(...)` | .NET 標準 API (上限は exclusive。空の範囲 `Next(0)` / `Next(n, n)` は下限を返す) |
| `r.NextFloat()` / `r.NextSingle()` | **Y** | `r:NextFloat()` | [0, 1) の float |
| `r.Range(min, max)` | **Y** | `r:Range(min, max)` | 両端含む整数 (Lua の `math.random(m, n)`。tcs 独自) |
| `Random.NextDouble()` | **-** | | double はサブセット外 |
| `Random.NextBytes(buf)` | **-** | | |
| `Random.Shuffle(arr)` | **-** | | |

---

## 16. System.Collections.Generic — 型一覧

| 型 | 状態 | Lua マッピング | 備考 |
|----|:----:|--------------|------|
| `List<T>` | **Y** | sequence table | null 要素は TCS1003 診断 |
| `Dictionary<TKey, TValue>` | **Y** | hash table | null 値は TCS1003 診断。値で比べる key (struct / record) は TCS1001 で build を止める |
| `HashSet<T>` | **-** | | |
| `Queue<T>` | **-** | | |
| `Stack<T>` | **-** | | |
| `LinkedList<T>` | **-** | | |
| `SortedList<TK, TV>` | **-** | | |
| `SortedDictionary<TK, TV>` | **-** | | TCS1002 |
| `SortedSet<T>` | **-** | | |
| `KeyValuePair<TK, TV>` | **-** | | foreach 内で暗黙使用 |
| `IEnumerable<T>` | **N/A** | | Roslyn 型チェックのみ |
| `ICollection<T>` | **N/A** | | |
| `IList<T>` | **N/A** | | |
| `IDictionary<TK, TV>` | **N/A** | | |
| `IReadOnlyList<T>` / `IReadOnlyCollection<T>` | **N/A** | | |

---

## 17. List\<T\> メンバー

Lua sequence table は `nil` 要素を保持できないため、`null` 要素の保存は未対応。
直接の `null` / 参照型 `default` は TCS1003 として報告する。

| メンバー | 状態 | Lua マッピング | 区分 |
|---------|:----:|--------------|:----:|
| `new List<T>()` | **Y** | `{}` | T |
| `new List<T> { 1, 2, 3 }` | **Y** | `{1, 2, 3}` | T |
| `new List<T>(source)` | **Y** | `List.Copy(source)` (変更可能な struct 要素は型別 `__copy` を渡す) | T+R。source は `List<T>`、`T[]`、Dictionary の `Keys` / `Values`。initializer との併用は対象外 |
| `list[i]` (get/set) | **Y** | 読み出しは `(list[i + 1] or __tcs_oob())` (false になりうる要素 (bool / 型引数 / object) は nil と比べる)、書き込みは `if list[i + 1] == nil then __tcs_oob() end list[i + 1] = v` | T。範囲外は fault (il-spec §12)。書き込みは受け手、添字、値を評価してから範囲を見る (C# の順序)。string の `s[i]` は負の添字も範囲外。要素は nil を持たない (TCS1003) ので、範囲内の key は必ず値を持つ。配列も同じだが、要素が nil になりうる配列 (参照型 / `T?` / 型引数の要素) は穴を持つので検査しない |
| `.Count` | **Y** | `#list` | T |
| `.Add(item)` | **Y** | 文位置は `list[#list + 1] = item` (受け手が変数/field 連鎖の場合。引数が呼び出しを含むときは `local __tcs_v = item` に先に束縛)。受け手が式 (`Get().Add(x)` 等) のときは `table.insert(list, item)` | T |
| `.Remove(item)` | **Y** | `List.Remove(list, item)` | T+R。等価は EqualityComparer\<T\>.Default (float と `float?` の NaN 同士、float field が NaN の struct 同士は等しい)。Contains / IndexOf も同じ |
| `.RemoveAt(index)` | **Y** | `table.remove(list, idx+1)` | T |
| `.Clear()` | **Y** | | T |
| `.Contains(item)` | **Y** | `List.Contains(list, item)` | T+R |
| `.IndexOf(item)` | **Y** | `List.IndexOf(list, item)` | T+R |
| `.FindIndex(match)` | **Y** | `List.FindIndex(list, fn)` | T+R。無ければ -1。開始位置つきの overload は TCS1002 |
| `.RemoveAll(match)` | **Y** | `List.RemoveAll(list, fn)` | T+R。.NET と同じく述語は各要素に 1 回ずつ先頭から呼び、残す要素の順を保つ。消した数を返す |
| `.AddRange(collection)` | **-** | | |
| `.Insert(index, item)` | **-** | | |
| `.InsertRange(index, collection)` | **-** | | |
| `.RemoveRange(index, count)` | **-** | | |
| `.Find(predicate)` / `.FindLast()` / `.FindAll()` | **-** | | |
| `.FindLastIndex()` | **-** | | |
| `.Sort()` / `.Sort(comparison)` | **Y** | `List.Sort(list, comparison)` | T+R。比較なしの順序は .NET の既定の comparer と同じく NaN が最小。string の順序は .NET の ordinal (UTF-16 の code unit の順。大文字が小文字より先)。.NET と同じ introsort で、同値の要素の並びと比較関数の呼び出し列も同じ |
| `.Reverse()` | **-** | | |
| `.BinarySearch(item)` | **-** | | |
| `.Exists(predicate)` | **-** | | |
| `.ForEach(action)` | **-** | | |
| `.GetRange(index, count)` / `.Slice()` | **-** | | |
| `.ConvertAll(converter)` | **-** | | |
| `.CopyTo(array)` / `.ToArray()` | **-** | | |
| `.TrueForAll(predicate)` | **-** | | |
| `.LastIndexOf(item)` | **-** | | |
| `.AsReadOnly()` | **-** | | |
| `.Capacity` | **-** | | |

---

## 18. Dictionary\<TKey, TValue\> メンバー

Lua hash table は `nil` 代入を key 削除として扱うため、`null` 値の保存は未対応。
直接の `null` / 参照型 `default` は TCS1003 として報告する。

wire format 契約: Dictionary は素の Lua table (`{[k]=v, ...}`) であり、
metatable も bookkeeping フィールドも持たない。`--ref` (host) 関数へ渡すと、
host 側はユーザーエントリだけを持つ table をそのまま受け取る
(`Dictionary<string, object>` のヘテロ値、`--ref` ハンドル、ネスト
Dictionary / List も透過)。`Count` / `Keys` / `Values` は保存メタデータでは
なく pairs 走査で都度計算される。

`Dictionary<float, V>` の NaN key は .NET (`EqualityComparer<float>.Default`) と
同じく 1 つの key として扱う。Lua table は NaN を key にできないので、table 上は
文字列 `"NaN"` の key に置き (`Dict.FloatKey`)、`Keys` と foreach で NaN に戻す。
host が raw table を読むときはこの key が見える。判定は呼び出し地点の静的な
key 型で行うので、key 型が型 parameter の dict (`Dictionary<K, V>` を受ける
generic method に K = float で渡したもの) と `object` などの key に入れた NaN は
対象外 (Lua table に置けず `table index is NaN` で止まる)。C backend は float の
key を対応外として生成前に止める (`Dictionary key type not supported`)。

値で比べる key (struct / record struct / struct の `T?` / record class) の
Dictionary は対応外。.NET (`EqualityComparer<K>.Default`) は等しい値の key を
同じ key とみなすが、Lua table は table の同一性で引くので、等しい値の別の
table では `ContainsKey` / 索引が外れる。作る地点 (`new Dictionary<K, V>()`、
target-typed `new()`) を TCS1001 `ValueEqualityDictionaryKey(K)`
とし、transpiler は build を止める (Lua / C とも出力しない。analyzer は warning)。
key 型が型 parameter の dict (generic method の中で作るもの) は対象外。

C backend の `Dictionary<string, V>` のハッシュは seed を持たない FNV-1a で、chain の長さに上限が無い。
同じ key を同じ順で入れれば、列挙の順序は実行ごとに同じになる (Lua は実行ごとの seed で変わるので、backend をまたいだ順序は未規定のまま)。
その代わり、同じハッシュ値の文字列を作るのは容易で、host が外から受け取った信頼できない文字列を key にすると、N 回の操作に N の 2 乗に比例する時間がかかりうる。

| メンバー | 状態 | Lua マッピング | 区分 |
|---------|:----:|--------------|:----:|
| `new Dictionary<K,V>()` | **Y** | `{}` | T |
| `new Dictionary<K,V> { ... }` | **Y** | `{[k]=v, ...}` | T |
| `dict[key]` (get/set) | **Y** | `dict[key]` | T |
| `.Count` | **Y** | `Dict.Count(dict)` | T+R |
| `.Add(key, value)` | **Y** | `dict[key] = value` | T |
| `.Remove(key)` | **Y** | `Dict.Remove(dict, key)` | T+R |
| `.ContainsKey(key)` | **Y** | `(dict[key] ~= nil)` | T |
| `.Keys` | **Y** | `Dict.Keys(dict)` | T+R |
| `.Values` | **Y** | `Dict.Values(dict)` | T+R |
| `.TryGetValue(key, out value)` | **Y** | out 代入 + bool | T |
| `.TryAdd(key, value)` | **-** | | |
| `.ContainsValue(value)` | **-** | | |
| `.Clear()` | **-** | | |

---

## 19. LINQ — TinySystem.Linq

LINQ は `TinySystem.Linq.Enumerable` (`TinySystem/Linq.cs`) の `List<T>` の拡張メソッドで、`using TinySystem.Linq;` で使う (q.md Q18)。
tcs のサブセットの C# で書いてあり、tcs は user のコードと一緒に Lua / C へ変換し、.NET の project は `TinySystem/TinySystem.props` で同じファイルをコンパイルするので、3 経路が同じ実装で動く。
意味の正本はそのファイルと doc comment。`System.Linq` は namespace ごと TCS1002 (`using System.Linq;` と並べると、受け手が `List<T>` の TinySystem.Linq が選ばれ、TinySystem.Linq に無い overload は TCS1002)。
すべて即時評価で、列を返す演算は新しい `List<T>` を返す。callback は各要素にちょうど 1 回、先頭から順に呼び、走査は呼び出した時点の Count まで。メソッドチェーンだけで、クエリ構文 (`from x in ...`) は対象外。
毎フレームの一時の `List<T>` は作ってよい (C で要素あたり約 8 ns)。struct の要素は `readonly record struct` にする (lambda の parameter の copy が省ける。§2 の `struct` の行)。

### 対応済み

| メソッド | 状態 | 備考 |
|---------|:----:|------|
| `.Where(predicate)` / `.Select(selector)` | **Y** | |
| `.Any()` / `.Any(predicate)` / `.All(predicate)` / `.Count(predicate)` | **Y** | 要素数は `.Count` property |
| `.FirstOrDefault()` / `.FirstOrDefault(predicate)` | **Y** | 無ければ default(T) |
| `.Sum()` / `.Sum(selector)` | **Y** | `List<int>` / `List<float>` と、int / float を返す selector。int は checked (途中の overflow で fault)。float は float のまま先頭から足す (System.Linq は double で足す) |
| `.Min()` / `.Max()` / `.Min(selector)` / `.Max(selector)` | **Y** | int / float / string。等しい値は先に出た方。float の NaN は最小の値 (Min は NaN を見た時点で返す、Max は NaN 以外があればその最大値)。string は ordinal で null を飛ばし、空なら null。int / float の空の列は fault |
| `.MinBy(key)` / `.MaxBy(key)` | **Y** | key は int / float / string で、比べ方は OrderBy と同じ。key の等しい要素は先に出た方。空の列は fault (System.Linq は参照型の要素なら null) |
| `.OrderBy(key)` / `.OrderByDescending(key)` | **Y** | key は int / float / string (enum は `(int)` で渡す、`float?` 等は対象外)。安定 sort で key は各要素 1 回。float の NaN は最小、string は ordinal で null が最小 |
| `new List<T>(source)` (copy constructor) | **Y** | source は `List<T>`、`T[]`、Dictionary の `Keys` / `Values`。`ToList()` の代わり (§16) |
| Dictionary の `Keys` / `Values` を受け手にした上の演算 | **Y** | List に写して List の版に渡す。要素型の Sum / Min / Max (`d.Values.Sum()` など) も |

### 未対応

`System.Linq` の他の演算は TCS1002。First / Last / LastOrDefault / Take / Skip / ToList / Count() / ToDictionary は、添字、copy constructor、範囲の loop、foreach で書く (q.md Q18)。
Dictionary の `Keys` / `Values` も受け手にでき (`d.Values.Where(..).OrderBy(..)`、`d.Values.Sum()`)、List に写して List の版に渡す。列挙の順序は未規定 (§18) なので、順序が要るときは OrderBy で並べる。
配列は LINQ の受け手にならない (`new List<T>(array)` で写す)。遅延評価 (`IEnumerable<T>`) は持たない。

---

## 20. System.Text

| 型 | 状態 | 備考 |
|----|:----:|------|
| `StringBuilder` | **-** | |
| `Encoding` | **-** | |
| `Regex` (`System.Text.RegularExpressions`) | **-** | Lua パターンで部分代替可能 |
| `Rune` | **P** | `string.EnumerateRunes()` の foreach 変数と `.Value`、文字列化のみ (§13)。`new Rune(n)` は TCS1002、`string.Join` の要素と `Rune?` の文字列化は TCS1001 |

---

## 21. System.IO

| 型 | 状態 | 備考 |
|----|:----:|------|
| `File` | **-** | Lua `io` で代替可能 |
| `Path` | **-** | |
| `Directory` | **-** | |
| `Stream` | **-** | |
| `StreamReader` | **-** | |
| `StreamWriter` | **-** | |
| `TextReader` / `TextWriter` | **-** | |

---

## 22. System.Threading.Tasks

| 型 | 状態 | 備考 |
|----|:----:|------|
| `Task` | **N/A** | async/await 未対応 |
| `Task<T>` | **N/A** | |
| `ValueTask` | **N/A** | |
| `ValueTask<T>` | **N/A** | |

---

## 23. System.Numerics

| 型 | 状態 | 備考 |
|----|:----:|------|
| `Vector2` | **-** | 外部数学ライブラリ/facade で必要なら検討 |
| `Vector3` | **-** | |
| `Vector4` | **-** | 外部数学ライブラリ/facade で必要なら検討 |
| `Matrix3x2` | **-** | |
| `Matrix4x4` | **-** | |
| `Quaternion` | **-** | |

---

# Part III: 診断・ツール

---

## 24. CLI・診断機能

| 機能 | 状態 | 備考 |
|------|:----:|------|
| C# コンパイルエラー報告 | **Y** | ソース位置付き |
| 未対応構文の診断 | **Y** | TCS1001 / analyzer と transpiler/check で共有 (`struct` / `record struct` / `partial` 型 / `lock` / `nameof` など) |
| 未対応で build を止める | **Y** | TCS1001 / TCS1002 は transpiler が error にし、Lua を書かず exit 1 (watch / 増分 session も commit しない)。TCS1003 は warning で、`tcs check` と `--warnaserror` の変換だけが exit 1 にする。analyzer は warning のまま (`.editorconfig` で上書き可能) |
| 未対応 BCL API の診断 | **Y** | TCS1002 / analyzer と transpiler/check で共有。core API allowlist は完全シグネチャ単位で、member 外に加えて名前だけ一致する未実装 overload も検出する。完全修飾型qualifierはmemberとして重複診断しない |
| collection null 保存の警告 | **Y** | TCS1003 / analyzer と transpiler で共有 |
| 複数ファイル入力 | **Y** | 共有 Compilation でクロスファイル参照 |
| namespace 解決 | **Y** | namespace の中の型は修飾名 (§ namespace ブロック行)。`--entry` は `A.Color` / simple 名 (一意なら) で引ける (`--snapshot` 時は `A_Color` も可) |
| CLI | **Y** | `tcs a.cs b.cs [-o out.lua]`, `tcs check a.cs`, `--help`, `--version` |
| CI gate | **Y** | GitHub Actions で `run-tests.sh` / sample `tcs check` / analyzer demo / analyzer pack |
| ソースマップ | **Y** | `--sourcemap` で `.lua.map` 出力 |
| 実行 trace | **Y** | `--trace <path-prefix>` で対象ファイルの文の直前に `__tcs_at` (関数 local の現在行) と `__tcs_trace["<file>:<line>"] = true` (global) を出し、対象ファイルの型 table を `__tcs_trace_classes` (global) に登録する。reload chunk も同じ。host 契約は README |
| Lua stack trace SourceMap 注釈 | **Y** | `--map-stacktrace out.lua.map [trace.txt]` |
| watch モード | **Y** | `--watch` でファイル監視 |
| Lua CMake platform 分岐 | **Y** | Linux/Windows/macOS/iOS-family/Emscripten/BSD/generic Unix |
| 依存 lock / publish runtime 同梱 | **Y** | package pin + packages.lock.json + runtime/tinysystem.lua |
| Lua と生成コードの予約と重なる識別子 | **Y** | Lua の予約語、`__` 始まり、非 ASCII、`new` / `ctor`、型名と namespace 名の `_` は error (§28)。analyzer / `tcs check` は TCS1001 `LuaKeywordIdentifier(end)` / `DoubleUnderscoreIdentifier(__x)` 等 |

## 25. 許容される C# エラー (TinyC# 固有)

| エラーコード | 内容 | 理由 |
|------------|------|------|
| CS0029 | 暗黙の型変換 | diagnostic位置の式がenum ↔ numeric integerの場合だけ許容 (`char`は除外) |
| CS0266 | 明示的変換が必要 | diagnostic位置の式がenum ↔ numeric integerの場合だけ許容 (`char`は除外) |
| CS0019 | 演算子適用不可 | enumとnumeric integer (`char`除外) の`==` / `!=`だけ許容 |
| CS0535 | interface 未実装 | 未実装memberがすべて、同じclassで宣言した同名・同型のpublic instance fieldで代替できるpropertyの場合だけ許容。setterにはmutable fieldが必要 |

## 26. 数値型マッピング

| C# 型 | Lua 型 | 備考 |
|-------|-------|------|
| int, long, short, byte | number (integer) | 全て Lua number に統一 |
| float, double, decimal | number (float) | サフィックス除去 |
| enum 値 | number (integer) | 整数定数 |
| bool | boolean | |
| string | string | UTF-8 前提 |
| null | nil | |
| class instance | table | metatable 付き |

## 27. 既知の意味論差 (実行可能な正本)

C# と Lua 出力の意味論差のうち、正規化 (bool 表記) で吸収できない
例別の受容差異は `Transpiler.Tests/SpecConformance/known-differences.json` に理由付きで
登録し、spec conformance sweep が実行時に参照する。代表例:

- string の参照等価: Lua の string は intern される値のため、別インスタンス比較で
  C# (false) と異なり true になる
- 文字列長: C# は UTF-16 code unit 数、Lua は byte 数 (UTF-8)
- 数値表示: Lua は integer/float を区別し `4` / `4.0`、C# double は `4`。
  differential は数値等価で比較する

## 28. Lua 出力の名前規則

名前は表を持たず規則で写す (`Shared/LuaNaming.cs`)。user の型 (入力の tree に宣言された型) の
member は C# の識別子をそのまま Lua / IL / C の名前にし、`--ref` の型 (host) の member だけ
host の wire format (snake_case) に写す (q.md Q16)。ユーザ型の型名 (`Player`) は写さない。

| C# | Lua | 規則 |
|----|-----|------|
| user の field / property / method `BeginPass`, `hp`, `_maxHp`, `XMLValue` | `BeginPass`, `hp`, `_maxHp`, `XMLValue` | C# の識別子のまま。IL の名前 (tcs2c の C 名 `tcs_entry_<Class>_<Method>` の元) も同じ |
| user の enum メンバ `BigValue`, `RGBA8` | `BigValue`, `RGBA8` | C# の識別子のまま |
| method の overload `Show()`, `Show(int)`, `Total<T>(List<T>, Func<T, float>)` | `Show`, `Show__int`, `Total__List_T__Func_T_float` | 同じ型に同名の method が複数あるときだけ、引数の型を `__` でつないで付ける (ref / out は `ref_` 等を前置、`int[]` は `int_arr`、`int?` は `int_opt`)。引数の無い overload は C# の名前のまま。interface の method を実装する method は interface の method の名前 |
| Lua の予約語と同名のローカル束縛 `local`, `end` | `local_`, `end_` | `_` を後置。同じ member body に `local_` があれば `local__` のように足す |
| ユーザ型と同名のローカル束縛 `V` | `V_` | 予約語と同じ規則 |
| custom property `Width` | `get_Width` / `set_Width` | accessor 名の接頭辞に C# の名前 |
| record の positional parameter `PosX` | field `PosX` (ctor 引数も C# 名) | そのまま |
| entry の callback `OnInit`, `OnFrame`, `OnReload` | `OnInit`, `OnFrame`, `OnReload` | host は C# の名前で呼ぶ |
| `--ref` 型の member `BeginPass`, `_maxHp` | `begin_pass`, `_max_hp` | 先頭を小文字にし、以降の大文字の前に `_` を入れて小文字化。先頭の `_` は保つ。小文字を含まない名前 (`CLEAR`) は既に snake_case とみなす |
| `--ref` 型の enum メンバ `DontCare`, `Depth24Stencil8` | `DONT_CARE`, `DEPTH24_STENCIL8` | 上の結果を全大文字 |
| `--ref` 型の member で予約語に落ちる名前 `End`, `Do` | `end_`, `do_` | `_` を後置 |
| `--ref` 型の static アクセス `Lub.Gfx.BeginPass` | `lub.gfx.begin_pass` | 入れ子の型名を全小文字で `.` 結合 |
| `--ref` 型に入れ子の enum `Lub.Gfx.PixelFormat.Rgba8` | `lub.gfx.RGBA8` | enum 名を省いて親の下に平らに置く |
| `--ref` 型のパスで予約語になる区切り `End.Api.Get` | `end_.api.get` | member と同じく `_` を後置 |
| `--ref` 型のパスの先頭と同名のローカル束縛 `lub` | `lub_` | ユーザ型と同名のローカル束縛と同じ規則 |
| `const` field (enum メンバ以外) | 値を inline | C# の意味論どおり |
| BCL / TinySystem のメンバ (`List.Add`, `Math.Min`) | runtime の名前のまま | source に宣言の無い symbol は写さない |

user の member は C# の識別子のまま Lua の名前になるので、同じ型の中で Lua の名前が衝突することは無い。overload に型を付けた名前が他の member と同じ綴りになる場合 (`F(int)` の `F__int` と method `F__int`) は MethodOverload (TCS1001)。

user の宣言の識別子が Lua と生成コードの予約と重なる綴りも、build を止める TCS1001
(transpiler / 増分 session は Lua を出さない error、`tcs check` は exit 1、analyzer は
warning、tcs2c も IL の前に止まる) にする。`--ref` の型は対象外。

| 識別子 | 診断 | 理由 |
|----|-----|------|
| Lua の予約語と同名の member / 型 `end` | `LuaKeywordIdentifier(end)` | member と型は C# の名前のまま table の key や global になる。local は `_` を後置して写す (上の表) |
| `__` で始まる識別子 (member / 型 / namespace / local / parameter) `__hp` | `DoubleUnderscoreIdentifier(__hp)` | Lua の metamethod (`__index` / `__eq`) と生成コードの `__copy` / `__tcs_*` の領域 |
| 非 ASCII の識別子 `速度` | `NonAsciiIdentifier(速度)` | Lua の識別子は ASCII のみ |
| 生成コードが class table に置く key と同名の member `@new`, `ctor` | `ReservedClassKey(new)` | `T.new` (ctor) / `S.ctor` (positional record struct) を上書きする |
| 型名の `_` `My_Type` | `UnderscoreTypeName(My_Type)` | namespace の中の型は namespace 修飾名を `_` で結合した平らな global (`A.B.C` → `A_B_C`) になるので、`_` を含む名前は別の修飾名と同じ global になり得る |
| namespace 名の `_` `A.B_C` | `UnderscoreNamespaceName(B_C)` | 同上 |
| `--ref` の interface の実装 `class Impl : IHostHook` | `RefInterfaceImplementation(IHostHook)` | interface 経由の呼び出しは host の名前 (`h:do_thing()`)、実装は C# の名前 (`Impl:DoThing`) で届かない。user の interface の実装と、host の interface を値として受け渡すだけなら可 |

member 名と local 名の `_` (`max_hp`、`_init`、`a_b`) はそのまま書ける。

