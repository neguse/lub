# TinyC# Support Matrix

C# 14 言語機能および .NET System 名前空間の全量対応状況一覧。

## 凡例

- **Y** = 対応済み
- **P** = 部分対応
- **-** = 未対応
- **N/A** = 対象外 (意図的にスコープ外)
- **T** = トランスパイラ側の変換
- **R** = ランタイム (tinysystem.lua) の関数

---

## サマリ

### 言語機能

| カテゴリ | Y | P | - | N/A | 計 |
|---------|---|---|---|-----|---|
| 組み込み型 | 7 | 2 | 8 | 7 | 24 |
| ユーザー定義型 | 3 | 1 | 2 | 2 | 8 |
| 名前空間・using | 3 | 0 | 3 | 1 | 7 |
| 型宣言 | 3 | 0 | 6 | 0 | 9 |
| メンバー宣言 | 11 | 1 | 9 | 1 | 22 |
| ローカル宣言 | 2 | 2 | 6 | 1 | 11 |
| 文 | 10 | 0 | 8 | 4 | 22 |
| リテラル | 6 | 0 | 5 | 1 | 12 |
| 演算子 | 18 | 1 | 10 | 5 | 34 |
| 高度な式 | 6 | 1 | 8 | 2 | 17 |
| パターンマッチング | 4 | 0 | 13 | 0 | 17 |
| 修飾子 | 5 | 2 | 16 | 3 | 26 |
| ジェネリクス | 1 | 1 | 5 | 0 | 7 |
| 継承 | 4 | 0 | 2 | 0 | 6 |
| **小計** | **83** | **11** | **101** | **27** | **222** |

### 標準ライブラリ

| 名前空間 | Y | P | - | N/A | 計 |
|---------|---|---|---|-----|---|
| System 基本型 | 5 | 2 | 16 | 3 | 26 |
| String メンバー | 12 | 0 | 12 | 0 | 24 |
| Math メンバー | 11 | 0 | 14 | 0 | 25 |
| Random メンバー | 3 | 0 | 5 | 0 | 8 |
| List\<T\> メンバー | 10 | 0 | 14 | 0 | 24 |
| Dictionary\<K,V\> メンバー | 9 | 1 | 3 | 0 | 13 |
| LINQ (Enumerable) | 12 | 0 | 31 | 0 | 43 |
| Collections.Generic 型 | 2 | 0 | 8 | 5 | 15 |
| System.Text | 0 | 0 | 3 | 0 | 3 |
| System.IO | 0 | 0 | 7 | 0 | 7 |
| System.Threading.Tasks | 0 | 0 | 0 | 4 | 4 |
| System.Numerics | 0 | 0 | 6 | 0 | 6 |
| **小計** | **64** | **3** | **119** | **12** | **198** |

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
| `uint` (UInt32) | **P** | number | lub3d API マッピング用 |
| `ulong` (UInt64) | **-** | | |
| `nint` (IntPtr) | **P** | number | lub3d VoidPtr マッピング用 |
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
| Nullable 値型 (`int?`) | **-** | |
| Nullable 参照型 (`string?`) | **N/A** | Lua は常に nil 可能 |
| タプル `(int, string)` | **-** | |
| 配列 `int[]` | **-** | List\<T\> で代替 |
| 匿名型 `new { }` | **-** | |
| `Span<T>` / `ReadOnlySpan<T>` | **N/A** | |
| ポインタ型 `int*` | **N/A** | |
| 関数ポインタ `delegate*` | **N/A** | |
| `ref struct` | **N/A** | |

### 1.4 ユーザー定義型

| 型 | 状態 | Lua マッピング | 備考 |
|----|:----:|--------------|------|
| `class` | **Y** | table + metatable | |
| `struct` | **N/A** | | class に寄せる方針 |
| `record` / `record class` | **-** | | |
| `record struct` | **-** | | |
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
| `struct` 宣言 | **-** | class に寄せる |
| `record` 宣言 (C# 9) | **-** | |
| `record struct` 宣言 (C# 10) | **-** | |
| `interface` 宣言 | **Y** | 出力なし |
| `enum` 宣言 | **Y** | |
| `delegate` 宣言 | **-** | |
| `partial` 型 (C# 2) | **-** | |
| `file` 型 (C# 11) | **-** | |

### 2.3 メンバー宣言

| 機能 | 状態 | Lua マッピング | 備考 |
|------|:----:|--------------|------|
| フィールド | **Y** | `self.Field` | |
| フィールド初期化子 | **Y** | コンストラクタ内 | |
| `const` フィールド | **-** | | |
| オートプロパティ (`{ get; set; }`) | **Y** | フィールド直接 | |
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
| 演算子オーバーロード | **-** | | |
| 暗黙/明示変換演算子 | **-** | | |
| ローカル関数 (C# 7) | **-** | | |
| 静的ローカル関数 (C# 8) | **-** | | |
| 拡張メソッド (C# 3) | **-** | LINQ のみ組み込み対応 |
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
| 分解宣言 `var (a, b) = ...` (C# 7) | **-** | | |
| 破棄 `_ = expr` (C# 7) | **-** | | |
| トップレベル文 (C# 9) | **P** | グローバルスコープ出力 | |
| using 宣言 `using var` (C# 8) | **-** | | |
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
| `do-while` ループ | **-** | | |
| `for` ループ (C 形式) | **Y** | `for i=s,e do` / while 展開 | |
| `foreach` ループ | **Y** | `ipairs`/`pairs` | |
| `break` | **Y** | `break` | |
| `continue` | **-** | | Lua 5.5 goto で実装可能 |
| `return` | **Y** | `return expr` | |
| `throw` | **-** | | |
| `try` / `catch` / `finally` | **-** | | |
| `using` 文 (リソース破棄) | **-** | | |
| `lock` | **N/A** | | シングルスレッド |
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
| 文字 (`'A'`) | **-** | | |
| 2進数 (`0b1010`, C# 7) | **-** | | |
| 16進数 (`0xFF`) | **-** | | |
| 桁区切り (`1_000_000`, C# 7) | **-** | | |
| verbatim 文字列 (`@"..."`) | **-** | | |
| raw 文字列 (`"""..."""`, C# 11) | **-** | | |
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
| `+=` `-=` `*=` `/=` `%=` | **Y** | 展開 `x = x op y` | |
| `? :` (三項) | **Y** | IIFE | falsy 安全 |
| `??` (null 合体) | **Y** | `or` | |
| `?.` (null 条件アクセス, C# 6) | **Y** | IIFE nil チェック | |
| `(T)x` (キャスト) | **Y** | 透過 (型消去) | |
| `is null` / `is not null` | **Y** | `== nil` / `~= nil` | |
| `is Type` | **Y** | `getmetatable() ==` | |
| `new T(args)` | **Y** | `T.new(args)` | |
| `??=` (null 合体代入, C# 8) | **-** | | |
| `?[]` (null 条件インデクサ, C# 6) | **-** | | |
| `?.Prop = v` (null条件代入, C# 14) | **-** | | |
| `as` (安全キャスト) | **-** | | |
| `typeof(T)` | **-** | | |
| `nameof(x)` (C# 6) | **-** | | |
| `default` / `default(T)` (C# 7.1) | **-** | | |
| `sizeof(T)` | **N/A** | | |
| `~` (ビット反転) | **-** | | |
| `<<` `>>` `>>>` (シフト) | **-** | | |
| `&` `\|` `^` (ビット演算) | **-** | | |
| `..` (Range, C# 8) | **-** | | |
| `^x` (Index from end, C# 8) | **-** | | |
| `new T { ... }` (初期化子) | **P** | List/Dict のみ | |
| `new(args)` (ターゲット型, C# 9) | **-** | | |
| `x!` (null 許容抑制, C# 8) | **N/A** | | |
| `stackalloc` | **N/A** | | |
| `&x` `*x` `->` (ポインタ) | **N/A** | | |
| `delegate { }` (匿名メソッド, C# 2) | **-** | | |
| `delegate*` (関数ポインタ, C# 9) | **N/A** | | |

### 4.3 高度な式

| 式 | 状態 | Lua マッピング | 備考 |
|---|:----:|--------------|------|
| ラムダ (式本体) `x => expr` | **Y** | `function(x) return expr end` | |
| ラムダ (文本体) `(x) => { }` | **Y** | `function(x) ... end` | |
| switch 式 (C# 8) | **Y** | IIFE if-elseif | |
| `this` | **Y** | `self` | |
| `base.Method()` | **Y** | 親テーブルメソッド | |
| メソッドチェーン (LINQ) | **Y** | ネスト呼び出し | |
| `with` 式 (C# 9) | **-** | | |
| LINQ クエリ構文 (C# 3) | **-** | | メソッドチェーンで代替 |
| メソッドグループ変換 | **-** | | |
| `throw` 式 (C# 7) | **-** | | |
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
| 宣言パターン `is int i` | 7 | **-** | | |
| `var` パターン `is var v` | 7 | **-** | | |
| プロパティパターン `is { Name: "x" }` | 8 | **-** | | |
| 位置パターン `is (1, 2)` | 8 | **-** | | |
| タプルパターン `(a, b) is (1, 2)` | 8 | **-** | | |
| 関係パターン `is > 0` | 9 | **-** | | |
| `and` パターン `is > 0 and < 100` | 9 | **-** | | |
| `or` パターン `is 1 or 2` | 9 | **-** | | |
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
| `partial` (C# 2) | **-** | |
| `required` (C# 11) | **-** | |
| `unsafe` | **N/A** | |

### 6.3 引数修飾子

| 修飾子 | 状態 | 備考 |
|--------|:----:|------|
| `ref` | **-** | |
| `out` | **-** | |
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
| `base.Method()` | **Y** | 親テーブル参照 | |
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
bool  break  case  class  default(switch)  double  else  enum  false
float  for  foreach  if  int  interface  is(部分)  long  namespace
new  null  private  public  protected(部分)  return  static  string
switch  this  true  var  void  while
```

### 未対応

```
abstract  as  byte  catch  char  checked  const  continue  decimal
delegate  do(do-while)  event  explicit  extern  finally  fixed  goto
implicit  in(foreach以外)  internal  lock  object  operator  out  override
params  readonly  ref  sbyte  sealed  short  sizeof  stackalloc  struct
throw  try  typeof  uint(部分)  ulong  unchecked  unsafe  ushort
using(宣言)  virtual(部分)  volatile  yield
```

### コンテキストキーワード

| 状態 | キーワード |
|:----:|---------|
| **Y** | `and`(パターン), `get`, `not`(パターン), `or`(パターン), `set`, `value`, `var` |
| **P** | `when`(switch内のみ), `nint`(lub3d用) |
| **-** | `add`, `alias`, `allows`, `args`, `ascending`, `async`, `await`, `by`, `descending`, `dynamic`, `equals`, `extension`, `field`, `file`, `from`, `global`, `group`, `init`, `into`, `join`, `let`, `managed`, `nameof`, `notnull`, `nuint`, `on`, `orderby`, `partial`, `record`, `remove`, `required`, `scoped`, `select`, `unmanaged`, `where`, `with`, `yield` |

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
| `.Replace(old, new)` | **Y** | `String.Replace(str, old, new)` | T+R |
| `.StartsWith(s)` | **Y** | `String.StartsWith(str, s)` | T+R |
| `.EndsWith(s)` | **Y** | `String.EndsWith(str, s)` | T+R |
| `.Trim()` | **Y** | `String.Trim(str)` | T+R |
| `.Substring(i)` / `.Substring(i,n)` | **Y** | `String.Substring(str, i[, n])` | T+R |
| `.Split(sep)` | **Y** | `String.Split(str, sep)` | T+R |
| `.ToUpper()` | **Y** | `string.upper(str)` | T |
| `.ToLower()` | **Y** | `string.lower(str)` | T |
| `.ToString()` | **Y** | `tostring(x)` | T |
| `string + string` | **Y** | `..` | T |
| `.IndexOf(c)` / `.IndexOf(s)` | **-** | | |
| `.LastIndexOf(c)` / `.LastIndexOf(s)` | **-** | | |
| `.IndexOfAny(chars)` | **-** | | |
| `.Insert(i, s)` | **-** | | |
| `.Remove(i)` / `.Remove(i, n)` | **-** | | |
| `.PadLeft(n)` / `.PadRight(n)` | **-** | | |
| `.TrimStart()` / `.TrimEnd()` | **-** | | |
| `.ToCharArray()` | **-** | | |
| `.Equals(s, comp)` | **-** | | |
| `.CompareTo(s)` | **-** | | |
| `String.IsNullOrEmpty(s)` (static) | **-** | | |
| `String.IsNullOrWhiteSpace(s)` (static) | **-** | | |
| `String.Format(fmt, ...)` (static) | **-** | | 補間で代替 |
| `String.Join(sep, ...)` (static) | **-** | | |
| `String.Concat(...)` (static) | **-** | | |
| `String.Compare(a, b)` (static) | **-** | | |
| `String.Equals(a, b)` (static) | **-** | | |
| `String.Empty` (static) | **-** | | |
| `.ReplaceLineEndings()` | **-** | | |
| `[int]` (char indexer) | **-** | | |

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
| `Math.Pow(x, y)` | **-** | | Lua `^` で可能 |
| `Math.Exp(x)` | **-** | | |
| `Math.Log(x)` / `Math.Log10(x)` / `Math.Log2(x)` | **-** | | |
| `Math.Round(x)` / `Math.Round(x, digits)` | **-** | | |
| `Math.Truncate(x)` | **-** | | |
| `Math.Sign(x)` | **-** | | |
| `Math.Tan(x)` | **-** | | |
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
| `List<T>` | **Y** | sequence table | |
| `Dictionary<TKey, TValue>` | **Y** | hash table | |
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
| `.Sort()` / `.Sort(comparison)` | **-** | | |
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
| `.TryGetValue(key, out value)` | **P** | key 存在チェックのみ | T |
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
| `.Min()` | **Y** | `List.Min(list)` | T+R |
| `.Max()` | **Y** | `List.Max(list)` | T+R |
| `.Sum()` | **Y** | `List.Sum(list)` | T+R |
| `.ToList()` | **Y** | `List.ToList(list)` | T+R |
| チェーン `.Where().Select().ToList()` | **Y** | ネスト呼び出し | T |

### 未対応

| メソッド | 状態 | 備考 |
|---------|:----:|------|
| `.SelectMany()` | **-** | |
| `.OrderByDescending()` | **-** | |
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
| `.Skip()` / `.SkipLast()` / `.SkipWhile()` | **-** | |
| `.Take()` / `.TakeLast()` / `.TakeWhile()` | **-** | |
| `.Chunk()` | **-** | |
| `.Last()` / `.LastOrDefault()` | **-** | |
| `.Single()` / `.SingleOrDefault()` | **-** | |
| `.ElementAt()` / `.ElementAtOrDefault()` | **-** | |
| `.DefaultIfEmpty()` | **-** | |
| `.Count()` (LINQ) | **-** | `.Count` プロパティで代替 |
| `.LongCount()` | **-** | |
| `.Average()` | **-** | |
| `.Aggregate()` | **-** | |
| `.Contains()` (LINQ) | **-** | |
| `.SequenceEqual()` | **-** | |
| `.ToArray()` | **-** | |
| `.ToDictionary()` | **-** | |
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
| `Vector2` | **-** | lub3d Glm で将来対応予定 |
| `Vector3` | **-** | |
| `Vector4` | **-** | lub3d Glm で将来対応予定 |
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
| 未対応構文の警告 | **Y** | Warnings コレクション |
| 複数ファイル入力 | **Y** | 共有 Compilation でクロスファイル参照 |
| namespace 解決 | **Y** | 透過 |
| CLI | **Y** | `tcs a.cs b.cs [-o out.lua] [--sourcemap] [--watch]` |
| ソースマップ | **Y** | `--sourcemap` で `.lua.map` 出力 |
| watch モード | **Y** | `--watch` でファイル監視 |
| 命名規約チェック | **Y** | PascalCase/camelCase 警告 |

## 25. 許容される C# エラー (TinyC# 固有)

| エラーコード | 内容 | 理由 |
|------------|------|------|
| CS0029 | 暗黙の型変換 | enum ↔ int を許容 |
| CS0266 | 明示的変換が必要 | enum ↔ int を許容 |
| CS0019 | 演算子適用不可 | enum == int を許容 |
| CS0535 | interface 未実装 | フィールド = プロパティとして許容 |

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
