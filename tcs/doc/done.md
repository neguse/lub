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

### T103,T115: Compact C# baseline / support matrix 整理 ✓ (2026-06-29)
- C# 14 / full BCL 全対応ではなく、モダンな .NET/C# 開発体験に必要な小さな baseline を採用
- `doc/support-matrix.md` に Core / Useful / Out of scope の分類を追加
- 言語機能と標準ライブラリのサマリへ優先度列を追加し、現実装に合わせて対応状況を更新
- `doc/tasks.md` の推奨着手順を T97 起点に整理し、T103/T115 は完了扱いにした
- 判断: lub3d 直対応は tcs 本体の主目的から外し、外部 API facade / `--ref` の参考統合として扱う
- 残課題: T101/T102 で実行環境の信頼性を固める

### T97: 未対応構文を黙殺しない診断へ統一 ✓ (2026-06-29)
- `VisitMember` と class/record 内 member の default 経路で unsupported warning を出すようにした
- `VisitStatement` の default 経路で `throw`, `try/catch`, `using`, local function などが黙って消えないようにした
- unsupported pattern の `TODO pattern` コメントを warning 化した
- binary/unary/postfix/assignment expression の unsupported コメントも warning 化した
- 診断位置に file/line/column を含め、将来の T122 analyzer で使い回しやすい形に寄せた
- テスト7件追加、全234テストパス
- 判断: T122 でルール共有を進める前段として、まず既存 `Warnings` API に最小統合した
- 残課題: T122 で Roslyn Analyzer PoC、T98 で top-level statements の復旧または禁止を決める

### T101-T102: Lua 実行環境の再現性 ✓ (2026-06-29)
- CLI 生成 Lua に TinySystem runtime prelude をデフォルト埋め込みし、`--no-runtime` で bare 出力へ戻せるようにした
- runtime prelude 埋め込み時も `.lua.map` の Lua 行番号が実ファイルの行番号に合うよう SourceMap offset を追加
- `TestHelper` / `run-tests.sh` / `run-tests.ps1` で `lua -v` が `Lua 5.5` で始まることを検証するようにした
- CLI E2E テスト3件と Lua version テスト1件を追加し、全238 tcs テスト + 5 analyzer テストが通過
- 変更ファイル: Program.cs, LuaRuntime.cs, SourceMap.cs, TestHelper.cs, CliRuntimeTests.cs, RuntimeTests.cs, run-tests.sh, run-tests.ps1, README.md
- よかったこと: runtime をファイルパス参照ではなく埋め込みにしたため、生成 Lua 単体で List/Dict/Math/String/Random/HotReload を再現できる
- 判断: エンジン統合や runtime 外部供給の余地を残すため、Transpiler API は bare Lua のままとし、CLI にだけ runtime 埋め込みを適用した
- 残課題: 配布時に runtime/tinysystem.lua を CLI と同梱する packaging 方針は T118 で整理する

### T98-T100,T104-T105: Core 正しさ穴埋め ✓ (2026-06-29)
- T98: top-level statements を Lua chunk へ出力し、型定義を先に emit して同一/別ファイルの class 参照を可能にした
- T99: `base.Method(args)` を `Base.Method(self, args)` へ変換し、override/new method 内の親実装呼び出しを修正した
- T100: null 条件アクセス内の String/List/Dictionary method/property/indexer を通常アクセスと同じ runtime mapping に通した
- T104: initializer なし instance field / auto property を型別 default (`0`/`false`/`nil`) で constructor 初期化するようにした
- T105: 一般 `for` ループの incrementor を文として emit し、`i--`, `i += 2`, 複数 incrementor を正しく処理するようにした
- テスト13件追加: TopLevelStatementTests, InheritanceTests, NullConditionalTests, ClassTests, ForLoopTests
- 変更ファイル: Transpiler.cs, LuaEmitter.cs, LuaEmitter.Expressions.cs, LuaEmitter.Statements.cs, TopLevelStatementTests.cs, InheritanceTests.cs, NullConditionalTests.cs, ClassTests.cs, ForLoopTests.cs, support-matrix/current/tasks
- よかったこと: 既存の statement emit / default value / runtime mapping を再利用し、特殊ケースを増やしすぎずに Core gap を埋められた
- 判断: top-level statements は禁止ではなく復旧を選び、C# 側 compilation は top-level 検出時だけ `OutputKind.ConsoleApplication` に切り替える
- 残課題: T106 の lambda block SourceMap、T107 の `--ref` watch、T108 の TinySystem facade

### T106-T107: SourceMap/watch 開発体験ギャップ修正 ✓ (2026-06-29)
- T106: lambda block 生成時に一時 `_sb` / `_luaLine` / `_currentSource` を復元し、仮 emit の SourceMap を削除するようにした
- T106: `AppendLine` が multiline 文字列の実出力行数を `_luaLine` に反映するようにした
- T107: watch モードの監視対象に `--ref` ファイルを含め、参照 stub/facade 更新でも rebuild されるようにした
- T107: `--ref` ファイルの存在チェックを CLI 引数検証に追加した
- テスト2件追加: block lambda 後続行 SourceMap、`--ref` watch rebuild
- 変更ファイル: SourceMap.cs, LuaEmitter.cs, LuaEmitter.Expressions.cs, Program.cs, SourceMapTests.cs, WatchModeTests.cs
- よかったこと: SourceMap のずれは行カウンタ復元だけでなく multiline output の実行数加算も同時に直す必要があると確認できた
- 判断: lambda block 内部の個別行マッピングはまだ粗いが、今回の修正範囲は「後続行を壊さない」ことに絞った
- 残課題: T108 の TinySystem facade、T114 の Lua runtime error から SourceMap を引く仕組み

### T108: TinySystem C# facade 同期 ✓ (2026-06-29)
- `TinySystem/RuntimeFacades.cs` を追加し、runtime/tinysystem.lua と対応する `Random`, `Math`, `String`, `List`, `Dict` の C# 型チェック用 API を定義した
- Transpiler の Roslyn compilation に TinySystem.dll を参照追加し、手書き stub なしで `TinySystem.Random.NextFloat()` などが解決できるようにした
- `TinySystem.*` facade static call を Lua runtime global (`Random.*`, `Math.*`, `String.*`, `List.*`, `Dict.*`) へ変換する mapping を追加した
- `RandomSemanticTests` の手書き `Random` stub を削除し、`TinySystem.Random` facade 参照へ移行した
- facade E2E テスト5件追加、Random 既存テスト3件も facade 経由で通過
- 変更ファイル: RuntimeFacades.cs, Transpiler.cs, LuaEmitter.Expressions.cs, TinySystemFacadeTests.cs, RandomSemanticTests.cs, current/tasks/done
- よかったこと: TinySystem.dll を Transpiler compilation に入れることで、IDE/build/check/transpile が同じ facade を見られる基盤になった
- 判断: `System.Action` / `System.Func` は BCL 型をそのまま使い、TinySystem 側で別名 delegate を増やさない
- 残課題: T122 の analyzer で TinySystem facade を許可 API 判定に使う、T7-T11 で実サンプルから不足 facade を洗い出す

### T7: サンプル検証ハーネス整備 ✓ (2026-06-29)
- `samples/hello.cs`, `samples/game.cs`, `samples/inventory.cs` を実ファイルから読み、C# → Lua 5.5 実行まで検証する E2E テストを追加した
- 期待 Lua 文字列比較ではなく、代表 API の戻り値を検証する形にした
- 検証内容: Hello greeting/add, Battle.Run summary, Inventory/Game.Test summary
- テスト3件追加、サンプルが現行 Compact C# baseline 上で動作することを確認
- 変更ファイル: SampleE2ETests.cs, current/tasks/done
- よかったこと: T98-T108 の基盤修正後に既存サンプルがそのまま実行でき、baseline の実用経路を確認できた
- 判断: `samples/lub3d_hello.cs` は外部 stub が必要なため T7 対象外とし、T121 で扱いを整理する
- 残課題: T8-T11 で entity/statemachine/inventory/collision サンプルを拡充し、不足 API を洗い出す

### T8: エンティティ定義サンプル ✓ (2026-06-29)
- `samples/entity.cs` を追加し、class, field, auto property, enum, constructor, method による状態更新の最小サンプルを作った
- `EntitySample.Run()` の E2E テストを追加し、生成・移動・ダメージ・switch expression による表示文字列を Lua 5.5 で検証した
- テスト1件追加、期待値 `Slime:enemy@6,2 HP=13` を確認
- 変更ファイル: samples/entity.cs, SampleE2ETests.cs, current/tasks/done
- よかったこと: T104 の field/property default と T98-T100 の Core 修正後の構文をサンプルでも自然に使えることを確認できた
- 判断: 既存 `samples/game.cs` から分離せず、entity 単体の読みやすい最小サンプルとして追加した
- 残課題: T9 状態遷移サンプル、T10 inventory Dictionary 拡張、T11 collision サンプル

### T9: 状態遷移サンプル ✓ (2026-06-29)
- `samples/statemachine.cs` を追加し、enum, switch 文, switch 式, method call による状態遷移サンプルを作った
- default case が先頭にある switch 文を E2E で検証し、Lua 出力では default section を最後の `else` として emit するよう修正した
- `StateMachineSample.Run()` の E2E テストを追加し、期待値 `open,open,locked,closed` を確認
- テスト1件追加
- 変更ファイル: samples/statemachine.cs, LuaEmitter.Statements.cs, SampleE2ETests.cs, current/tasks/done
- よかったこと: サンプル作成で switch default 非末尾の実バグを検出し、仕様に近い形へ修正できた
- 判断: switch 文の section 順序は case section を先に評価し、default は出現位置に関わらず fallback として最後に出す
- 残課題: T10 inventory Dictionary 拡張、T11 collision サンプル

### T10: インベントリ Dictionary サンプル拡張 ✓ (2026-06-29)
- `samples/inventory.cs` に `Dictionary<string, Item>` の名前検索インデックスを追加した
- `Add` で List と Dictionary の両方を更新し、`ContainsKey` と indexer lookup を `Summary()` 経由で使うようにした
- 既存 Sample E2E の期待値を `Items=4 Total=330 Best=Sword Shield=1` に更新し、List/LINQ/Dictionary の実サンプルを同時に検証した
- 変更ファイル: samples/inventory.cs, SampleE2ETests.cs, current/tasks/done
- よかったこと: 既存 Inventory サンプルの流れを保ったまま Dictionary の代表操作を自然に追加できた
- 判断: `DictSemanticTests` の単体保証に加え、ユーザー向け sample でも `ContainsKey` + indexer を通す形にした
- 残課題: T11 collision サンプルとサブセット妥当性レビュー

### T11: 衝突処理サンプル + サブセット妥当性レビュー ✓ (2026-06-29)
- `samples/collision.cs` を追加し、`Vec2` と `CircleCollider` を `class` で表現した衝突判定サンプルを作った
- `CollisionSample.Run()` の E2E テストを追加し、期待値 `hit,miss,hit` を Lua 5.5 で確認した
- `objective.md` の型リストから Core 風に見える `struct` 記述を外し、ユーザー定義 `struct` / `record struct` は Useful 扱いで class/record 代替する方針を明記した
- サンプル検証の結果、現時点の entity/statemachine/inventory/collision ではユーザー定義 struct は不要と判断した
- テスト1件追加
- 変更ファイル: samples/collision.cs, SampleE2ETests.cs, objective.md, current/tasks/done
- よかったこと: collision でも class ベースの Vec2 で十分に読みやすく、struct 実装を Core に戻す根拠は出なかった
- 判断: ユーザー定義 struct は T120 の診断/実装方針タスクに残し、サンプル側では class/record 代替を標準とする
- 残課題: T119 でサンプルから必要になった標準ライブラリ候補だけ追加検討する

### T109: Dictionary TryGetValue セマンティクス ✓ (2026-06-29)
- `Dictionary<TKey,TValue>.TryGetValue(key, out value)` を out 代入 + bool 戻り値として変換するようにした
- key が存在する場合は value へ代入して `true`、存在しない場合は TValue の default (`0`/`false`/`nil`) を代入して `false` を返す
- `out var` / 既存 out 変数の両方を statement emit 側で扱い、条件式内でも宣言済み変数として使えるようにした
- テスト4件追加: found, missing default, `if` 条件内 out var, 既存 string out 変数の missing
- 変更ファイル: LuaEmitter.Expressions.cs, LuaEmitter.Statements.cs, DictSemanticTests.cs, current/tasks/support-matrix/done
- よかったこと: out/ref 全般対応へ広げず、Compact C# baseline に必要な `TryGetValue` だけを閉じた IIFE とローカル宣言で実装できた
- 判断: `Dictionary` は Lua `nil` を key 不在と同一視する現方針を維持し、null 値セマンティクスは T111 へ残す
- 残課題: T111 で `List<T>` null 要素と `Dictionary<K,V>` null 値の扱いを定義する

### T110: LINQ Count/ToDictionary Core 実装 ✓ (2026-06-29)
- LINQ `.Count()` / `.Count(predicate)` を `List.Count(list, predicate)` へ変換するようにした
- LINQ `.ToDictionary(keySelector)` / `.ToDictionary(keySelector, valueSelector)` を `List.ToDictionary` runtime へ追加した
- TinySystem C# facade に predicate 版 `Count` と `ToDictionary` overload を追加し、dotnet 側の型チェックと Lua runtime を同期した
- テスト4件追加: Count, Count(predicate), ToDictionary key selector, ToDictionary value selector
- 変更ファイル: runtime/tinysystem.lua, RuntimeFacades.cs, LuaEmitter.Expressions.cs, LinqSemanticTests.cs, current/tasks/support-matrix/done
- よかったこと: objective.md の LINQ baseline に既に含まれていた API を実装へ追いつかせ、サンプルから必要になりやすい集計/辞書化を Core として扱えるようになった
- 判断: 遅延評価は導入せず、既存 LINQ runtime と同じ即時評価 table 生成に揃える
- 残課題: T119 で追加 LINQ (`OrderByDescending`, `Take`, `Skip`, `Last/LastOrDefault` など) の必要性をサンプルベースで判断する

### T111: List/Dictionary null 保存セマンティクス ✓ (2026-06-29)
- Lua table の制約に合わせ、`List<T>` の null 要素と `Dictionary<K,V>` の null 値は TinyC# では未対応として禁止する方針にした
- shared compliance facts に `TCS1003` を追加し、analyzer と transpiler warning の両方で同じ検出ルールを使うようにした
- 検出対象: collection initializer、`List.Add(null)`, `list[i] = null`, `Dictionary.Add(k, null)`, `dict[k] = null`, `ToDictionary(..., _ => null)` の直接 `null` / 参照型 `default`
- analyzer テスト2件、transpiler diagnostic テスト1件を追加
- 変更ファイル: TinyCsComplianceFacts.cs, TinyCsComplianceAnalyzer.cs, TinyCsComplianceAnalyzerTests.cs, Transpiler.cs, DiagnosticTests.cs, README.md, current/tasks/support-matrix/done
- よかったこと: sentinel を runtime に導入せず、Lua の `nil` 表現とずれるケースを事前診断に寄せて実装を小さく保てた
- 判断: nullable flow の完全解析や null key の扱いまでは広げず、ソース上で直接分かる null 値保存をまず警告する
- 残課題: 必要になれば T119/T120 以降で null key 診断やより強い nullable flow 解析を検討する

### T112: HotReload mtime の組み込み環境対応 ✓ (2026-06-29)
- `runtime/tinysystem.lua` の default `HotReload.mtime` から `io.popen('stat ...')` 依存を削除し、shell 非依存の no-op (`nil`) にした
- `HotReload.watch` / `HotReload.update` は `HotReload.mtime` を `pcall` 経由で呼び、host 側 mtime 実装の例外で runtime 全体が落ちないようにした
- engine 側は `HotReload.mtime = function(path) return fs.mtime(path) end` のように注入する方針を README/current/support-matrix に明記した
- HotReload テスト2件追加: default mtime が shell 不要で安全に nil を返すこと、注入 mtime で watch/update が reload すること
- 変更ファイル: runtime/tinysystem.lua, HotReloadTests.cs, README.md, current/tasks/support-matrix/done
- よかったこと: デスクトップ専用の `stat` 推測をやめ、iOS/WASM/Switch など shell-less 環境でも安全な default にできた
- 判断: 純 Lua で不完全な mtime 推測を続けず、HotReload の file watch は host API 注入を明示要件にする
- 残課題: T113 で Lua ビルドのプラットフォーム分岐、T114 で runtime error と SourceMap 連携を整理する

### T113: Lua CMake platform 分岐 ✓ (2026-06-29)
- `CMakeLists.txt` の Lua build を Linux / Windows / macOS / iOS-family / Emscripten / BSD / generic Unix で分岐し、非 Windows 全部を `LUA_USE_LINUX` + `m dl` にする状態を解消した
- Linux は `LUA_USE_LINUX` + `m` + `${CMAKE_DL_LIBS}`、macOS は `LUA_USE_MACOSX` + `m`、iOS/tvOS/watchOS は `LUA_USE_IOS`、Emscripten/unknown は `LUA_USE_C89` にした
- `run-tests.sh` / `run-tests.ps1` は Lua binary が存在しない場合だけでなく、`CMakeLists.txt` / `luaconf.h` / `lua.c` より古い場合や version mismatch 時も再ビルドするようにした
- Linux 環境で `cmake -B build -DCMAKE_BUILD_TYPE=Release -S . && cmake --build build --parallel 2` が通ることを確認した
- 変更ファイル: CMakeLists.txt, run-tests.sh, run-tests.ps1, README.md, current/tasks/support-matrix/done
- よかったこと: CMake の platform 判定を `CMAKE_SYSTEM_NAME` ベースに寄せ、desktop と embedded/WASM の前提を分けられた
- 判断: iOS-family と Emscripten はこの repo では cross build 実行まではせず、compile definitions の方針と host 側検証境界を文書化する
- 残課題: T114 で runtime error と SourceMap 連携を整理する

### T114: SourceMap runtime error 注釈 CLI ✓ (2026-06-29)
- `SourceMapResolver` を追加し、`.lua.map` JSON から Lua 行番号を C# ファイル:行番号へ解決できるようにした
- `tcs --map-stacktrace <output.lua.map> [trace.txt]` を追加し、Lua stack trace 内の `file.lua:line:` 行へ `--> file.cs:line` 注釈を付けて stdout に出すようにした
- stack trace 行が SourceMap の exact key に無い場合は、直前の mapping を使う nearest lookup にした
- `.lua.map` の仕様を README に明記した: `version: 1`, `mappings: { "<luaLine>": { "file": "...", "line": n } }`
- テスト2件追加: resolver の exact/nearest 注釈、CLI `--map-stacktrace` の注釈出力
- 変更ファイル: SourceMapResolver.cs, Program.cs, SourceMapTests.cs, CliRuntimeTests.cs, README.md, current/tasks/support-matrix/done
- よかったこと: runtime 実行器に依存せず、Lua の標準 stack trace テキストを後処理するだけで既存 SourceMap を実用経路へ接続できた
- 判断: 現時点の SourceMap は statement の最初の出力行中心なので、generated line 内部の完全精度ではなく nearest lookup による実用的な復元を採用した
- 残課題: SourceMap の列情報や multi-line 式内部の精度改善は、必要になった時に別タスクで扱う

### T116: README / objective / q / current 同期 ✓ (2026-06-29)
- README に `--ref` の参照専用 stub/facade 手順を追加し、CMake 要件を `3.12+` へ更新した
- `objective.md` を TinySystem facade 実装済み、collection null 診断、実装済み String/LINQ 対象範囲に合わせた
- `q.md` の Q2/Q3/Q5 を resolved な決定事項として整理し、namespace mapping、entrypoint、CLI/watch/sourcemap 方針を明記した
- `doc/current.md` の次タスクを T117/T119/T122 へ同期した
- 変更ファイル: CMakeLists.txt, README.md, objective.md, q.md, current/tasks/done
- よかったこと: 実装で固まった CLI と facade の境界を古い検討メモから回収できた
- 判断: commit 履歴欄は実 git 履歴として残し、未コミットの今回作業は done/current の完了タスク欄で表現する
- 残課題: T117 で CLI UX、T118 で配布/依存の再現性、T121 で外部エンジンサンプルの扱いを整理する

### T117: CLI 引数 UX 改善 ✓ (2026-06-29)
- `--help` / `-h` を追加し、usage を stdout に出して exit 0 にした
- `--version` を追加し、`tcs 0.1.0` を stdout に出して exit 0 にした
- unknown option を error として stderr に出し、exit 1 にした
- `-o` / `--ref` の値欠落を検出し、次の option を値として誤読しないようにした
- README の `src/*.cs` は shell glob 展開に依存する例であることを明記した
- CLI テスト5件追加
- 変更ファイル: Program.cs, CliRuntimeTests.cs, README.md, current/tasks/support-matrix/done
- よかったこと: 引数パースを大きな parser 導入なしで、既存 CLI の構造に沿って明確化できた
- 判断: glob 展開は CLI 側ではまだ実装せず、T117 ではドキュメント明記に留める
- 残課題: T118 で配布/依存の再現性、T121 で外部エンジンサンプルの扱いを整理する

### T118: 依存・配布の再現性 ✓ (2026-06-29)
- floating package version (`4.*`, `17.*`, `2.*`) を解決済み version へ pin した
- `Directory.Build.props` で `RestorePackagesWithLockFile=true` を有効化し、各 project の `packages.lock.json` を生成した
- `Transpiler.csproj` の publish/build 出力へ `runtime/tinysystem.lua` を `runtime/` 配下でコピーするようにした
- `dotnet publish Transpiler/Transpiler.csproj -c Release -o /tmp/tcs-publish` を実行し、publish 出力単体で runtime prelude 埋め込みが動くことを確認した
- README に `deps/lua` submodule policy、package pin/lock policy、CLI publish 手順と runtime 同梱方針を追記した
- 変更ファイル: Directory.Build.props, *.csproj, packages.lock.json, README.md, current/tasks/support-matrix/done
- よかったこと: runtime を publish output に含めたことで、開発 checkout 以外から実行した CLI でも `--no-runtime` なしの通常変換が再現できる
- 判断: NuGet は floating range を使わず、今 restore で解決されていた version へ明示 pin する
- 残課題: T119 で標準ライブラリ小拡張、T120 で struct 方針、T121 で外部エンジンサンプルの扱いを整理する

### T119: 標準ライブラリ小拡張 ✓ (2026-06-29)
- Compact C# baseline とサンプルで不足しやすい API に絞り、`String.IndexOf`, `String.Join`, `Math.Pow`, `List.Sort`, LINQ `OrderByDescending` / `Take` / `Skip` / `Last` / `LastOrDefault` を追加した
- Lua runtime と TinySystem C# facade を同期し、transpiler の instance/static/extension method mapping を追加した
- LINQ 追加 API は既存方針どおり遅延評価ではなく即時評価 table を返す
- テスト13件追加: string IndexOf/Join overloads, Math.Pow, List.Sort overloads, LINQ OrderByDescending/Take/Skip/Last/LastOrDefault
- 変更ファイル: runtime/tinysystem.lua, RuntimeFacades.cs, LuaEmitter.Expressions.cs, StringMethodTests.cs, MathSemanticTests.cs, ListSemanticTests.cs, LinqSemanticTests.cs, README.md, objective.md, current/tasks/support-matrix/done
- よかったこと: T119 の候補を全部入り BCL に広げず、ゲームスクリプトで使いやすい小核だけを TDD で増やせた
- 判断: `List.Reverse`, `Find` 系、DateTime/TimeSpan 相当は現サンプルでは必要性が薄いため未対応のまま維持する
- 残課題: T120 で struct 方針、T121 で外部エンジンサンプルの扱いを整理する

### T120: struct 方針を TCS1001 診断として確定 ✓ (2026-06-29)
- ユーザー定義 `struct` / `record struct` は現時点で Lua へ class 相当に lowering せず、TCS1001 の未対応構文診断として扱う方針に確定した
- `record struct` の analyzer / transpiler 診断テストを追加し、既存 `struct` 診断と同じ shared compliance facts 経由で検出されることを確認した
- `objective.md` と `doc/support-matrix.md` の記述を、曖昧な「class に寄せる」から「class / record class で代替」に更新した
- テスト2件追加: analyzer `RecordStructDeclaration_ReportsUnsupportedSyntax`, transpiler `UnsupportedRecordStructDeclaration_ReportsWarning`
- 変更ファイル: TinyCsComplianceAnalyzerTests.cs, DiagnosticTests.cs, README.md, objective.md, current/tasks/support-matrix/done
- よかったこと: 値セマンティクスの不完全な再現に踏み込まず、現サンプルで有効だった class/record class 代替を仕様境界として明確にできた
- 判断: 外部数学型や engine API の値型が必要になった場合は、ユーザー定義 struct lowering ではなく facade/stub か専用 runtime 型として別途検討する
- 残課題: T121 で外部エンジンサンプルの扱いを整理する

### T121: 外部エンジン連携サンプル整理 ✓ (2026-06-29)
- `samples/lub3d_hello.cs` を削除し、engine agnostic な `samples/host_api_game.cs` + `samples/host_api_stub.cs` に置き換えた
- `host_api_stub.cs` は `--ref` 用の型チェック専用 stub とし、Lua 出力には含めない方針を sample と README で明示した
- E2E テストで ref source が Lua に出ないこと、実行時に注入した `Screen` / `Time` / `Log` Lua table で sample が動くことを確認した
- テスト1件追加: `Sample_HostApiRef_TranspilesAndRuns`
- 変更ファイル: samples/host_api_game.cs, samples/host_api_stub.cs, samples/lub3d_hello.cs, SampleE2ETests.cs, README.md, current/tasks/done
- よかったこと: `--ref` の代表例を特定エンジン名から外し、host API 境界だけを小さく示せるようになった
- 判断: lub3d 連携は過去の generator 参考実装として記録に残し、tcs 本体の sample は engine agnostic に保つ
- 残課題: T122 の Rider 実機確認が残る

### T122: Rider リアルタイム警告向け tcs Roslyn Analyzer PoC ✓ (2026-07-12)
- TinyCs.Analyzers / analyzer tests / samples/analyzer-demo / `tcs check` / CI workflow / analyzer nupkg consumer 検証 / InspectCode headless 検証 / Rider helper script (bash + PowerShell) を整備した
- Rider 実機 (Windows 10.0.26200, JetBrains Toolbox) で TCS1001 x5 / TCS1002 x1 / TCS1003 x1 が editor inspection に表示され、`.editorconfig` の severity=error 昇格が Rider 表示にも反映されることを確認し、Q12 を go で解決した
- Windows pwsh 7 で `$isWindows` / `$isMacOS` が read-only 自動変数と衝突して precheck が落ちる不具合を修正した (Linux の pwsh 7.6.3 で再現・検証)
- run-tests.ps1 の bash helper script 検証は WSL bash 検出が壊れやすく Rider 確認にも寄与しないため撤去し、`verify-rider-scripts.sh` の検証は run-tests.sh (Linux/CI) のみとした
- 変更ファイル: run-tests.ps1, samples/analyzer-demo/rider-env.ps1, samples/analyzer-demo/verify-inspectcode.ps1, q.md, doc/tasks.md, doc/done.md, doc/current.md, CLAUDE.md
- よかったこと: pwsh を Linux に導入して Windows の失敗を手元で再現でき、実機往復を減らして修正を検証できた
- 判断: precheck 全 pass を Rider 起動の前提にした設計は目的過剰だった。go/no-go 確認は analyzer-demo build pass だけで進められると判断し、bash gate は撤去した
- 残課題: T123 (analyzer package 正式導線) / T124 (診断一致の継続検証)

### タスク棚卸し・トリアージ再設定 ✓ (2026-07-12)
- T122 go 後の方向をタスク棚卸しで再設定し、lub (`../lub`) の Haxe 代替検証を P0 とした
- T125 (script 層ギャップ分析) / T126 (00_hello 相当を tcs で実行) / T127 (hot reload 検証) を新設
- T123 は release 手順の README 化へ縮小して P2 降格 (NuGet 公開は Q8 どおり当面やらない、consumer gate は run-tests で恒常化済み)
- T124 はタスクとしてクローズし、run-tests の恒常ゲート (analyzer-demo expected diagnostics / nupkg consumer 検証) として扱う
- `../lub` は readonly。lub 側に変更が必要な場合は feature request を出す
- 変更ファイル: doc/tasks.md, doc/current.md, doc/done.md, CLAUDE.md
- 判断: stub を書き始める前にギャップ分析 (T125) を置き、snake_case リネームやエントリ契約など tcs 本体機能になり得るものと shim で吸収するものを先に切り分ける

### T125: lub script 層のギャップ分析 ✓ (2026-07-12)
- lub の entry/hot reload/module/core API/lubx 契約を実装読解し、tcs で 00_hello 相当の最小コードを transpile/check して実験した
- 成果物: `doc/lub-gap-analysis.md`。ギャップは G1 (object initializer 黙殺 = 既存バグ) / G2 (module return なし) / G3 (naming warning で check exit 1) / G4 (multi-return 表現なし) の4点
- 名前は無変換で emit されるためリネーム機構は不要、`--ref` と lub extern の構図一致、`.lua` entry は lub 外パス可 + mtime poll で hotswap、と PoC 経路が lub 無変更で成立する見込みを確認した
- 切り分け: G1-G4 は全て tcs 側機能 (T128-T131 として起票)、stub/起動定型は tcs 側 samples で吸収、lub への feature request は現時点なし
- 変更ファイル: doc/lub-gap-analysis.md, doc/tasks.md, doc/current.md, doc/done.md
- よかったこと: 読解だけで済ませず最小コードを実 transpile したことで、黙殺バグ (G1) と check exit 1 (G3) という読解では出ない事実を掴めた
- 判断: G1 は lub 非依存の正しさバグなので PoC 都合と切り離して最優先に置いた。multi-return (G4) は 00_hello に不要なので breakout 級まで先送りした
- 残課題: T128 → T129 → T130 → T126 → T127 → T131 の順で実装・検証

### T128: object initializer の黙殺修正 (G1) ✓ (2026-07-12)
- `new T { X = v }` の初期化子が診断なしで消える既存バグを修正した (T97 方針)
- 通常 class は IIFE (`local __init = T.new(...)` + field 代入) で対応、`--ref` 型 (Lua 出力対象外) は plain table literal `{key = value}` へ emit するようにした
- `--ref` 型の `new` はこれまで存在しない `.new()` を emit して実行時エラーだったが、lub の opts table 契約にそのまま合う形になった
- implicit `new(args)` / `new() { ... }` にも List/Dict/ref/class の同じ経路を配線した
- ネストした初期化子・collection add 形式・ref 型 constructor 引数は TCS1001 warning (黙殺しない)
- テスト5件追加 (ObjectInitializerTests): class 初期化子、constructor 併用、ref 型 table literal、ref 型空 table、ネスト警告
- 変更ファイル: LuaEmitter.cs, LuaEmitter.Expressions.cs, Transpiler.cs, ObjectInitializerTests.cs, doc/support-matrix.md, current/tasks/done
- よかったこと: ref 型かどうかを DeclaringSyntaxReferences と ReferenceTrees の照合だけで判定でき、emitter に大きな状態を足さずに済んだ
- 判断: 通常 class の初期化子は TCS1001 にせず対応した。record `with` の IIFE 前例があり、コストが低かったため
- 残課題: T129 (module return) → T130 (naming 抑制) → T126 (00_hello)

### T129: entry class 指定で module return を出す (G2) ✓ (2026-07-12)
- `--entry <Class>` CLI オプションを追加し、出力 Lua の末尾に `return <Class>` を追記するようにした
- lub の entry module 契約 (require が callback table を返す) に tcs 出力単体で適合できる
- entry class が存在しない場合と `--ref` 型 (Lua 出力対象外) の場合はエラーにする
- Transpiler API は `TranspileWithDiagnostics(..., entryClass:)`、watch モードにも配線
- テスト4件追加 (EntryClassTests): dofile が class table を返す、not found エラー、ref-only エラー、CLI E2E
- 変更ファイル: Transpiler.cs, Program.cs, EntryClassTests.cs, README.md, current/tasks/done
- よかったこと: emitter 出力の末尾追記だけで済み、SourceMap のずれも runtime prelude 埋め込み (前置) とも干渉しない
- 判断: entry 検証は GetTypeByMetadataName で行い、namespace 付き (`Foo.Bar`) はそのまま Lua の namespace table パスに一致する形にした
- 残課題: T130 (naming 抑制) → T126 (00_hello)

### T130: naming warning の抑制手段 (G3) ✓ (2026-07-12)
- `--no-naming-check` CLI flag を transpile / `tcs check` の両方に追加した (デフォルト挙動は不変)
- lub の wire format (lowerCamel callback / snake_case API) を使うコードを `tcs check` ゲートに乗せられるようになった
- Transpiler API は `TranspileWithDiagnostics(..., checkNaming:)`。TCS1001/1002/1003 の準拠診断は抑制対象外
- テスト4件追加 (NamingSuppressionTests): デフォルト警告あり、抑制で警告なし、抑制時も TCS1001 は残る、check の exit code 比較
- 変更ファイル: Transpiler.cs, Program.cs, NamingSuppressionTests.cs, README.md, current/tasks/done
- よかったこと: NamingAnalyzer は元々独立していたので、呼び出しの conditional 化だけで済んだ
- 判断: file 単位や diagnostic 単位の細かい suppress は必要になるまで作らない。flag 一発で全 naming warning を止める
- 残課題: T126 (00_hello) → T127 (hot reload)

### T126: 00_hello 相当を tcs で動かす + G5 --prelude ✓ (2026-07-12)
- `samples/lub/` に hello.cs (entry) / lub_stub.cs (--ref) / lub_shim.lua (--prelude) / run-lub.sh (staging + headless 起動) を追加した
- lub 実機 (lavapipe + xvfb headless) で tcs 生成 Lua が onInit → onFrame ループ → onQuit まで動き、`--capture` の frame 30 画像が clear_color (0.1, 0.1, 0.2) どおりであることを確認した
- 新ギャップ G5 を発見: lub C 側は flat global を expose し、namespace table は Haxe 専用 prelude が組み立てる。汎用機能 `--prelude <shim.lua>` (ユーザー Lua を出力へ前置、SourceMap offset 合算) を tcs に追加して解決した
- ギャップ分析の誤り2点を訂正: 非 hxml entry の dirname は package.path に追加されない (samples/<mod>/.lub の cwd 相対レイアウト前提)、API は namespace table ではなく flat global
- Program.cs の Run 系引数を BuildOptions record に整理した
- テスト3件追加 (PreludeTests): prelude 前置で ref API が解決される E2E、missing file エラー、+ 全318テスト green
- 変更ファイル: Program.cs, PreludeTests.cs, samples/lub/*, doc/lub-gap-analysis.md, README.md, current/tasks/done
- よかったこと: lub の --capture がそのまま画面検証ゲートになった。読解ベースの分析2点が実測で覆っており、PoC を先にやる判断が正しかった
- 判断: lub 側 HAXE_PRELUDE 相当は lub 固有 shim (samples/lub/lub_shim.lua) と汎用 CLI (--prelude) に分離し、tcs 本体に lub 依存を入れない
- 残課題: T127 (hot reload) → T131 (multi-return)。lub feature request 候補: .lua entry の任意パス対応

### T127: lub 上の hot reload 検証 ✓ (2026-07-12)
- lub 実行中に `tcs --watch` で entry .lua を再生成 → lub の mtime poll → `lume.hotswap` が発火し、Lua エラーなしで clear 色の変更が反映されることを確認した (変更後の frame を `--capture` して赤画面を検証)
- 発見1: `tcs --watch` が Changed イベントしか拾わず、エディタの atomic save (tmp へ書いて rename) を取りこぼしていた。NotifyFilters.FileName + Created/Renamed handler を追加し、os.replace での保存でも rebuild が走ることを実測した
- 発見2: WatchModeTests が `dotnet run` の wrapper だけ Kill して子の Transpiler --watch プロセスを leak していた (47個蓄積、inotify 枯渇で他テストが flaky 化)。`proc.Kill(entireProcessTree: true)` に修正し、3連続 green + leak 0 を確認した
- tcs 側 HotReload runtime は lub 経路では不要 (lume.hotswap が担う)。併用しない方針を current.md に明記
- 変更ファイル: Program.cs, WatchModeTests.cs, current/tasks/done
- よかったこと: 実機の hot reload 検証が、watch の atomic save バグとテストの process leak という2つの既存問題を炙り出した
- 判断: leak した watch プロセス群は当セッションで kill して掃除した。flaky の再発条件 (inotify 上限) は leak 修正で根が取れている
- 残課題: T131 (multi-return) → breakout 級サンプル移植の判断

### T131: Lua multi-return 対応 (G4) ✓ (2026-07-12)
- `--ref` method の `out` 引数を Lua multi-return 受けにマップした: void 戻りは `a, b = f(args)`、値戻りは IIFE で `__ret, a, b = f(args)` を受けて `__ret` を返す (条件式内でも使える)
- out 変数の local 宣言は既存の EmitOutVarDeclarations (statement 冒頭) をそのまま利用し、`out _` は Lua の `_` へ落ちる
- lub の `Io.load_text` (text/version/status) 型 API を C# の out 引数シグネチャで書けるようになった
- テスト3件追加 (MultiReturnTests): multi-return 受け、値戻り + 条件式、discard
- 変更ファイル: LuaEmitter.Expressions.cs, MultiReturnTests.cs, current/tasks/done
- よかったこと: TryGetValue の先例 (statement 冒頭 local + 代入) に乗ったので、emitter の新規機構なしで表現できた
- 判断: `ref` 引数は未対応警告に留めた (lub API に前例がなく、C# 側の意味論も multi-return と一致しない)
- 残課題: T132 (breakout 級サンプル移植) で実戦検証
