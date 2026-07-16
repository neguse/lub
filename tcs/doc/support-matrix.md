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
| struct / record struct | **Useful** | 現時点は TCS1001 未対応診断。値セマンティクス需要が出るまで class/record class で代替 |
| if / switch / loop / lambda / pattern | **Core** | ゲームロジックと小さな業務ロジックの表現力として必要 |
| 演算子オーバーロード (算術) | **Core** | ベクトル/行列など math 型の表現に必要。二項 `+ - * / %` と単項 `-` だけを Lua metamethod へ写像し、変換演算子と `==`/`!=`/比較系は対象外 |
| LINQ メソッドチェーン | **Core** | `Where`/`Select`/`Any`/`All`/`First`/`Last`/`OrderBy`/`Take`/`Skip`/集計の小核だけ即時評価で提供 |
| ユーザー定義ジェネリクス | **Out** | 型消去 runtime と複雑さが釣り合わない。組み込み generic 型に限定 |
| reflection / dynamic / expression tree | **Out** | Lua 5.5 backend と compact baseline に合わない |
| async / Task / threading | **Out** | ホットリロード可能なシングルスレッド Lua 実行を基本にする |
| unsafe / Span / pointer | **Out** | 組み込み backend の低レベル最適化フェーズまで扱わない |

### 標準ライブラリの baseline

| 領域 | 優先度 | 方針 |
|------|:------:|------|
| `String`, `Math`, `Random`, `Console` | **Core** | 小さなスクリプトで頻出する API を厳選して提供 |
| `List<T>`, `Dictionary<K,V>` | **Core** | sequence/hash table として明確に対応 |
| LINQ `Enumerable` | **Core** | 即時評価のメソッドチェーンだけ。クエリ構文と遅延評価は非目標 |
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
| ローカル宣言 | **Core** | 3 | 1 | 6 | 1 | 11 |
| 文 | **Core** | 12 | 0 | 6 | 4 | 22 |
| リテラル | **Core** | 12 | 0 | 0 | 1 | 13 |
| 演算子 | **Core** | 24 | 1 | 8 | 5 | 38 |
| 高度な式 | **Core** | 6 | 2 | 9 | 2 | 19 |
| パターンマッチング | **Core** | 8 | 1 | 8 | 0 | 17 |
| 修飾子 | **Useful** | 4 | 2 | 20 | 4 | 30 |
| ジェネリクス | **Core/Out** | 1 | 1 | 5 | 0 | 7 |
| 継承 | **Useful** | 4 | 0 | 2 | 0 | 6 |
| **小計** | | **99** | **18** | **94** | **26** | **237** |

### 標準ライブラリ

| 名前空間 | 優先度 | Y | P | - | N/A | 計 |
|---------|:------:|---|---|---|-----|---|
| System 基本型 | **Core/Useful** | 4 | 2 | 19 | 3 | 28 |
| String メンバー | **Core** | 15 | 0 | 17 | 0 | 32 |
| Math メンバー | **Core** | 17 | 0 | 15 | 0 | 32 |
| Random メンバー | **Core** | 3 | 0 | 6 | 0 | 9 |
| List\<T\> メンバー | **Core** | 11 | 0 | 18 | 0 | 29 |
| Dictionary\<K,V\> メンバー | **Core** | 10 | 0 | 3 | 0 | 13 |
| LINQ (Enumerable) | **Core/Useful** | 18 | 0 | 30 | 0 | 48 |
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
| `double` (Double) | **Y** | number | |
| `bool` (Boolean) | **Y** | boolean | |
| `decimal` (Decimal) | **-** | | |
| `char` (Char) | **-** | | string で代替 |
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
| Nullable 値型 (`int?`) | **P** | `null`/値/HasValue/Value/GetValueOrDefault |
| Nullable 参照型 (`string?`) | **N/A** | Lua は常に nil 可能 |
| タプル `(int, string)` | **-** | |
| 配列 `int[]` | **P** | 初期化子、index、Length。List\<T\> を推奨 |
| 匿名型 `new { }` | **-** | |
| `Span<T>` / `ReadOnlySpan<T>` | **N/A** | |
| ポインタ型 `int*` | **N/A** | |
| 関数ポインタ `delegate*` | **N/A** | |
| `ref struct` | **N/A** | |

### 1.4 ユーザー定義型

| 型 | 状態 | Lua マッピング | 備考 |
|----|:----:|--------------|------|
| `class` | **Y** | table + metatable | |
| `struct` | **-** | | TCS1001。class / record class で代替 |
| `record` / `record class` | **P** | table + metatable | positional record |
| `record struct` | **-** | | TCS1001。record class で代替 |
| `interface` | **P** | 出力なし | Roslyn 型チェックのみ |
| `enum` | **Y** | 定数テーブル | |
| `delegate` 型定義 | **N/A** | | Action/Func で代替 |
| ネストされた型 | **-** | | |

---

## 2. 宣言 (Declarations)

### 2.1 名前空間・using

| 機能 | 状態 | 備考 |
|------|:----:|------|
| `namespace N { }` (ブロック) | **Y** | Lua ではフラット化 |
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
| `struct` 宣言 | **-** | TCS1001。class / record class で代替 |
| `record` 宣言 (C# 9) | **P** | positional record |
| `record struct` 宣言 (C# 10) | **-** | TCS1001。record class で代替 |
| `interface` 宣言 | **Y** | 出力なし |
| `enum` 宣言 | **Y** | |
| `delegate` 宣言 | **-** | |
| `partial` 型 (C# 2) | **-** | TCS1001。partial subtreeをunsupported marker化し、同名table上書きを防ぐ |
| `file` 型 (C# 11) | **-** | |

### 2.3 メンバー宣言

| 機能 | 状態 | Lua マッピング | 備考 |
|------|:----:|--------------|------|
| フィールド | **Y** | `self.Field` | initializer なしは型別 default |
| フィールド初期化子 | **Y** | コンストラクタ内 | |
| `const` フィールド | **Y** | `ClassName.CONST` | |
| オートプロパティ (`{ get; set; }`) | **Y** | フィールド直接 | initializer なしは型別 default |
| プロパティ初期化子 | **Y** | | |
| カスタム getter/setter | **Y** | `get_`/`set_` メソッド | |
| 式本体メンバー (`=>`, C# 6) | **Y** | | |
| インスタンスメソッド | **Y** | `obj:Method()` | |
| 静的メソッド | **Y** | `Class.Method()` | |
| コンストラクタ | **Y** | `Class.new(args)` | |
| 静的コンストラクタ | **-** | | |
| デストラクタ / ファイナライザ | **N/A** | | |
| イベント | **-** | | |
| インデクサ (`this[int]`) | **-** | | |
| 演算子オーバーロード | **P** | Lua metamethod (`__add`/`__sub`/`__mul`/`__div`/`__mod`/`__unm`) | 二項 `+ - * / %` と単項 `-`。複数 overload は metamethod 内で実行時型分岐 (class は metatable、数値/文字列/bool は `type()`)。`==`/`!=`/比較系は TCS1001 (record の `__eq` のみ) |
| 暗黙/明示変換演算子 | **-** | | TCS1001 |
| ローカル関数 (C# 7) | **-** | | unsupported 診断あり |
| 静的ローカル関数 (C# 8) | **-** | | unsupported 診断あり |
| 拡張メソッド (C# 3) | **P** | static method 呼び出しへ展開 | LINQ は専用処理 |
| 拡張メンバー (C# 14) | **-** | | |
| primary constructor (C# 12) | **-** | | |
| デフォルト引数値 | **P** | Roslyn パース | |

### 2.4 ローカル宣言

| 機能 | 状態 | Lua マッピング | 備考 |
|------|:----:|--------------|------|
| `var x = expr;` | **Y** | `local x = expr` | |
| `int x = expr;` | **Y** | `local x = expr` | |
| `ref` ローカル (C# 7) | **N/A** | | |
| ローカル定数 `const` | **-** | | |
| 分解宣言 `var (a, b) = ...` (C# 7) | **P** | `local a, b = ...` | record positional property 限定 |
| 破棄 `_ = expr` (C# 7) | **-** | | |
| トップレベル文 (C# 9) | **Y** | Lua chunk | 型定義を先に出力してから実行 |
| using 宣言 `using var` (C# 8) | **-** | | unsupported 診断あり |
| `scoped` ローカル (C# 11) | **-** | | |
| パターン変数 `if (x is int i)` (C# 7) | **-** | | |
| `required` メンバー (C# 11) | **-** | | |

---

## 3. 文 (Statements)

| 文 | 状態 | Lua マッピング | 備考 |
|---|:----:|--------------|------|
| 変数宣言文 | **Y** | `local x = ...` | |
| 式文 (メソッド呼び出し等) | **Y** | | |
| `if` / `else if` / `else` | **Y** | `if`/`elseif`/`else`/`end` | |
| `switch` 文 | **Y** | if-elseif チェーン | |
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
| `lock` | **N/A** | `do ... end` fallback | TCS1001。同期はせずbody/scopeだけ保持 |
| `yield return` / `yield break` (C# 2) | **-** | | |
| `goto` / ラベル | **-** | | |
| `checked` / `unchecked` | **N/A** | | |
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
| 補間文字列 (`$"...{x}..."`) | **Y** | `tostring` + `..` | |
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
| `+` `-` `*` `/` `%` (算術) | **Y** | そのまま | |
| `+` (文字列連結) | **Y** | `..` | 型で自動判定 |
| `==` `!=` | **Y** | `==` `~=` | |
| `<` `<=` `>` `>=` | **Y** | そのまま | |
| `&&` `\|\|` | **Y** | `and` `or` | |
| `!` (論理否定) | **Y** | `not` | |
| `-x` (単項マイナス) | **Y** | `-x` | |
| `++x` `x++` (インクリメント) | **Y** | `x = x + 1` (文) | |
| `--x` `x--` (デクリメント) | **Y** | `x = x - 1` (文) | |
| `=` (代入) | **Y** | そのまま | |
| `+=` `-=` `*=` `/=` `%=` `&=` `\|=` `^=` `<<=` `>>=` | **Y** | 展開 `x = x op y` | bool への `&=` `\|=` `^=` は未対応 |
| `? :` (三項) | **Y** | IIFE | falsy 安全 |
| `??` (null 合体) | **Y** | `or` | |
| `?.` (null 条件アクセス, C# 6) | **Y** | IIFE nil チェック | String/List/Dict mapping 対応 |
| `(T)x` (キャスト) | **Y** | 透過 (型消去) | |
| `is null` / `is not null` | **Y** | `== nil` / `~= nil` | |
| `is Type` | **Y** | `getmetatable() ==` | |
| `new T(args)` | **Y** | `T.new(args)` | |
| `??=` (null 合体代入, C# 8) | **Y** | nil check + assignment | |
| `?[]` (null 条件インデクサ, C# 6) | **Y** | IIFE nil チェック | List/array は 0→1 indexed |
| `?.Prop = v` (null条件代入, C# 14) | **-** | | |
| `as` (安全キャスト) | **-** | | |
| `typeof(T)` | **-** | | |
| `nameof(x)` (C# 6) | **-** | 定数文字列 + unsupported marker fallback | TCS1001。semantic判定し、同名ユーザーmethodは通常call |
| `default` / `default(T)` (C# 7.1) | **Y** | 型別 default | |
| `sizeof(T)` | **N/A** | | |
| `~` (ビット反転) | **Y** | `~x` | 整数のみ (64bit 幅、下記注記) |
| `<<` `>>` (シフト) | **Y** | `<<` `>>` | 整数のみ。`>>>` は未対応。負数 `>>` は C# (算術) と Lua (論理) で異なる (下記注記) |
| `&` `\|` `^` (ビット演算) | **Y** | `&` `\|` `~` (二項) | 整数と enum のみ。C# `^` は Lua 二項 `~`。bool operand は TCS1001 未対応 (Lua native が boolean を拒否し、and/or は非短絡の C# `&` `\|` と意味論が変わるため) |
| `..` (Range, C# 8) | **-** | | |
| `^x` (Index from end, C# 8) | **-** | | |
| `new T { ... }` (初期化子) | **Y** | List/Dict、class (IIFE + field 代入)、`--ref` 型 (plain table)。ネストした初期化子は TCS1001 | |
| `new(args)` (ターゲット型, C# 9) | **Y** | 初期化子含む | |
| `x!` (null 許容抑制, C# 8) | **Y** | 型チェック専用、Lua へは透過 | |
| `stackalloc` | **N/A** | | |
| `&x` `*x` `->` (ポインタ) | **N/A** | | |
| `delegate { }` (匿名メソッド, C# 2) | **-** | | |
| `delegate*` (関数ポインタ, C# 9) | **N/A** | | |

ビット演算の幅意味論: C# `int` は 32bit だが、Lua 整数は 64bit であり、
ビット演算は Lua native 演算子への写像のため常に 64bit 幅で評価される。

- `~x`、負数を含む `& \| ^`、32bit を溢れる `<<` は上位 32bit の扱いが C# と異なる
- C# の `>>` (int) は算術シフト (符号拡張)、Lua の `>>` は論理シフト (0 埋め) のため、
  負数の右シフトは一致しない
- C# はシフト量を 31 (int) でマスクするが、Lua はマスクしない (64 以上で 0)

32bit 意味論が必要な箇所は、移植側で `& 0xFFFFFFFF` などの明示マスク
(必要なら符号の手動復元) を行う運用とする。

### 4.3 高度な式

| 式 | 状態 | Lua マッピング | 備考 |
|---|:----:|--------------|------|
| ラムダ (式本体) `x => expr` | **Y** | `function(x) return expr end` | |
| ラムダ (文本体) `(x) => { }` | **Y** | `function(x) ... end` | |
| switch 式 (C# 8) | **Y** | IIFE if-elseif | |
| `this` | **Y** | `self` | |
| `base.Method()` | **Y** | `Base.Method(self, ...)` | |
| メソッドチェーン (LINQ) | **Y** | ネスト呼び出し | |
| `with` 式 (C# 9) | **P** | shallow copy + field override | record/class table 限定 |
| LINQ クエリ構文 (C# 3) | **-** | | メソッドチェーンで代替 |
| メソッドグループ変換 | **-** | | |
| `throw` 式 (C# 7) | **-** | | unsupported 診断あり |
| コレクション式 `[1,2,3]` (C# 12) | **-** | | |
| タプル式 `(a, b)` (C# 7) | **-** | | |
| `await` 式 (C# 5) | **N/A** | | |
| 式ツリー `Expression<>` (C# 3) | **N/A** | | |
| 静的ラムダ `static =>` (C# 9) | **-** | | |
| ラムダのデフォルト引数 (C# 12) | **-** | | |
| ラムダの戻り値型 (C# 10) | **-** | | |
| ラムダに `params` (C# 12) | **-** | | |
| `checked(expr)` / `unchecked(expr)` | **P** | 透過 | Roslyn がパース |

---

## 5. パターンマッチング

| パターン | C# ver | 状態 | Lua マッピング | 備考 |
|---------|--------|:----:|--------------|------|
| 定数パターン `is 42`, `is null` | 7 | **Y** | `== 42`, `== nil` | |
| 型パターン `is MyClass` | 7 | **Y** | `getmetatable() ==` | |
| `not` パターン `is not null` | 9 | **Y** | `~= nil` | |
| 破棄パターン `_` | 7 | **Y** | switch 式の default | |
| 宣言パターン `is int i` | 7 | **P** | 型判定 + 一部 local 束縛 | root if 限定 |
| `var` パターン `is var v` | 7 | **-** | | |
| プロパティパターン `is { Name: "x" }` | 8 | **Y** | property 比較展開 | |
| 位置パターン `is (1, 2)` | 8 | **-** | | |
| タプルパターン `(a, b) is (1, 2)` | 8 | **-** | | |
| 関係パターン `is > 0` | 9 | **Y** | 比較式展開 | |
| `and` パターン `is > 0 and < 100` | 9 | **Y** | and 結合 | |
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
| `protected` | **P** | Roslyn 解決、Lua 区別なし |
| `internal` | **-** | |
| `protected internal` | **-** | |
| `private protected` (C# 7.2) | **-** | |
| `file` (C# 11) | **-** | |

### 6.2 型・メンバー修飾子

| 修飾子 | 状態 | 備考 |
|--------|:----:|------|
| `static` | **Y** | static メソッド/フィールド |
| `abstract` | **-** | |
| `virtual` | **P** | metatable でオーバーライド可能だが明示的仕組みなし |
| `override` | **-** | |
| `sealed` | **-** | |
| `new` (メンバー隠蔽) | **Y** | メソッド再定義 |
| `readonly` | **-** | |
| `const` | **-** | |
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
| `this` (拡張メソッド) | **-** | |
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
| ユーザー定義ジェネリッククラス | **-** | |
| ジェネリックメソッド | **-** | |
| 型制約 (`where T : ...`) | **-** | |
| 共変性/反変性 (`in`/`out`, C# 4) | **-** | |
| 静的抽象/仮想インターフェースメンバー (C# 11) | **-** | |

---

## 8. 継承

| 機能 | 状態 | Lua マッピング | 備考 |
|------|:----:|--------------|------|
| 単一継承 | **Y** | metatable チェーン | |
| `base()` コンストラクタ | **Y** | `Base.new()` + setmetatable | |
| `base.Method()` | **Y** | `Base.Method(self, ...)` | |
| 多重インターフェース実装 | **Y** | Roslyn 型チェックのみ | |
| 抽象クラス | **-** | | |
| 共変戻り値型 (C# 9) | **-** | | |

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
| `///` XML ドキュメント | **N/A** | |

---

## 10. C# バージョン別対応状況

| C# ver | 年 | 主要機能 | 状態 |
|--------|---|---------|:----:|
| 1.0 | 2002 | class, struct, interface, enum, delegate, 制御構文, 例外処理 | **P** |
| 2.0 | 2005 | generics, nullable, iterators, `??`, partial, anonymous methods | **P** |
| 3.0 | 2007 | LINQ, ラムダ, `var`, 拡張メソッド, 初期化子, 匿名型 | **P** |
| 4.0 | 2010 | `dynamic`, named/optional 引数, 共変性/反変性 | **-** |
| 5.0 | 2012 | `async`/`await`, caller info | **N/A** |
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
new  null  operator(部分)  private  public  protected(部分)  return  static  string
switch  this  true  var  void  while
```

### 未対応

```
abstract  as  byte  catch  char  checked  const  decimal
delegate  event  explicit  extern  finally  fixed  goto
implicit  in(foreach以外)  internal  lock  object  out  override
params  readonly  ref  sbyte  sealed  short  sizeof  stackalloc  struct
throw  try  typeof  uint(部分)  ulong  unchecked  unsafe  ushort
using(宣言)  virtual(部分)  volatile  yield
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
| Lua 5.5 予約語と同名の宣言 (`end`, `repeat`, `until`, `global` 等) | **-** | TCS1001 `LuaKeywordIdentifier(name)`。emit すると不正 Lua になるため拒否 (自動リネームなし) |
| verbatim 識別子 (`@float`, `@out` 等) | **Y** | ValueText (`@` なし) で emit。`@end` 等 Lua 予約語になるものは上記 TCS1001 |

---

# Part II: 標準ライブラリ

---

## 12. System 名前空間 — 型一覧

| 型 | 状態 | 対応内容 | 備考 |
|----|:----:|---------|------|
| `Object` | **P** | `ToString()` → `tostring()` | |
| `String` | **P** | 後述の個別メンバー表参照 | |
| `Math` | **Y** | 後述の個別メンバー表参照 | `math` ラッパー |
| `MathF` | **-** | | float 版 Math |
| `Console` | **Y** | `WriteLine` → `print` | |
| `Convert` | **-** | | |
| `Environment` | **-** | | |
| `Array` | **-** | | List で代替 |
| `Tuple` | **-** | | |
| `ValueTuple` | **-** | | |
| `Nullable<T>` | **-** | | |
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
| `Action<>` / `Func<>` / `Predicate<>` | **Y** | 型消去 → Lua function | |
| `Lazy<T>` | **-** | | |
| `WeakReference<T>` | **-** | | |

---

## 13. String メンバー

| メンバー | 状態 | Lua マッピング | 区分 |
|---------|:----:|--------------|:----:|
| `.Length` | **Y** | `#str` | T |
| `.Contains(s)` | **Y** | `String.Contains(str, s)` | T+R |
| `.Replace(old, new)` | **Y** | `String.Replace(str, old, new)` (`old == ""`は即時error) | T+R |
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
| `String.IsNullOrWhiteSpace(s)` (static) | **-** | | |
| `String.Format(fmt, ...)` (static) | **-** | | 補間で代替 |
| `String.Join(sep, ...)` (static) | **Y** | `String.Join(sep, values)` | T+R |
| `String.Concat(...)` (static) | **-** | | |
| `String.Compare(a, b)` (static) | **-** | | |
| `String.Equals(a, b)` (static) | **-** | | |
| `String.Empty` (static) | **-** | | |
| `.ReplaceLineEndings()` | **-** | | |
| `[int]` (char indexer) | **-** | | |

引数なし`Split()`のwhitespace判定はLua `%s`によるbyte/locale単位であり、
.NETのUnicode `Char.IsWhiteSpace`とは一致しない。これはUTF-8 byte列を使う既知制約に含む。

---

## 14. Math メンバー

| メンバー | 状態 | Lua マッピング | 区分 |
|---------|:----:|--------------|:----:|
| `Math.PI` | **Y** | `Math.PI` (`math.pi`) | R |
| `Math.Min(a, b)` | **Y** | `Math.Min(a, b)` | R |
| `Math.Max(a, b)` | **Y** | `Math.Max(a, b)` | R |
| `Math.Clamp(v, min, max)` | **Y** | `Math.Clamp(v, min, max)` | R |
| `Math.Abs(x)` | **Y** | `Math.Abs(x)` | R |
| `Math.Floor(x)` | **Y** | `Math.Floor(x)` | R |
| `Math.Ceiling(x)` | **Y** | `Math.Ceil(x)` | R |
| `Math.Sqrt(x)` | **Y** | `Math.Sqrt(x)` | R |
| `Math.Sin(x)` | **Y** | `Math.Sin(x)` | R |
| `Math.Cos(x)` | **Y** | `Math.Cos(x)` | R |
| `Math.Atan2(y, x)` | **Y** | `Math.Atan2(y, x)` | R |
| `Math.E` | **-** | | |
| `Math.Tau` | **-** | | |
| `Math.Pow(x, y)` | **Y** | `Math.Pow(x, y)` | R |
| `Math.Exp(x)` | **Y** | `Math.Exp(x)` (`math.exp`) | R |
| `Math.Log(x)` / `Math.Log(x, base)` | **Y** | `Math.Log(x[, base])` (`math.log`) | R |
| `Math.Log10(x)` / `Math.Log2(x)` | **-** | | `Math.Log(x, base)` で代替 |
| `Math.Round(x)` / `Math.Round(x, digits)` | **Y** | `Math.Round(x[, digits])` | R。C# と同じ偶数丸め (banker's rounding) |
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
| `Random.Next()` | **Y** | `Random.Next()` | R |
| `Random.NextFloat()` | **Y** | `Random.NextFloat()` | R |
| `Random.Range(min, max)` | **Y** | `Random.Range(min, max)` | R |
| `new Random()` / `new Random(seed)` | **-** | | |
| `Random.Shared` (static) | **-** | | |
| `Random.Next(min, max)` (.NET 標準 API) | **-** | | |
| `Random.NextDouble()` | **-** | | |
| `Random.NextBytes(buf)` | **-** | | |
| `Random.Shuffle(arr)` | **-** | | |

---

## 16. System.Collections.Generic — 型一覧

| 型 | 状態 | Lua マッピング | 備考 |
|----|:----:|--------------|------|
| `List<T>` | **Y** | sequence table | null 要素は TCS1003 診断 |
| `Dictionary<TKey, TValue>` | **Y** | hash table | null 値は TCS1003 診断 |
| `HashSet<T>` | **-** | | |
| `Queue<T>` | **-** | | |
| `Stack<T>` | **-** | | |
| `LinkedList<T>` | **-** | | |
| `SortedList<TK, TV>` | **-** | | |
| `SortedDictionary<TK, TV>` | **-** | | |
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
| `list[i]` (get/set) | **Y** | `list[i+1]` | T |
| `.Count` | **Y** | `#list` | T |
| `.Add(item)` | **Y** | `table.insert(list, item)` | T |
| `.Remove(item)` | **Y** | `List.Remove(list, item)` | T+R |
| `.RemoveAt(index)` | **Y** | `table.remove(list, idx+1)` | T |
| `.Clear()` | **Y** | | T |
| `.Contains(item)` | **Y** | `List.Contains(list, item)` | T+R |
| `.IndexOf(item)` | **Y** | `List.IndexOf(list, item)` | T+R |
| `.AddRange(collection)` | **-** | | |
| `.Insert(index, item)` | **-** | | |
| `.InsertRange(index, collection)` | **-** | | |
| `.RemoveRange(index, count)` | **-** | | |
| `.RemoveAll(predicate)` | **-** | | |
| `.Find(predicate)` / `.FindLast()` / `.FindAll()` | **-** | | |
| `.FindIndex(predicate)` / `.FindLastIndex()` | **-** | | |
| `.Sort()` / `.Sort(comparison)` | **Y** | `List.Sort(list, comparison)` | T+R |
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

## 19. System.Linq — Enumerable メソッド

LINQ はメソッドチェーン形式のみ対応。クエリ構文 (`from x in ...`) は未対応。
ランタイム `List.Where` 等で即時評価 (遅延評価なし)。

### 対応済み

| メソッド | 状態 | Lua マッピング | 区分 |
|---------|:----:|--------------|:----:|
| `.Where(predicate)` | **Y** | `List.Where(list, fn)` | T+R |
| `.Select(selector)` | **Y** | `List.Select(list, fn)` | T+R |
| `.Any()` / `.Any(predicate)` | **Y** | `List.Any(list, fn)` | T+R |
| `.All(predicate)` | **Y** | `List.All(list, fn)` | T+R |
| `.First()` / `.First(predicate)` | **Y** | `List.First(list, fn)` | T+R |
| `.FirstOrDefault()` / `.FirstOrDefault(pred)` | **Y** | `List.FirstOrDefault(list, fn)` | T+R |
| `.OrderBy(keySelector)` | **Y** | `List.OrderBy(list, fn)` | T+R |
| `.OrderByDescending(keySelector)` | **Y** | `List.OrderByDescending(list, fn)` | T+R |
| `.Take(count)` | **Y** | `List.Take(list, count)` | T+R |
| `.Skip(count)` | **Y** | `List.Skip(list, count)` | T+R |
| `.Last()` / `.LastOrDefault()` | **Y** | `List.Last/LastOrDefault(list, fn)` | T+R |
| `.Min()` | **Y** | `List.Min(list)` | T+R |
| `.Max()` | **Y** | `List.Max(list)` | T+R |
| `.Sum()` | **Y** | `List.Sum(list)` | T+R |
| `.Count()` / `.Count(predicate)` | **Y** | `List.Count(list, fn)` | T+R |
| `.ToList()` | **Y** | `List.ToList(list)` | T+R |
| `.ToDictionary(key)` / `.ToDictionary(key, value)` | **Y** | `List.ToDictionary(list, keyFn, valueFn)` | T+R |
| チェーン `.Where().Select().ToList()` | **Y** | ネスト呼び出し | T |

### 未対応

| メソッド | 状態 | 備考 |
|---------|:----:|------|
| `.SelectMany()` | **-** | |
| `.ThenBy()` / `.ThenByDescending()` | **-** | |
| `.GroupBy()` | **-** | |
| `.Join()` / `.GroupJoin()` | **-** | |
| `.Distinct()` / `.DistinctBy()` | **-** | |
| `.Union()` / `.UnionBy()` | **-** | |
| `.Intersect()` / `.IntersectBy()` | **-** | |
| `.Except()` / `.ExceptBy()` | **-** | |
| `.Concat()` | **-** | |
| `.Zip()` | **-** | |
| `.Append()` / `.Prepend()` | **-** | |
| `.SkipLast()` / `.SkipWhile()` | **-** | |
| `.TakeLast()` / `.TakeWhile()` | **-** | |
| `.Chunk()` | **-** | |
| `.Single()` / `.SingleOrDefault()` | **-** | |
| `.ElementAt()` / `.ElementAtOrDefault()` | **-** | |
| `.DefaultIfEmpty()` | **-** | |
| `.LongCount()` | **-** | |
| `.Average()` | **-** | |
| `.Aggregate()` | **-** | |
| `.Contains()` (LINQ) | **-** | |
| `.SequenceEqual()` | **-** | |
| `.ToArray()` | **-** | |
| `.ToHashSet()` | **-** | |
| `.ToLookup()` | **-** | |
| `.Cast()` / `.OfType()` | **-** | |
| `.Reverse()` (LINQ) | **-** | |
| `.MinBy()` / `.MaxBy()` | **-** | |
| `Enumerable.Range()` (static) | **-** | |
| `Enumerable.Repeat()` (static) | **-** | |
| `Enumerable.Empty()` (static) | **-** | |
| 遅延評価 | **-** | 全て即時評価 |

---

## 20. System.Text

| 型 | 状態 | 備考 |
|----|:----:|------|
| `StringBuilder` | **-** | |
| `Encoding` | **-** | |
| `Regex` (`System.Text.RegularExpressions`) | **-** | Lua パターンで部分代替可能 |

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
| 未対応構文の警告 | **Y** | TCS1001 / analyzer と transpiler/check で共有 (`struct` / `record struct` / `partial` 型 / `lock` / `nameof` など) |
| 未対応 BCL API の警告 | **Y** | TCS1002 / analyzer と transpiler/check で共有。core API allowlist は完全シグネチャ単位で、member 外に加えて名前だけ一致する未実装 overload も検出する。完全修飾型qualifierはmemberとして重複診断しない |
| collection null 保存の警告 | **Y** | TCS1003 / analyzer と transpiler で共有 |
| 複数ファイル入力 | **Y** | 共有 Compilation でクロスファイル参照 |
| namespace 解決 | **Y** | 透過 |
| CLI | **Y** | `tcs a.cs b.cs [-o out.lua]`, `tcs check a.cs`, `--help`, `--version` |
| CI gate | **Y** | GitHub Actions で `run-tests.sh` / sample `tcs check` / analyzer demo / analyzer pack |
| ソースマップ | **Y** | `--sourcemap` で `.lua.map` 出力 |
| Lua stack trace SourceMap 注釈 | **Y** | `--map-stacktrace out.lua.map [trace.txt]` |
| watch モード | **Y** | `--watch` でファイル監視 |
| Lua CMake platform 分岐 | **Y** | Linux/Windows/macOS/iOS-family/Emscripten/BSD/generic Unix |
| 依存 lock / publish runtime 同梱 | **Y** | package pin + packages.lock.json + runtime/tinysystem.lua |
| 命名規約チェック | **Y** | PascalCase/camelCase 警告 |

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
