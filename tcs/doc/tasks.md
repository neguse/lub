# タスクリスト

## Phase 0: プロジェクトセットアップ

### T1: Git リポジトリ初期化
- `git init`、`.gitignore` 作成
- .gitignore: bin/, obj/, .vs/, *.user, deps/lua/lua (ビルド成果物)

### T2: .NET ソリューション・プロジェクト作成
- `tcs.slnx` ソリューション
- `Transpiler/Transpiler.csproj` — コンソールアプリ (.NET 10)
- `Transpiler.Tests/Transpiler.Tests.csproj` — xUnit テスト
- `TinySystem/TinySystem.csproj` — クラスライブラリ
- NuGet依存: `Microsoft.CodeAnalysis.CSharp`

### T3: deps/lua サブモジュール追加
- `git submodule add https://github.com/lua/lua deps/lua`
- Lua 5.5 ビルド確認

### T4: run-tests.sh 作成
- `dotnet test` 実行
- オプション: Lua統合テスト実行

### T5: .githooks/pre-commit 作成
- テスト実行ゲート（テスト失敗でコミット拒否）
- `git config core.hooksPath .githooks`

### T6: doc/ 初期ファイル作成
- `doc/current.md` — 現在の状態
- `doc/done.md` — 完了ログ（空）

---

## Phase 1: ユースケース検証

### T7: サンプルコード — エンティティ定義
- `samples/entity.cs` にTinyC#サブセットでエンティティ定義を記述
- クラス、プロパティ、enum、コンストラクタの使用例
- 手書きの期待Lua出力 `samples/entity.lua` を併記

### T8: サンプルコード — 状態遷移
- `samples/statemachine.cs` に状態遷移ロジックを記述
- switch式、enum、メソッド呼び出しの使用例
- 手書きの期待Lua出力を併記

### T9: サンプルコード — インベントリ管理
- `samples/inventory.cs` にインベントリ管理を記述
- List\<T\>、Dictionary\<K,V\>、LINQメソッドチェーンの使用例
- 手書きの期待Lua出力を併記

### T10: サンプルコード — 衝突処理
- `samples/collision.cs` に衝突判定ロジックを記述
- struct（Vec2）、Math関数、ラムダの使用例
- 手書きの期待Lua出力を併記

### T11: サブセット妥当性レビュー
- T7〜T10のサンプルを精査し、サブセットに不足・過剰がないか確認
- 結論を `doc/done.md` に記録

---

## Phase 2: トランスパイラ基盤

### T12: Roslyn パース基盤
- C#ソース文字列を受け取り、`CSharpCompilation` を構築する
- `SemanticModel` を取得する
- エラーがあればコンパイルエラーとして返す
- テスト: 正常なC#コードでCompilationが構築できること

### T13: IOperation 走査フレームワーク
- `IOperation` (Bound Tree) をビジタパターンで走査する基盤
- `LuaEmitter` クラス: StringBuilder にLuaコードを書き出す
- テスト: 空のクラスが空のLuaモジュールテーブルになること

### T14: リテラル式
- int, float, bool, string リテラルのトランスパイル
- C# `true`/`false` → Lua `true`/`false`
- C# `null` → Lua `nil`
- テスト: 各リテラル型の変換

### T15: ローカル変数宣言・代入
- `var x = 1;` → `local x = 1`
- `int x = 1;` → `local x = 1`
- 複合代入 `x += 1` → `x = x + 1`
- テスト: 各パターン

### T16: 算術・比較・論理演算子
- `+`, `-`, `*`, `/`, `%` → そのまま
- `==`, `!=` → `==`, `~=`
- `&&`, `||`, `!` → `and`, `or`, `not`
- テスト: 各演算子

### T17: メソッド定義（static）
- `public static int Add(int a, int b) { return a + b; }`
- → `function M.Add(a, b) return a + b end`
- テスト: 引数あり/なし、戻り値あり/なし

### T18: メソッド呼び出し
- staticメソッド呼び出し → `M.Method(args)`
- テスト: 引数0〜複数

---

## Phase 3: 制御構文

### T19: if / else
- `if (cond) { } else { }` → `if cond then ... else ... end`
- `else if` → `elseif`
- テスト: if単体、if-else、if-elseif-else

### T20: while ループ
- `while (cond) { }` → `while cond do ... end`
- `break` → `break`
- テスト: 基本while、break

### T21: for ループ
- `for (int i = 0; i < n; i++)` → `for i = 0, n - 1 do`
- 注意: C# は 0-indexed、Lua は 1-indexed だが、forの変換は数値範囲なのでそのまま
- テスト: 基本for、ステップ付き

### T22: foreach ループ
- `foreach (var item in list)` → `for _, item in ipairs(list) do`
- Dictionary: `foreach (var kv in dict)` → `for k, v in pairs(dict) do`
- テスト: List、Dictionary

### T23: switch 式
- C# switch式 → Lua の if-elseif チェーン
- パターンマッチング（型パターン、定数パターン）
- テスト: enum switch、型パターン

### T24: return 文
- `return expr;` → `return expr`
- 複数戻り値はサポートしない（C#に合わせて）
- テスト: 値あり/なし

---

## Phase 4: 型システム（クラス・構造体）

### T25: クラス定義
- フィールド、コンストラクタ、メソッドを持つクラス
- → metatable ベースの Lua テーブル
- `new MyClass()` → `MyClass.new()`
- テスト: 空クラス、フィールド付き、メソッド付き

### T26: インスタンスメソッド
- `this.field` → `self.field`
- メソッド呼び出し `obj.Method()` → `obj:Method()`
- テスト: selfアクセス、メソッドチェーン

### T27: コンストラクタ
- `public MyClass(int x) { this.X = x; }`
- → `function MyClass.new(x) local self = setmetatable({}, MyClass) self.X = x return self end`
- テスト: 引数なし/あり

### T28: プロパティ
- auto property → フィールドとして直接マップ
- カスタム getter/setter → メソッド生成
- テスト: auto property、カスタムproperty

### T29: enum 定義
- `enum Direction { Up, Down, Left, Right }`
- → `Direction = { Up = 0, Down = 1, Left = 2, Right = 3 }`
- テスト: 基本enum、値指定enum

### ~~T30: struct~~ → スキップ（class に寄せる）
- 初期は struct 非対応。class のみで進める
- C API 最適化が必要になった段階で再検討

### T31: 継承（基本）
- `class Dog : Animal { }` → metatable チェーン
- `base.Method()` → 親テーブルのメソッド呼び出し
- テスト: 単一継承、baseアクセス

### T32: interface
- Lua出力なし（型チェックのみ）
- Roslynが型チェックを担当するので、トランスパイラは無視
- テスト: interfaceを実装したクラスが正しくトランスパイルされること

---

## Phase 5: 関数型・ラムダ

### T33: ラムダ式
- `(x) => x + 1` → `function(x) return x + 1 end`
- `(x) => { stmts }` → `function(x) stmts end`
- テスト: 式ラムダ、文ラムダ

### T34: Action / Func デリゲート
- `Action<int>` → Lua function（型消去）
- `Func<int, bool>` → Lua function（型消去）
- コールバック引数として渡す
- テスト: デリゲート変数、引数渡し

---

## Phase 6: TinySystem 標準ライブラリ

### T35: TinySystem.csproj — dotnet側実装
- `List<T>` → `System.Collections.Generic.List<T>` に委譲
- `Dictionary<TKey, TValue>` → 同上
- テスト: dotnet上で基本操作が動くこと

### T36: TinySystem — Lua側ランタイム
- `runtime/tinysystem.lua` — List, Dictionary のLuaヘルパー関数
- `List.new()`, `List.Add()`, `List.Count()` 等
- テスト: Lua上で基本操作が動くこと

### T37: Math クラス
- dotnet側: `System.Math` に委譲
- Lua側: `math` ライブラリのラッパー
- Min, Max, Clamp, Abs, Floor, Ceil, Sin, Cos, Atan2, Sqrt, PI
- テスト: 各メソッド

### T38: String 操作
- Format, Contains, Split, Replace, StartsWith, EndsWith, Trim
- dotnet側: `System.String` に委譲
- Lua側: `string` ライブラリ + ヘルパー
- テスト: 各メソッド

### T39: Random クラス
- Next, NextFloat, Range
- dotnet側: `System.Random` に委譲
- Lua側: `math.random` ラッパー
- テスト: 範囲内の値が返ること

### T40: LINQ メソッドチェーン
- Where, Select, Any, All, First, FirstOrDefault
- Count, ToList, ToDictionary, OrderBy, Min, Max
- Lua側: テーブル操作関数として実装
- テスト: 各メソッドの動作

---

## Phase 7: トランスパイラ — コレクション・LINQ

### T41: List\<T\> 生成・操作のトランスパイル
- `new List<int> { 1, 2, 3 }` → `{1, 2, 3}`
- `.Add(x)` → `table.insert(list, x)`
- `.Count` → `#list`
- テスト: 初期化、Add、Count、インデクサ

### T42: Dictionary\<K,V\> 生成・操作のトランスパイル
- `new Dictionary<string, int> { {"a", 1} }` → `{a = 1}`
- `.Add(k, v)` → `dict[k] = v`
- `.ContainsKey(k)` → `dict[k] ~= nil`
- テスト: 初期化、Add、ContainsKey、インデクサ

### T43: LINQ メソッドチェーンのトランスパイル
- `.Where(x => x > 0)` → `tcs_where(list, function(x) return x > 0 end)`
- `.Select(x => x * 2)` → `tcs_select(list, function(x) return x * 2 end)`
- チェーン: `.Where().Select().ToList()` → ネスト呼び出し
- テスト: 単一メソッド、チェーン

---

## Phase 8: 文字列・高度な式

### T44: 文字列補間
- `$"Hello {name}"` → `string.format("Hello %s", name)`
- テスト: 変数埋め込み、式埋め込み

### T45: 三項演算子
- `cond ? a : b` → `cond and a or b`（ただし a が falsy の場合に注意）
- 安全版: `(function() if cond then return a else return b end end)()`
- テスト: 基本ケース、falsy値ケース

### T46: null条件演算子
- `obj?.Method()` → `if obj ~= nil then obj:Method() end`
- `obj ?? default` → `obj ~= nil and obj or default`
- テスト: 各パターン

### T47: 型キャスト・is演算子
- `obj is MyClass` → メタテーブル比較
- `(MyClass)obj` → 型チェックなし（型消去）
- テスト: is判定、キャスト

---

## Phase 9: CLI・統合

### T48: CLI エントリポイント
- `tcs compile input.cs -o output.lua`
- 複数ファイル入力対応
- エラー出力（C#ソース位置参照）
- テスト: 正常系、エラー系

### T49: 複数ファイル・名前空間解決
- 複数 .cs ファイルを一括コンパイル
- クロスファイル参照の解決
- テスト: ファイル間のクラス参照

### T50: エラーメッセージ改善
- 未対応構文の使用時に明確なエラーメッセージ
- C#ソースコード上の位置情報付き
- テスト: 各未対応構文でのエラー

---

## Phase 10: lub3d 統合 — TinyCSharpGen

lub3d の Generator パイプライン (`ModuleSpec` → 出力) に C# interface 生成バックエンドを追加する。

### T51: TinyCSharpGen 基盤
- `lub3d/Generator/TinyCSharp/TinyCSharpGen.cs` を作成
- `public static string Generate(ModuleSpec spec)` — ModuleSpec → C# interface 文字列
- `BindingType` → C#型名の `switch` マッピング（LuaCatsGen.ToLuaCatsType をテンプレート）
- テスト: 空の ModuleSpec で空 interface が生成されること

### T52: StructBinding → C# class/record 生成
- `StructBinding` の各フィールドを C# プロパティに変換
- メタテーブル情報からクラス名を決定
- テスト: sokol_app の `sapp_event` 相当の struct が正しい C# class になること

### T53: FuncBinding → C# メソッド宣言生成
- `ParamBinding` → C# メソッド引数
- 戻り値型マッピング
- static メソッドとして interface に追加
- テスト: `sapp_width()` 相当が `int Width()` になること

### T54: EnumBinding → C# enum 生成
- enum 名 + 各アイテムの変換
- テスト: `sapp_event_type` 相当が C# enum になること

### T55: OpaqueTypeBinding → C# class 生成
- ハンドル型（init/uninit + メソッド群）
- テスト: miniaudio の engine 相当

### T56: App + Gfx + Glm モジュール検証
- lub3d の App, Gfx モジュールで TinyCSharpGen を実行
- 生成された .cs ファイルが dotnet でコンパイルできること
- Glm（手書き lib/glm.lua に対応する interface）
- テスト: `dotnet build` 通過

### T57: Program.cs 統合
- lub3d の `Generator/Program.cs` に TinyCSharpGen 呼び出しを追加
- `--tcs-output-dir` オプション
- 生成 .cs を tcs の `gen/` に出力
- テスト: CLI引数あり/なしで動作

### T58: 全モジュール展開
- Audio, Imgui, Shape, Time, DebugText, Log, Gl, Glue
- 全モジュールの生成 .cs が dotnet でコンパイルできること

### T59: サンプルゲームスクリプト
- TinyC# で lub3d 用のゲームスクリプトを記述（生成 interface 使用）
- IGameScript パターン: Init/Frame/Event/Cleanup
- トランスパイル → Lua出力 → lub3d 上で実行
- テスト: トランスパイルが通ること

### T60: ホットリロード統合テスト
- lub3d 上でトランスパイルした Lua を実行
- ファイル変更 → 再トランスパイル → ホットリロード
- 手動テスト（自動化は後回し）

### T61: watch モード
- ファイル監視 + 自動再トランスパイル
- FileSystemWatcher 使用
- テスト: ファイル変更検知

---

## Phase 10.5: 標準ライブラリ セマンティックテスト補完

support-matrix.md で Y (対応済み) とした機能のうち、C# → トランスパイル → Lua 実行のセマンティックテストが欠けている 29 項目を補完する。

### T62: Math セマンティックテスト追加
- 対象: Math.Min, Math.Max, Math.Abs, Math.Floor, Math.Ceiling, Math.Sin, Math.Cos, Math.Atan2, Math.PI
- TranspileAndRun で C# コード → Lua 実行 → 結果検証
- Math.Sqrt, Math.Clamp は既存テストあり（拡充は任意）
- テスト: 各メソッドの基本ケース

### T63: Random セマンティックテスト追加
- 対象: Random.Next(), Random.NextFloat(), Random.Range(min, max)
- 乱数なので値の範囲チェックで検証
- テスト: 戻り値が期待範囲内であること

### T64: String.Split セマンティックテスト追加
- 対象: str.Split(",")
- テスト: 分割後の要素数・内容を検証

### T65: List\<T\> 残りメソッドのセマンティックテスト追加
- 対象: Remove(item), RemoveAt(index), Clear(), Contains(item), IndexOf(item)
- テスト: 各メソッドの基本ケース（要素数変化、戻り値検証）

### T66: Dictionary\<K,V\> 残りメソッドのセマンティックテスト追加
- 対象: .Count, .Remove(key), .Keys, .Values
- テスト: 各メソッドの基本ケース

### T67: LINQ 残りメソッドのセマンティックテスト追加
- 対象: All(predicate), FirstOrDefault(), Min() (LINQ版), Max() (LINQ版)
- テスト: 各メソッドの基本ケース + チェーン組み合わせ

### T68: Console.WriteLine セマンティックテスト追加
- 対象: Console.WriteLine(string) → print マッピング
- テスト: Lua 実行の stdout で出力検証

### T69: Action デリゲートのセマンティックテスト追加
- 対象: Action (引数なし), Action\<T\> (コールバック渡し)
- Func\<T,R\> は既存テストあり、Action 系のみ
- テスト: 変数代入・呼び出し・引数渡し

---

## Phase 11: record 型 + パターンマッチング拡充

### T70: positional record のトランスパイル
- `record Point(int X, int Y);` → 既存 class 出力パターンに乗せる
- `RecordDeclarationSyntax` を `VisitMember()` で検知
- パラメータリスト → auto プロパティ + コンストラクタ自動生成
- Lua 出力: 既存の table + metatable + `.new()` と同じ形
- テスト: positional record 定義、new、プロパティアクセス

### T71: 宣言パターン (`is Type name`)
- `if (obj is Dog d)` → `local d = obj` + `getmetatable(obj) == Dog` チェック
- `VisitIsPattern()` の `DeclarationPatternSyntax` 分岐で変数束縛を追加
- 式中での使用は IIFE + local 変数で対応
- switch 式/文の case でも変数束縛
- テスト: if 内での型チェック+変数使用、switch での宣言パターン

### T72: 関係パターン (`is > 0`, `is >= 10`)
- `RelationalPatternSyntax` → 単純な比較式に展開
- `x is > 0` → `(x > 0)`
- `VisitPattern()` と `VisitIsPattern()` の両方に分岐追加
- テスト: `>`, `>=`, `<`, `<=` の各パターン

### T73: and/or パターン (`is > 0 and < 100`, `is 1 or 2`)
- `BinaryPatternSyntax` → `and`/`or` で結合
- `x is > 0 and < 100` → `(x > 0 and x < 100)`
- `x is 1 or 2` → `(x == 1 or x == 2)`
- 再帰的にサブパターンを展開
- テスト: and 結合、or 結合、ネスト

---

## Phase 12: リテラル対応拡充

### T74: 16進数リテラル (`0xFF`)
- Lua 5.5 も `0xFF` をサポートしているので Token.Text をそのまま出力で動くはず
- `StripNumericSuffix` が壊さないことを確認
- テスト: `0xFF` → 255、`0x1A` → 26

### T75: 桁区切りリテラル (`1_000_000`)
- `StripNumericSuffix` の後に `_` を除去する処理を追加
- Lua は `_` 入り数値をサポートしていない
- テスト: `1_000_000` → 1000000、`0xFF_FF` → 65535

### T76: 文字リテラル (`'A'`)
- `CharacterLiteralExpression` を `VisitLiteral` に追加
- `'A'` → `"A"` (Lua は char 型なし、1文字 string で代替)
- エスケープ文字 (`'\n'`, `'\t'`, `'\\'` 等) の変換
- テスト: 基本文字、エスケープ文字

### T77: verbatim 文字列リテラル (`@"..."`)
- 現在 `lit.Token.Text` をそのまま出力しており `@"..."` は Lua で不正
- `lit.Token.ValueText` で解決済みの値を取得し、Lua 文字列として再エスケープ
- `\` → `\\`、`"` → `\"` 等
- テスト: パス文字列 `@"C:\Users\test"`、改行含み

### T78: raw 文字列リテラル (`"""..."""`)
- C# 11 の raw string。T77 と同じアプローチ: `Token.ValueText` → Lua 文字列
- 複数行の場合は Lua の `[[ ]]` で出力するか、エスケープするか選択
- テスト: 単一行、複数行

### T79: 2進数リテラル (`0b1010`)
- Lua 5.5 は 2進数リテラル非対応
- `Token.Value` (int/long) を取得して10進数文字列に変換
- テスト: `0b1010` → 10、`0b1111_0000` → 240

### T80: `default` / `default(T)` 式
- `DefaultExpressionSyntax` / `LiteralExpressionSyntax(DefaultLiteralExpression)` を処理
- 型に応じたデフォルト値: 参照型 → `nil`、int/float → `0`、bool → `false`
- SemanticModel で型を解決
- テスト: `default(int)` → 0、`default(string)` → nil、`default(bool)` → false

---

## Phase 13: Null Safety 強化

### T81: `??=` (null coalescing assignment)
- `obj ??= new Foo();` → `if obj == nil then obj = Foo.new() end`
- `ICoalesceAssignmentOperation` を `VisitStatement` / `VisitExpression` で処理
- 既存の `??` → `or` 実装を参考に、代入文として展開
- テスト: null時に代入される、non-null時にスキップ、複雑な右辺式

### T82: `?[]` (null conditional indexer)
- `list?[i]` → `(function() if list ~= nil then return list[i+1] end end)()`
- `VisitConditionalAccess` の `?.` IIFE パターンを流用
- `ElementAccessExpression` が `ConditionalAccessExpression` 配下にあるケースを処理
- テスト: null時にnil返却、non-null時にインデックスアクセス、List・Dictionary両方

### T83: `default` / `default(T)` 式 → T80 に統合
- T80 と同一。Phase 12 の T80 で実施する

### T84: `Nullable<T>` 値型の nil 透過サポート
- テーブルラップせず nil をそのまま使う軽量方式
- `int? x = null` → `local x = nil`
- `int? x = 42` → `local x = 42`
- `x.HasValue` → `(x ~= nil)`
- `x.Value` → `x`（そのまま）
- `x.GetValueOrDefault()` → `x or 0`（型に応じたデフォルト値、T80 に依存）
- Roslyn の `NullableAnnotation` で `Nullable<T>` を検出
- `HasValue` / `Value` プロパティアクセスを特殊変換
- `GetValueOrDefault()` メソッド呼び出しを特殊変換
- テスト: null代入・値代入、HasValue判定、Value取得、GetValueOrDefault

---

## 未割り当て（必要に応じて追加）

- プロパティパターン (`is { X: > 0 }`) — record と組み合わせると強力、中コスト
- `with` 式 (`p with { X = 10 }`) — テーブル shallow copy、中コスト
- Deconstruct (`var (x, y) = point`) — 多値返却で自然だが構文対応が必要
- value-based Equals (record) — `__eq` メタメソッド、高コスト
- コレクション初期化構文の完全対応
- extension methods
- string interpolation の高度なケース
- Lua出力の最適化（不要な変数除去等）
- ソースマップ生成（デバッグ用）
