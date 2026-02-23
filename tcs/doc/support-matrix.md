# TinyC# サポートマトリクス

## 凡例
- ✓ = サポート済み
- ◑ = 部分サポート（制限あり）
- ✗ = 未サポート
- T = トランスパイラ側の変換
- R = ランタイム (tinysystem.lua) の関数

---

## 型宣言

| C# 構文 | サポート | Lua 出力 | 備考 |
|---------|:------:|---------|------|
| class | ✓ T | table + metatable + `.new()` | |
| enum | ✓ T | 定数テーブル `{A=0, B=1}` | |
| interface | ◑ T | 出力なし (型チェックのみ) | Roslyn が検証、Lua は透過 |
| struct | ✗ | — | class に寄せる方針 |
| record | ✗ | — | |
| ユーザ定義 generic | ✗ | — | List/Dict のみ型消去で対応 |

## メンバ

| C# 構文 | サポート | Lua 出力 | 備考 |
|---------|:------:|---------|------|
| フィールド | ✓ T | `self.Field = val` | コンストラクタ内で初期化 |
| auto プロパティ | ✓ T | フィールドと同一扱い | |
| custom get/set | ✓ T | `get_Prop()` / `set_Prop(value)` | |
| static メソッド | ✓ T | `Class.Method()` | |
| instance メソッド | ✓ T | `obj:Method()` | SemanticModel で自動判定 |
| expression-bodied | ✓ T | `function() return expr end` | |
| コンストラクタ | ✓ T | `Class.new(args)` | |
| base() 呼び出し | ✓ T | `Base.new()` + `setmetatable` | |
| 継承 | ✓ T | metatable チェーン | 単一継承のみ |
| operator overload | ✗ | — | |
| event / delegate | ✗ | — | ラムダで代替 |
| extension method | ✗ | — | LINQ のみ組み込み対応 |

## 制御構文

| C# 構文 | サポート | Lua 出力 | 備考 |
|---------|:------:|---------|------|
| if / else if / else | ✓ T | `if / elseif / else / end` | |
| while | ✓ T | `while cond do ... end` | |
| for (数値) | ✓ T | `for i=start,limit do` | `i++` パターンを最適化 |
| for (一般) | ✓ T | while ループに展開 | |
| foreach (List) | ✓ T | `for _, v in ipairs(t) do` | |
| foreach (Dict) | ✓ T | `for k, v in pairs(t) do` | KVP 構造体を生成 |
| switch 文 | ✓ T | `if / elseif / else / end` | break は暗黙的 |
| switch 式 | ✓ T | IIFE + if-elseif | 定数パターンのみ |
| break | ✓ T | `break` | |
| return | ✓ T | `return expr` | |
| do-while | ✗ | — | |
| try / catch / finally | ✗ | — | |
| using 文 | ✗ | — | |
| yield | ✗ | — | |
| goto | ✗ | — | |
| continue | ✗ | — | |

## 式・演算子

| C# 構文 | サポート | Lua 出力 | 備考 |
|---------|:------:|---------|------|
| `+` (算術) | ✓ T | `+` | |
| `+` (文字列結合) | ✓ T | `..` | 型で自動判定 |
| `-` `*` `/` `%` | ✓ T | そのまま | |
| `==` `!=` | ✓ T | `==` `~=` | |
| `<` `<=` `>` `>=` | ✓ T | そのまま | |
| `&&` `\|\|` `!` | ✓ T | `and` `or` `not` | |
| `??` | ✓ T | `or` | |
| `?.` | ✓ T | IIFE nil チェック | |
| `? :` (三項) | ✓ T | IIFE | falsy 安全 |
| `i++` / `i--` (文) | ✓ T | `i = i + 1` / `i = i - 1` | |
| `++i` / `--i` (式) | ✓ T | `(i + 1)` / `(i - 1)` | |
| `=` `+=` `-=` `*=` `/=` `%=` | ✓ T | 展開 `x = x op y` | |
| `(Type)expr` キャスト | ✓ T | 透過 (型消去) | |
| `is null` / `is not null` | ✓ T | `== nil` / `~= nil` | |
| `is Type` | ✓ T | `getmetatable() ==` | |
| ラムダ `x => expr` | ✓ T | `function(x) return expr end` | |
| ラムダ `(x) => { }` | ✓ T | `function(x) ... end` | |
| `$"...{expr}..."` | ✓ T | `"..." .. tostring(expr) .. "..."` | |
| `new Class(args)` | ✓ T | `Class.new(args)` | |
| `new List<T>{...}` | ✓ T | `{v1, v2, ...}` | |
| `new Dict<K,V>{...}` | ✓ T | `{[k1]=v1, ...}` | |
| `this` | ✓ T | `self` | |
| `base` | ✓ T | `self` (metatable チェーン経由) | |
| `typeof` | ✗ | — | |
| `as` | ✗ | — | |
| `nameof` | ✗ | — | |
| `await` | ✗ | — | |
| 配列 `new T[]` | ✗ | — | List で代替 |
| LINQ クエリ構文 | ✗ | — | メソッドチェーンのみ |

## リテラル

| C# リテラル | サポート | Lua 出力 | 備考 |
|------------|:------:|---------|------|
| int | ✓ T | そのまま | |
| float/double | ✓ T | サフィックス除去 (`3.0f` → `3.0`) | |
| long/uint 等 | ✓ T | サフィックス除去 | |
| bool | ✓ T | `true` / `false` | |
| string | ✓ T | そのまま | |
| null | ✓ T | `nil` | |
| char | ✗ | — | string で代替 |

## コレクション (トランスパイラ + ランタイム)

### List\<T\>

| C# API | サポート | Lua 出力 | 区分 |
|--------|:------:|---------|:----:|
| `new List<T>()` | ✓ | `{}` | T |
| `new List<T>{1,2,3}` | ✓ | `{1,2,3}` | T |
| `list[i]` | ✓ | `list[i + 1]` | T (0→1変換) |
| `.Count` | ✓ | `#list` | T |
| `.Add(x)` | ✓ | `table.insert(list, x)` | T |
| `.Remove(x)` | ✓ | `List.Remove(list, x)` | T+R |
| `.RemoveAt(i)` | ✓ | `table.remove(list, i+1)` | T |
| `.Clear()` | ✓ | IIFE pairs 削除 | T |
| `.Contains(x)` | ✓ | `List.Contains(list, x)` | T+R |
| `.IndexOf(x)` | ✓ | `List.IndexOf(list, x)` | T+R |

### LINQ (List\<T\> / IEnumerable\<T\>)

| C# API | サポート | Lua 出力 | 区分 |
|--------|:------:|---------|:----:|
| `.Where(predicate)` | ✓ | `List.Where(list, fn)` | T+R |
| `.Select(selector)` | ✓ | `List.Select(list, fn)` | T+R |
| `.Any([predicate])` | ✓ | `List.Any(list, fn)` | T+R |
| `.All(predicate)` | ✓ | `List.All(list, fn)` | T+R |
| `.First([predicate])` | ✓ | `List.First(list, fn)` | T+R |
| `.FirstOrDefault([pred])` | ✓ | `List.FirstOrDefault(list, fn)` | T+R |
| `.OrderBy(keySelector)` | ✓ | `List.OrderBy(list, fn)` | T+R |
| `.Min([selector])` | ✓ | `List.Min(list, fn)` | T+R |
| `.Max([selector])` | ✓ | `List.Max(list, fn)` | T+R |
| `.Sum([selector])` | ✓ | `List.Sum(list, fn)` | T+R |
| `.ToList()` | ✓ | `List.ToList(list)` | T+R |
| `.Count()` (LINQ) | ✗ | — | `.Count` プロパティで代替 |
| `.Distinct()` | ✗ | — | |
| `.GroupBy()` | ✗ | — | |
| `.SelectMany()` | ✗ | — | |
| `.Skip()` / `.Take()` | ✗ | — | |
| `.Aggregate()` | ✗ | — | |
| `.ToDictionary()` | ✗ | — | |
| 遅延評価 | ✗ | — | 全て即時評価 |

### Dictionary\<K,V\>

| C# API | サポート | Lua 出力 | 区分 |
|--------|:------:|---------|:----:|
| `new Dictionary<K,V>()` | ✓ | `{}` | T |
| `new Dictionary{{"k",v}}` | ✓ | `{["k"]=v}` | T |
| `dict[key]` | ✓ | `dict[key]` | T |
| `.Count` | ✓ | `Dict.Count(dict)` | T+R |
| `.Add(k, v)` | ✓ | `dict[k] = v` | T |
| `.Remove(k)` | ✓ | `Dict.Remove(dict, k)` | T+R |
| `.ContainsKey(k)` | ✓ | `(dict[k] ~= nil)` | T |
| `.TryGetValue(k, out v)` | ◑ | `(dict[k] ~= nil)` | T (値取得なし) |
| `.Keys` | ✓ | `Dict.Keys(dict)` | T+R |
| `.Values` | ✓ | `Dict.Values(dict)` | T+R |
| `.ContainsValue(v)` | ✗ | — | |

## String

| C# API | サポート | Lua 出力 | 区分 |
|--------|:------:|---------|:----:|
| `.Length` | ✓ | `#str` | T |
| `.Contains(s)` | ✓ | `String.Contains(str, s)` | T+R |
| `.Replace(old, new)` | ✓ | `String.Replace(str, old, new)` | T+R |
| `.StartsWith(s)` | ✓ | `String.StartsWith(str, s)` | T+R |
| `.EndsWith(s)` | ✓ | `String.EndsWith(str, s)` | T+R |
| `.Trim()` | ✓ | `String.Trim(str)` | T+R |
| `.Substring(start[,len])` | ✓ | `String.Substring(str, s, l)` | T+R |
| `.ToUpper()` | ✓ | `string.upper(str)` | T |
| `.ToLower()` | ✓ | `string.lower(str)` | T |
| `.Split(sep)` | ✓ | `String.Split(str, sep)` | T+R |
| `.ToString()` (任意の型) | ✓ | `tostring(x)` | T |
| `string.Format()` | ✗ | — | 文字列補間で代替 |
| `.IndexOf()` | ✗ | — | |
| `.PadLeft/Right()` | ✗ | — | |
| `.TrimStart/End()` | ✗ | — | |
| `string.IsNullOrEmpty()` | ✗ | — | |
| `string.Join()` | ✗ | — | |

## Math (ランタイム)

| C# API | サポート | Lua 出力 | 区分 |
|--------|:------:|---------|:----:|
| `Math.Min(a, b)` | ✓ | `Math.Min(a, b)` | R |
| `Math.Max(a, b)` | ✓ | `Math.Max(a, b)` | R |
| `Math.Abs(x)` | ✓ | `Math.Abs(x)` | R |
| `Math.Floor(x)` | ✓ | `Math.Floor(x)` | R |
| `Math.Ceil(x)` | ✓ | `Math.Ceil(x)` | R |
| `Math.Sqrt(x)` | ✓ | `Math.Sqrt(x)` | R |
| `Math.Sin(x)` | ✓ | `Math.Sin(x)` | R |
| `Math.Cos(x)` | ✓ | `Math.Cos(x)` | R |
| `Math.Atan2(y, x)` | ✓ | `Math.Atan2(y, x)` | R |
| `Math.Clamp(v, min, max)` | ✓ | `Math.Clamp(v, min, max)` | R |
| `Math.PI` | ✓ | `Math.PI` | R |
| `Math.Round()` | ✗ | — | |
| `Math.Pow()` | ✗ | — | Lua `^` で代替可能 |
| `Math.Log()` | ✗ | — | |
| `Math.Tan()` | ✗ | — | |

## Random (ランタイム)

| C# API | サポート | Lua 出力 | 区分 |
|--------|:------:|---------|:----:|
| `Random.Next([min[,max]])` | ✓ | `Random.Next(min, max)` | R |
| `Random.NextFloat()` | ✓ | `Random.NextFloat()` | R |
| `Random.Range(min, max)` | ✓ | `Random.Range(min, max)` | R |

## 診断・ツール

| 機能 | サポート | 備考 |
|-----|:------:|------|
| C# コンパイルエラー報告 | ✓ | ソース位置付き |
| 未対応構文の警告 | ✓ | Warnings コレクション |
| 複数ファイル入力 | ✓ | 共有 Compilation でクロスファイル参照解決 |
| namespace 解決 | ✓ | 透過 (Lua 出力に影響なし) |
| CLI | ✓ | `tcs a.cs b.cs [-o out.lua] [--sourcemap] [--watch]` |
| ソースマップ | ✓ | `--sourcemap` で `.lua.map` 出力 (Lua行→C#行) |
| watch モード | ✓ | `--watch` でファイル監視+自動再トランスパイル |

## 数値 (暗黙の変換)

| C# | Lua | 備考 |
|----|-----|------|
| int, long, short, byte | number (double) | 全て Lua number に統一 |
| float, double, decimal | number (double) | サフィックス除去 |
| enum 値 | number | 整数定数として扱う |
| bool | boolean | `true` / `false` |
| string | string | UTF-8 前提 |
| null / object | nil / table | |

## 許容される C# エラー (TinyC# 固有)

| エラーコード | 内容 | 理由 |
|------------|------|------|
| CS0029 | 暗黙の型変換 | enum ↔ int を許容 |
| CS0266 | 明示的変換が必要 | enum ↔ int を許容 |
| CS0019 | 演算子適用不可 | enum == int を許容 |
| CS0535 | interface 未実装 | フィールド = プロパティとして許容 |
