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
| struct / record struct | **Core** | 値セマンティクス対応済み (T219b)。member / static / operator / 値等価 / readonly copy 省略。C backend は素の C struct |
| if / switch / loop / lambda / pattern | **Core** | ゲームロジックと小さな業務ロジックの表現力として必要 |
| 演算子オーバーロード (算術) | **Core** | ベクトル/行列など math 型の表現に必要。二項 `+ - * / %` と単項 `-` だけを static 関数へ写像し (呼び出し箇所で overload を静的解決)、変換演算子と `==`/`!=`/比較系は対象外 |
| LINQ メソッドチェーン | **Core** | `TinySystem.Linq` の `List<T>` の拡張メソッド (`Where`/`Select`/`Any`/`All`/`Count`/`FirstOrDefault`/`OrderBy`/集計) を即時評価で提供 (§19) |
| ユーザー定義ジェネリクス | **Core** | class / record / struct / method と型制約。tcs VM と C は閉じた型へ特殊化する (§7、q.md Q20)。TinySystem.Linq もこれで書く |
| reflection / dynamic / expression tree | **Out** | compact baseline に合わない |
| async / Task / threading | **Out** | ホットリロード可能なシングルスレッドの実行 (tcs VM) を基本にする |
| unsafe / Span / pointer | **Out** | 組み込み backend の低レベル最適化フェーズまで扱わない |

### 標準ライブラリの baseline

| 領域 | 優先度 | 方針 |
|------|:------:|------|
| `String`, `Math`, `Random`, `Console` | **Core** | 小さなスクリプトで頻出する API を厳選して提供 |
| `List<T>`, `Dictionary<K,V>` | **Core** | 可変長の列と hash table として明確に対応 |
| LINQ `Enumerable` | **Core** | `System.Linq` の代わりに `TinySystem.Linq` (§19)。クエリ構文と遅延評価は非目標 |
| `System.Numerics` 風のベクトル型 | **Useful** | engine / math facade として必要になった時点で追加 |
| `DateTime` / `TimeSpan` / 軽量 IO | **Useful** | サンプルが要求したら最小 API だけ追加 |
| `System.IO` 全面対応 | **Out** | engine API (`--ref` の host) で代替。BCL 再実装はしない |
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
| String メンバー | **Core** | 12 | 0 | 17 | 3 | 32 |
| Math メンバー | **Core** | 17 | 0 | 15 | 0 | 32 |
| Random メンバー | **Core** | 3 | 0 | 6 | 0 | 9 |
| List\<T\> メンバー | **Core** | 16 | 0 | 16 | 0 | 32 |
| Dictionary\<K,V\> メンバー | **Core** | 11 | 0 | 2 | 0 | 13 |
| LINQ (TinySystem.Linq) | **Core/Useful** | 9 | 0 | 0 | 0 | 9 |
| Collections.Generic 型 | **Core/Useful** | 4 | 1 | 5 | 5 | 15 |
| System.Text | **Out** | 0 | 0 | 3 | 0 | 3 |
| System.IO | **Out** | 0 | 0 | 7 | 0 | 7 |
| System.Threading.Tasks | **Out** | 0 | 0 | 0 | 4 | 4 |
| System.Numerics | **Useful** | 0 | 0 | 6 | 0 | 6 |
| **小計** | | **81** | **2** | **131** | **12** | **226** |

---

# Part I: 言語機能

---

## 1. 型 (Types)

### 1.1 組み込み値型

| 型 | 状態 | 備考 |
|----|:----:|------|
| `int` (Int32) | **Y** | i32 (§26) |
| `long` (Int64) | **N/A** | 数値は i32 / f32 だけ (il-spec §3)。`long` の型と `L` suffix の literal は TCS1001 `LongType` |
| `float` (Single) | **Y** | f32 (§26) |
| `double` (Double) | **N/A** | 数値は i32 / f32 だけ (il-spec §3)。`double` の型と、suffix の無い実数リテラル (`1.5`、`1e3`、`1d`) は TCS1001 `DoubleType` / `DoubleLiteral`。実数は `f` suffix の float で書く |
| `bool` (Boolean) | **Y** | 文字列化 (`ToString()`・連結・補間・`Console.WriteLine`・`string.Join` の要素、`bool?` と object に入れた値も) は .NET と同じ `True` / `False` (`__tcs_bstr`) |
| `decimal` (Decimal) | **-** | |
| `char` (Char) | **Y** | 整数 code unit (byte)。literal は ASCII のみ (非 ASCII は TCS1001 `NonAsciiCharLiteral`、string literal で書く)。`(char)x` の x が 0〜255 の外なら tcs VM と C は fault する (C の文言は `char.ConvertFromUtf32` を案内する。.NET は UTF-16 の code unit を作る)。cast を通らずに範囲を出た値 (`c++`) は文字列化の地点で fault する。算術 / 比較 / switch / pattern は int と同じ、文字列化 (連結・補間・`ToString`・`WriteLine`) は 1 byte の string。`Char.*` は §13b |
| `byte` (Byte) | **P** | `Utf8Bytes` の添字 `u[i]` の結果を受ける型で、i32 と同じ表現。tcs VM と C は 0〜255 に丸めないので、丸めが起きる `(byte)x` (範囲内の定数は可) と `byte` への `++` / `--` / 複合代入は TCS1001 `ByteNarrowing` で止める |
| `sbyte` (SByte) | **-** | |
| `short` (Int16) | **-** | |
| `ushort` (UInt16) | **-** | |
| `uint` (UInt32) | **P** | i32 と同じ表現。外部 API facade 用。値は 0〜2^31-1 に限る。2^31 以上の定数は TCS1001 `UIntRange` (int と enum への cast の operand と、enum の member の値は 2 の補数の bit として受ける: `unchecked((int)0xFFFFFFFF)`)。結果がその範囲を出うる演算 (`+` `-` `*` `<<` `~`、`++` `--`、`+=` `-=` `*=` `<<=`) は TCS1001 `UIntArithmetic`。比較、`&` `|` `^` `>>` `/` `%`、変換は使える |
| `ulong` (UInt64) | **-** | |
| `nint` (IntPtr) | **P** | 外部 API facade 用 |
| `nuint` (UIntPtr) | **-** | |

### 1.2 組み込み参照型

| 型 | 状態 | 備考 |
|----|:----:|------|
| `string` (String) | **Y** | 不変の UTF-8 の byte 列 (il-spec §11、§13) |
| `object` (Object) | **-** | |
| `dynamic` | **N/A** | |

### 1.3 特殊型

| 型 | 状態 | 備考 |
|----|:----:|------|
| `void` | **Y** | 戻り値型として |
| Nullable 値型 (`int?`) | **Y** | `null`/値/HasValue/Value/GetValueOrDefault/`??`/`??=`/`?.` (receiver が `S?` の member / method、結果の `T?` wrap)/null 比較/lifted 演算子 (算術・bit・比較・`bool?` の三値 `&` `\|`)。IL は明示ノード (il-spec §3)、C backend は `{ has, v }`。`.Value` の値なしは fault、文字列化は空文字列 |
| Nullable 参照型 (`string?`) | **N/A** | 注釈は型の検査だけに使う。参照型は注釈によらず null を持てる |
| タプル `(int, string)` | **Y** | 戻り値、分解、`(a, b) = (b, a)`、変数と field への保持、`List<(int, int)>`、配列、`T?`。7 要素まで (8 要素以上は TCS1001 `TupleArity`)。tcs VM と C は struct、.NET は ValueTuple |
| 配列 `int[]` | **P** | 初期化子、index、Length。`new T[n]` の要素は default で埋める (値型は default の値、参照型は null)。null が default になる要素型 (参照型 / `T?` / 型パラメータ (`where T : struct` も含む)、`new int[n][]` を含む) の `new T[n]` と、初期化子・代入の null / 型パラメータの `default` 要素は TCS1003 の警告 (`tcs check` は exit 1。`new T[0]` は対象外)。tcs VM と C はこれらの null の要素を持ち、Length と foreach と添字は .NET と同じ結果になる。多次元配列 (`int[,]`、`new int[5, 2]`) は TCS1001 `MultiDimensionalArray` (jagged 配列 `int[][]` は可)。List\<T\> を推奨 |
| 匿名型 `new { }` | **-** | |
| `Span<T>` / `ReadOnlySpan<T>` | **N/A** | |
| ポインタ型 `int*` | **N/A** | |
| 関数ポインタ `delegate*` | **N/A** | |
| `ref struct` | **N/A** | |

### 1.4 ユーザー定義型

| 型 | 状態 | 備考 |
|----|:----:|------|
| `class` | **Y** | 基底 class は持てない (継承は対象外、§8)。interface の実装は可。`override string ToString()` は書ける (下の `override` の行)。Equals / GetHashCode の override は TCS1001 `OverrideMember` (値の比較は record / record struct で書く)。ToString を override していない class の値の文字列化 (`ToString()`・連結・補間・`Console.WriteLine`・`string.Join` の要素) は TCS1001 `UnsupportedToString(T)` (C# は型名を書く) |
| `struct` | **Y** | 値意味論 (il-spec §10)。tcs VM は register では葉の field ごとの register、heap (class の field、配列 / List の要素、static) では C と同じ inline の配置で持ち、C backend は素の C struct。instance member は静的自由関数 (C backend は `Tcs_S *self` のアドレス渡し)。static field、static auto property、static method、custom な static property、演算子は class と同じ規則で書ける (generic な struct の static field は `GenericStaticField`)。List.Contains / IndexOf / Remove は memberwise 等価。copy constructor `new List<T>(source)` と LINQ の lambda parameter、static method の method group の parameter、式本体 (`=>`) の lambda と method の戻り値、`?.` 呼び出しの引数、user operator の operand、式位置の代入の値、switch 式の arm の値、struct の `T?` の値 (値があれば中の struct) も値 copy。override は `ToString` だけ (Equals / GetHashCode は TCS1001 `OverrideMember`)。const は使用箇所で値に畳む。object / interface への変換 (boxing。`object o = s`、`Console.WriteLine(s)`、`(object)s`、struct の `T?` も) は TCS1001 `StructBoxing`。`foreach (IShape s in structs)` も。interface の制約を持つ型引数 (`where T : IShape`) に struct を入れるのも TCS1001 `StructInterfaceConstraint(S)`。data を運ぶ struct は `readonly record struct` を既定にする (IL は readonly な struct の copy を省く) |
| `record` / `record class` | **Y** | positional record。IL 契約に IsRecord で収載し、C backend は型ごとの構造等価関数と実行時 layout の with copy を持つ。record の継承 (`record D(...) : B(...)`) は対象外 (§8)。==/!= は実行時型の一致と、各 positional field の値等価 (struct field は memberwise、struct の `T?` field は値の有無と中の値、float / `float?` field と generic な record の型 parameter の field は float.Equals で NaN 同士も等しい。型 parameter に struct を入れた field は memberwise。object / interface の field は中の値の実行時の型で比べ、record なら値等価、string と数は値、他の class は参照。C# の合成 Equals と同じ)。static field (const / static readonly 込み) と static auto property は class と同じく pre-zero して型をまたぐ初期化順で初期化する。Equals / GetHashCode 呼びと override は対象外 (override は TCS1001)。文字列化は `ToString` を override すれば書け、しなければ TCS1001 `UnsupportedToString(T)` (C# は member の一覧を書く)。C# の `(T)x` downcast は IlCast で C 側 fault 対象 |
| `record struct` | **Y** | positional ctor と合成 `op_Equality`。値等価 ==/!= (field ごとの比較は record class と同じく EqualityComparer\<T\>.Default、struct の List 検索の ValueType.Equals も同じ) と with 式。readonly (record) struct は copy 全省略。IL 契約 (IlStructInfo) と C backend、hot reload の migration も struct と同じ |
| `interface` | **Y** | 値を作らない型。実装する interface の一覧は class / record の実行時の型が持つ (il-spec §2)。class の `is I` / `as I` / 型 switch は §型パターンの行。property は実装の形 (自動 property、custom、式本体、record の positional) によらず interface 経由なら `get_X` / `set_X` を呼ぶ。継承した interface の member も派生した interface の型から呼べる。実装のない property は C# と同じ CS0535 で、field では実装できない |
| `enum` | **Y** | initializer 無しの field / auto property / `default(E)` の既定値は member 値に依らず 0。member の値は基底型 (byte / sbyte / short / ushort / int / uint) の定数を実行時の 32 bit の 2 の補数に写す (uint の 0x80000000 は int.MinValue)。文字列化 (`ToString()`・文字列連結・補間・`Console.WriteLine`、`E?` も) は Enum.ToString と同じく member 名、`[Flags]` は名前の `", "` 連結、表せない値は数値 (`String.FormatEnum`、名前の表は文字列化する地点にだけ出す。`--ref` の enum は stub の名前)。書式は `ToString("G" / "F" / "D")` と補間の `{e:G}` / `{e:F}` (名前 / 名前の組み合わせ) と `{e:D}` (数値)。`ToString` の他の書式 (`"X"` 等、定数でない書式) は TCS1002、補間の他の書式は TCS1001 `UnsupportedFormatSpecifier`。generic の `T` に入れた enum (単相化した IL の `String.FormatEnum`) と `string.Join` に渡した enum の List / 配列 (`String.JoinEnum`) も member 名。`object` に入れた enum は数値のまま (実行時の値が enum の型を持たない) |
| `delegate` 型定義 | **N/A** | Action/Func で代替 |
| ネストされた型 (class / struct / record / record struct / enum / interface の中の型) | **Y** | 独立の型で、IL の名前は外側の型の名前を `_` でつなぐ (`Outer.Inner` → `Outer_Inner`、`Game.Outer.Inner` → `Game_Outer_Inner`)。C も同じ名前。外側の型の private を読める。generic な型の中の型は TCS1001 `NestedTypeInGenericType`。generic でない型の中の generic な型は可 |

---

## 2. 宣言 (Declarations)

### 2.1 名前空間・using

| 機能 | 状態 | 備考 |
|------|:----:|------|
| `namespace N { }` (ブロック) | **Y** | IL の型の名前は平ら。namespace の中の型は namespace 修飾名を `_` でつないだ名前 (`A.Color` → `A_Color`、`Game.Gfx.Color` → `Game_Gfx_Color`)、namespace に属さない型は simple 名。型の IL の名前は他の型の有無に左右されない。`--ref` 型は namespace と入れ子の型名を全小文字で `.` でつないだ host の path (`Lub.Gfx` → `lub.gfx`)、enum はその型か namespace の下に平らに置く |
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
| `struct` 宣言 | **Y** | field / method / property / 演算子 / static member / パラメータ付き ctor (複数可)。indexer は TCS1001 `StructMember(IndexerDeclaration)` |
| `record` 宣言 (C# 9) | **P** | positional record |
| `record struct` 宣言 (C# 10) | **Y** | readonly 含む。Equals/GetHashCode 呼びは対象外、文字列化は `ToString` を override すれば書け、しなければ TCS1001 `UnsupportedToString(T)` |
| `interface` 宣言 | **Y** | 出力なし |
| `enum` 宣言 | **Y** | |
| `delegate` 宣言 | **-** | |
| `partial` 型 (C# 2) | **-** | TCS1001 |
| `file` 型 (C# 11) | **-** | |

### 2.3 メンバー宣言

| 機能 | 状態 | 備考 |
|------|:----:|------|
| フィールド | **Y** | initializer なしは型別 default |
| フィールド初期化子 | **Y** | コンストラクタの本文の前に実行 |
| static フィールド / プロパティ初期化子 | **Y** | 型単位で、Main より前に実行する。初期化子 (と、そこから呼ぶ method・constructor・accessor・interface 実装の本文) が読み書きする他の型の static を先に初期化する (ファイル順・宣言順に依存しない)。lambda の本文・method group の先 (delegate を作るだけで実行しない) と delegate 値の呼び出しの先は辿らない。依存は制御フローを見ない過大評価 (分岐の両側、interface 呼び出しは全実装) なので、実際には起きない循環ができることがある。循環は宣言順で先の型を先に触ったものとして解き、相手は初期化中の型の default を読む (実際の循環なら、宣言順に触ったときの C# と同じ。偽の循環では C# と違い得る)。tcs VM と C は Main より前に全型を初期化するので、初期化子の副作用 (log 等) の順は初回アクセス時に初期化する .NET と違い得る |
| `const` フィールド | **Y** | 使用箇所で値に畳む |
| オートプロパティ (`{ get; set; }`) | **Y** | フィールドとして持つ。initializer なしは型別 default。interface を実装する自動 property は accessor も出し、interface 経由の読み書きはそれを通る |
| プロパティ初期化子 | **Y** | |
| カスタム getter/setter | **Y** | IL は `get_X` / `set_X` の method |
| 式本体メンバー (`=>`, C# 6) | **Y** | |
| インスタンスメソッド | **Y** | |
| 静的メソッド | **Y** | |
| メソッドの overload | **Y** | IL の名前に引数の型を付ける (`Sum__List_T__Func_T_int`、§28)。interface の method を実装する method は interface の method の名前 (interface 経由の呼び出しと同じ名前)。別の interface から同じ名前の method を実装して名前がぶつかる場合と、`--ref` の型の overload は TCS1001 `MethodOverload` |
| コンストラクタ | **Y** | 複数書ける。IL の名前は class が `new`、struct が `ctor` で、method の overload と同じ規則で、2 つ以上ある型の引数のある constructor に引数の型を付ける (`C.new__int`、`S.ctor__float__float`)。呼び出し箇所は束縛で選んだ constructor を直接呼ぶ。`: this(...)` は TCS1001 `ThisConstructorInitializer` |
| 静的コンストラクタ | **-** | |
| デストラクタ / ファイナライザ | **N/A** | |
| イベント | **-** | |
| インデクサ (`this[int]`) | **-** | |
| 演算子オーバーロード | **P** | 二項 `+ - * / %` と単項 `-`。IL は static 関数 (`V.__add` / `V.__sub` / `V.__mul` / `V.__div` / `V.__mod` / `V.__unm`、同じ operator の複数 overload は `V.__mul_1` `V.__mul_2` …) で、呼び出し箇所が束縛で選んだ overload を直接呼ぶ (実行時の振り分けは無い)。`==`/`!=`/比較系は TCS1001 (record の値の等価は合成する) |
| 暗黙/明示変換演算子 | **-** | TCS1001 |
| ローカル関数 (C# 7) | **-** | unsupported 診断あり |
| 静的ローカル関数 (C# 8) | **-** | unsupported 診断あり |
| 拡張メソッド (C# 3) | **P** | static method の呼び出しにする。LINQ (TinySystem.Linq) も user の拡張メソッドと同じ |
| 拡張メンバー (C# 14) | **-** | |
| primary constructor (C# 12) | **-** | |
| デフォルト引数値 | **P** | 省略した末尾の引数は、呼び出しが静的に束縛した宣言 (interface) の既定値を呼び出し側で渡す。明示の null は null のまま渡る (C# と同じ) |

### 2.4 ローカル宣言

| 機能 | 状態 | 備考 |
|------|:----:|------|
| `var x = expr;` | **Y** | |
| `int x = expr;` | **Y** | |
| `ref` ローカル (C# 7) | **N/A** | TCS1001 `RefType` |
| ローカル定数 `const` | **-** | |
| 分解宣言 `var (a, b) = ...` (C# 7) | **P** | 右辺を 1 回評価して展開する。record positional property 限定。既存変数への分解代入 `(a, b) = rhs` も対応 |
| 破棄 `_ = expr` (C# 7) | **Y** | 評価して捨てる |
| トップレベル文 (C# 9) | **Y** | 型の static を初期化してから文を実行 |
| using 宣言 `using var` (C# 8) | **-** | unsupported 診断あり |
| `scoped` ローカル (C# 11) | **-** | |
| パターン変数 `if (x is int i)` (C# 7) | **Y** | 式文脈 (ternary / 複合条件 / lambda) も束縛 |
| `required` メンバー (C# 11) | **-** | |

---

## 3. 文 (Statements)

| 文 | 状態 | 備考 |
|---|:----:|------|
| 変数宣言文 | **Y** | |
| 式文 (メソッド呼び出し等) | **Y** | |
| `if` / `else if` / `else` | **Y** | |
| `switch` 文 | **Y** | 定数に加え関係 (`case > 5:`) / or / 型 / `when` パターンラベル対応 |
| `while` ループ | **Y** | |
| `do-while` ループ | **Y** | |
| `for` ループ (C 形式) | **Y** | 一般 incrementor 対応 |
| `foreach` ループ | **Y** | List、配列、Dictionary とその `Keys` / `Values`、Queue、HashSet、`EnumerateRunes` |
| `break` | **Y** | |
| `continue` | **Y** | |
| `return` | **Y** | |
| `throw` | **-** | unsupported 診断あり |
| `try` / `catch` / `finally` | **-** | unsupported 診断あり |
| `using` 文 (リソース破棄) | **-** | unsupported 診断あり |
| `lock` | **-** | TCS1001 |
| `yield return` / `yield break` (C# 2) | **-** | |
| `goto` / ラベル | **-** | |
| `checked` / `unchecked` (文) | **-** | TCS1001 `CheckedStatement` |
| `fixed` | **N/A** | |
| `unsafe` | **N/A** | |
| `await foreach` (C# 8) | **-** | |
| 空文 (`;`) | **Y** | Roslyn がパース |

---

## 4. 式 (Expressions)

### 4.1 リテラル

| リテラル | 状態 | 備考 |
|---------|:----:|------|
| 整数 (`42`) | **Y** | |
| 浮動小数点 (`3.14`, `1.0f`, `2.5d`) | **Y** | `f` suffix の float。suffix の無い実数と `d` は TCS1001 `DoubleLiteral` (§1.1) |
| `bool` (`true`/`false`) | **Y** | |
| `null` | **Y** | |
| 文字列 (`"hello"`) | **Y** | UTF-8 の byte 列 |
| 補間文字列 (`$"...{x}..."`) | **Y** | object の hole は中の値で文字列にする (`__tcs_nstr`。値が float なら .NET と同じ文字列、null は `""`。C backend は object の補間を受けない)。float (と `float?`) の書式 `G` / `E` / `F` / `N` / `R`、int (と `int?`) の書式 `G` / `E` / `F` / `N` / `D` / `X` / `R` (小文字・精度つきも) は .NET の `ToString(format)` と同じ文字列 (`String.FormatFloat` / `String.FormatInt`、il-spec §13。`T?` の null は空文字列)。他の書式 (独自の書式 `0.00` / `#,0`、`C`、`P`、object と型引数の穴の書式、enum の `G` / `F` / `D` 以外) は TCS1001 `UnsupportedFormatSpecifier(書式)` (黙って別の文字列にしない)。bool / string / char / Rune と user の型の穴は書式を使わない (.NET も IFormattable でなければ無視する)。raw 補間 (`$$"""..."""`) の `$` の個数より短い brace の連続は文字のまま。書式指定子 `F N D X E G R` の精度に数字以外があれば TCS1001 `MalformedFormatSpecifier` |
| 文字 (`'A'`) | **Y** | 整数 code unit (§1.1) |
| 2進数 (`0b1010`, C# 7) | **Y** | |
| 16進数 (`0xFF`) | **Y** | |
| 桁区切り (`1_000_000`, C# 7) | **Y** | |
| verbatim 文字列 (`@"..."`) | **Y** | |
| raw 文字列 (`"""..."""`, C# 11) | **Y** | |
| UTF-8 文字列 (`"..."u8`, C# 11) | **N/A** | |

### 4.2 演算子

| 演算子 | 状態 | 備考 |
|--------|:----:|------|
| `+` `-` `*` `/` `%` (算術) | **Y** | 整数の `+ - *` は i32 の wrap。整数の `/` `%` は C# と同じ 0 方向の truncation で、除数 0 と `int.MinValue / -1` は fault (il-spec §5)。float の `%` は .NET と同じく被除数の符号を持つ剰余 |
| `+` (文字列連結) | **Y** | 型で自動判定。operand は float → .NET と同じ文字列 (`__tcs_fstr`)、bool / bool? → `True` / `False` (null は `""`)、object → 中の値 (`__tcs_nstr`。値が float なら float と同じ文字列、null は `""`。C backend は object の連結を受けない)、null の string → `""`。`+=` の右辺も同じ変換 |
| `==` `!=` | **Y** | record (class) の静的型どうしは値の等価。object / interface / `where T : class` の型引数どうしは C# と同じく参照の比較 (実行時の値が record でも値の等価を呼ばない。C backend は pointer の比較、generic の単相化の後も参照の比較のまま) |
| `<` `<=` `>` `>=` | **Y** | |
| `&&` `\|\|` | **Y** | 右 operand は必要なときだけ評価 |
| `!` (論理否定) | **Y** | |
| `-x` (単項マイナス) | **Y** | |
| `++x` `x++` (インクリメント) | **Y** | 前置は更新後、後置は更新前の値。custom property / 副作用 (getter 含む) receiver は 1 回評価。右側の ++ / 代入が書き換える local を左 operand / 代入先の添字が読むときは左を temp に退避 (C# の左→右評価) |
| `--x` `x--` (デクリメント) | **Y** | 同上 |
| `=` (代入) | **Y** | 受け手 / 添字は右辺より先に 1 回評価し、式位置の値は代入した値 (右辺が受け手を差し替えても読み直さない)。右辺が struct の格納先 (class の field、配列要素、static field) を差し替え得るときは、値を temp に置いてから格納先を辿り直して書く (`+=` 等 / `??=` も同じ) |
| `+=` `-=` `*=` `/=` `%=` `&=` `\|=` `^=` `<<=` `>>=` | **Y** | `x = x op y` と同じ演算。bool への `&=` `\|=` `^=` は未対応 |
| `? :` (三項) | **Y** | IL では temp + if の文になる (式の途中・入れ子・条件・lambda 本体も)。C# の評価順を保つ |
| `??` (null 合体) | **Y** | 右辺は左辺が null のときだけ評価 |
| `?.` (null 条件アクセス, C# 6) | **Y** | String/List/Dict の member も可。連鎖 (`x?.M().N()` / `x?.P.Q` / `x?[i].M()`) は一番内側を receiver に対する呼び出し / member にする。delegate の `f?.Invoke(...)` は null でなければ delegate 値を呼ぶ (戻り値は `T?` に wrap) |
| object への値の変換 (boxing) | **P** | int / float / bool だけ。char / byte / enum などは IL で int と同じ値になるので TCS1001 `ValueBoxing` (文字列連結、補間、`Console.WriteLine` の引数は文字列にするだけなので書ける) |
| `(T)x` (キャスト) | **Y** | 数値変換は il-spec §5 / §6 の契約: `(int)f` は 0 方向 truncation (`__tcs_trunc`)、整数 (int / uint / short / byte / char など) → float は f32 へ丸める (暗黙変換の代入・引数・return・比較の昇格も同じ。定数は float literal に畳む)。C は `(float)` / `tcs_trunc_f32`。object / interface / 型引数からの cast は実行時に型を確かめ、合わなければ fault する (class / record、generic でない interface、string、bool、int、float)。List / Dictionary / Queue / HashSet / 配列 / delegate / generic な型 / 型引数 `T` / enum / char / byte / `T?` への cast は TCS1001 `UnverifiableCast` |
| `is null` / `is not null` | **Y** | |
| `is Type` | **Y** | designation なしの binary 形も対応。判定できる型は §5 の型パターンの行 |
| `new T(args)` | **Y** | |
| `??=` (null 合体代入, C# 8) | **Y** | 式位置の値は代入後の左辺。左辺が `T?` なら値は `T` (C# の型)。受け手 / 添字は右辺より先に確定する (右辺が受け手の local を差し替えても元の object に書く) |
| `?[]` (null 条件インデクサ, C# 6) | **Y** | List / 配列 |
| `?.Prop = v` (null条件代入, C# 14) | **-** | |
| `as` (安全キャスト) | **-** | |
| `typeof(T)` | **-** | |
| `nameof(x)` (C# 6) | **Y** | 定数文字列。C# の定数式として両 backend で畳む (T250)。同名ユーザー method は通常 call |
| `default` / `default(T)` (C# 7.1) | **Y** | 型別 default |
| `sizeof(T)` | **N/A** | |
| `~` (ビット反転) | **Y** | 整数のみ (32 bit、下記注記) |
| `<<` `>>` (シフト) | **Y** | 整数のみ。C# 意味論 (count は 31 でマスク、`>>` は算術シフト)。`>>>` は未対応 |
| `&` `\|` `^` (ビット演算) | **Y** | 整数と enum のみ。bool operand は TCS1001 |
| `..` (Range, C# 8) | **-** | |
| `^x` (Index from end, C# 8) | **-** | |
| `new T { ... }` (初期化子) | **Y** | List/Dict、class (constructor の後に member へ代入)、`--ref` 型 (tcs VM と C は、class なら host の data class の object、struct なら 0 の値に field を入れた値)。ネストした初期化子 (`Child = { A = 1 }` / `Items = { a, b }`) は C# と同じく既存 member への代入 / Add |
| `new(args)` (ターゲット型, C# 9) | **Y** | 初期化子含む |
| `x!` (null 許容抑制, C# 8) | **Y** | 型の検査だけで、実行時の意味は無い |
| `stackalloc` | **N/A** | |
| `&x` `*x` `->` (ポインタ) | **N/A** | |
| `delegate { }` (匿名メソッド, C# 2) | **-** | |
| `delegate*` (関数ポインタ, C# 9) | **N/A** | |

ビット演算の幅意味論: 整数は i32 の 2 の補数 (il-spec §5) で、`~x`、負数を含む `& | ^`、`<<` の折り返しは C# `int` と一致する。シフトは count を 31 でマスクし、`>>` は算術シフトで、C# と同じ。


### 4.3 高度な式

| 式 | 状態 | 備考 |
|---|:----:|------|
| ラムダ (式本体) `x => expr` | **Y** | closure は変数を捕捉する (il-spec §7) |
| ラムダ (文本体) `(x) => { }` | **Y** | |
| switch 式 (C# 8) | **Y** | 網羅しない switch 式 (`_` の arm が無い) で一致する arm が無いと、tcs VM と C は結果の型の既定値で続く (.NET は SwitchExpressionException。il-spec §12 は fault と定める)。`tcs check` も診断しない (§27) |
| `this` | **Y** | |
| `base.Method()` | **N/A** | TCS1001 `BaseMemberAccess` (継承は対象外、§8) |
| メソッドチェーン (LINQ) | **Y** | |
| `with` 式 (C# 9) | **P** | 値の copy の上に member を上書き。record class / record struct / struct |
| LINQ クエリ構文 (C# 3) | **-** | メソッドチェーンで代替。TCS1001 `QueryExpression` |
| メソッドグループ変換 | **P** | static method (拡張 method を含む、型名で修飾してもしなくても) は呼び出しと同じ関数の参照。struct の parameter (readonly struct と `T?` を除く) は受ける側で copy する (il-spec §10)。tcs VM の method group の delegate は reload の後は新しい本文を呼ぶ (vm-design §8.4)。instance method の method group は TCS1001 (`InstanceMethodGroup`) |
| `throw` 式 (C# 7) | **-** | unsupported 診断あり |
| コレクション式 `[1,2,3]` (C# 12) | **-** | |
| タプル式 `(a, b)` (C# 7) | **Y** | struct の値。要素の名前 `(x: 1, y: 2)` も可。要素の型が違うタプルの値への変換 (`(float, float) f = intTuple`) は TCS1001 `TupleConversion`。文字列化は TCS1001 (`UnsupportedToString` / `StructBoxing`)。Dictionary の key は他の struct と同じく TCS1001 |
| 名前付き引数 `F(x: 1)` (C# 4) | **-** | TCS1001 `NamedArgument`。位置渡しに落ちて別の意味になるため build を止める error (§24) |
| `await` 式 (C# 5) | **N/A** | |
| 式ツリー `Expression<>` (C# 3) | **N/A** | |
| 静的ラムダ `static =>` (C# 9) | **-** | |
| ラムダのデフォルト引数 (C# 12) | **-** | |
| ラムダの戻り値型 (C# 10) | **-** | |
| ラムダに `params` (C# 12) | **-** | |
| `checked(expr)` / `unchecked(expr)` | **P** | checked な i32 の `+ - *` と単項 `-` は overflow で fault する (IL の `__tcs_cadd` 等、C は `tcs_checked_add` 等)。除算と float → int の変換は checked でなくても範囲外を fault にするので同じ。それ以外の checked な演算 (明示の整数変換、`++` / `--`、複合代入、int 以外の型、lifted) は TCS1001 `CheckedOperation` |

---

## 5. パターンマッチング

| パターン | C# ver | 状態 | 備考 |
|---------|--------|:----:|------|
| 定数パターン `is 42`, `is null` | 7 | **Y** | 入力が object で定数が数か bool のもの (`o is 5`、`o is true`) は TCS1001 `BoxedValuePattern(T)` (型パターンの行) |
| 型パターン `is MyClass` | 7 | **Y** | 値型 `T` / `T?` の入力 (struct、数、bool、enum) を同じ型 `T` で照合するのは値の有無だけ (`m is int y` は値があれば中の値を y に入れる)。object / interface / 型引数からの List / Dictionary / Queue / HashSet / 配列 / generic な型 / 型引数 `T` / enum / char / byte への型 test は TCS1001 `UnverifiableTypeTest`。実行時表現の無い型 (delegate、`object`、metadata の class、`--ref` の generic な型) への型 test は TCS1001 `TypeTestWithoutRuntimeType`。interface への `is` / `as` / 型 switch は class と record の値を判定する (tcs VM は実行時の型の表、C は型 id の switch)。interface の継承も辿る。次は TCS1001 `InterfaceTypeTest`: 参照型でない入力 (struct、`T?`、`class` 制約の無い `T`。struct は `StructBoxing` で interface に入らない)、generic な interface、metadata と `--ref` の interface。`--ref` の generic でない class / interface への型 test と cast は、`tcs check` と `tcs c` では TCS1001 `RefTypeTest` / `RefCast` / `RefInterfaceTypeTest` で、`tcs vm` は止めずに型の表で判定する (C の module と混ぜるときは型の番号を合わせる、object-model §10.6。`--ref` の interface への型 test は `tcs vm` も変換で止まる)。object の入力の数 (int / float) への型 test (`o is int`、`o is int or float`、`int =>` の arm) と、object に入った数と bool の値を読む pattern (宣言パターン `o is int x` / `b is bool z`、数と bool の定数パターン、`and` で型の test に続く pattern) は TCS1001 `BoxedValuePattern(T)`。IL は object の int と float を見分けず、C backend はこれらを持たない。値は cast (`(int)o`) で取り出す。object の `is bool` と string の pattern、`T?` の入力の pattern は対象外 |
| `not` パターン `is not null` | 9 | **Y** | |
| 破棄パターン `_` | 7 | **Y** | `_ when 条件` は条件付きの arm |
| 宣言パターン `is int i` | 7 | **Y** | if / ternary / 複合条件 / lambda / switch で束縛。struct は入力 (`S?` は中の値) の copy を束縛。入力が object で型が数か bool のもの (`o is int i`) は TCS1001 `BoxedValuePattern(T)` (型パターンの行) |
| `var` パターン `is var v` | 7 | **-** | |
| プロパティパターン `is { Name: "x" }` | 8 | **Y** | 型を書かない `{ }` も null を除く。`o is I { W: 1 }` のように型を書くと、型の検査の後に member をその型として読む。`T?` は値の有無を見て中の値の member を読む (`p is Pt { X: 2 }` のように型を書いたときも同じ) |
| 位置パターン `is (1, 2)` | 8 | **-** | |
| タプルパターン `(a, b) is (1, 2)` | 8 | **-** | TCS1001 `TuplePattern` |
| 関係パターン `is > 0` | 9 | **Y** | |
| `and` パターン `is > 0 and < 100` | 9 | **Y** | 入力が呼び出しを含む式なら temp に 1 回評価してから照合 (`or` / 複数条件の property パターンも同じ) |
| `or` パターン `is 1 or 2` | 9 | **Y** | |
| 括弧パターン `is not (A or B)` | 9 | **Y** | switch 式 / 文、`is` |
| 拡張プロパティパターン `{ A.B: v }` | 10 | **-** | |
| リストパターン `[1, 2, ..]` | 11 | **-** | |
| スライスパターン `[.., last]` | 11 | **-** | |
| Span パターン | 11 | **-** | |

---

## 6. 修飾子 (Modifiers)

### 6.1 アクセス修飾子

| 修飾子 | 状態 | 備考 |
|--------|:----:|------|
| `public` | **Y** | Roslyn 解決。実行時の区別は無い |
| `private` | **Y** | Roslyn 解決。実行時の区別は無い |
| `protected` | **N/A** | TCS1001 `ProtectedMember` (継承は対象外、§8) |
| `internal` | **Y** | Roslyn 解決。実行時の区別は無い |
| `protected internal` | **N/A** | TCS1001 `ProtectedMember` |
| `private protected` (C# 7.2) | **N/A** | TCS1001 `ProtectedMember` |
| `file` (C# 11) | **-** | |

### 6.2 型・メンバー修飾子

| 修飾子 | 状態 | 備考 |
|--------|:----:|------|
| `static` | **Y** | static メソッド/フィールド |
| `abstract` | **N/A** | TCS1001 `AbstractClass` / `AbstractMember` (継承は対象外、§8) |
| `virtual` | **N/A** | TCS1001 `VirtualMember` |
| `override` | **P** | `override string ToString()` だけ (class / record / struct / record struct)。呼び出し、連結、補間、`Console.WriteLine` は静的に解決して直接呼ぶ。null の値と、ToString が返した null は連結と補間では空文字列 (C# と同じ)。struct の連結と補間は copy の上で呼ぶ。型引数の値も呼ぶ。`string.Join` の要素と `S?` の連結 / 補間 / `Console.WriteLine` は TCS1001 (`?.ToString()` は書ける)。他の override は TCS1001 `OverrideMember` (`sealed override` も) |
| `sealed` | **P** | `sealed class` は可 (意味は変わらない)。member の `sealed` は TCS1001 `SealedMember` |
| `new` (メンバー隠蔽) | **N/A** | TCS1001 `NewMemberHiding` |
| `readonly` | **Y** | field (Roslyn が代入を検査し、実行時は通常の field)、`readonly struct` / `readonly record struct` (copy 省略) |
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
| `ref` | **N/A** | ユーザー定義メソッドの宣言は TCS1001 `RefParameter`、`--ref` host メソッドへの `ref` 引数は TCS1001 `RefArgument`。ref の戻り値は TCS1001 `RefType` |
| `out` | **Y** | 自作メソッドは `out var` / `out T x` / `out _` と、前もって宣言した局所変数 (lambda に capture されない物) を渡せる (tcs VM は戻り値の後ろの register で返し、C はポインタで写す)。field、配列の要素、parameter、lambda に capture される局所変数を渡すのは TCS1001 (`OutArgumentNotDeclaration`。受け手には呼び出しが正常に終わってから書くので、呼び出しの中から見える受け手は C# と違って見える)。同じ局所変数を 1 つの呼び出しの 2 つの out に渡すのも TCS1001 (`OutArgumentDuplicate`。呼び出しの中で parameter が別名になる)。delegate / lambda / constructor の out は TCS1001 (`OutParameter`)。`--ref` の host メソッドと BCL (`TryGetValue` / `TryParse`) は宣言済みの変数も受ける |
| `in` (C# 7.2) | **N/A** | ユーザー定義メソッドの宣言は TCS1001 `InParameter`、`--ref` host メソッドへの `in` 引数は TCS1001 `InArgument` |
| `ref readonly` (C# 12) | **N/A** | TCS1001 `RefParameter` / `RefType` |
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
| 組み込み `List<T>`, `Dictionary<K,V>` | **Y** | 単相化した要素型で持つ |
| `Action<T>` / `Func<T,R>` | **P** | closure (tcs VM は proto と捕捉の列、C は lifted 関数と捕捉の cell) |
| ユーザー定義ジェネリック型 (class / record / struct / record struct / interface) | **Y** | tcs VM と C は閉じた型の copy に特殊化する (単相化。top level の型と、generic でない型の中の型だけ)。static field / static auto property は TCS1001 `GenericStaticField` (const は可)。外から `Box<int>.M()` のように static member を呼ぶ形は対象外 |
| ジェネリックメソッド | **Y** | tcs VM と C は閉じた method に特殊化する。method group (`G.Id<int>`、型引数を推論させた `G.Id`) も。interface の generic method は、interface と実装する型に同じ名前の閉じた method を作る |
| 型制約 (`where T : ...`) | **Y** | Roslyn が検査し、interface 制約の member 呼び出しは実装の method を呼ぶ。`IComparable<T>` 経由の比較などは API 側で TCS1002 (§12) |
| 共変性/反変性 (`in`/`out`, C# 4) | **-** | |
| 静的抽象/仮想インターフェースメンバー (C# 11) | **-** | interface の operator 宣言は TCS1001 `InterfaceOperatorDeclaration` |

---

## 8. 継承

class / record の継承は対象外 (q.md Q15)。多態は interface の実装と型による分岐、組み合わせは field で書く。以下は TCS1001 で build を止める (`tcs check` は exit 1、`tcs vm` / `tcs c` は出力を書かない。analyzer は warning)。`--ref` の型 (host の宣言) は診断の対象にしない。

| 機能 | 状態 | 備考 |
|------|:----:|------|
| 基底 class (`class D : B`、`record D(...) : B(...)`) | **N/A** | TCS1001 `ClassInheritance(B)`。`System.Attribute` などの BCL の class も同じ |
| `base(...)` コンストラクタ | **N/A** | TCS1001 `BaseConstructorInitializer` |
| `base.Method()` | **N/A** | TCS1001 `BaseMemberAccess` |
| 多重インターフェース実装 | **Y** | class / record / struct の `: IFoo, IBar`。interface 同士の継承も可 |
| 抽象クラス | **N/A** | TCS1001 `AbstractClass` |
| 共変戻り値型 (C# 9) | **N/A** | |

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
| 7.3 | 2018 | タプル `==`/`!=` (TCS1001 `TupleEquality`), unmanaged 制約 | **-** |
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

予約語は `and` `break` `do` `else` `elseif` `end` `false` `for` `function` `global` `goto` `if` `in` `local` `nil` `not` `or` `repeat` `return` `then` `true` `until` `while`。

| 識別子 | 状態 | 備考 |
|--------|:----:|------|
| 予約語と同名のローカル束縛 (local 変数 / parameter / lambda parameter / foreach 変数 / pattern designation の `local`, `end`, `nil` 等) | **Y** | IL の local の名前は `local` → `local_` に写す。写した先が同じ member body (lambda 連鎖を含む) に識別子として現れる場合は `_` を足して衝突を避ける (`local_` があれば `local__`)。宣言と全参照 (closure 内含む) を同じ symbol として写す (`LocalNames`) |
| 予約語と同名の member 宣言 (型 / method / property / field / enum メンバ / record positional parameter の `end`, `repeat`, `until`, `global` 等) | **-** | TCS1001 `LuaKeywordIdentifier(name)` |
| `self` と同名の宣言 | **-** | TCS1001 `ReservedIdentifier(self)` (ローカル束縛も写さずに止める)。IL は instance member の receiver を `self` と呼ぶ。`__tcs_` を含む `__` で始まる名前は `DoubleUnderscoreIdentifier` (§28) |
| runtime の型と同名の型宣言 (`TinySystem`, `List`, `Dict`, `Math`, `String`, `TcsQueue`, `TcsHashSet`) | **-** | TCS1001 `RuntimeGlobalIdentifier(name)`。build を止める error (§24)。namespace の中の型も同じ。集合は `Compliance.RuntimeGlobals`。interface と `Random` は対象外 |
| ユーザ型と同名のローカル束縛 (`static int Sum(V a, int V)` 等) | **Y** | 予約語と同じく、IL の local の名前は `V` → `V_` に写す (IL は型を名前で参照する: `V.new`、operator の `V.__add`、型内 static member)。写した先がユーザ型名と同じなら更に `_` を足す |
| verbatim 識別子 (`@float`, `@out` 等) | **Y** | `@` を除いた名前。予約語になるもの (`@end` 等) はローカル束縛なら上記のとおり写し、member なら TCS1001 |

---

# Part II: 標準ライブラリ

BCL の型は既定で通さない。下の一覧で対応している型と member だけが通り、それ以外の `System.*` / `Microsoft.*` の型は、member を使う地点も型を書く地点 (宣言、基底、キャスト、型引数) も TCS1002 で止まる。
属性の中は実行時の意味を持たないので対象外。

---

## 12. System 名前空間 — 型一覧

| 型 | 状態 | 対応内容 | 備考 |
|----|:----:|---------|------|
| `Object` | **P** | `ToString()` は中の値で文字列にする (`__tcs_nstr`)。`Equals` / `GetHashCode` / `ReferenceEquals` / `GetType` / `MemberwiseClone` (record と数値と enum の合成 / 継承した member も) は TCS1002 (値の比較は `==`) | 値が float なら .NET と同じ文字列。class / record / struct / List / Dictionary の値は fault (静的型がそれらなら TCS1001 `UnsupportedToString(T)`)。型引数の値も同じ。C backend は object の ToString を受けない |
| `String` | **P** | 後述の個別メンバー表参照 | |
| `Math` | **Y** | 後述の個別メンバー表参照 | |
| `MathF` | **-** | | float 版 Math。TCS1002 (System.Math の float overload を使う) |
| `Console` | **Y** | `WriteLine` と 1 引数の `Write` (文字列化は WriteLine と同じ) | object の引数は中の値で文字列にする (`__tcs_nstr`。値が float なら .NET と同じ文字列、null は空行。C backend は object の引数を受けない)。null の string は空文字列として書く (`WriteLine` なら空行) |
| `int` / `float` の `Parse(string)` | **Y** | | 不正な文字列は fault (.NET は FormatException) |
| `int` / `float` の `TryParse(string, out v)` | **Y** | IL は `Math.TryParseInt` / `Math.TryParseFloat` | 失敗時の out は 0。int は NumberStyles.Integer (前後の空白、先頭の符号、10 進数字だけ。小数・指数・16 進・空白だけは失敗) |
| `int` / `float` の `ToString(format)` | **Y** | `String.FormatInt` / `String.FormatFloat` | 定数の標準の書式だけ (補間の書式と同じ: float は `G` / `E` / `F` / `N` / `R`、int は加えて `D` / `X`、小文字と 9 桁までの精度も)。空の書式は書式なしと同じ。他の書式と定数でない書式は TCS1002 |
| `int` / `float` の `CompareTo(同じ型)` | **Y** | IL は `Math.CompareTo(a, b)` | -1 / 0 / 1。float の NaN は他のどの値より小さく、NaN 同士は 0 (Single.CompareTo と同じ)。それ以外の CompareTo (object overload の `i.CompareTo(5L)`、enum、`IComparable<T>` 経由と generic の `T`、差を返す `char`、`string`) は TCS1002 |
| 数値と bool の他の static method (`float.IsNaN`、`float.IsInfinity`、`int.Max`、`float.Abs`、`bool.Parse` など) | **-** | | TCS1002。NaN は `x != x`、最大と絶対値は `Math.Max` / `Math.Abs` で書く。定数 (`int.MaxValue`、`float.NaN`、`float.PositiveInfinity`、`float.NegativeInfinity` など) は使える |
| `Convert` | **-** | | |
| `Environment` | **P** | `GetEnvironmentVariable(s)` (無ければ null) | 他メンバーは未対応 |
| `Array` | **-** | | List で代替。`Array.Sort` などの member (配列の `Length` 以外) は TCS1002 |
| `Tuple` | **-** | | |
| `ValueTuple` | **-** | タプルの構文で書く。型名を書くのは TCS1001 `ValueTupleName` | |
| `Nullable<T>` | **Y** | `int?` 等 (Part I 参照) | 配列と Dictionary の値の null は TCS1003。List の要素は null を持てる |
| `Enum` (静的メソッド) | **-** | | TCS1002 |
| `Exception` (全サブクラス) | **-** | | try/catch 未対応 |
| `IDisposable` | **-** | | |
| `IComparable<T>` | **-** | | |
| `IEquatable<T>` | **-** | | |
| `ICloneable` | **-** | | |
| `Guid` | **-** | | |
| `DateTime` | **-** | | |
| `TimeSpan` | **-** | | |
| `Random` | **Y** | `TinySystem.Random` (§15) | `using System;` だけで見える `Random` は System.Random で TCS1002 |
| `StringComparison` | **-** | | |
| `Type` | **N/A** | | リフレクション。型を書くのは可、member は TCS1002 |
| `Attribute` | **N/A** | | 自作の属性の基底に書くのは可、member は TCS1002 |
| `GC` | **N/A** | | |
| `Action<>` / `Func<>` / `Predicate<>` | **Y** | closure (§7) | `f.Invoke(...)` / `f?.Invoke(...)` は `f(...)` と同じ呼び出し。delegate どうしの `==` / `!=` は TCS1001 `DelegateEquality`、`+` / `-` / `+=` / `-=` は TCS1001 `DelegateCombine`、`Equals` は TCS1002 (C は変換のたびに closure を作り、C# の結果と揃えられない)。null との比較は可 |
| `Lazy<T>` | **-** | | |
| `WeakReference<T>` | **-** | | |

---

## 13. String メンバー

文字列は UTF-8。.NET の `string` だけが UTF-16 で、位置を扱う API の結果が backend で揃わないので、`Length`、`Substring`、`IndexOf`、添字、`foreach (char c in s)` は TCS1002 で止め、診断に置き換え先を書く。文字と byte の位置は §20b。

| メンバー | 状態 | 備考 |
|---------|:----:|------|
| `.Length` | **N/A** | TCS1002。`s.RuneCount()` か `new Utf8Bytes(s).Length` (§20b) |
| `.Contains(s)` | **Y** | |
| `.Replace(old, new)` | **Y** | `old == ""` は fault (.NET は ArgumentException)、`new == null` は削除 |
| `.StartsWith(s)` | **Y** | |
| `.EndsWith(s)` | **Y** | 空の suffix は true |
| `.Trim()` | **Y** | |
| `.Substring(i)` / `.Substring(i,n)` | **N/A** | TCS1002。`s.RuneSubstring(start, count)` か `Utf8Bytes.Slice` (§20b) |
| `.Split()` / `.Split(sep)` | **Y** | 引数なしは whitespace、空 / null の separator は元の文字列 1 要素。空の要素を残す |
| `.ToUpper()` | **Y** | ASCII の文字だけを変える (.NET は ASCII の外の文字も変える。§27) |
| `.ToLower()` | **Y** | 同上 |
| `.ToString()` | **Y** | |
| `string + string` | **Y** | |
| `.IndexOf(c)` / `.IndexOf(s)` | **N/A** | TCS1002。`Contains`、`Split`、`Utf8Bytes` の走査 (§20b) |
| `.LastIndexOf(c)` / `.LastIndexOf(s)` | **-** | |
| `.IndexOfAny(chars)` | **-** | |
| `.Insert(i, s)` | **-** | |
| `.Remove(i)` / `.Remove(i, n)` | **-** | |
| `.PadLeft(n)` / `.PadRight(n)` | **-** | |
| `.TrimStart()` / `.TrimEnd()` | **-** | |
| `.ToCharArray()` | **-** | |
| `.Equals(s, comp)` | **-** | |
| `.CompareTo(s)` | **-** | |
| `String.IsNullOrEmpty(s)` (static) | **Y** | null か `""` |
| `string.CompareOrdinal(a, b)` (static) | **Y** | -1 / 0 / 1。順序は il-spec §11 の ordinal で、null は他のどの string より小さい。値は 0 との比較 (`< 0`、`== 0` など) の operand でだけ使える。.NET の契約は符号だけで、値そのものは 2 文字ずつ比べる位置で変わる (`("", "x")` は -120、`("abc", "ab")` は 1) ので、それ以外の使い方は TCS1001 `CompareOrdinalValue`。範囲つきの overload は TCS1002 |
| `String.IsNullOrWhiteSpace(s)` (static) | **-** | |
| `String.Format(fmt, ...)` (static) | **-** | 補間で代替 |
| `String.Join(sep, ...)` (static) | **Y** | List / 配列の要素は連結と同じく文字列化する (float は .NET と同じ文字列)。C backend の要素は string / int / float / bool |
| `String.Concat(...)` (static) | **-** | |
| `String.Compare(a, b)` (static) | **-** | |
| `String.Equals(a, b)` (static) | **-** | |
| `String.Empty` (static) | **-** | |
| `.ReplaceLineEndings()` | **-** | |
| `[int]` (char indexer) | **N/A** | TCS1002。`new Utf8Bytes(s)[i]` (§20b) か `EnumerateRunes` |
| `(int)ch` / `(char)n` (char ↔ int) | **Y** | `(int)ch` は恒等 (char は整数)、`(int)'a'` は定数畳み込み。`(char)n` は範囲を検査する (`__tcs_char`。byte と範囲内の定数からは恒等) |
| `foreach (char c in s)` | **N/A** | TCS1002。`foreach (var r in s.EnumerateRunes())` か `Utf8Bytes` の走査 (§20b) |
| `.Contains(char)` / `.StartsWith(char)` / `.EndsWith(char)` / `.Replace(char, char)` / `.Split(char)` | **Y** | char 引数を 1 byte の string として同名の string の版と同じに扱う |
| `.EnumerateRunes()` | **Y** | `foreach` の collection 位置限定。壊れた並びは `Encoding.UTF8` と同じ位置で U+FFFD として読み続ける (3 経路で同じ)。`r.Value` (`System.Text.Rune`) は codepoint 整数そのもの。`r` の文字列化 (`ToString`・連結・補間・`WriteLine`) は `Char.ConvertFromUtf32(r)` |

`Trim()` と引数なしの `Split()` の空白は .NET の `char.IsWhiteSpace` と同じ Unicode の集合で、3 経路で結果が同じ (`runtime/utf8` の `tcs_utf8_is_white_space`)。

---

## 13b. Char メンバー (static、整数 code unit の ASCII 判定 / 変換)

| メンバー | 状態 | 備考 |
|---------|:----:|------|
| `char.IsDigit(c)` / `IsLetter` / `IsLetterOrDigit` / `IsWhiteSpace` / `IsUpper` / `IsLower` | **Y** | ASCII の範囲 (tcs VM と C は同じ表) |
| `char.ToUpper(c)` / `char.ToLower(c)` | **Y** | ASCII |
| `c.ToString()` | **Y** | 1 byte の string |
| `char.ConvertFromUtf32(n)` | **Y** | codepoint の UTF-8 文字列。負の値、surrogate (0xD800〜0xDFFF)、0x10FFFF 超は tcs VM / C とも同じ文言で fault (.NET は ArgumentOutOfRangeException) |
| `char.IsPunctuation` / `IsSymbol` / `IsControl` / `GetNumericValue` | **-** | |

---

## 14. Math メンバー

| メンバー | 状態 | 備考 |
|---------|:----:|------|
| `Math.PI` | **Y** | |
| `Math.Min(a, b)` | **Y** | float は MathF.Min と同じく NaN を伝播し、-0 を +0 より小さいとみなす |
| `Math.Max(a, b)` | **Y** | float は MathF.Max と同じく NaN を伝播し、+0 を -0 より大きいとみなす |
| `Math.Clamp(v, min, max)` | **Y** | |
| `Math.Abs(x)` | **Y** | |
| `Math.Floor(x)` | **Y** | 結果は float のまま (-0 を保つ) |
| `Math.Ceiling(x)` | **Y** | 結果は float のまま (-0 を保つ) |
| `Math.Sqrt(x)` | **Y** | |
| `Math.Sin(x)` | **Y** | |
| `Math.Cos(x)` | **Y** | |
| `Math.Atan2(y, x)` | **Y** | |
| `Math.E` | **-** | |
| `Math.Tau` | **-** | |
| `Math.Pow(x, y)` | **Y** | |
| `Math.Exp(x)` | **Y** | |
| `Math.Log(x)` / `Math.Log(x, base)` | **Y** | base が 1、または x が 1 でなく base が 0 / +∞ なら NaN (MathF.Log と同じ) |
| `Math.Log10(x)` / `Math.Log2(x)` | **-** | `Math.Log(x, base)` で代替 |
| `Math.Round(x)` / `Math.Round(x, digits)` | **Y** | C# と同じ偶数丸め (banker's rounding)。0 に丸めた結果は x の符号を持つ (`Round(-0.25)` は -0) |
| `Math.Truncate(x)` | **-** | |
| `Math.Sign(x)` | **Y** | |
| `Math.Tan(x)` | **Y** | |
| `Math.Asin(x)` / `Math.Acos(x)` / `Math.Atan(x)` | **-** | |
| `Math.Sinh(x)` / `Math.Cosh(x)` / `Math.Tanh(x)` | **-** | |
| `Math.Cbrt(x)` | **-** | |
| `Math.CopySign(x, y)` | **-** | |
| `Math.FusedMultiplyAdd(x, y, z)` | **-** | |
| `Math.IEEERemainder(x, y)` | **-** | |
| `Math.DivRem(a, b)` | **-** | |
| `Math.BigMul(a, b)` | **-** | |
| `Math.ScaleB(x, n)` | **-** | |
| `Math.MaxMagnitude(x, y)` / `Math.MinMagnitude(x, y)` | **-** | |
| `Math.SinCos(x)` | **-** | |

---

## 15. Random メンバー

`Random` は `TinySystem.Random` として使う (`using TinySystem;` か `TinySystem.Random` と完全修飾)。
`using System;` だけで見える `Random` は System.Random と見なし、TCS1002 (`use TinySystem.Random`) になる。

| メンバー | 状態 | 備考 |
|---------|:----:|------|
| `new Random()` / `new Random(seed)` | **Y** | `TinySystem.Random` (System.Random と同じ形)。tcs VM と C は同じ xoshiro256** の列を作り、seed 固定時は bit 一致し、`Random.Seed(seed)` 後の `Shared` とも一致 (il-spec §13)。seed 未指定は時刻などから作る seed で予測できるので、敵対的な用途には向かない。instance は GC object |
| `Random.Shared` (static) | **Y** | |
| `Random.Seed(seed)` | **Y** | `Shared` の列の固定 (tcs 独自。System.Random に無い) |
| `r.Next()` / `r.Next(max)` / `r.Next(min, max)` | **Y** | .NET 標準 API (上限は exclusive。空の範囲 `Next(0)` / `Next(n, n)` は下限を返す) |
| `r.NextFloat()` / `r.NextSingle()` | **Y** | [0, 1) の float |
| `r.Range(min, max)` | **Y** | 両端含む整数 (tcs 独自) |
| `Random.NextDouble()` | **-** | double はサブセット外 |
| `Random.NextBytes(buf)` | **-** | |
| `Random.Shuffle(arr)` | **-** | |

---

## 16. System.Collections.Generic — 型一覧

| 型 | 状態 | 備考 |
|----|:----:|------|
| `List<T>` | **Y** | 要素は null を持てる (§17) |
| `Dictionary<TKey, TValue>` | **Y** | null 値は TCS1003 の警告 (§18)。値で比べる key (struct / record) は TCS1001 で build を止める |
| `HashSet<T>` | **Y** | 値で比べる要素 (struct / record) と float は TCS1001 (§17c) |
| `Queue<T>` | **Y** | §17b |
| `Stack<T>` | **-** | |
| `LinkedList<T>` | **-** | |
| `SortedList<TK, TV>` | **-** | |
| `SortedDictionary<TK, TV>` | **-** | TCS1002 |
| `SortedSet<T>` | **-** | |
| `KeyValuePair<TK, TV>` | **P** | foreach 内で暗黙使用。`Key` / `Value` |
| `IEnumerable<T>` | **N/A** | Roslyn 型チェックのみ。型を書くのは可、member は TCS1002 |
| `ICollection<T>` | **N/A** | 同上 |
| `IList<T>` | **N/A** | 同上 |
| `IDictionary<TK, TV>` | **N/A** | 同上 |
| `IReadOnlyList<T>` / `IReadOnlyCollection<T>` | **N/A** | 同上 |

---

## 17. List\<T\> メンバー

要素に `null` を入れられる (tcs VM / C / .NET で同じ結果)。tcs VM と C は要素の値を連続した buffer に置く。

| メンバー | 状態 | 備考 |
|---------|:----:|------|
| `new List<T>()` | **Y** | |
| `new List<T> { 1, 2, 3 }` | **Y** | |
| `new List<T>(source)` | **Y** | source は `List<T>`、`T[]`、Dictionary の `Keys` / `Values`。initializer との併用は対象外 |
| `list[i]` (get/set) | **Y** | 範囲外は fault (il-spec §12)。書き込みは受け手、添字、値を評価してから範囲を見る (C# の順序)。負の添字も範囲外。配列も同じ |
| `.Count` | **Y** | |
| `.Add(item)` | **Y** | |
| `.Remove(item)` | **Y** | 等価は EqualityComparer\<T\>.Default (float と `float?` の NaN 同士、float field が NaN の struct 同士は等しい)。Contains / IndexOf も同じ |
| `.RemoveAt(index)` | **Y** | |
| `.Clear()` | **Y** | |
| `.Contains(item)` | **Y** | |
| `.IndexOf(item)` | **Y** | |
| `.FindIndex(match)` | **Y** | 無ければ -1。開始位置つきの overload は TCS1002 |
| `.RemoveAll(match)` | **Y** | .NET と同じく述語は各要素に 1 回ずつ先頭から呼び、残す要素の順を保つ。消した数を返す |
| `.AddRange(collection)` | **-** | |
| `.Insert(index, item)` | **Y** | 範囲外 (0 未満か Count より大きい) は fault |
| `.InsertRange(index, collection)` | **-** | |
| `.RemoveRange(index, count)` | **-** | |
| `.Find(predicate)` / `.FindLast()` / `.FindAll()` | **-** | |
| `.FindLastIndex()` | **-** | |
| `.Sort()` / `.Sort(comparison)` | **Y** | 比較なしの順序は .NET の既定の comparer と同じく NaN が最小。string の順序は .NET の ordinal (UTF-16 の code unit の順。大文字が小文字より先)。.NET と同じ introsort で、同値の要素の並びと比較関数の呼び出し列も同じ |
| `.Reverse()` | **-** | |
| `.BinarySearch(item)` | **-** | |
| `.Exists(predicate)` | **-** | |
| `.ForEach(action)` | **-** | |
| `.GetRange(index, count)` | **Y** | 範囲外は fault |
| `.Slice()` | **-** | |
| `.ConvertAll(converter)` | **-** | |
| `.CopyTo(array)` / `.ToArray()` | **-** | |
| `.TrueForAll(predicate)` | **-** | |
| `.LastIndexOf(item)` | **-** | |
| `.AsReadOnly()` | **-** | |
| `.Capacity` | **-** | |

---

## 17b. Queue\<T\> メンバー

C は連続 buffer と先頭の添字で、先頭の空きが半分以上あれば詰める (Enqueue / Dequeue は償却 O(1))。tcs VM は List と同じ連続 buffer で、Dequeue は残りの要素を前に詰める (要素数に比例する時間)。要素は null を持てる。

| メンバー | 状態 | 備考 |
|---------|:----:|------|
| `new Queue<T>()` | **Y** | |
| `.Enqueue(item)` | **Y** | |
| `.Dequeue()` | **Y** | 空なら fault |
| `.Peek()` | **Y** | 空なら fault。変更可能な struct の要素は copy |
| `.Count` | **Y** | |
| `.Clear()` | **Y** | |
| `.Contains(item)` | **Y** | 等価は `List.Contains` と同じ |
| `foreach` | **Y** | 始めた時点の要素の写しを先頭から反復する (途中の Enqueue / Dequeue は列挙に影響しない。.NET は InvalidOperationException) |
| `new Queue<T>(source)` / `new Queue<T>(capacity)` | **-** | TCS1002 |
| `.TryDequeue` / `.TryPeek` / `.ToArray` / `.CopyTo` / `.EnsureCapacity` / `.TrimExcess` | **-** | TCS1002 |

---

## 17c. HashSet\<T\> メンバー

要素は int / enum / char / string / class (同一性) / null。.NET の HashSet と同じ構造で、列挙の順は slot の順 (Remove した slot は次の Add が最後に空いたものから使う) なので 3 経路で同じ順になる。
値で比べる要素 (struct / record struct / struct の `T?` / record class) は Dictionary の key と同じく対応外で、float と double も対応外 (作る地点が TCS1001 `UnsupportedHashSetElement(T)`)。
`bool` の要素は tcs VM では動くが C backend が止める (`HashSet element type not supported`)。`int?` の要素は tcs VM (`struct keys are not in the VM yet`) と C backend の両方が止める。要素の型が型 parameter の HashSet は対象外 (Dictionary の key と同じ)。
C backend の class の要素の hash は address で、若い object を入れたまま GC の境界を跨ぐと次の操作で 1 回作り直す。

| メンバー | 状態 | 備考 |
|---------|:----:|------|
| `new HashSet<T>()` | **Y** | |
| `new HashSet<T> { a, b }` | **Y** | |
| `.Add(item)` | **Y** | 追加したら true |
| `.Remove(item)` | **Y** | 消したら true |
| `.Contains(item)` | **Y** | |
| `.Count` | **Y** | |
| `.Clear()` | **Y** | |
| `foreach` | **Y** | 始めた時点の要素の写しを slot の順に反復する |
| `new HashSet<T>(source)` / `new HashSet<T>(capacity)` / comparer の overload | **-** | TCS1002 |
| `.UnionWith` などの集合演算 / `.TryGetValue` / `.RemoveWhere` / `.CopyTo` | **-** | TCS1002 |

---

## 18. Dictionary\<TKey, TValue\> メンバー

列挙の順 (.NET の Dictionary の順ではなく tcs の仕様):

- `foreach`、`Keys`、`Values` は、今入っている key を最初に入れた順に列挙する。既にある key への上書きは位置を変えず、
  `Remove` した key をもう一度足すと末尾に付く
- Dictionary の `foreach` の途中で新しい key を足すのは fault (.NET は `InvalidOperationException`)。値の上書きと
  `Remove` は許し、消した key はその後の列挙に出ない
- HashSet も挿入順。HashSet の `foreach` は始めた時点の要素を回り、途中の変更は列挙に影響しない
- C backend、VM、.NET (TinySystem.props が入れる `TinySystem/Collections.cs` の Dictionary / HashSet が BCL の同じ名前の型を隠す) が同じ順になる (`tcs2c.Tests` の `DictOrder_*`)

値の `null` (直接の `null` / 参照型の `default`) は TCS1003 の警告 (`tcs check` は exit 1)。
tcs VM と C は null の値を持ち、`Count`、`ContainsKey`、`TryGetValue`、列挙は .NET と同じ結果になる。

float と double の key の Dictionary は対応外。
.NET (`EqualityComparer<float>.Default`) は NaN どうしを同じ key とみなすが、C backend は float の key を持たず、VM は NaN の key と列挙を持たない。
HashSet の float の要素 (`UnsupportedHashSetElement(float)`) と同じく、作る地点 (`new Dictionary<float, V>()`、target-typed `new()`) を TCS1001 `UnsupportedDictionaryKey(float)` とし、`tcs vm` / `tcs c` は build を止める (analyzer は warning)。
key 型が型 parameter の dict (generic method の中で作るもの) は対象外。

user の class の key (参照で比べる) は tcs VM が .NET と同じ結果を出し、C backend は emit で `Dictionary key type not supported` として止まる (`tcs check` は通す)。

値で比べる key (struct / record struct / struct の `T?` / record class) の Dictionary は対応外。
.NET (`EqualityComparer<K>.Default`) は等しい値の key を同じ key とみなす。
作る地点 (`new Dictionary<K, V>()`、target-typed `new()`) を TCS1001 `ValueEqualityDictionaryKey(K)` とし、`tcs vm` / `tcs c` は build を止める (analyzer は warning)。
key 型が型 parameter の dict (generic method の中で作るもの) は対象外。

C backend の `Dictionary<string, V>` のハッシュは seed を持たない FNV-1a で、chain の長さに上限が無い。
列挙の順はハッシュに依らず挿入順 (上の規則)。
ハッシュに seed が無いので、同じハッシュ値の文字列を作るのは容易で、host が外から受け取った信頼できない文字列を key にすると、N 回の操作に N の 2 乗に比例する時間がかかりうる。

| メンバー | 状態 | 備考 |
|---------|:----:|------|
| `new Dictionary<K,V>()` | **Y** | |
| `new Dictionary<K,V> { ... }` | **Y** | |
| `dict[key]` (get/set) | **Y** | 無い key の読みは fault (.NET は KeyNotFoundException) |
| `.Count` | **Y** | |
| `.Add(key, value)` | **Y** | 既にある key の Add は tcs VM と C では値を上書きする (.NET は ArgumentException。§27) |
| `.Remove(key)` | **Y** | |
| `.ContainsKey(key)` | **Y** | |
| `.Keys` | **Y** | |
| `.Values` | **Y** | |
| `.TryGetValue(key, out value)` | **Y** | 無ければ out は default で false |
| `.TryAdd(key, value)` | **-** | |
| `.ContainsValue(value)` | **-** | |
| `.Clear()` | **Y** | |

---

## 19. LINQ — TinySystem.Linq

LINQ は `TinySystem.Linq.Enumerable` (`TinySystem/Linq.cs`) の `List<T>` の拡張メソッドで、`using TinySystem.Linq;` で使う (q.md Q18)。
tcs のサブセットの C# で書いてあり、tcs は user のコードと一緒に IL にして tcs VM / C で動かし、.NET の project は `TinySystem/TinySystem.props` で同じファイルをコンパイルするので、3 経路が同じ実装で動く。
意味の正本はそのファイルと doc comment。`System.Linq` は namespace ごと TCS1002 (`using System.Linq;` と並べると、受け手が `List<T>` の TinySystem.Linq が選ばれ、TinySystem.Linq に無い overload は TCS1002)。
すべて即時評価で、列を返す演算は新しい `List<T>` を返す。callback は各要素にちょうど 1 回、先頭から順に呼び、走査は呼び出した時点の Count まで。メソッドチェーンだけで、クエリ構文 (`from x in ...`) は対象外。
毎フレームの一時の `List<T>` は作ってよい (int の Where は要素あたり C で約 5 ns、tcs VM で約 30 ns。q.md Q18)。struct の要素を `readonly record struct` にすると IL は lambda の parameter の copy を省くが、測った範囲では tcs VM と C の時間は変わらない。

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
Dictionary の `Keys` / `Values` も受け手にでき (`d.Values.Where(..).OrderBy(..)`、`d.Values.Sum()`)、List に写して List の版に渡す。列挙の順は挿入順 (§18)。
配列は LINQ の受け手にならない (`new List<T>(array)` で写す)。遅延評価 (`IEnumerable<T>`) は持たない。

---

## 20. System.Text

| 型 | 状態 | 備考 |
|----|:----:|------|
| `StringBuilder` | **-** | |
| `Encoding` | **-** | |
| `Regex` (`System.Text.RegularExpressions`) | **-** | |
| `Rune` | **P** | `string.EnumerateRunes()` の foreach 変数と `.Value`、文字列化のみ (§13)。`new Rune(n)` は TCS1002、`string.Join` の要素と `Rune?` の文字列化は TCS1001 |

---

## 20b. TinySystem.Text

文字列の文字と byte の位置の API。`using TinySystem.Text;` で使う。
正本は C の `runtime/utf8` で、tcs VM と C はこれを呼ぶ。.NET は `TinySystem/Utf8.cs` の同じ結果を返す C# 版。
文字は Unicode scalar value (`EnumerateRunes` の 1 個)。範囲の外と null の受け手は fault (.NET は例外)。

| メンバー | 状態 | 説明 |
|---------|:----:|------|
| `s.RuneCount()` | **Y** | 文字数。文字列の長さに比例する時間 |
| `s.RuneSubstring(start, count)` | **Y** | `start` 文字目から `count` 文字の string。文字列の長さに比例する時間 |
| `new Utf8Bytes(s)` | **Y** | UTF-8 の byte 列の view (`readonly struct`)。`default(Utf8Bytes)` と未初期化の field は長さ 0 の view で、`ToString()` は `""`、`Slice(0, 0)` は可、`u[i]` は範囲外の fault。.NET はここで `byte[]` に 1 度だけ変換し、tcs VM / C は文字列をそのまま指す |
| `u.Length` | **Y** | byte 数 |
| `u[i]` | **Y** | byte。O(1) |
| `u.Slice(start, count)` | **Y** | byte を複製しない view (値) |
| `u.ToString()` | **Y** | byte 列の string。文字の途中で切った slice の壊れた並びは U+FFFD になる (.NET の `Encoding.UTF8` と同じ位置で同じ個数) |

`Runes.RuneCount(s)` の static の形と `s?.RuneCount()` も同じ。`Utf8Bytes?` は user の struct の `T?` と同じで、`n == null`、`n?.Length`、`n?[i]`、`n?.Slice(a, b)`、`n?.ToString()`、`n.Value` が使える。`u == null` と `u?.Length` は C# の compile error (CS0019 / CS0023)。
`object` への変換は TCS1001 `StructBoxing`。連結・補間・`Console.WriteLine` は TCS1001 (`ToString()` を書く)。`Utf8Bytes` を値として持つ record は TCS1001 `Utf8BytesInRecord` (等価が揃わない)。`List<Utf8Bytes>` の `Contains` / `IndexOf` / `Remove` は TCS1002 (要素の等価が揃わない)。method group は TCS1001 `TextApiMethodGroup`。Dictionary の key は TCS1001 (struct と同じ)。

## 21. System.IO

| 型 | 状態 | 備考 |
|----|:----:|------|
| `File` | **-** | |
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
| 未対応構文の診断 | **Y** | TCS1001 / analyzer と `tcs check` / `tcs vm` / `tcs c` で共有 (`struct` / `record struct` / `partial` 型 / `lock` / `nameof` など) |
| 未対応で build を止める | **Y** | TCS1001 / TCS1002 は error で、`tcs vm` / `tcs c` は出力を書かず exit 1 (`tcs vm --watch` と増分 session も最後に通った build を保つ)。TCS1003 は warning で、`tcs check` だけが exit 1 にする。analyzer は warning のまま (`.editorconfig` で上書き可能) |
| 未対応 BCL API の診断 | **Y** | TCS1002 / analyzer と `tcs check` / `tcs vm` / `tcs c` で共有。core API allowlist は完全シグネチャ単位で、member 外に加えて名前だけ一致する未実装 overload も検出する。完全修飾型qualifierはmemberとして重複診断しない |
| collection null 保存の警告 | **Y** | TCS1003 / analyzer と `tcs check` で共有 |
| 複数ファイル入力 | **Y** | 共有 Compilation でクロスファイル参照 |
| namespace 解決 | **Y** | namespace の中の型は修飾名 (§ namespace ブロック行)。entry class は `A.Color` / simple 名 (一意なら) で引ける (`tcs vm --entry` / `tcs c --entry` は IL 名 `A_Color`) |
| CLI | **Y** | `tcs check a.cs b.cs [--ref stub.cs]`, `tcs vm ... -o out.tcsb`, `tcs c ... -o out.c`, `--help`, `--version`。subcommand の無い呼び出しは usage と exit 1 |
| CI gate | **Y** | GitHub Actions で `run-tests.sh` / sample `tcs check` / analyzer demo / analyzer pack |
| 実行 trace | **Y** | `tcs vm --trace <path-prefix>` で対象ファイルの文と代入を記録する (prefix は path の segment 単位。README) |
| watch モード | **Y** | `tcs vm --watch` でファイル監視、reload の batch か restart を標準出力に書く (README) |
| 依存 lock / publish runtime 同梱 | **Y** | package pin + packages.lock.json + `tcs c` が埋め込む runtime の C |
| 予約された綴りの識別子 | **Y** | 予約語 (§11) と同名の member と型、`__` 始まり、非 ASCII、`new` / `ctor`、型名と namespace 名の `_` は error (§28)。analyzer / `tcs check` は TCS1001 `LuaKeywordIdentifier(end)` / `DoubleUnderscoreIdentifier(__x)` 等 |

## 25. 許容される C# エラー (TinyC# 固有)

| エラーコード | 内容 | 理由 |
|------------|------|------|
| CS0029 | 暗黙の型変換 | diagnostic位置の式がenum ↔ numeric integerの場合だけ許容 (`char`は除外) |
| CS0266 | 明示的変換が必要 | diagnostic位置の式がenum ↔ numeric integerの場合だけ許容 (`char`は除外) |
| CS0019 | 演算子適用不可 | enumとnumeric integer (`char`除外) の`==` / `!=`だけ許容 |
| CS0535 | interface 未実装 | 未実装memberがすべて、同じclassで宣言した同名・同型のpublic instance fieldで代替できるpropertyの場合だけ許容。setterにはmutable fieldが必要 |

## 26. 数値型マッピング

| C# 型 | IL の型 (tcs VM / C) | 備考 |
|-------|-------|------|
| int, uint, byte | i32 | il-spec §3。uint は 0〜2^31-1、byte は 0〜255 を受ける (§1.1) |
| float | f32 | サフィックス除去 |
| long, double | (なし) | TCS1001 `LongType` / `DoubleType` (§1.1) |
| enum 値 | i32 | 整数定数 |
| bool | bool | |
| string | string | UTF-8 前提 |
| null | null | |
| class instance | object の参照 | |

## 27. 既知の意味論差 (実行可能な正本)

C# と tcs VM の意味論差のうち、正規化 (bool 表記) で吸収できない
例別の受容差異は `Transpiler.Tests/SpecConformance/known-differences.json` に理由付きで
登録し、spec conformance sweep が実行時に参照する (いまは空)。

次は tcs VM と C が .NET と違う結果を出す (各節に書いた仕様の差)。この他に、.NET が例外を投げる所の多くで tcs は fault で止まる (il-spec §12)。

- static の初期化子は Main の前に全型の分を実行する。.NET は型に初めて触れたときに実行するので、初期化子の副作用の順が違い得る (§2.3)
- `string.ToUpper` / `ToLower` は ASCII の文字だけを変える。.NET は ASCII の外の文字も変える (`"é".ToUpper()` は .NET で `É`、tcs VM と C で `é`) (§13)
- `Dictionary.Add` に既にある key を渡すと値を上書きする。.NET は ArgumentException (§18)
- Queue の `foreach` の途中の Enqueue / Dequeue は、始めた時点の要素の写しを回るので列挙に影響しない。.NET は InvalidOperationException (§17b)

次は仕様の差ではなく、tcs VM か C が .NET と違う結果を出す不具合。

- 網羅しない switch 式で一致する arm が無いと、tcs VM と C は fault せずに結果の型の既定値で続く (int なら 0)。.NET は SwitchExpressionException (§4)
- `--ref` の型の user operator (`a + b`) は `tcs check` を通るが、`tcs vm` と `tcs c` は型の誤りで止まる
- user の class を key にした Dictionary は、C backend だけが emit で止まる (§18)

## 28. 名前規則

名前は表を持たず規則で写す (`SelfHost/Compiler/Declarer.cs` の `LuaNames` と `IlNames.cs`)。user の型 (入力の tree に宣言された型) の
member は C# の識別子をそのまま IL / C の名前にし、`--ref` の型 (host) の member だけ
host の wire format (snake_case) に写す (q.md Q16)。ユーザ型の型名 (`Player`) は写さない。
`--ref` の型の path は host の関数の名前 (IL の foreign 関数、C の `tcs_host_<path を _ 連結>`) になる。

| C# | IL の名前 | 規則 |
|----|-----|------|
| user の field / property / method `BeginPass`, `hp`, `_maxHp`, `XMLValue` | `BeginPass`, `hp`, `_maxHp`, `XMLValue` | C# の識別子のまま。IL の名前 (tcs2c の C 名 `tcs_entry_<Class>_<Method>` の元) も同じ |
| user の enum メンバ `BigValue`, `RGBA8` | `BigValue`, `RGBA8` | C# の識別子のまま |
| method の overload `Show()`, `Show(int)`, `Total<T>(List<T>, Func<T, float>)` | `Show`, `Show__int`, `Total__List_T__Func_T_float` | 同じ型に同名の method が複数あるときだけ、引数の型を `__` でつないで付ける (ref / out は `ref_` 等を前置、`int[]` は `int_arr`、`int?` は `int_opt`)。引数の無い overload は C# の名前のまま。interface の method を実装する method は interface の method の名前 |
| 入れ子の型 `Outer.Inner`、namespace の中の `Game.Outer.Inner` | `Outer_Inner`、`Game_Outer_Inner` | 外側の型と namespace の名前を `_` でつなぐ。型名の `_` は TCS1001 なので、別の型と同じ名前にならない |
| 予約語 (§11) と同名のローカル束縛 `local`, `end` | `local_`, `end_` | `_` を後置。同じ member body に `local_` があれば `local__` のように足す |
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

user の member は C# の識別子のまま IL の名前になるので、同じ型の中で名前が衝突することは無い。overload に型を付けた名前が他の member と同じ綴りになる場合 (`F(int)` の `F__int` と method `F__int`) は MethodOverload (TCS1001)。

user の宣言の識別子のうち、下の予約された綴りは build を止める TCS1001 (`tcs check` は exit 1、`tcs vm` / `tcs c` は IL の前に止まる、analyzer は warning) にする。`--ref` の型は対象外。

| 識別子 | 診断 | 備考 |
|----|-----|------|
| 予約語 (§11) と同名の member / 型 `end` | `LuaKeywordIdentifier(end)` | local は `_` を後置して写す (上の表) |
| `__` で始まる識別子 (member / 型 / namespace / local / parameter) `__hp` | `DoubleUnderscoreIdentifier(__hp)` | IL が作る名前 (operator の `__add`、`__copy`、`__tcs_*` の intrinsic) の領域 |
| 非 ASCII の識別子 `速度` | `NonAsciiIdentifier(速度)` | lexer が報告する |
| constructor の IL の名前と同名の member `@new`, `ctor` | `ReservedClassKey(new)` | IL の constructor は class が `new`、struct が `ctor` (§2.3) |
| 型名の `_` `My_Type` | `UnderscoreTypeName(My_Type)` | namespace の中の型の IL の名前は namespace 修飾名を `_` で結合した平らな名前 (`A.B.C` → `A_B_C`) なので、`_` を含む名前は別の修飾名と同じ名前になり得る |
| namespace 名の `_` `A.B_C` | `UnderscoreNamespaceName(B_C)` | 同上 |
| `--ref` の interface の実装 `class Impl : IHostHook` | `RefInterfaceImplementation(IHostHook)` | interface 経由の呼び出しは host の名前 (`do_thing`)、実装は C# の名前 (`DoThing`) で届かない。`tcs vm` は止めないが、interface 経由の呼び出しは host の関数を呼ぶ。user の interface の実装と、host の interface を値として受け渡すだけなら可 |

member 名と local 名の `_` (`max_hp`、`_init`、`a_b`) はそのまま書ける。
