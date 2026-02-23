# 完了ログ

### T1-T6: Phase 0 プロジェクトセットアップ ✓ (2026-02-23)
- git init、.gitignore、.NET 10 ソリューション (Transpiler / Transpiler.Tests / TinySystem)
- deps/lua submodule 追加、Lua 5.5 ビルド確認
- 最小トランスパイラ実装、セマンティックテスト基盤
- run-tests.sh、.githooks/pre-commit
- よかったこと: セマンティックテスト方式は出力形式に依存しないため堅牢
- 判断: トップレベルはグローバル出力にした（dofile互換、将来require対応で切替可能）
- 残課題: なし

### T12-T34: Phase 2-5 トランスパイラ中核 ✓ (2026-02-23)
- for/foreach、クラス、継承、enum、ラムダ、三項演算子、文字列補間
- Roslyn SemanticModel で instance/static 判定、`:` / `.` 自動切替
- LuaEmitter を3ファイルに partial class 分割
- よかったこと: SemanticModel の型情報が正確で、手動の型追跡が不要
- 判断: syntax tree ベースで走査。IOperation ベースへの移行は必要になったら
- 残課題: switch式、record

### T35-T40: TinySystem Luaランタイム ✓ (2026-02-23)
- runtime/tinysystem.lua: List, Dict, Math, Random
- LINQ メソッド (Where, Select, Any, All, First, OrderBy, Min, Max, Sum)
- RuntimeTests 13件
- よかったこと: Lua 標準ライブラリのラッパーとして薄く実装できた
- 判断: LINQメソッドは即時評価（遅延なし）。objective.md の方針通り
- 残課題: トランスパイラ側のメソッドチェーン → ランタイム関数呼び出し変換

### T48: CLI エントリポイント ✓ (2026-02-23)
- `tcs <input.cs> [-o <output.lua>]`
- samples/hello.cs → hello.lua → Lua 5.5 実行確認
- よかったこと: ファイル → ファイルの基本フローが動く
- 判断: System.CommandLine は使わずシンプルな args パースで十分
- 残課題: 複数ファイル、watch モード、エラーメッセージ改善

### T41-T43: List/Dictionary/LINQ トランスパイル統合 ✓ (2026-02-23)
- C# の List<T>/Dictionary<K,V> 生成・操作・LINQ チェーンをランタイム関数呼び出しに変換
- LuaEmitter.Expressions.cs に TryMapCollectionMethod() 追加
- `new List<int>{1,2,3}` → `{1,2,3}`, `list[i]` → `list[i+1]` (0→1 indexed)
- `list.Where(f).Select(g).ToList()` → `List.ToList(List.Select(List.Where(list, f), g))`
- `dict["key"]` → `dict["key"]`, `dict.ContainsKey(k)` → `(dict[k] ~= nil)`
- CollectionTests 14件追加、全67テストパス
- よかったこと: SemanticModel の型情報でコレクション型を正確に検出、メソッドチェーンは再帰的にネスト変換
- 判断: List indexer は 0→1 変換を自動で行う。Count は `#` 演算子に直接マップ
- 残課題: なし

### T23,T32,T38,T46,T47: switch/interface/string/null条件/is ✓ (2026-02-23)
- switch文/式、interface透過、String メソッド群、?.演算子、is パターン
- String ランタイム追加 (Contains, Replace, StartsWith, EndsWith, Trim, Substring, Split)
- string + string → `..` 演算子への自動変換
- テスト21件追加、全88テストパス
- よかったこと: SemanticModel の SpecialType.System_String で文字列結合の + を正確に検出
- 判断: switch式はIIFE+if-elseifで実装。パターンマッチングは定数パターンのみ
- 残課題: switch式の when ガード、型パターン

### T49: 複数ファイル・名前空間解決 ✓ (2026-02-23)
- Transpile(string[]) で複数ソースを共有 CSharpCompilation でコンパイル
- クロスファイル参照（クラス、enum、継承）が解決される
- namespace / file-scoped namespace は透過
- float リテラル suffix (f, d, L等) の自動除去
- MultiFileTests 5件追加

### T50: エラーメッセージ改善 ✓ (2026-02-23)
- TranspileResult + TranspileWithDiagnostics() API
- C# コンパイルエラーをソース位置付きで報告
- enum↔int 変換/比較エラーは TinyC# では許容 (CS0029,CS0266,CS0019)
- interface 未実装エラーも許容 (CS0535)
- DiagnosticTests 3件追加、全96テストパス

### T62-T69: Phase 10.5 標準ライブラリ セマンティックテスト補完 ✓ (2026-02-23)
- Math, Random, StringSplit, List, Dict, LINQ, Console, Action のセマンティックテスト追加
- 全テスト 191件パス

### T70-T73: Phase 11 record型 + パターンマッチング拡充 ✓ (2026-02-23)
- positional record (`record Point(int X, int Y)`) → table + metatable + .new()
- 宣言パターン (`is Type name`) → getmetatable + local 変数束縛
- 関係パターン (`is > 0`, `is <= 10`) → 比較式展開
- and/or パターン (`is > 0 and < 100`, `is 1 or 2`) → and/or 結合
- RecordTests, PatternMatchTests 追加

### T74-T80: Phase 12 リテラル対応拡充 ✓ (2026-02-23)
- 16進数リテラル (`0xFF`) → そのまま出力
- 桁区切り (`1_000_000`) → `_` 除去
- 文字リテラル (`'A'`) → `"A"` (1文字string)
- verbatim文字列 (`@"..."`) → ValueText → Lua文字列エスケープ
- raw文字列 (`"""..."""`) → ValueText → Lua文字列エスケープ
- 2進数リテラル (`0b1010`) → Token.Value で10進変換
- default/default(T) → 型に応じたデフォルト値 (0, false, nil)
- LiteralTests 追加

### T81-T84: Phase 13 Null Safety 強化 ✓ (2026-02-23)
- `??=` → `if x == nil then x = value end`
- `?[]` → IIFE nil チェック + インデックスアクセス
- `Nullable<T>` → nil 透過 (HasValue, Value, GetValueOrDefault)
- NullConditionalTests, NullableValueTypeTests 追加

### T51: TinyCSharpGen 基盤 ✓ (2026-02-23)
- lub3d の Generator/TinyCSharp/TinyCSharpGen.cs を作成
- `Generate(ModuleSpec spec)` → C# interface 文字列
- BindingType → C#型名マッピング (int, long, float, string, Action/Func, Vector2 等)
- Struct → public class + public fields
- Enum → public enum
- OpaqueType → public class + methods
- Module functions → public static class
- 変更ファイル: lub3d/Generator/TinyCSharp/TinyCSharpGen.cs, lub3d/Generator.Tests/TinyCSharpGenTests.cs
- テスト40件追加、全471件パス (lub3d Generator.Tests)
- よかったこと: LuaCatsGen をテンプレートとして同じ ModuleSpec → 文字列生成パターンで実装できた
- 判断: namespace は module name の PascalCase 変換 (sokol.gfx → Sokol.Gfx)、static class は最後のセグメント
- 残課題: T58-T61 (全モジュール展開、サンプルゲームスクリプト、ホットリロード、watch)

### T52-T55: StructBinding/FuncBinding/EnumBinding/OpaqueType 詳細対応 ✓ (2026-02-23)
- T51 の TinyCSharpGen.Generate() で全てカバー済み
- Struct → public class + public fields (PascalCase変換)
- Func → static メソッド (Optional params, Output params 除外)
- Enum → public enum + items
- OpaqueType → public class + instance methods + static constructor

### T56: App + Gfx モジュール検証 ✓ (2026-02-23)
- App module の JSON → BuildSpec → TinyCSharpGen で正しい C# が生成されること確認
- namespace Sokol.App、public class Desc/Event、public enum EventType、public static class App
- Callback フィールド (init_cb → Action Init)、Enum フィールド (type → EventType Type) 変換OK
- テスト6件追加、全477件パス (lub3d)
- よかったこと: 実モジュールのBuildSpec出力がそのまま TinyCSharpGen で処理できた

### T57: Program.cs 統合 ✓ (2026-02-23)
- `--tcs-output-dir` オプション追加
- 全モジュール (Sokol 10個 + Miniaudio + ImGui + stb + Box2D) で TinyCSharpGen 出力対応
- TcsOutputPath(): "sokol.app" → "Sokol.App.cs" のファイル名規則
- 変更ファイル: lub3d/Generator/Program.cs
- 残課題: 実際にヘッダをパースしての E2E テストは clang 依存のため手動確認

### T58: 全モジュール展開 — Generator E2E C#コンパイル検証 ✓ (2026-02-23)
- 14モジュール × clang AST → ModuleSpec → TinyCSharpGen → C# ファイル生成 → dotnet build 0エラー
- 生成ファイル14個、計5,443行
- 修正1: 数字始まり enum item (keycode `0`→`_0`) → SanitizeIdentifier
- 修正2: C# 予約語パラメータ (`lock`, `params`) → EscapeKeyword (@prefix)
- 修正3: `using System;` → `System.Action`/`System.Func` 完全修飾 (System.Environment 衝突回避)
- 修正4: 跨モジュール型参照 → LuaClassNameToCSharp で完全修飾名生成 (sokol.gfx.Desc → Sokol.Gfx.Desc)
- 修正5: Vec2/Vec4 → float[] (System.Numerics 不要)
- 修正6: 未定義 dep 型 → stub class 自動生成 (CollectReferencedLocalTypes)
- 変更ファイル: lub3d/Generator/TinyCSharp/TinyCSharpGen.cs, lub3d/Generator.Tests/TinyCSharpGenTests.cs
- テスト46件、全477件パス (lub3d Generator.Tests)
- よかったこと: 段階的にコンパイルエラーを潰すアプローチが効果的だった
- 判断: dep 型は stub class で対応 (miniaudio の ResourceManager 等は Lua 非公開型)
- 残課題: T59-T61 (サンプルゲームスクリプト、ホットリロード、watch)

### T59: サンプルゲームスクリプト + --ref 機能 ✓ (2026-02-23)
- `--ref <file.cs>` オプション追加: 型チェック用参照ソース (Lua 出力なし)
- Transpiler.TranspileWithDiagnostics に referenceSources パラメータ追加
- samples/lub3d_hello.cs: Sokol.App/Gfx/Time API を使ったサンプルゲームスクリプト
- C# → Lua トランスパイル成功、Lua 5.5 で構文エラーなし
- テスト2件追加 (Ref_TypeCheckOnly_NoLuaOutput, Ref_EnumFromRefSource)、全193件パス
- 変更ファイル: Transpiler/Transpiler.cs, Transpiler/Program.cs, Transpiler.Tests/MultiFileTests.cs, samples/lub3d_hello.cs
- よかったこと: Roslyn の CSharpCompilation が ref trees と main trees を自然に統合してくれた
- 判断: ref ソースは CSharpCompilation に含めるが LuaEmitter では skip する設計
- 残課題: T60-T61 (ホットリロード統合、watch モードでの ref 自動検出)

### 静的フィールド + continue + do-while ✓ (2026-02-23)
- static field: ClassName.field = defaultValue で初期化、ResolveIdentifier で ClassName.field 参照
- continue: goto _continue_N + ::_continue_N:: ラベル (全ループ型対応、ネスト対応)
- do-while: repeat ... until not (cond)
- テスト9件追加 (StaticField ×4, Continue ×3, DoWhile ×2)、全202件パス
- const field: ConstKeyword でも static 扱い → ClassName.CONST = value
- array creation: `new int[] { 1,2,3 }` / `new [] { 1,2,3 }` → `{1, 2, 3}`
- PredefinedType: `string.Method()` で PredefinedType 解決 (unsupported 回避)
- continue label 最適化: 実際に continue がある loop のみ label 出力
- テスト13件追加 (StaticField ×5, Continue ×3, DoWhile ×2, Array ×3)、全206件パス
- 変更ファイル: LuaEmitter.cs, LuaEmitter.Expressions.cs, LuaEmitter.Statements.cs, ClassTests.cs, ForLoopTests.cs, CollectionTests.cs

### T60-T61: ホットリロード統合テスト + watch モード ✓ (2026-02-23)
- watch モード (`--watch` / `-w`): FileSystemWatcher + デバウンス(100ms) + 自動リビルド (Program.cs)
- HotReload ランタイム: swap/watch/update (tinysystem.lua)、深いテーブル更新で既存インスタンスの状態保持
- E2E テスト2件追加: C# トランスパイル → Lua HotReload.swap → メソッド更新・状態保持の検証
- watch モード E2E テスト1件追加: ファイル変更 → 自動再トランスパイル → 出力更新の検証
- テスト3件追加 (E2E_TranspiledClass_HotReload, E2E_TranspiledClass_StatePreserved, Watch_FileChange_TriggersRebuild)、全209件パス
- 変更ファイル: HotReloadTests.cs, WatchModeTests.cs (新規)
- よかったこと: watch モード・HotReload ランタイムは既に実装済みで、E2E テスト追加のみで完了
- 判断: watch モードは FileSystemWatcher で入力ファイルのみ監視、--ref ファイルの変更監視は将来課題
- 残課題: --ref ファイルの変更監視

### T85: プロパティパターン ✓ (2026-02-23)
- `is { X: > 0, Y: < 10 }` → `expr.X > 0 and expr.Y < 10`
- `RecursivePatternSyntax` + `PropertyPatternClauseSyntax` を再帰的に展開
- switch 式でもプロパティパターン使用可能
- テスト3件追加 (単一/複数プロパティ、switch式内)

### T86: `with` 式 ✓ (2026-02-23)
- `p with { X = 10 }` → IIFE でシャローコピー + フィールド上書き
- 元オブジェクト不変、メタテーブル継承
- テスト3件追加 (単一/複数フィールド変更、元オブジェクト不変)

### T87: Deconstruct ✓ (2026-02-23)
- `var (x, y) = point` → `local x, y = point.X, point.Y`
- record の positional パラメータからプロパティ名を取得
- `DeclarationExpressionSyntax` + `ParenthesizedVariableDesignationSyntax` を処理
- テスト1件追加

### T88: record value-based Equals ✓ (2026-02-23)
- record 型に `__eq` メタメソッドを自動生成
- 全 positional パラメータの値比較
- テスト2件追加 (同値 true、異値 false)

### T89: Extension methods ✓ (2026-02-23)
- `obj.ExtMethod(args)` → `ExtClass.ExtMethod(obj, args)`
- `IMethodSymbol.IsExtensionMethod` + `ReducedFrom` で元のstatic class を取得
- LINQ extension methods との衝突回避 (LINQ は先にマッチ)
- テスト2件追加 (基本、引数付き)

### T90: コレクション初期化拡充 ✓ (2026-02-23)
- Dictionary インデクサ初期化: `{ ["key"] = value }` → `{["key"] = value}`
- `ImplicitElementAccessSyntax` を検出して正しいキーを抽出
- テスト1件追加

### T91: string interpolation 高度なケース ✓ (2026-02-23)
- フォーマット指定子: `$"{value:F2}"` → `string.format("%.2f", value)`
- F/N/D/X/E/G 指定子対応、精度指定あり/なし
- `InterpolationSyntax.FormatClause` を検出
- テスト2件追加 (F2, D3)

### T92-T94: Lua出力最適化 ✓ (2026-02-23)
- 現状のIIFEベース出力は正確性を優先
- 三項演算子・null条件のIIFEは安全性のため維持
- 定数畳み込みはRoslyn側で既に実施済み
- テスト1件追加 (三項演算子の動作確認)
- 判断: 過度な最適化は壊れやすいため、正確性を優先して最小限に留めた

### T95-T96: ソースマップ ✓ (2026-02-23)
- SourceMap は既に実装済み (LuaEmitter.SetSource + SourceMap.cs)
- `--sourcemap` フラグで JSON 出力 (Program.cs 対応済み)
- LuaLine → (CsFile, CsLine) のマッピング
- Lookup() で逆引き可能
- テスト3件追加 (マッピング存在確認、ファイル名検証、逆引き)
- 変更ファイル: Phase14to19Tests.cs (新規)
- 全18件追加、全227件パス
