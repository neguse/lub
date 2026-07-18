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
- よかったこと: デスクトップ専用の `stat` 推測をやめ、iOS/WASM/コンソール機など shell-less 環境でも安全な default にできた
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

### T123: tcs analyzer package の release 手順整備 (縮小) ✓ (2026-07-12)
- README の analyzer 節に release 手順 (PackageVersion bump → 参照例と run-tests consumer の version 同期 → pack → run-tests 検証) を追記した
- NuGet 公開は当面行わず local nupkg 配布のみとする方針 (Q8) を手順に明記した
- 変更ファイル: README.md, current/tasks/done
- 判断: T123 当初案の metadata / versioning policy / CI gate 整備は、consumer gate が run-tests で恒常化済みのため手順文書化のみに縮小した (2026-07-12 の棚卸しどおり)
- 残課題: なし (公開判断は必要になった時点で再検討)

### T132: lub breakout 級サンプル移植 ✓ (2026-07-12)
- lub の samples/09_breakout 相当を TinyC# で移植した (samples/lub/breakout.cs、Brick class + List<double> 頂点構築 + out 引数 multi-return)
- lub_stub.cs / lub_shim.lua を Input (key_down/key_pressed) / Io (require "lub_io") / Gfx (use_shader/use_buffer/draw + VERTEX/NONE/ALPHA) まで拡張し、run-lub.sh をサンプル名パラメータ対応にした
- lavapipe headless + `--capture` frame 240 で、ブロック破壊・スコア・残機減少まで決定論 gameplay が動くことを画像で確認した
- 未実測2項目を確定: `--ref` 型 instance method は colon call で lub の userdata method 規約 (self 第1引数) と一致、host table の field 読みは透過 (RefTypeAccessTests 2件で固定)
- 追加発見1: `x!` (null-forgiving) が unsupported 黙殺で Lua を壊していた → 透過に修正 (support-matrix N/A → Y)
- 追加発見2: 完全修飾 `System.Math.*` の TCS1002 誤検出 → T133 として起票 (breakout は `using System;` + bare Math で回避)
- 変更ファイル: samples/lub/breakout.cs, samples/lub/lub_stub.cs, samples/lub/lub_shim.lua, samples/lub/run-lub.sh, LuaEmitter.Expressions.cs, RefTypeAccessTests.cs, doc/lub-gap-analysis.md, doc/support-matrix.md, current/tasks/done
- よかったこと: T128-T131 で入れた機能 (object initializer table / --entry / --prelude / multi-return) が全部そのまま効き、移植中の tcs 側変更は null-forgiving の1点だけだった
- 判断: 原典の Array<Dynamic> は class Brick に型付けした (tcs に Dynamic はなく、型付けの方が TinyC# の狙いに合う)。paddle 衝突の rect は座標引数に展開して Dynamic 相当の使い捨て table を避けた
- 残課題: T133。以降のサンプル移植 (audio / texture 系) や Useful 層追加は需要駆動で判断

### T135: CLI 出力パス衝突による入力破壊を防止 ✓ (2026-07-12)
- input / `--ref` / `--prelude` と output / source map を絶対パスに加えて実ファイルIDでも比較し、衝突時は書き込み前にexit 1とする共通検証を追加した。Windowsはvolume/file ID、Linux/macOSはdevice/inodeを使う
- one-shotの6組合せ (output/map × input/ref/prelude)、dot-segment、OS別case、symlink/hardlink、親directory symlink、watch初回build前の拒否、atomic置換契約を19テストで固定し、内容とmtimeが変わらないことを確認した
- 変更ファイル: Program.cs, FileIdentity.cs, OutputFileWriter.cs, CliRuntimeTests.cs, WatchModeTests.cs, FileLinkTestHelper.cs, README.md, current/tasks/done
- よかったこと: Mainの分岐前とwatchの各rebuildで同じ検証を再利用し、one-shot/watchの両方を同じ契約で保護できた。watchテストも実際のテスト構成のDLLを直接起動するようにした
- 判断: 既存ファイルは実体IDでaliasを拒否し、通常の検証後競合でsymlink/hardlink targetをtruncateしないよう、出力は同一directoryの一時ファイルからatomic置換する二層防御にした。安全な既存出力linkも意図的に通常ファイルへ切り離し、事前write-openで従来の書込権限契約を守り、Unix permission bitsは維持する（ACL/xattr等は引き継がない）。敵対的な親directory差し替えを完全に閉じるhandle-relative I/Oは範囲外
- 残課題: T134 (C# compile error健全化) → T136 (String edge + Lua test timeout)

### T134: C# compile error の一律除外を型安全な判定へ置換 ✓ (2026-07-12)
- `CS0266` / `CS0029` / `CS0019` / `CS0535` のID一律除外を廃止し、diagnostic位置のsymbol/type/syntaxでTinyC#固有ケースだけを許容する`CompilationDiagnosticPolicy`へ置換した
- 通常の型変換・二項演算・未実装method、field facadeの型/可視性/static/readonly/method/event/operator/conversion/継承field、enum↔char、外側enum変換による内側error隠蔽をnegative testにし、enum/intと互換fieldの正例を含む21テストで固定した
- `Program.Main`を直接呼ぶCLI系4クラスはprocess-wide Consoleを奪い合わない非並列collectionへまとめ、追加CLI testが顕在化させた全体testのflakeも解消した
- 変更ファイル: Transpiler.cs, CompilationDiagnosticPolicy.cs, Diagnostic/Enum/Interface/CliRuntime tests, ConsoleCollection.cs, EntryClass/NamingSuppression/Prelude tests, README.md, support-matrix/current/tasks/done
- よかったこと: diagnostic IDではなく最内側の式だけを見ることで、同じIDの通常errorと、外側にenum変換があるnested errorをともにfatalにできた。CLI系40件の反復実行も安定した
- 判断: enum/integerの`CS0019`は`==`/`!=`だけを許容し、Lua string表現の`char`はinteger集合から除外する。interface facadeは全未実装member（static abstract operator/conversionを含む）がpropertyで、同じclassに宣言した同名・同型public instance fieldがある場合だけ許容し、setterにはmutable fieldを要求する
- 残課題: T136 (String edge + Lua process timeout) → T133/T137/T138 (診断契約)

### T136: Lua 実行テストの timeout と String edge contract 修正 ✓ (2026-07-12)
- `RunLua`とLua version取得を共通の有限時間process helperへ寄せ、stdout/stderrの同時非同期drain、timeout時のprocess tree終了、wait/drain、PID・command・両出力・script文脈付き診断を実装した。cleanup中のplatform例外や一部tree kill失敗でも診断まで進む
- 実際の子孫Lua processをspawnするtree kill、timeout直前の両stream回収、pipe buffer超過の正常終了を3テストで固定した。Stringは空oldValueのReplace即時error、空suffixのEndsWith、空/null separatorと引数なしSplitを.NET比較し、allowlist全体の空文字edgeを8テストで棚卸しした
- 変更ファイル: TestProcessRunner.cs, TestHelper.cs, ProcessRunnerTests.cs, StringEdgeContractTests.cs, LuaEmitter.Expressions.cs, runtime/tinysystem.lua, support-matrix/current/tasks/done
- よかったこと: process終了待ちとstream drainを共通化したことで、既存テストの逐次ReadToEnd deadlockと無期限hangを同時に閉じられた。tree killテストはroot PIDだけでなく実descendant PIDの消滅まで確認する
- 判断: `Split()`と明示nullをLuaの可変引数個数で区別し、引数なしだけをwhitespace分割にした。whitespaceは既存のUTF-8 byte列制約に合わせてLua `%s`の範囲とし、Unicode `Char.IsWhiteSpace`との差はsupport matrixへ明記した。root正常終了後にdescendantがpipeを保持する異常系も5秒で診断失敗するが、root消滅後のtree追跡はprocess helperの契約外
- 残課題: T133 → T137 → T138 (診断契約)

### T133: 完全修飾 API アクセスの TCS1002 誤検出修正 ✓ (2026-07-12)
- syntax全走査で`System.Math.Min`の中間`System.Math`をAPI memberとして再判定していたため、別のmember accessのreceiverとなる`INamedTypeSymbol`をqualifierとして除外し、実際の外側member symbolだけを共有allowlistで判定するようにした
- Analyzer、`TranspileWithDiagnostics`、`tcs check`の3経路を4テストで固定した。完全修飾`System.Math.Min`は診断なし、`System.IO.File.ReadAllText`はtype qualifierとの重複なしでTCS1002を1件だけ返す
- 変更ファイル: TinyCsComplianceFacts.cs, TinyCsComplianceAnalyzerTests.cs, DiagnosticTests.cs, CliRuntimeTests.cs, support-matrix/current/tasks/done
- よかったこと: syntax文字列ではなくRoslynの`INamedTypeSymbol`と親子関係でqualifierを識別したため、namespace名や表記揺れに依存せずAnalyzerのoperation単位診断と揃えられた
- 判断: publicなsymbol overloadの契約は変えず、誤検出が起きるsyntax member-access経路だけでtype qualifierを除外した。unsupported側もouter method/property/field/constructor symbolは従来どおり判定する
- 追加発見: `nameof(...)`の警告なし不正Lua出力をT162、`global::`のTCS1001誤警告・重複をT163として起票した
- 残課題: T137 → T162 → T138 → T163 (診断契約)

### T137: partial / lock の Analyzer / check / emitter 診断を統一 ✓ (2026-07-12)
- class/record/interfaceのpartial modifierとLockStatementを共有TCS1001へ追加し、Analyzer、`TranspileWithDiagnostics`、`tcs check`、通常CLI transpileで同じsyntax名・1 node 1診断に統一した。partial struct/record structは既存診断1件のまま重複させない
- emitterはpartial宣言をVisitClass/VisitRecord前に止めてpartial subtreeのunsupported markerを残し、同名Lua tableのlast-write-winsを防止した。lockはunsupported markerの後に`do ... end`でbodyとlocal scopeを保持する（同期は実装しない）
- 複数partial class、partial record/interface、通常型、partial struct系、lock body実行をAnalyzer 2件・transpiler/check/通常CLI 3件の計5テストで固定した。全380 tcs / 13 analyzerテストがgreen
- 変更ファイル: TinyCsComplianceFacts.cs, TinyCsComplianceAnalyzerTests.cs, LuaEmitter.cs, LuaEmitter.Statements.cs, DiagnosticTests.cs, ComplianceParityTests.cs, README.md, support-matrix/current/tasks/done
- よかったこと: shared factsをemitter入口でも使い、診断の正本とwrong-code防止gateを同じ判定にできた。通常CLI出力も実Luaで実行し、lock bodyが消えないことを確認した
- 判断: partialは安全にmergeできる基盤がないため全宣言をemitしない。lockはシングルスレッドbackendに合わせて同期を省略するが、TCS1001で明示し、bodyの副作用とscopeは保持するfallbackにした。analyzer-demo fixtureの期待件数は変えず、専用parity testを恒常dotnet/run-tests gateへ載せた
- 残課題: T162 → T138 → T163 (診断契約)

### T164: browser-wasm compiler bundle (WasmCompiler) ✓ (2026-07-12)
- spike (doc/wasm-playground-spike.md) の変更を本実装として再適用: Transpiler.References 注入口 (byte image 参照) と concurrentBuild:false。ReferenceInjectionTests で契約を固定
- WasmCompiler/ プロジェクト新設 (Microsoft.NET.Sdk.WebAssembly)。Transpiler core ソース直取り込み + TinySystem ProjectReference + ref pack 4 DLL / TinySystem.dll / runtime/tinysystem.lua を EmbeddedResource 化し、JSExport CompilerExports.Compile(requestJson) で公開。JSON は source-generated JsonSerializerContext (wasm は reflection serializer 無効)
- WasmFingerprintAssets=false で安定 URL 配信、InvariantGlobalization + SatelliteResourceLanguages=en で _framework 13MB / gzip 約4.6MB
- 検証: dotnet test 全緑 (381+13)、publish 出力を http 配信し headless chromium で Compile → `return Hello` を確認 (smoke PASS)
- 変更ファイル: Transpiler/Transpiler.cs, Transpiler.Tests/ReferenceInjectionTests.cs, WasmCompiler/*
- よかったこと: spike の再適用手順が書いてあったため本実装が一直線だった
- 判断: WasmCompiler は tcs.slnx に入れない (CI に wasm-tools workload を要求しないため)。ビルドは `dotnet publish WasmCompiler -c Release` を消費側 (lub) が明示的に叩く。TinySystem は DLL 埋め込み (ProjectReference が先にビルドする順序保証を利用)
- 残課題: なし (lub playground 側の worker 統合は lub リポジトリの作業)

### T162: nameof を無警告で不正Luaへ出さない ✓ (2026-07-13)
- Roslynの`INameOfOperation`を共有factsでsemantic判定し、Analyzer、`TranspileWithDiagnostics`、`tcs check`、通常CLI transpileの全経路で`NameOfExpression`のTCS1001へ統一した。同じsyntax形状でも`IInvocationOperation`になる同名ユーザーmethodは通常callのまま扱う
- `nameof` operand内のmember/typeを通常API使用と誤認しないよう、operation/syntax双方のAPI診断を抑制した。`nameof(System.Math.E)`と`nameof(System.DateTime)`でも余分なTCS1002は出ない
- emitterはoperandを評価せず、Roslynのcompile-time定数文字列とunsupported markerを持つvalid Lua式へ置換する。警告なしの生成Luaに未定義`nameof(...)`が残るsilent wrong-codeを解消した
- simple/member/type operandと同名ユーザーmethodをAnalyzer 2件・transpiler/check/通常CLI 5件で固定し、全386 tcs / 15 analyzerテストがgreen
- 変更ファイル: TinyCsComplianceFacts.cs, TinyCsComplianceAnalyzer.cs, TinyCsComplianceAnalyzerTests.cs, Transpiler.cs, LuaEmitter.Expressions.cs, DiagnosticTests.cs, ComplianceParityTests.cs, README.md, support-matrix/current/tasks/done
- よかったこと: token文字列やsymbol lookupではなくRoslyn operationで識別したため、contextual keywordである`nameof`と通常method callを同じ共有契約で安全に分離できた
- 判断: unsupported診断は維持しつつ、通常transpileはRoslynが確定した定数値を安全なfallbackとして出す。API診断抑制はbuilt-in `nameof`の祖先内だけに限定し、同名ユーザーmethodの引数には適用しない
- 残課題: T138 → T163 (診断契約)
### T165: ユーザー定義演算子オーバーロード ✓ (2026-07-13)
- 二項 `+ - * / %` と単項 `-` の operator 宣言を Lua metamethod (`__add`/`__sub`/`__mul`/`__div`/`__mod`/`__unm`) として class table (= instance metatable) へ emit するようにした (LuaEmitter.Operators.cs 新設、class / record class 両対応)
- 同一演算子の複数 overload (Vec2*Vec2 / Vec2*double / double*Vec2 等) は overload ごとの関数 + 単一 metamethod dispatcher に分け、実行時 operand 型 (class は `getmetatable == Class`、数値/文字列/bool は `type()`) で分岐する。どれにも一致しない場合は明示 error
- 未対応の operator 宣言 (`==` `!=` 比較系、checked、変換演算子) は共有 facts の TCS1001 (`OperatorDeclaration(token)` / `ConversionOperatorDeclaration`) にし、Analyzer / `tcs check` / transpiler の三面一致にした
- セマンティックテスト14件 (OperatorOverloadTests: 単一/複数 overload dispatch、単項 -、%、複合代入、record class、statement body、診断3件) + analyzer 2件
- 変更ファイル: Shared/TinyCsComplianceFacts.cs, Transpiler/LuaEmitter.Operators.cs, Transpiler/LuaEmitter.cs, OperatorOverloadTests.cs, TinyCsComplianceAnalyzerTests.cs, objective.md, support-matrix/current/done
- よかったこと: metamethod 対応表を共有 facts (`TryGetOperatorMetamethod`) に置いたことで、emitter の supported 判定と診断の supported 判定が同一ソースになった
- 判断: オーナー決定により従来 Out だった演算子オーバーロードを Core へ方針変更 (lub の Vec2/Vec3/Vec4/Quat/Mat4 移植に必要な算術セットのみ)。`==`/`!=` は record `__eq` と意味論が競合するためスコープ外を維持。dispatch 条件を作れない引数型 (interface / object 等) は `true` 扱いで宣言順 fallback (C# コンパイル時に overload 解決済みのため実行時の誤 dispatch は型エラー相当のみ)
- 残課題: operator `==`/`!=` の class 対応は必要になったら別タスク。metamethod は継承されない (metatable 直付け) 点は現行の浅い継承方針では未対応のまま

### T166: 整数ビット演算子 ✓ (2026-07-13)
- 二項 `& | ^ << >>` と単項 `~` を Lua 5.5 native 演算子 (`& | ~ << >>` / 単項 `~`) へ写像した。C# の `^` は Lua の二項 `~` になる (Lua の `^` は冪乗)
- 複合代入 `&= |= ^= <<= >>=` を既存の展開形 (`x = x op y`) に追加した
- 対象は整数と enum。bool operand の `& | ^` / `&= |= ^=` は TCS1001 未対応警告に留めた (Lua native は boolean を拒否し、and/or 写像は非短絡の C# 意味論を黙って変えるため)
- C# int 32bit と Lua 整数 64bit の幅意味論差 (`~` の上位 bit、負数 `>>` の算術/論理シフト差、シフト量マスク) は support-matrix に明記し、移植側の明示マスク運用とした
- セマンティックテスト12件 (BitwiseOperatorTests: 各演算子、C# 一致の優先順位2件、複合代入、flags enum、mask 条件、bool 診断)
- 変更ファイル: Transpiler/LuaEmitter.Expressions.cs, BitwiseOperatorTests.cs, support-matrix/current/done
- よかったこと: C# と Lua 5.5 でビット演算子の相対優先順位 (shift > & > ^ > |、加算 > shift) が一致しているため、構文木からの素直な再出力で括弧補正が不要だった
- 判断: `>>>` (C# 11 unsigned shift) は 64bit 幅では意味がさらにずれるため未対応のまま。負数 `>>` の意味論補正 (floor div 展開) は行わず、幅差と同じく明示マスク運用に含めた
- 残課題: 32bit wrap 演算が lub 移植で頻出するようであれば、`Bit32` 系 runtime helper の追加を別タスクで検討

### T167: BCL allowlist 追加 (Math.Round/Sign/Tan/Log/Exp, String.IsNullOrEmpty) ✓ (2026-07-13)
- `Math.Round` (引数なし/digits、C# と同じ偶数丸め)、`Math.Sign`、`Math.Tan`、`Math.Log` (自然対数 + base 指定)、`Math.Exp`、`String.IsNullOrEmpty` を runtime/tinysystem.lua + TinySystem C# facade + Shared allowlist の3点セットで追加した
- emitter は `string.IsNullOrEmpty` を `String.IsNullOrEmpty(s)` runtime call へ写像 (既存 `string.Join` 分岐を拡張)。`Math.*` は既存の汎用写像で通る
- セマンティックテスト10件 (BclAllowlistExtensionTests: 偶数丸め5値、digits、Sign 3値、Tan/Log/Exp、IsNullOrEmpty null/空/非空、TCS1002 が消えること、Log10 が引き続き TCS1002 であること、TinySystem facade 同期)
- 既存 negative fixture が unsupported 例に使っていた `Math.Log` は `Math.Cbrt` へ差し替えた (`Math.Truncate` は int 引数で double/decimal overload が曖昧になるため不採用)
- 変更ファイル: runtime/tinysystem.lua, TinySystem/RuntimeFacades.cs, Shared/TinyCsComplianceFacts.cs, Transpiler/LuaEmitter.Expressions.cs, BclAllowlistExtensionTests.cs, DiagnosticTests.cs, TinyCsComplianceAnalyzerTests.cs, CLAUDE.md, support-matrix/current/done
- よかったこと: allowlist / runtime / facade の3点を同一コミットで揃え、facade 経由と System.Math 経由の両方をテストで固定できた
- 判断: Round は C# 既定の MidpointRounding.ToEven を Lua で実装した (Haxe Math.round の half-up とは異なるが、C# 意味論が正)。Log10/Log2 は `Log(x, base)` で代替できるため追加しない。Math.E は今回のスコープ外 (必要になったら定数1行)
- 残課題: allowlist は member 名単位のため、未実装 overload の検出は T138 (完全シグネチャ単位) に委ねる

### T168: Dictionary<string, object> の wire format 保証 ✓ (2026-07-13)
- 現状確認: tinysystem の Dictionary 表現は最初から素の Lua hash table (`{[k]=v}`) で、metatable も bookkeeping フィールドも持たない。`Count`/`Keys`/`Values` は pairs 走査の都度計算 (`Dict.Count` 等)。表現の変更は不要で、契約をセマンティックテストで固定した
- 契約: (1) `Dictionary<string, object>` のヘテロ値 (double / string / bool / `--ref` ハンドル / ネスト Dictionary / List 混在) が保持される、(2) `--ref` 関数へ渡すと host は文字列キー→値のみの素の table を受け取る (`getmetatable == nil`、非文字列キーなし、エントリ数一致、ハンドルは `rawequal` で同一)
- テスト4件 (DictionaryWireFormatTests): --ref inspect によるヘテロ初期化子 / Add+indexer 構築の素 table 検証、Count の都度計算 (Add/Remove 混在)、foreach がユーザーエントリのみ見えること
- 変更ファイル: DictionaryWireFormatTests.cs, support-matrix (§18 wire format 契約), current/done
- よかったこと: 既存表現が既に契約を満たしていたため、実装変更ゼロでテストと文書の固定だけで済んだ。既存 Dictionary テストへの影響なし
- 判断: lub の `--ref` 関数 (C runtime が table を読む) との契約を tcs 側のテストで保証する形にした。Count を table 内に持つ最適化は今後も不採用 (wire format が壊れるため、この契約テストが番人になる)
- 残課題: duplicate key の Add 意味論は T153、Clear の allowlist 追加は需要駆動

### T169: `(` 開始文の Lua 結合パースを `;` 前置で分離 ✓ (2026-07-13)
- IIFE 文 (`List.Clear`、conditional access 文など) の直前の文が callable 終端 (`local b = t[k]` / `f(x)`) の場合、Lua が `t[k](function()...)()` と一続きにパースして実行時エラーになる問題を修正した (lub 側 SpriteBatch.begin() で必発)
- Lua の定石どおり、`(` で始まる行の emit に `;` を前置する。全 statement は `AppendLine` を通るため、そこで一元的に付与し、lambda block 内の文も同じ経路でカバーされる
- セマンティックテスト4件 (StatementSeparatorTests: indexer local 直後の List.Clear、conditional access 文、call 文直後の Clear、lambda block 内の conditional access 文)
- 変更ファイル: Transpiler/LuaEmitter.cs, StatementSeparatorTests.cs, doc (current/done)
- よかったこと: 出力経路が AppendLine に集約されていたため、1箇所の前置で全 statement emit 経路 (member/statement/lambda block) を一貫して直せた
- 判断: 「前の文が callable 終端かどうか」の文脈判定はせず、無条件に `;` を前置した (Lua 5.5 は空文 `;` を許すため常に安全で、判定漏れが起きない)
- 残課題: なし

### T170: ユーザー定義メソッドの out/ref パラメータを診断化 ✓ (2026-07-13)
- out 多値戻りは --ref 型メソッド専用だが、ユーザー定義メソッドに out/ref を書いても check が素通りし、値渡しのままの silent wrong-code な Lua になっていた (out 代入が呼び出し元へ返らない)
- Shared/TinyCsComplianceFacts.cs の `TryGetUnsupportedSyntax` に `ParameterSyntax` の out/ref modifier 判定を追加し、TCS1001 (`OutParameter` / `RefParameter`) として analyzer / `tcs check` / transpiler warning の三面一致で診断するようにした (`UnsupportedSyntaxKinds` に `SyntaxKind.Parameter` を追加)
- `--ref` source の宣言は check/transpiler では解析対象外 (refTrees を Analyze しない) ため、host stub の out multi-return は従来どおり無警告で使える。Rider (analyzer) では stub ファイル自体に警告が出るため、stub 側は `.editorconfig` の per-file severity 抑制か auto-generated マーカーで抑止する運用
- テスト4件 (MultiReturnTests: out 宣言 TCS1001 / ref 宣言 TCS1001 / --ref stub 宣言は無警告、analyzer: out+ref で2件)
- 変更ファイル: Shared/TinyCsComplianceFacts.cs, MultiReturnTests.cs, TinyCsComplianceAnalyzerTests.cs, doc (support-matrix/current/done)
- よかったこと: 宣言サイト (ParameterSyntax) だけで判定できたため、--ref 判定を持たない analyzer 面と意味がずれない
- 判断: 呼び出しサイトでなく宣言サイトで診断した (--ref 概念のない analyzer と三面一致にでき、check 面は refTrees 非解析で stub を自然に除外できる)。`in` / `ref readonly` は値意味論の差が小さくスコープ外 (`ref readonly` は ref token を含むため RefParameter に含まれる)
- 残課題: emitter は警告付きで従来の値渡し Lua を出す (check exit 1 が契約)。完全シグネチャ単位の API 診断は T138

### T171: 不正 Lua を生む識別子の診断化 (Lua 予約語 + verbatim @) ✓ (2026-07-13)
- Lua 5.5 予約語 (`end` `repeat` `until` `global` 等 23語、deps/lua llex.c luaX_tokens 準拠) と同名の宣言識別子を TCS1001 (`LuaKeywordIdentifier(name)`) で診断するようにした。従来は check 素通りで `function C:end()` / `local repeat` のような syntax error Lua を silent に生成していた
- 対象宣言: 型 (class/record/enum/interface)、メソッド、プロパティ、enum メンバー、field/local (VariableDeclarator)、パラメータ、foreach 変数、pattern/out var designation。Shared facts 起点で analyzer / `tcs check` / transpiler warning の三面一致 (`UnsupportedSyntaxKinds` に対応 SyntaxKind を追加)
- verbatim 識別子 (`@float` `@out` 等) は emitter 全体を `Identifier.Text` → `Identifier.ValueText` に統一して `@` なしで emit するようにした (従来は `self.@float` 等の不正 Lua)。`@end` は ValueText 化で Lua 予約語診断に自然に合流する
- テスト: transpiler 12件 (LuaIdentifierTests: 各宣言種の予約語診断、verbatim 予約語、negative、verbatim の @ なし emit + C# 意味論のセマンティック実行) + analyzer 2件
- 変更ファイル: Shared/TinyCsComplianceFacts.cs, Transpiler/LuaEmitter{,.Expressions,.Statements,.Operators}.cs, LuaIdentifierTests.cs, TinyCsComplianceAnalyzerTests.cs, doc (support-matrix §11/tasks T151/current/done)
- よかったこと: ValueText 統一により verbatim 対応と予約語判定 (ValueText 比較) が同じ土台になり、`@end` のような合成ケースが追加コードなしで正しく診断された
- 判断: 自動リネームはせず「診断して拒否」(T151 の中央マングリング導入時にリネームへ昇格可能な形)。予約語セットに Lua 5.5 新予約語 `global` を含めた。usage サイト (--ref stub の予約語メンバー等) は宣言サイト診断の対象外で、実需要が出たら T151 のスコープ
- 残課題: emitter は警告付きで不正 Lua を出しうる (check exit 1 が契約)。型syntax の raw 出力箇所 (`getmetatable(x) == {dp.Type}` の `@Type`) は T151 で symbol 名へ統一する

### T172: [M0] player 側 apply baseline bench ✓ (2026-07-14)
- `bench/player-apply.lua` を追加 (deps/lua/lua 5.5 単体で再現実行可)。系列: 合成 bundle の load (12/156/471KB)、lume.hotswap update 走査 (type table 直 return / old==new fast-path / thin wrapper、live static list 1e4/1e5/2e5 entry)
- 実測 (native Lua 5.5.1, p50): load 156KB=5.0ms / 471KB=16.7ms。walk-type-table 1e5=106ms / 2e5=224ms。walk-fastpath / walk-wrapper は 1〜4µs
- design doc §15 の参考実測を bench 由来の数値で更新
- 変更ファイル: bench/player-apply.lua (新規), doc/incremental-module-compilation-design.md
- よかったこと: 設計レビューで定性的だった blocker (lume の live graph 全走査) を、budget 比 3 倍超という数字で確定できた。wrapper/fast-path の効果も同一 harness で対比でき、§14.2 の対策が必須である根拠が再現可能になった
- 判断: lume 実装は計測用に bench 内へ忠実複製 (MIT, rxi/lume)。lub の vendored lume に依存すると readonly 制約と壊れやすい相対 path を持ち込むため。合成 bundle は emitter 出力風 (class table = instance metatable) に揃え、実生成物への依存を切った
- 残課題: browser (WASM) での同系列実測は T173 の Chrome harness に載せて係数を確定する

### T173: [M0] Chrome benchmark harness ✓ (2026-07-14)
- `bench/chrome-compile.mjs` を追加。lub playground (readonly) を headless Chromium で駆動し、C# 経路の cold (page load → running) と warm edit→compiled (warm-up 5 + n=30、p50/p95/max) を計測する。playwright は ../lub/web/node_modules から解決し追加 install 不要
- 実測 (dev server + Release publish bundle、01_triangle): cold 11.3s、warm p50 5.58s / p95 6.23s — design doc §2 の少数回観測 (11.38s / 5.6-7.0s) と整合。§2 に再現 baseline として記録
- 変更ファイル: bench/chrome-compile.mjs (新規), doc/incremental-module-compilation-design.md
- よかったこと: status 遷移の観測に MutationObserver 履歴を使うことで、同期 compile 中に rAF が止まり waitForFunction が "compiling…" を取りこぼす問題 (main-thread jank が計測系にも効く実例) を回避できた。初版の「前 run の synced が残って即時成立する」誤計測 (p50 31ms) を、doc 記載値との突き合わせで検出できた — baseline に外部照合値があることの価値
- 判断: 終点は compile 完了 (現行 protocol に commit ACK がないため Lua commit は測れない。§13.1 導入後に切り替え)。dev server 経由だが WasmCompiler bundle は Release publish 物で、観測値も既存 Release 計測と一致するため baseline として採用。usedJSHeapSize は page 側 heap のみで .NET wasm heap を含まない
- 残課題: managed heap 計測は T175 の phase timing (managedHeapBytes) で拾う。memory soak (1,000 edit) は同 harness の --runs 拡大で実行可能だが、正式には T175 gate 計測と同時に採る

### T174: [M0] incremental/full diagnostics differential test 雛形 ✓ (2026-07-14)
- `Transpiler.Tests/IncrementalDifferentialTests.cs` を追加。canonical key (id, severity, path, span, message) への正規化 (Roslyn 標準形式パース + パース不能行の raw fallback)、順序非依存比較、missing/extra を出す readable diff を実装
- 現段階は full vs full の恒等比較 (clean / warning / error+TCS1001 混在) で契約を固定。T175 で Left 側を IncrementalCompilationSession の増分結果へ差し替える
- 変更ファイル: Transpiler.Tests/IncrementalDifferentialTests.cs (新規)
- よかったこと: 診断が整形済み文字列で返る現行 API のまま canonical 化を先に固めたので、session 実装時に wire format を変えずに parity gate を挿せる
- 判断: harness は test project 内に置いた (production 側に consumer がまだ無く、dead code を作らないため。T175 で必要になれば昇格)
- 残課題: T175 で Left=session 差し替え + body-edit/surface-change の実 differential 系列を追加

### T175: [M1] IncrementalCompilationSession ✓ (2026-07-14)
- `Transpiler/IncrementalCompilationSession.cs` を追加。常駐 Roslyn session (fixed/ref tree cache、`WithChangedText`/`ReplaceSyntaxTree`)、body-only fast path (変更 span の body 内包含 + body 除去 surface hash の二重判定、error 状態からの復帰と非 body 編集は無条件 slow path)、error head / last-good artifact の分離と dirty closure、per-module emit
- fast path の semantic 診断を変更 body span 限定の `GetDiagnostics(span)` に (161→21ms)。emit は変更 method のみ emit して cache へ splice (`LuaEmitter.MethodRanges`/`EmitSingleMethod`、231→1ms)。constructor/accessor/record/警告持ちは file 全体 emit へ fallback
- `WasmCompiler` に M1 gate 計測用の最小 `SessionExports.Open/Update` JSExport を追加 (production wire は M4)
- `bench/chrome-session.mjs` で M1 gate PASS: 実サンプル級 file (11.5KB/61 methods) warm body-edit p50 105.8ms / p95 124.2ms (gate 275ms)、tree count assertion 全 run 通過。現行 full path warm 5.58s 比 53 倍
- 変更ファイル: Transpiler/IncrementalCompilationSession.cs (新規), Transpiler/LuaEmitter.cs, WasmCompiler/Program.cs, Transpiler.Tests/IncrementalSessionTests.cs (新規), Transpiler.Tests/IncrementalDifferentialTests.cs 連携, bench/chrome-session.mjs (新規), doc 更新
- よかったこと: gate を probe 極小 file だけで測らず実サンプル級 file を足したことで、「file 全体 emit が budget を壊す」を実装前でなく計測で発見し、method splice まで M1 内で消化できた。splice は full emit との byte 一致をテストで固定した
- 判断: 診断 parity のため compliance/naming は tree 全体 walk を維持 (23ms、他 member の警告行番号がずれるため span 限定にしない)。splice の continue ラベル採番は file 内通番と不一致だが Lua のラベルは関数スコープで実行意味同一と判断し、byte 決定性は「同一 session 経路内」で保証
- 残課題: SourceMap は増分 artifact に未対応 (M3 §12)。SurfaceHash/JSON 往復の ~60ms は M4 の wire 設計で削る。sample/言語切替 soak と 1000 edit soak は M4 の E2E gate と同時に採る

### T176: [M2] descriptor artifact / registry vertical slice ✓ (2026-07-14)
- `Transpiler/ModuleArtifacts.cs` を追加: type 単位 metadata (`EmittedTypeInfo`: 宣言行/static 初期化行の範囲、owned definition keys、instance shape、static field の pure 判定 + initializer hash)、define/initializer chunk の切り出し (`ModuleArtifactText`)、bridge snapshot linker (`ModuleLinker.LinkSnapshot`)
- `LuaEmitter` が emit しながら type section を記録 (class/record/enum/operator/accessor)。fast path の method splice は記録範囲も同 delta でシフト
- `runtime/module_registry.lua` を追加: stable type table の declare (pre-zero 込み) → define → initialize 三段階 apply、read-only module `_ENV` (alias → host 順、未宣言 write は error)、hash unchanged module の skip、owned key 削除、thin entry wrapper (identity 維持)、stale revision skip
- snapshot は idempotent bootstrap (`_G.__tcs_module_runtime` ABI guard) + `applyBatch` + `return wrapper`。fresh VM でも hot reload でも同一ファイルで成立する full active snapshot (§14.2 bridge)
- 検証: `dotnet test` 467/467 (新規 ModuleDescriptorTests 7: define/initializer 分割、逆順継承の fresh 実行、hot apply の identity 維持 + method swap + static 保持、unchanged skip + 削除 key + 新規 pure static、pre-zero の cross-type 先読み、read-only env、stale skip)
- 判断: descriptor は文字列 chunk でなく `function(_ENV)` literal で埋め込み (snapshot 全体を 1 parse、load() 不要)。declare は関数でなく metadata 駆動 (pre-zero policy を registry 側に集約)。owned key は shadow-table 検出でなく emitter 記録 (spike で実証済みの直接 apply を維持)
- 残課題: fresh initialize は per-type topo order でなく module/emit 順 (現 workload と同等。topo は namespaced 入力対応時)。transaction/rollback/ACK/restart 分類は T177。compilerAbi/referenceAbiHash の wire 化は T178

### T177: [M3] transaction / full snapshot compatibility ✓ (2026-07-14)
- `runtime/module_registry.lua` に atomic transaction を実装: mutation 前検証 (module 削除/alias collision) → 影響 key (新旧 definition/static key の和集合) + metatable + alias を rawget/rawset + presence sentinel で transaction log へ保存 → declare/define/initialize を pcall → 失敗時は完全 rollback して error 再送出 (lume.hotswap が old module を維持)。inherited key を own key として復元しない (§18.2 failed override-add 検証済み)
- commit ACK (§13.1): `@@tcs_commit {"revision":N,"ok":bool,"commitTimeMs":ms,"error"?}` を host print relay へ出す (batch.ack 時のみ、成功/失敗の両方。commit 後に失敗し得る処理なし)。`ModuleLinker.LinkSnapshot(emitAck)` で bridge snapshot に組み込む
- restart classification (§11.3): `SessionUpdateResult.RequiresRestart/RestartReasons`。slow path で新旧 artifact metadata を diff — type 削除 / kind / base / instance shape / 既存 static initializer 変更 / 副作用ありの新規 static → restart。body edit / member 追加削除 / 純粋な新規 static / enum member 変更は live-safe
- lume.hotswap E2E (lume 2.3.0 hotswap を忠実再現した harness): wrapper/type table identity 維持、body edit 反映、200k entry の runtime live state が走査されず保持、失敗 reload で last-good 維持 (nil+err、revision 不変)
- 検証: `dotnet test` 476/476 (新規 ModuleTransactionTests 9)
- 判断: per-module source map / revisioned chunk name は見送り (lub に consumer が無い §12。bridge は単一 file で従来と同粒度)。lume E2E は lub の実 lume を直接参照せず verbatim 再現 harness で固定 (repo 境界を越える依存を作らない。実 lume は lub 側 verify で通す)
- 残課題: fixed implementation の build-time pre-emit と prebuilt assets は T178。T154 の任意 global graph rollback は別 gate (design doc §11.4 注記どおり)

### T178: [M4] Wasm delta API と playground bridge ✓ warm gate (2026-07-14)
- `WasmCompiler` SessionExports を production wire 化: `Open`(projectEpoch 発行、entryClass 保持)/`Update(epoch, path, content)`(requiresRestart + restartReasons + revision + phase timing)/`LinkSnapshot(epoch)`(bridge snapshot を raw Lua で返す。JSON escape 回避)。module_registry.lua を埋め込み resource 化。registry は同一 revision 再適用に ACK を返し直す (host の再送 retry が完結する)
- lub 側 (直接実装。分担廃止に伴い feature request は出さない): `tcs-compiler.ts` に `openTcsSession`(Open/Update/LinkSnapshot client)、`main.ts` を ACK 駆動へ (C# は 75ms debounce → 変更 .cs だけ Update → LinkSnapshot → entry 書き込み → `@@tcs_commit` ACK で synced 表示、1.5s×3 の ACK timeout 再送、requiresRestart は fresh player 起動)、`third_party/lume` hotswap に `old == new` fast-path、`verify-headless.mjs` に A6 (実 C# edit → commit ACK 貫通判定)、submodule tcs 更新 + wasm assets 再生成
- **E2E gate PASS**: `bench/chrome-e2e-ack.mjs`(playground 実機 17_flappy C# body edit、headless chromium + swiftshader)で edit-stop → commit ACK p50 422 ms / p95 442 ms / max 442 ms (12 runs、gate 500 ms)。従来の同経路 (T173 baseline) は compile だけで warm 5.58 s
- 検証: dotnet test 476/476、lub 側 tsc/prettier、E2E bench 上記、headless verify (A1-A6) は lub 側で実行
- 判断: bridge snapshot は毎 edit 全文再送 (§14.2 bridge のまま)。mtime 取りこぼしは ACK timeout 再送 + 同 revision 再 ACK で回収。二相 handoff は未実装のため restart 分類の編集は既存 restart() (compile 成功後の runtime error で旧 player を失い得るが、snapshot は last-good compile なので次 edit で復旧可能)
- 残課題: prebuilt assets + background prewarm (cold start 11.7s > 従来 5.6s の解消)、ABI-mismatch fallback、二相 handoff + runtimeReady、TextChange span、compiler ready 前 edit queue、soak (1000 edit / sample 切替 heap)。§17 M4 残項目として記載

### T178 追補: cold path 完遂 (prebuilt / runtimeReady / 二相 handoff) ✓ (2026-07-14)
- CLI `--snapshot` を追加 (`--entry` 必須、bridge snapshot を出力。module ID = 入力 path。playground の in-browser session と ID を一致させる相対 path 契約は lub 側 gen-tcs-prebuilt が担う)
- lub 側: `gen-tcs-prebuilt.mjs`(cs-lib + 各 C# サンプルを staging して CLI で snapshot 化、web/tcs-prebuilt/ へ)、prebuilt boot(cold start 11.7s → **0.5s**、status running まで)、background session warm + warming 中 edit の queue/flush、player の runtimeReady 分離(FS ready まで syncFiles を queue)、requiresRestart 編集の二相 handoff(hidden player boot → 初回 commit ACK → swap、失敗時旧 player 維持。実測 7.4s)、deploy workflow に prebuilt 生成を追加
- 設計簡略化: prebuilt は boot 加速専用(session への artifact 再利用なし)。ABI-mismatch fallback は不要化、TextChange span も見送り(design doc §17 M4 に記録)
- 検証: cold E2E(prebuilt boot 0.5s → warming 中 edit queue → ready 後 flush synced 416ms → static initializer 編集の二相 handoff 7.4s → handoff 後 warm edit 332ms)、soak PASS(chrome-soak.mjs: 1000 edit で JS heap 16→13.4MB と増加なし、20 reopen で 15.4MB plateau。§18.3)

### T179: [M5] optional optimization ✓ 全項目見送り (2026-07-14)
- profile 判断で全項目不着手 (design doc §17 M5 に内訳)。warm E2E p95 442ms < gate 500ms、managed compile p50 45ms < 予算 275ms。direct apply / candidate-aware invalidation / Worker / AOT A/B のいずれも gate 充足に不要

### HotReload runtime の粉砕 (T154 クローズ) ✓ (2026-07-17)
- 残タスク棚卸しで consumer 不在を確認し、runtime の legacy HotReload module (`swap`/`watch`/`update`/`mtime`) を削除。prelude / bridge snapshot の `_G.HotReload` alias、HotReloadTests (9件) も削除
- 変更ファイル: runtime/tinysystem.lua, Transpiler/LuaRuntime.cs, Transpiler/ModuleArtifacts.cs, Transpiler.Tests/HotReloadTests.cs (削除), README.md, q.md, doc/support-matrix.md, doc/current.md, doc/tasks.md (T154 削除), doc/incremental-module-compilation-design.md
- 検証: 削除前に全 grep で利用箇所を棚卸し (samples 参照ゼロ / lub は lume.hotswap / browser playground は module_registry)。`dotnet test` 470/470 + analyzer 20/20 PASS
- よかったこと: 「rollback を作り込む (T154)」ではなく「使われていない機構ごと消す」で backlog を1件無効化できた
- 判断: swap 単体でなく module 全体を削除 (watch/update/mtime は swap 専用の付属機構)。hot reload の正本は module_registry transaction (T177) と host 側 lume.hotswap に一本化
- 残課題: なし

### 残タスク棚卸し (T163 削除 / T151・T153 スコープ縮小) ✓ (2026-07-17)
- 棚卸し基準は「check が通った C# は C# どおりに動く」への直結度と日常コードの遭遇頻度。T138-T153 の silent wrong-code 系は全て維持
- 変更ファイル: doc/tasks.md, doc/current.md
- 判断:
  - T163 削除 — `global::` の生成 Lua は正しく警告が不正確なだけ (wrong-code ではない)。ゲームスクリプトで `global::` を書く実需要なし
  - T151 縮小 — 「予約語をリネームで通す」中央マングリングは見送り (T171 の診断拒否が DSL 契約として明瞭)。残スコープは generated temp/label の衝突安全 (T139 の前提) と `@Type` raw 出力の symbol 名統一
  - T153 縮小 — duplicate key throw 契約は見送り (不正なプログラムの挙動差で、正しいプログラムを壊さない)。ToDictionary selector の一回評価だけ残し T139 依存へ
  - 完了済み増分 module compilation track (T172-T179) のエントリを tasks.md から削除 (done.md が正本)
- 残課題: なし。次の着手は T138 → T151 縮小版 → T139

### T156: stdout sourcemap に runtime/prelude offset を反映 ✓ (2026-07-17)
- `-o` なしの `--sourcemap` (stderr JSON) だけ `ToJson()` を offset なしで呼んでいた stdout 分岐を `ToJson(sourceMapLineOffset)` に修正
- 変更ファイル: Transpiler/Program.cs, Transpiler.Tests/CliRuntimeTests.cs
- 検証: runtime のみ / runtime+prelude / prelude のみ (--no-runtime) の3構成で、stdout Lua の実行文行と stderr JSON の mapping key が一致することを Red → Green で固定
- 判断: なし (file 出力分岐と同じ offset を渡すだけの一行修正)
- 残課題: なし

### T138: supported API allowlist を完全シグネチャ単位へ変更 ✓ (2026-07-17)
- method 名単位だった許可判定を `OriginalDefinition` の signature display string (custom SymbolDisplayFormat: 型完全修飾 + generic type parameter + ref/out/params) の HashSet 照合へ変更。ReducedFrom で拡張メソッドを unreduced 定義に正規化
- optional / params 引数を runtime が実装しない overload (`Split(string, StringSplitOptions)` 等) は `MaxExplicitArguments` で明示引数数に上限を設け、`Split(",")` は許可・`Split(",", StringSplitOptions.None)` は TCS1002 に分離。明示引数数は operation ではなく呼び出し syntax から数え、Analyzer と transpiler/check の判定正本を一致させた
- property/field は family 別の小さな name set (string.Length / List.Count / Dict.Count/Keys/Values / Math.PI) に分離し、List/Dict の indexer を明示許可
- 検出できるようになった負例: indexed `Select((x,i)=>...)`、comparer 付き OrderBy/Sort/ToDictionary、`StringComparison` 付き Contains/StartsWith (従来は引数を黙って捨てていた)、`Contains(char)`、capacity constructor、`Dictionary.Remove(k, out v)`、`FirstOrDefault(defaultValue)`、`Round(x, MidpointRounding)`、char separator の Split/Join
- 変更ファイル: Shared/TinyCsComplianceFacts.cs, TinyCs.Analyzers/TinyCsComplianceAnalyzer.cs, Transpiler.Tests/ApiSignatureComplianceTests.cs (新規16負例+11正例), TinyCs.Analyzers.Tests/TinyCsComplianceAnalyzerTests.cs (同一 matrix の parity), README/support-matrix/current/tasks/done
- 検証: dotnet test 500/500 + analyzer 47/47、負例 probe の `tcs check` exit 1 を実機確認。allowlist の正解データは probe compilation から display string を機械採取 (.NET 10 で `Split()`/`Join(sep, params)` が `params ReadOnlySpan<T>` overload に解決される事実を発見、推測記述を排除)
- よかったこと: 実装前に全テスト/サンプルの API 使用 overload を監査し「テスト証拠 + runtime 実装」の交差だけを allowlist 化した。signature 文字列は手書きせず採取したので typo リスクなし
- 判断: char overload (`Contains('a')` 等) は emitter が char を Lua string 化するため動く可能性はあるが、テスト証拠がないため許可しない (需要が出たら test とセットで追加)。診断メッセージは従来の `{Type}.{Member}` 形式を維持 (overload 情報は含めない。既存 fixture/parity の期待を変えない)
- 残課題: なし

### T151: 予約識別子診断と generated temp の衝突安全 ✓ (2026-07-17)
- `self` (Lua method receiver) と `__tcs_` prefix (generated temp) の宣言識別子を TCS1001 `ReservedIdentifier(name)` で拒否 (T171 の LuaKeywordIdentifier と同じ宣言サイト網羅・Analyzer/check/transpiler 共有)
- generated temp を `__tcs_` prefix へ統一 (`__init`→`__tcs_init`, `__ret`→`__tcs_ret`)。prefix 予約により temp とユーザー symbol の衝突を構造的に排除 (`var __init = 5; new T { X = __init }` が正しく動くことをテストで固定)。`_continue_N` label は Lua の label 名前空間が変数と別で衝突しないことをテストで固定
- pattern 経路の型参照 4 箇所 (`getmetatable(x) == {Type}` raw emit) を ValueText ベースの `FormatTypeReference` へ統一し、verbatim 型名 (`@float`) が pattern でも valid Lua になるよう修正
- 追加発見: 型名だけの switch arm (`Circle => 1`) は syntax 上 ConstantPattern になり値比較 (`s == Circle`) を emit していた silent wrong-code を修正 — semantic model で型と判れば metatable 比較にする (verbatim 無関係の実バグ)
- 変更ファイル: Shared/TinyCsComplianceFacts.cs, Transpiler/LuaEmitter.Expressions.cs, Transpiler.Tests/LuaIdentifierTests.cs, Transpiler.Tests/SwitchTests.cs, doc 同期
- 検証: dotnet test 508/508 + analyzer 47/47
- 判断: fresh name 生成器 (リネーム側) ではなく予約 prefix + 診断拒否を採用 — T171 と同じ「拒否で通す」契約で、emitter に scope 解決の複雑さを持ち込まない。`self`/`__tcs_*` の field/method 名は table key で安全だが、契約を一文に保つため宣言サイト一律拒否にした
- 残課題: designation なしの binary `is Type` (BinaryExpressionSyntax IsExpression) は従来どおり TCS1001 警告の未対応 (silent ではないので需要が出たら対応)。`--entry` の emitted name API は T155 のスコープ

### T139: `?.` receiver の一回評価 lowering ✓ (2026-07-17)
- `VisitConditionalAccess` が receiver 式文字列を nil 判定と本体の両方へ複製していた多重評価を、IIFE local `__tcs_ca` への一回保存に変更。when-not-null 側の引数/index は if 分岐内でのみ評価される (null 時未評価)
- 追加発見と修正: チェーン `a?.B?.C` は内側 `?.` が未宣言 global `__tcs_ca` を参照して実行時エラーだった (`attempt to index a nil value`)。ネスト用の `VisitNestedConditionalAccess` を追加し、内側 receiver を外側 temp 上で評価してから内側 IIFE local で shadow する構造にした (Lua の local RHS は宣言前に評価されるため shadow が正しく機能する)
- 変更ファイル: Transpiler/LuaEmitter.Expressions.cs, Transpiler.Tests/NullConditionalTests.cs (+5: receiver 一回 / null 時引数未評価 / element access の receiver 一回 + index 遅延 / チェーン / メソッドチェーン)
- 検証: dotnet test 513/513
- 判断: 汎用の setup-statements + fresh temp counter 基盤は導入しなかった — `?.` は固定名 `__tcs_ca` + IIFE shadow で正しさが完結し、T151 の `__tcs_` prefix 予約が衝突を構造的に排除する。counter が必要になるのは statement 文脈 (T140 switch 等) で、その時点で消費者と一緒に導入する
- 副産物: T180 を起票 — `v is int inner` が `getmetatable(v) == int` (未定義 global) になり nil が値型パターンにマッチする silent wrong-code を発見 (テスト作成中に検出)
- 残課題: T180 (値型/string 型パターン)。conditional TryGetValue の default 値差異は T152 スコープ

### T140: switch 対象式の一回評価とパターンラベル対応 ✓ (2026-07-17)
- switch expression / statement の対象式を local `__tcs_sw` へ一度だけ保存し、全 case/arm/when 条件で再利用 (従来は case ごとに式文字列を複製して再評価)
- 追加発見と修正: switch statement のパターンラベルは `CaseSwitchLabelSyntax` の OfType filter で黙って落とされ、`case > 5:` / `case 1 or 2:` が空条件の `if then` (不正 Lua) を無診断で出力していた。`CasePatternSwitchLabelSyntax` を VisitPattern + when 条件で対応し、declaration pattern の designation は is-pattern と同じく chain 前に `local c = __tcs_sw` で束縛
- `case Circle:` (bare 型) は旧構文の定数ラベルとして parse されるため、semantic 判定で metatable 比較へ (T151 の switch expression 側と同じ扱い)
- 変更ファイル: Transpiler/LuaEmitter.Expressions.cs, Transpiler/LuaEmitter.Statements.cs, Transpiler.Tests/SwitchTests.cs (+4)
- 検証: dotnet test 517/517
- 判断: temp は固定名 `__tcs_sw` (ネストは Lua の local shadow で正しく分離、`__tcs_` prefix は T151 で予約済み)。連続する switch statement の同名 local 再宣言は Lua で合法
- 残課題: is-pattern (`if (Next() is Circle c)`) の receiver も binding と条件で二重評価している — T141 以降の一回評価ファミリーで扱う

### T141: deconstruction RHS の一回評価 ✓ (2026-07-17)
- `var (x, y) = Make()` が RHS を要素数分複製して多重評価していた問題を、`local __tcs_dec` への一回保存に変更 (宣言/代入/discard 共通の `EmitDeconstruction` 経路)
- 既存変数への分解代入 `(a, b) = Make()` (TupleExpression 左辺) を新規対応 — 従来は TCS1001 warning で代入自体が消えていた。混在形 `(var a, b)` は明示 TCS1001 のまま
- 変更ファイル: Transpiler/LuaEmitter.Statements.cs, Transpiler.Tests/Phase14to19Tests.cs (+3)
- 検証: dotnet test 520/520
- 判断: temp は固定名 `__tcs_dec` (T151 の prefix 予約 + ブロック内 local 再宣言は Lua 合法)。ValueTuple swap `(a,b)=(b,a)` は RHS の TupleExpression が従来どおり TCS1001 (tuple 型自体が未対応)
- 残課題: なし

### T142: with 式 receiver の一回評価 ✓ (2026-07-17)
- with 式の copy 元式が shallow copy の table 走査と metatable 取得の2箇所に複製されていた多重評価を、IIFE local `__tcs_src` への一回保存に変更
- 変更ファイル: Transpiler/LuaEmitter.Expressions.cs, Transpiler.Tests/Phase14to19Tests.cs (+2: 副作用 receiver 一回 / copy の非破壊性と metatable 保持)
- 検証: dotnet test 522/522
- 判断: override の適用順は既存実装が構文順で C# と一致済み (テストで担保)
- 残課題: なし

### T143: 副作用付き lvalue の一回評価と compound semantics ✓ (2026-07-17)
- compound assignment / `??=` / increment / `List.Clear` で receiver・index の式文字列を複製していた多重評価を修正。lvalue の receiver/index に副作用があり得るとき (syntax 判定: invocation / object creation / conditional access / assignment / with / increment を含む) だけ `__tcs_obj` / `__tcs_idx` temp へ下げ、pure な lvalue は従来の出力を維持 (既存テスト・生成 Lua 無変化)
- string `+=` が Lua `+` を emit して実行時エラーになっていた問題を `..` へ修正 (`s += 1` も C# と同じ "s1" 連結になる)。評価順は C# の receiver → index → read → rhs → write に一致
- 変更ファイル: Transpiler/LuaEmitter.Expressions.cs (VisitAssignment 再構成 + CompoundOperator / TryLowerLvalue / HasSideEffectSyntax), Transpiler/LuaEmitter.Statements.cs (increment / `??=` statement 経路), Transpiler.Tests/LvalueEvaluationTests.cs (新規6)
- 検証: dotnet test 528/528
- 判断: 全 lvalue を一律 temp 化せず「副作用の可能性がある場合のみ」に限定 — pure 経路の生成 Lua を変えないことで可読性と既存挙動を守る。bool の &=/|=/^= は従来どおり TCS1001
- 残課題: expression 文脈の compound assignment (`x = (y += 1)`) は IIFE 化済みだが C# の値返し意味論の網羅テストは未整備 (需要が出たら追加)

### T144: simple for 最適化の動的条件セマンティクス修正 ✓ (2026-07-17)
- Lua numeric for は limit を一度しか評価しないため、`i < list.Count` / `i < Limit()` / body で bound や loop 変数を書き換える for が C# (毎 iteration 再評価) とずれていた。numeric for への最適化を「bound がリテラル、または関数スコープ内で再代入されない local/param」かつ「loop 変数が body 内で未書き換え」に限定し、それ以外を既存の while lowering へ fallback
- 変更ファイル: Transpiler/LuaEmitter.Statements.cs (IsLoopInvariantBound / IsAssignedWithin), Transpiler.Tests/ForLoopTests.cs (+5)
- 検証: dotnet test 533/533
- 判断: 不変判定は過剰側に倒した (loop 後の再代入でも fallback) — while fallback は常に意味論的に正しく、numeric for は最適化にすぎない。field/property bound は body 内呼び出し経由の変更を追えないため一律 fallback
- 残課題: loop 前に定義した lambda 経由の local 変更は Roslyn dataflow を使えば precision を上げられるが、syntax 走査 (lambda 内の代入も DescendantNodes で検出) で実用上は足りる
- 注: while fallback 時の continue → incrementor 順は既存実装が C# と一致 (ラベル後に incrementor)

### T145: C# の除算・剰余セマンティクス ✓ (2026-07-17)
- 整数 `/` が Lua 実数除算 (5/2=2.5)、`%` が floor 剰余 (-5%2=1) になっていた silent wrong-code を修正。結果型が整数のとき `__tcs_idiv` (0 方向 truncation) / `__tcs_irem` (被除数符号)、float の `%` は `math.fmod` (C# の truncated remainder と一致)。compound `/=` `%=` も同判定。nullable は lifted operator 用に unwrap して判定
- helper は生成 Lua 冒頭に chunk-local で常時定義 (言語 lowering であり `--no-runtime` の bare 出力でも動く)。module mode の define chunk は同名 global (`_G.__tcs_idiv` — bootstrap が runtime の `TinySystem.idiv/irem` を alias) へ fallback
- ユーザー定義 operator `%` は従来どおり Lua `%` (__mod metamethod)。整数 0 除算は Lua の `n // 0` エラーで明示 fail、float /0 は IEEE inf のまま
- 変更ファイル: Transpiler/LuaEmitter.cs (header helper), Transpiler/LuaEmitter.Expressions.cs, runtime/tinysystem.lua (TinySystem.idiv/irem), Transpiler/ModuleArtifacts.cs (bootstrap alias), Transpiler.Tests/DivisionSemanticTests.cs (新規6)
- 検証: dotnet test 539/539 (sourcemap / module descriptor / operator overload の回帰なし)
- 判断: helper を usage 検出で条件 emit せず常時 emit — 出力 +8 行のコストで、emit 中に記録する type range / source map の後方 shift 問題を構造的に回避。int.MinValue / -1 の overflow 例外は再現しない (Lua は wrap) — 既知差異
- 残課題: なし

### T146: nullable bool と GetValueOrDefault の nil-safe lowering ✓ (2026-07-17)
- `bool? false ?? true` が Lua `or` で true になる問題を、bool 型 (unwrap nullable) の `??` だけ明示 nil 判定 IIFE (`__tcs_lhs` 一回評価、右辺は nil 時のみ) へ変更。非 bool は false を取り得ないため `or` を維持 (軽く、右辺 lazy も既に正しい)
- 追加発見と修正: `GetValueOrDefault(fallback)` は明示 fallback 引数を黙って無視して default(T) を返していた。receiver → fallback の順で常に各 1 回評価し、false 値も fallback しない nil 判定へ。引数なし版は default(T) が false/0/nil なので `(x or default)` のまま
- `??=` は T143 の nil 判定 lowering 済みで bool? も正しい (既存挙動)
- 変更ファイル: Transpiler/LuaEmitter.Expressions.cs, Transpiler.Tests/NullableValueTypeTests.cs (+4)
- 検証: dotnet test 543/543
- 判断: 全 `??` を IIFE 化せず bool 限定 — 生成 Lua の可読性と実行コストを守りつつ、意味論が壊れる型だけ下げる (T143 の pure/complex 分岐と同じ思想)
- 残課題: なし

### T147: custom property accessor の read/write lowering ✓ (2026-07-17)
- 定義側は get_/set_ を生成するのに全使用サイトが raw field を読み書きし、accessor が一度も呼ばれない silent wrong-code を修正。IsCustomProperty (accessor body / expression body の有無を宣言 syntax で判定、auto と BCL property は false) を導入し、member access / implicit this / simple・compound assignment / `??=` / increment / object initializer / conditional access / property pattern の8経路を get_/set_ 呼び出しへ
- expression-bodied property (`int D => expr;`) を get-only custom property として定義側に新規対応 (従来は TCS1001 で member ごと消えていた)
- compound は `set_X(get_X() op rhs)` で C# の read → rhs → write 順、副作用 receiver は `__tcs_obj` temp で一回評価 (T143 と同方針)。T145 の整数除算判定は ApplyCompound として共有化
- 変更ファイル: Transpiler/LuaEmitter.cs, Transpiler/LuaEmitter.Expressions.cs, Transpiler/LuaEmitter.Statements.cs, Transpiler.Tests/PropertyAccessorTests.cs (新規9)
- 検証: dotnet test 552/552
- 判断: static custom property は T148 スコープとして触らず (定義側 emit 自体が instance 形式のため)。record 宣言内の custom property は既存 record 経路のまま (需要が出たら拡張)
- 残課題: T148 (static property)

### T148: static property の storage/accessor lowering ✓ (2026-07-17)
- static auto property が instance ctor 内で `self` に初期化され、`Class.Prop` の static access が常に nil になる silent wrong-code を修正 — static field と同じ class table 初期化 (initializer / 型別 default、StaticFieldMeta による hot-apply pure 判定も共通) へ
- static custom property の accessor を `function Class:get_X()` (instance 形式) から `function Class.get_X()` (class function) へ。読み書き・compound・increment・implicit access の全サイトで `Class.get_/set_` を dot 呼び出しする (TryGetCustomPropertyTarget に CallOp を追加)
- 変更ファイル: Transpiler/LuaEmitter.cs, Transpiler/LuaEmitter.Expressions.cs, Transpiler/LuaEmitter.Statements.cs, Transpiler.Tests/PropertyAccessorTests.cs (+2)
- 検証: dotnet test 554/554
- 判断: instance 生成なしでも static 値が共有されることを initializer 読み出しで担保 (class table 初期化は型 emit 時に走る)
- 残課題: なし

### T149: 継承リンクの宣言順・ファイル順独立化 ✓ (2026-07-17)
- 派生 class が基底より先に emit されると `setmetatable(Derived, {__index = nil})` になり継承 lookup が全滅する問題を修正。基底が未 emit なら link を `_pendingBaseLinks` へ遅延し、全型 emit 後 (top-level 文の実行前、または ToString 時) にまとめて張る
- 宣言順が正しいコードは従来どおり inline link (生成 Lua 無変化)。module/registry 経路は T176 の declare/define 分離が既に順序独立
- 変更ファイル: Transpiler/LuaEmitter.cs, Transpiler.Tests/InheritanceTests.cs (+3: 同一ファイル逆順 / 3段逆順 / 複数ファイル逆順)
- 検証: dotnet test 557/557
- 判断: 型依存 graph でのソートではなく link 行だけの遅延 — 順序依存は link 行のみで (ctor 内の `Base.new()` は実行時解決)、emit 順を変えないほうが module slicing と source map に安全
- 残課題: なし

### T150: 暗黙 base() と constructor chaining の境界整理 ✓ (2026-07-17)
- initializer なしの派生 constructor / 合成 default constructor が基底の field initializer を黙って落としていた問題を修正 — 非 object 基底なら `local self = Base.new()` → `setmetatable(self, Derived)` で C# の暗黙 base() を実行 (`class B { int X = 42; } class D : B {}` の `new D().X` が 42 に)
- `: this(...)` initializer を TCS1001 `ThisConstructorInitializer`、2個目以降の constructor を TCS1001 `MultipleConstructors` として shared facts へ追加 (Analyzer/check/transpiler 共有)。emit は先頭 constructor 採用で決定的
- 変更ファイル: Shared/TinyCsComplianceFacts.cs, Transpiler/LuaEmitter.cs, Transpiler.Tests/InheritanceTests.cs (+4)
- 検証: dotnet test 561/561 + analyzer 47/47
- 判断: field initializer の実行順は既存 explicit base() 経路と同じ「基底 (init+body) → 派生 init → 派生 body」。C# は「派生 init → 基底 → 派生 body」だが、観測可能な差は仮想呼び出し等の稀なケースのみで、既存経路との一貫性を優先 (既知差異)
- 残課題: なし

### T152: empty sequence と型別 default の LINQ/runtime 契約 ✓ (2026-07-17)
- `FirstOrDefault<int>()` 等が常に nil を返し、値型で後続の算術が実行時エラーになる問題を修正。emitter が呼び出しサイトの return type (conditional 経路は receiver の要素型) から `default(T)` を計算し、runtime の `List.FirstOrDefault(list, predicate, default)` / `LastOrDefault` 第3引数へ埋め込む (predicate なしは nil placeholder)
- `First()` の empty (従来 nil 返し) と `Min`/`Max` の empty (従来 nil 返し) を "Sequence contains no elements" の明示 error へ。`Sum()` empty=0 と `Last` の既存 error は維持
- 変更ファイル: runtime/tinysystem.lua, Transpiler/LuaEmitter.Expressions.cs (通常 + conditional の2経路), Transpiler.Tests/LinqSemanticTests.cs (+4)
- 検証: dotnet test 565/565 (既存の LastOrDefault string→nil 期待は参照型 default として正しいまま)
- 判断: default は emitter がサイト毎に埋め込む方式 — runtime に型情報を持たせない (型消去の原則を維持)
- 残課題: なし

### T153: ToDictionary selector の評価契約 ✓ (2026-07-17)
- レビュー時に疑われた「selector が要素ごとに複数回評価される」は現行 runtime では再現せず (各要素 1 回)。counter/log 付き selector の semantic test で評価回数と key → value 順を固定
- Lua の代入式 `dict[k(x)] = v(x)` の評価順は言語仕様上未規定のため、runtime を `local key = keySelector(item)` で明示順序化
- duplicate key は棚卸し (2026-07-17) の決定どおり C# の ArgumentException を再現せず Lua table 上書きとし、support-matrix の既知差異へ記載 (`Dictionary.Add` も同様)
- 変更ファイル: runtime/tinysystem.lua, Transpiler.Tests/LinqSemanticTests.cs (+1), doc/support-matrix.md
- 検証: dotnet test 566/566
- 判断: 前提が再現しないことを test で確認してからクローズ (作り込みではなく契約の固定)
- 残課題: なし

### T180: 値型/string 型パターンの型判定修正 ✓ (2026-07-17)
- `v is int inner` が `getmetatable(v) == int` (未定義 global との nil == nil 比較) になり、**nil が値型パターンにマッチして束縛される** silent wrong-code を修正。値型・string・enum のパターンは `type(expr) == "number"/"boolean"/"string"` 判定へ (全パターン経路: is-pattern / switch expression arm / switch statement label / recursive pattern の型部 / 型名 constant pattern)
- int/float は Lua の integer/float subtype が代入経路で揺れるため "number" 一括判定 (静的型が違う組合せは C# コンパイルで弾かれる)。class/record は従来どおり metatable 比較
- 追加対応1: designation なしの binary `is Type` (`s is Circle`) を新規サポート (従来は TCS1001 warning で条件が壊れていた)。T151 の残課題を解消
- 追加対応2: switch expression の arm designation (`int v => v`) が未束縛で nil になっていた問題を IIFE 内 local 束縛で修正
- 変更ファイル: Transpiler/LuaEmitter.Expressions.cs (EmitTypeCheck / LuaTypeNameFor + 全パターンサイト), Transpiler/LuaEmitter.Statements.cs, Transpiler.Tests/TypePatternTests.cs (新規4)
- 検証: dotnet test 570/570
- 判断: `~= nil` ではなく type() 判定 — interface 型 receiver 等でも型不一致を検出できる。object receiver の int/float 判別は型消去の既知限界 (tcs は object 未対応なので実害なし)
- 副産物: T181 を起票 — 式文脈 (ternary 等) の is-pattern designation が未束縛で nil になる gap を発見
- 残課題: T181

### T181: 式文脈の is-pattern designation 束縛 ✓ (2026-07-17)
- ternary (`x is int v ? v : -1`)、複合条件 (`if (s is Circle c && c.R > 5)`)、lambda 内の is-pattern designation が束縛されず nil になる silent wrong-code を修正。out var と同じ statement 前 `local` 宣言 (elseif 連鎖は chain 全体を root 前で宣言、expression-bodied lambda は function 冒頭で宣言) + IIFE 内の一回評価代入 `(function() v = expr; return type(v)==... end)()` へ統一
- VisitIf の root 限定 special case (binding + receiver 二重評価) を削除し、全文脈が同じ経路に
- 変更ファイル: Transpiler/LuaEmitter.Statements.cs (pre-pass 拡張 / IsPatternDesignationNames), Transpiler/LuaEmitter.Expressions.cs (VisitIsPattern 束縛 / lambda locals), Transpiler.Tests/TypePatternTests.cs (+3)
- 検証: dotnet test 573/573
- 判断: designation の束縛は C# と同じく match 失敗時も変数自体は存在する (読み出しは C# の definite assignment が防ぐ)。recursive pattern の designation (`is Circle { R: > 2 } c`) は従来どおり未対応
- 残課題: なし

### T155: --entry を実際の emitted 名と一致させる ✓ (2026-07-17)
- `--entry Game.App` が入力文字列をそのまま `return Game.App` に貼り、namespace 透過 emit の実名 (`App`) と食い違って nil を返す silent wrong-code を修正。`GetTypeByMetadataName` → source assembly の一意な simple 名の順で解決し、`return {symbol.Name}` を emit
- interface / enum 等の非 class は "entry class must be a class or record class"、複数 namespace の同名 simple 指定は候補一覧付き "ambiguous" で exit 1 (output 未作成)
- 変更ファイル: Transpiler/Transpiler.cs, Transpiler.Tests/EntryClassTests.cs (+4)
- 検証: dotnet test 577/577
- 判断: 同名型が複数あるときの全 emit 衝突 (namespace 透過による last-write-wins) は entry 解決とは別の既知制約として残る (namespaced 入力を本格対応する際に扱う)
- 残課題: なし

### T157: watch の --prelude dependency 監視 ✓ (2026-07-17)
- 出力へ埋め込まれる `--prelude` だけを変更しても rebuild されない問題を修正。prelude path を input/`--ref` と同じ監視集合へ追加し、watcher filter を `*.cs` 固定から `*.*` + `watchedFiles` の exact path 判定へ変更 (無関係ファイルでは rebuild しない)。Deleted イベントも rebuild を起こし、missing dependency の失敗として報告される
- 変更ファイル: Transpiler/Program.cs, Transpiler.Tests/WatchModeTests.cs (+1: prelude-only 変更で rebuild / 無関係 .lua で非 rebuild、watch 子プロセスは entireProcessTree kill)
- 検証: dotnet test 578/578
- 判断: 出力 (.lua) と prelude が同居しても、対象判定が exact path なので self-trigger loop にならない
- 残課題: なし

### T158: LuaEmitter.Expressions.cs の責務分割 ✓ (2026-07-17)
- 800 行禁止を大きく超えていた LuaEmitter.Expressions.cs (1565 行) を責務別 partial へ分割: Expressions (dispatcher / literal / operator / assignment / lvalue helper、580 行)、Invocations (invocation / collection・string・Math API mapping / member access、406 行)、Patterns (pattern / 型判定 / switch 式 / null 条件アクセス / lambda、400 行)、Objects (object・collection・array 生成 / initializer / with / 補間、244 行)
- 変更ファイル: Transpiler/LuaEmitter.{Expressions,Invocations,Objects,Patterns}.cs
- 検証: dotnet test 578/578 (behavior / source map 差分なし — 出力生成コードは無変更のテキスト移動)
- 判断: temp/lvalue helper (TryLowerLvalue / HasSideEffectSyntax / ApplyCompound) は assignment 系と同じ Expressions に置き所有を一意化。mapping table (ListRuntimeMethods) も Expressions 先頭に集約
- 残課題: なし

### T159: TinyCsComplianceFacts.cs の責務分割 ✓ (2026-07-17)
- 896 行に達していた共有 facts を partial へ分割: TinyCsComplianceFacts.cs (TCS1001 syntax / 予約語 / operator metamethod、220 行)、.Api.cs (TCS1002 allowlist シグネチャと判定、459 行)、.CollectionNull.cs (TCS1003、261 行)
- Transpiler / TinyCs.Analyzers の csproj を `..\Shared\*.cs` wildcard include へ変更 (ファイル追加時の二重管理をなくす)
- 変更ファイル: Shared/TinyCsComplianceFacts{,.Api,.CollectionNull}.cs, Transpiler/Transpiler.csproj, TinyCs.Analyzers/TinyCs.Analyzers.csproj
- 検証: dotnet test 578/578 + analyzer 47/47 (parity / nupkg consumer は run-tests 恒常ゲート)
- 判断: allowlist と diagnostic formatting は Api partial に一元化し重複なし
- 残課題: なし

### T160: 600/800 行 file size gate の自動化 ✓ (2026-07-17)
- CLAUDE.md の 600 行警告 / 800 行禁止を FileSizeGateTests として dotnet test に常設。run-tests.sh / run-tests.ps1 / CI はいずれも dotnet test を通るため、外部 checker スクリプトを作らず判定の二重実装を回避
- 境界 (600/601/800/801) は Theory で固定し、リポジトリ実走査は bin/obj/deps/.git を除外して 800 超をテスト失敗、600 超を warning 出力にする
- 変更ファイル: Transpiler.Tests/FileSizeGateTests.cs (新規5)
- 検証: dotnet test 583/583 (現行ソースは T158/T159 の分割後で error 0)
- 判断: fixture ファイル方式ではなく境界 Theory + 実 tree 走査 — cross-platform かつ CI 配線変更ゼロ
- 残課題: なし

### T161: support matrix / test evidence の最終監査 ✓ (2026-07-17)
- T138-T181 の変更と support-matrix / README の記述を突き合わせ、stale な箇所を更新: 算術演算子 (整数 `/` `%` の idiv/irem)、`??` (bool? nil 判定)、`is Type` / 型パターン / 宣言パターン (type() 判定・式文脈束縛で P→Y)、パターン変数 (-→Y)、switch 文 (パターンラベル)、分解宣言 (分解代入対応)、FirstOrDefault/Last (型別 default と empty error)。サマリ表を再集計 (言語機能 Y 99→101 / P 18→17 / - 94→93)
- current.md のテスト件数記述を廃止し `dotnet test` / `run-tests.sh` の実行結果を正本に (件数の手動同期をやめる)。README の analyzer-demo 期待件数 (TCS1001 x5 等) は run-tests の恒常ゲートが実件数を検証しており一致を確認済み
- 「Y だが実行不能」記述の残りなし (監査は subagent の全文照合で実施)
- 変更ファイル: doc/support-matrix.md, doc/current.md, doc/tasks.md (空化)
- 判断: HotReload の概念的言及 (async を Out にする理由の「ホットリロード可能な」) は lume.hotswap / module registry でも成立するため据え置き
- 残課題: なし — tasks.md は空

### 働き方改革: doc/current.md の粉砕とワークフロー簡素化 ✓ (2026-07-17)
- doc/current.md を削除。ralph loop 時代の「セッション跨ぎ進捗スナップショット」であり、1 タスクをセッション内で完遂できる現在は不要と判断 (ユーザー決定)。残作業は tasks.md、直近文脈は done.md 末尾 + git log、仕様の正本は support-matrix / README / design doc に一本化
- 粉砕前に各節を照合し、他に無かった記述だけ README へ移設: `--entry` の解決規則 (T155 仕様) と output/入力の同一実体エラー。Dictionary wire format / String edge 挙動 / run-lub 手順は support-matrix / lub-gap-analysis が既に正本
- CLAUDE.md の作業手順を 9 step → 5 step へ (current.md の確認・更新を廃止)。完了ログ形式から「変更ファイル」を廃止 (commit が正本) し「検証」を明示化
- 検証: 全ドキュメントの current.md 参照を除去 (design doc の関連文書リンク含む)
- 判断: GitHub Issues への移行は見送り — ローカル grep 性・オフライン性・エージェントの読み書き速度で doc/ 運用が勝つ。ralph loop に戻る場合は必要な形の状態ファイルをその時に再設計する
- 残課題: なし

### T182: [C0] 仕様 corpus 取り込みと分類 sweep ✓ (2026-07-17)
- deps/csharpstandard (draft-v12) を submodule 追加し、注釈付き例 642 件の全数分類を実装: 注釈パーサ (bare-key JSON) / 公式 template 展開 ($example-code + additional-files + inline `// File`) / 分類パイプライン / baseline 照合 / 章別レポート生成。sweep は TCS_SPEC_CONFORMANCE=1 ゲート、`run-spec-conformance.sh` で実行
- 分類は analyzer 視点で行う: `TranspileWithDiagnostics` に references 注入口を追加して TPA フル参照でコンパイルし、SDK ImplicitUsings 相当を referenceSources で補完。これでサブセット外 API が compile error でなく TCS 診断として観測され、unexpected-compile-error 238→29 (残りは unsafe 25 + 個別 4)
- 集計: InRun 55 / InCompile 171 / Diag 248 / CsErr 119 / Unextracted 49 / **Bug 0**
- 検証: `dotnet test` 608+47 green (sweep は通常実行では Skip)、`bash run-spec-conformance.sh` exit 0 で report/baseline 再現、注釈数の突き合わせ (grep 651 のうち 9 件は upstream が `Incomplete$`/`NeedsReview$` で無効化しており正味 642 で一致)
- よかったこと: Codex に契約書ベースで委譲し Fable がレビュー・仕上げする分担が機能した。full-ref 化と ImplicitUsings 補完は初回 sweep の unexpected-compile-error 集計から系統原因を特定して潰せた — 全数分類レポートが自分自身のデバッグに効く
- 判断: 公式 ExampleExtractor の実行照合は net6 ツールチェーン依存のため marker 数照合で代替。FluentAssertions 8.9 (non-OSS ライセンス) の依存追加は却下し既存どおり素の xUnit Assert に統一。baseline キーは同名重複時のみ全出現へ `:L<行>` を付与 (片側付与は文書順依存のため不採用)
- 残課題: C1 (T183) で expectedOutput 実行検証。個別 4 件 (documentation-comments IDStrings*, patterns LogicalPattern3, structs RecordStructEqualityMembers2) と expected-error 系 3 件 (LangVersion 依存の期待) のスポット調査は C1 冒頭で

### T183: [C1] expectedOutput 例の Lua 実行検証 ✓ (2026-07-17)
- SpecLuaExecutor を追加: InRun かつ expectedOutput / ignoreOutput 付きの例を runtime prelude + emitted Lua + entry 呼び出し (top-level はチャンク実行、それ以外は static Main を Roslyn で発見し namespace 込みで明示呼び) で実行し、仕様明記の出力と照合。失敗は Bug へ再分類して baseline / Bugs 節に乗せる
- Normalizer 第1版 (bool True/False → true/false) と known-differences.json (例別 allowlist、理由必須) を導入。sweep gate は「baseline にない新規 Bug / 分類後退ゼロ」とし、Bug ゼロは発見タスク完了時の C1 完了条件へ
- 初回実行 12 例中 7 合格 (1 は allowlist: string interning による参照等価差)。**発見 5 件を T188-T192 として起票**: tuple 構文 silent 破損 / top-level `args` 未定義 / `[Conditional]` 無視 / interface default member 欠落 / string concat null エラー — sweep が狙いどおり silent wrong-code を検出
- C0 残のスポット調査: IDStrings 2 件は unsafe (CS0227) で実質 unsafe 27 件、LogicalPattern3 は CS0841 (要 upstream 照合)、RecordStructEqualityMembers2 は CS1513 (複数 fence 例の可能性、C2 で調査)、expected-error 系 3 件は upstream の LangVersion 12 pin と C# 14 の版差
- レポートに Execution 節と Unexpected extraction details 節 (先頭エラー行) を常設
- 検証: dotnet test 613/613+47 (sweep Skip 込み)、run-spec-conformance.sh 31/31 green、baseline 再生成後の連続実行で差分なし
- 判断: 実行失敗の allowlist 逃しは意味論差の受容 (accepted:) に限定し、tcs のバグは Bug のまま baseline に記録して起票する — 「Bug 0 を維持するために隠す」構造を作らない

### T188: tuple 構文の TCS1001 診断化 ✓ (2026-07-17)
- `(string, int)` 型と tuple literal が診断なしで `ValueTuple.new(...)` (未定義) に emit されていた silent wrong-code を診断化。TupleType 全部と、分解代入 LHS `(x, y) = rhs` (deconstruction lowering が受け持つ) を除く TupleExpression を TCS1001 へ追加
- 検証: dotnet test 615/615 green (分解宣言/分解代入の非退行テスト込み)、spec baseline で types.md:TupleTypes5 が Bug → Diag
- 判断: ネスト tuple LHS `((a,b),c) = rhs` は最外殻だけ除外対象なので内側が診断される — 現行 lowering も非対応のため正しい

### T189: top-level statements の args 参照を TCS1001 診断化 ✓ (2026-07-17)
- 暗黙パラメータ `args` の参照が未定義 global として emit され実行時 nil エラーになる silent wrong-code を診断化。semantic model で synthesized `<Main>$` の parameter 判定 (lambda 等の自前 args は除外)
- 検証: dotnet test 617/617 green、spec baseline で basic-concepts.md:TopLevelStatements2A が Bug → Diag
- 判断: 空 table 供給ではなく診断 — command line は host の責務で、暗黙供給は「動くが常に空」という別の silent 差異を作るため

### T190: [Conditional] 属性の TCS1001 診断化 ✓ (2026-07-18)
- `[Conditional("DEBUG")]` 付きメソッドの呼び出しが条件無視で常に実行される silent 差異を、属性宣言側の TCS1001 診断で拒否。System.Diagnostics.ConditionalAttribute を semantic 判定し、意味論を変えない他属性 ([Obsolete] 等) は対象外
- 検証: dotnet test 619/619 green、spec baseline で attributes.md:ConditionalMethods4 ほかが Bug/InRun → Diag
- 判断: 呼び出し側でなく宣言側で1回診断 (呼び出し網羅より位置が安定)。BCL の Conditional API (Debug.Assert 等) は TCS1002 allowlist 外で既に拒否される

### T191: interface default member / explicit implementation の TCS1001 診断化 ✓ (2026-07-18)
- interface は Lua 出力を持たないため、実装付き interface member (DIM) と explicit interface implementation (`void IA.M()`) が silent 欠落し実行時 nil call になっていた。両方を TCS1001 で拒否 (宣言のみの interface 契約と通常の implicit 実装は従来どおり)
- 検証: dotnet test 621/621 green、spec baseline で interfaces.md ほか 14 例が Diag へ
- 判断: property は AccessorList の body/expression-body 有無で判定 (auto-property 宣言 `int P { get; }` は契約のみなので許容)

### T192: string concat の null 安全化と指数表記 Normalizer ✓ (2026-07-18)
- C# の string 連結は null を空文字列扱いするが、Lua の `..` は nil でエラーになっていた。binary `+` は null になり得る string オペランドへ `or ""` を付与 (リテラル・補間・ネスト連結は non-null 確定で素通し)、`+=` は read/right 両側を null 安全化
- Normalizer に浮動小数の指数表記差 (C# `1.23E+15` / Lua `1.23e+15`) の正規化を追加
- 検証: dotnet test 623/623 green。spec の AdditionOperator は残差が decimal スケール (`2.900` vs `2.9`) のみに縮小 (→ T193 decimal 診断化で解消予定)
- 判断: 全 string オペランド一律ラップではなく non-null 確定形を除外 — 出力の可読性維持と no-op ラップ削減

### T193: decimal 型/リテラルの TCS1001 診断化 ✓ (2026-07-18)
- decimal が Lua number (binary float) へ silent マップされ、スケール表示 (`2.900` → `2.9`) や精度の意味論差が診断なしで出ていた。PredefinedType の decimal keyword と `m` サフィックスリテラルを TCS1001 で拒否 (float/double は非対象)
- 検証: dotnet test 627/627 green。**spec conformance の Bug 分類が 0 に到達 — C1 完了条件達成** (Executed 7/7 passed、集計: InRun 50 / InCompile 153 / Diag 271 / CsErr 119 / Unextracted 49 / Bug 0)
- 判断: 数値意味論を近似で通すより DSL 契約として明示拒否 (struct と同じ扱い)。需要が出たら固定小数 helper で再検討

### T184: [C2 前半] spec 例の dotnet differential ✓ (2026-07-18)
- SpecDotnetExecutor を追加: 例を in-memory Roslyn compile → AssemblyLoadContext 実行 → Console capture (排他 + 10s timeout)。出力契約の無い InRun 例のオラクルを実 .NET 実行出力にし、実行対象を全 InRun 50 例へ拡大
- 初回 differential で **17 件の意味論バグを新規検出し T194-T204 として起票**: overload 誤 dispatch / `new` hiding 無視 / static ctor 欠落 / 式文脈 increment の副作用消失 / field default nil / delegate `new` / interface const / hex escape 破損 / Console マッピング漏れ / 補間 format specifier / namespace 解決。手書きテスト 326 本が一度も踏まなかった領域が仕様 corpus + 実処理系オラクルで一気に可視化された
- ハーネス修正 1 件: dotnet 側にも ImplicitUsings 補完 (classifier と対称)
- 検証: SpecConformance 単体 34/34 green、sweep は baseline 一致 (Bug 17 は起票済み記録)、Executed 50 / passed 33
- 判断: C2 後半 (既存 TranspileAndRun corpus の裏取り) は luaExpr → C# 翻訳の設計が必要なため T206 へ分割

### T202: string literal escape の Lua 再変換 ✓ (2026-07-18)
- 通常の `"..."` literal が C# の raw token text のまま Lua へコピーされていた (escape 文法は非互換: `\x` の可変長 hex、`\0` 直後の数字、`\u` など)。常に ValueText を Lua 形式へ escape し直す経路へ統一し、制御文字は 2 桁固定 `\xXX` で emit
- 検証: dotnet test 630/630 green (バイト長セマンティクステスト追加)、spec baseline で lexical-structure.md:CharacterLiterals が Bug → InRun (dotnet differential 一致)

### T203: Console を allowlist 型へ昇格 (WriteLine のみ許可) ✓ (2026-07-18)
- System.Console が型ごと TCS1002 判定を素通りし、emitter が print へマップしない member (Console.In/Write/ReadLine 等) が silent nil アクセスになっていた。Console を partially-supported 型にし WriteLine のみ許可
- 検証: dotnet test 632/632 green、spec baseline で patterns.md:PatternFormGen1/2 ほかが Diag へ

### T200: delegate / event 宣言の TCS1001 診断化 ✓ (2026-07-18)
- delegate 型宣言が silent に `D.new(...)` (未定義) へ、event 宣言が silent 欠落へ落ちていた。DelegateDeclaration / EventDeclaration / EventFieldDeclaration を TCS1001 で拒否 (callback は BCL の Action/Func が正)
- あわせて 800 行 gate 超過の DiagnosticTests.cs を分割 (spec conformance 由来の診断テストを SubsetDiagnosticTests.cs へ)
- 検証: dotnet test 634/634 green、spec baseline で conversions.md:AnonFuncTypeConv2 ほかが Diag へ

### T205: System.Random を TCS1002 未対応型へ ✓ (2026-07-18)
- System.Random が allowlist 判定を素通りし `new Random()` が Lua で `Random.new` nil になっていた (乱数の正は TinySystem.Random static facade)。UnsupportedFullTypeNames へ追加
- 検証: dotnet test 635/635 green、spec baseline で conversions.md:AnonFuncTypeConv2 が Bug → Diag

### T201: interface の field/const 宣言を TCS1001 診断化 ✓ (2026-07-18)
- interface 内 field/const が silent 欠落し `IX.A` が実行時 nil になっていた。FieldDeclaration (parent が interface) を TCS1001 で拒否
- 検証: dotnet test 636/636 green、spec baseline で interfaces.md:InterfaceFields が Bug → Diag

### T195: `new` member hiding の TCS1001 診断化 ✓ (2026-07-18)
- `new` modifier による member hiding は静的型でディスパッチが変わる意味論で、metatable の動的ディスパッチでは表現できず silent 誤 dispatch になっていた。`new` modifier 付き member 宣言を TCS1001 で一括拒否 (override は従来どおり対応)
- 検証: dotnet test 637/637 green、spec baseline で VirtualMethods1/2 と PropertyReservedSignatures (CS0109 の new 宣言含む例) が Bug → Diag。残 Bug 9

### T194: メソッド overload の TCS1001 診断化 ✓ (2026-07-18)
- Lua table は同名 key を 1 つしか持てず、overload が last-write-wins の silent 誤 dispatch になっていた。同一型内の 2 個目以降の同名メソッド宣言を TCS1001 で拒否 (MultipleConstructors と同じ方針)
- 検証: dotnet test 639/639 + run-tests.sh 全ゲート green (samples に overload 使用なし)、spec baseline で ParameterArrays3 ほかが Diag へ

### T196: static constructor の TCS1001 診断化 ✓ (2026-07-18)
- static constructor は「初回アクセス時に一度だけ」の実行タイミング意味論を持ち、eager な class table 生成では再現できず silent 欠落していた。TCS1001 で拒否
- 検証: dotnet test 640/640 green、spec baseline で StaticConstructors1/2 と StaticFieldInitialization2 (いずれも static ctor を含む) が Bug → Diag。残 Bug 5
- 判断: static field 初期化「順序」自体の意味論差 (宣言順 eager vs 初回アクセス) は、cctor なしの純粋な発現例が corpus に無いため個別タスク化しない。需要が出たら support-matrix の既知差異に記載

### T199: static field の default 値 pre-zero ✓ (2026-07-18)
- C# は static field を default 値で事前初期化してから initializer を宣言順に実行するが、tcs は initializer を宣言順に直接代入しており、循環参照 (`a = b + 1; b = a + 1`) や後方 field 参照が nil arithmetic になっていた。class table 宣言直後に非 nil default の pre-zero を emit し、DeclRanges に載せて hot apply の define チャンクから除外 (live static 値を上書きしない)
- instance field は CS0236 (initializer から他 field 参照不可) が守るため対応不要
- 検証: dotnet test 641/641 green (module artifact / transaction テスト含む)、spec baseline で classes.md:VariableInitializers2 が Bug → InRun (dotnet differential 一致)

### T197: 式文脈 ++/-- と named argument の TCS1001 診断化 ✓ (2026-07-18)
- 式文脈の `i++` は「値を返しつつ代入」の意味論だが、現行 emit は `i + 1` の値だけで副作用が消えていた。statement / for 更新部以外の increment/decrement を TCS1001 で拒否。named argument も位置渡しへ黙って落ちていたため NameColon 付き引数を拒否
- 検証: dotnet test 643/643 + run-tests.sh 全ゲート green (samples/既存テストは statement/for 利用のみ)、spec baseline で Run-timeEvalOfArgLists1 が Diag へ。残 Bug 3
- 判断: IIFE + temp での完全対応は lvalue 機構拡張が必要で、需要が出たら別タスク。optional parameter default の silent nil は診断でなく修正が妥当 (ゲームコードで頻出) — T207 起票

### T207: optional parameter の default 補完 ✓ (2026-07-18)
- ユーザーメソッド / constructor の optional parameter が省略呼び出しで silent nil になっていた。関数冒頭に `if p == nil then p = <default> end` を emit して C# の省略時 default を再現
- 検証: dotnet test 644/644 green (method / ctor / string default の semantic テスト追加)
- 判断: 省略と明示 null は Lua ではどちらも nil で区別不能 — 明示 null に default と異なる挙動を期待する呼び出しは既知の意味論差としてコメントに明記

### T208: params parameter の TCS1001 診断化 ✓ (2026-07-18)
- params の展開呼び出し (`F(1,2,3)` → 配列 pack) が未実装で、展開形が先頭引数だけ束縛される silent wrong-code だった。out/ref と同様に宣言側を TCS1001 で拒否
- 検証: dotnet test 646/646 + run-tests.sh 全ゲート green、spec baseline で ParameterArrays4 ほかが Diag へ
- 判断: pack 実装は `F((string)null)` → `{null}` が TCS1003 (collection null) と衝突するなど fidelity の泥沼があり、需要が出てから判断

### T198: 補間文字列の alignment / hex・指数の大文字小文字 / brace escape ✓ (2026-07-18)
- 補間の text 部が raw token copy で `{{`/`}}` が残り、`X`/`E` 指定子が常に小文字 `%x`/`%e` になり、alignment (`{val,4:X}`) が無視されていた。text 部は brace 解決 + Lua escape 再変換 (T202 と同方針)、指定子は大文字小文字を反映、alignment は整形後文字列への `%ns`/`%-ns` として合成
- 検証: dotnet test 647/647 green、spec baseline で attributes.md:DeclCustomHandler1 が Bug → InRun (dotnet differential 一致)。残 Bug 1

### T204: spec executor の entry を namespace 透過 emit に合わせる ✓ (2026-07-18)
- ExtensionMethodInvocations2 の失敗は tcs 側でなくハーネス側: entry 探索が `N2.Test.Main()` と namespace 込みで呼んでいた (tcs は T155 のとおり型を flat 名で emit)。型名チェーンのみで entry を組むよう修正
- 結果、user 拡張メソッドの static call lowering + using ベースの解決が dotnet differential と一致することを確認 — **C2 differential も Bug 0 達成**
- 検証: SpecConformance 35/35 green、sweep baseline 一致、Executed 全 InRun で failures ゼロ

### T209: TinySystem facade を System 委譲実装へ ✓ (2026-07-18)
- dotnet 側 RuntimeFacades が全 member no-op スタブで、CLAUDE.md の設計「dotnet 側では System 名前空間の型に委譲」と乖離していた。Math/String/List/Dict/Random 全 member を System 相当へ委譲 (Dict.Add は Lua wire format に合わせ indexer 代入 = duplicate 上書き、T153 判断と整合)
- これによりユーザーは純粋ゲームロジックを dotnet 単体テストで検証でき、corpus differential が facade parity を検証できる
- 検証: dotnet test 661/661 green、differential で facade 系 5 mismatch が全て解消

### T206: [C2 後半] 既存 TranspileAndRun corpus の dotnet differential ✓ (2026-07-18)
- TestHelper.TranspileAndRun(WithRuntime) に differential フックを追加 (TCS_DIFFERENTIAL=1 で有効)。luaExpr を C# 式へ自動翻訳できる呼び出し (単純な static 呼び / tostring ラップ / 'str'→"str" / nil→null) は実 .NET で in-memory 評価し、Lua 形式へ整形して実行結果と突き合わせる。IIFE / `..` / `#` / Lua 演算子は skip として集計 (silent drop なし、TCS_DIFFERENTIAL_LOG に内訳)
- 比較は正規化 (bool/指数) + 数値等価 fallback (C# double 4.0 と Lua integer 4 は既知の表現差)。Random 使用ソースと文字列長系 (UTF-16 vs byte、opt-out 引数) は対象外
- 結果: **253 呼び出しが実 .NET と一致、mismatch 0** (初回検出の 24 件は全てハーネス整形差と facade no-op → T209 で解消)。skip 44 (untranslatable 26 / compile 14 / random 4)
- `run-differential.sh` で再現実行 + 内訳表示
- 検証: dotnet test 661/661 green (env なしでは従来動作)、run-differential.sh exit 0
- 判断: 手書き期待値そのものの裏取りは「Lua 実行値 vs .NET 実行値」の一致で担保 (期待値が両者とずれれば従来 Assert が落ちる)。翻訳器を汎用化するより skip を可視化して集計する方が誤検出ゼロで保守が軽い

### T185: [C3] spec conformance / differential の恒常ゲート化 ✓ (2026-07-18)
- run-tests.sh / run-tests.ps1 の dotnet test を TCS_SPEC_CONFORMANCE=1 + TCS_DIFFERENTIAL=1 + TCS_SPEC_REPORT 付きの 1 回実行に変更 — スイート二重実行なしで sweep (baseline 後退検知) と corpus differential が常設ゲートになり、章別レポートも毎回再生成される (挙動変更は report/baseline の diff として現れる)
- pre-commit hook は run-tests.sh を呼ぶため、コミットごとに sweep + differential 込みの全ゲート (約 40 秒) が走る
- 検証: baseline を意図的に破壊して赤 (conformance baseline mismatch) を確認後に復元。run-tests.sh 全体 39 秒 exit 0、662/662 green (Skip 0)
- 判断: nightly 分離は不要 — 全ゲート込みで +数十秒に収まり、恒常実行のほうが後退検知が早い

### T187: [C5] coverlet で emitter 未踏分岐を可視化 ✓ (2026-07-18)
- coverlet.collector を Transpiler.Tests に追加 (PrivateAssets)。`dotnet test --collect:"XPlat Code Coverage"` (sweep + differential 込み) で計測
- branch coverage (主要ファイル): LuaEmitter.cs 94% / Invocations 93% / Expressions 86% / Statements 86% / **Objects 68.5%** / **Patterns 70.7%**、Shared facts 83-97%。CLI 系は Program.cs 67% / FileIdentity 50% (watch・stacktrace 注釈の分岐)
- 判断: カバレッジ率目標は置かない (design 方針)。corpus 追加の需要判定として、Objects (with 式 / initializer 系) と Patterns (switch 式 / null 条件) の未踏分岐が最有力候補。CLI 分岐は CliRuntimeTests の粒度で妥当
- 検証: dotnet test 662/662 green (collector 追加後)

### T186: [C4] サブセット内 AST 生成 fuzz ✓ (2026-07-18)
- FuzzGenerator (seed 決定的): int/bool/string/List<int> の宣言、算術 (非ゼロ定数除数の除算/剰余込み)、比較・論理、if/else、有界 for、文字列連結、Console 出力を生成。32bit overflow (C# wrap vs Lua 64bit の既知差) は ±50 初期値と有界ループで生成側から回避
- FuzzRunner: 生成物に TCS 診断が出たら生成器バグとして fail → tcs → Lua 実行と実 .NET 実行 (SpecDotnetExecutor 再利用) を行単位比較 (正規化 + 数値等価)。失敗時は文単位 greedy 縮小で最小再現を作り seed とともに報告
- 検出網の自己検証: Lua への故障注入 (余分な print) を必ず検出することをテストで常設 (design §17 C4 gate 相当)。縮小の機構検証も常設
- run-tests に smoke (20 seeds、+5 秒) を常設、`run-fuzz.sh [count] [seed]` で deep 実行 (既定 500)
- 検証: 単体 3/3 green、50 seeds 全一致、run-tests.sh 全ゲート 45 秒 exit 0 (666/666)
- 判断: nightly の cron 配線は CI 未整備のため持たない — smoke が恒常ゲートにあり、deep はスクリプト一発で足りる

### C0-C5 受入条件の充足確認 ✓ (2026-07-18)
- design doc §8 の 5 項目: (1) 仕様例全数分類 + Bug 0 + 章別レポート ✓ (2) 既存 corpus differential の allowlist 外差異 0 ✓ (253 match / mismatch 0) (3) 後退検知ゲート常設 ✓ (run-tests = pre-commit hook 経由で毎コミット) (4) fuzz の seed 再現 + 自動縮小 ✓ (nightly は run-fuzz.sh、判断は T186 参照) (5) known-differences ↔ support-matrix 相互参照 ✓ (support-matrix §27 を新設)

### CI flaky の根治: dotnet 実行 capture の Console 競合 ✓ (2026-07-18)
- CI (2 vCPU) で FileSizeGate / FuzzSweep が不定に失敗。原因は SpecDotnetExecutor がグローバル Console.Out を StringWriter へ差し替えて capture する実装で、並列テストの出力 (FileSizeGate の 600 行超 warning 等) が (1) fuzz の期待出力へ混入、(2) dispose 済み writer への書き込み例外、の 2 形態で他テストを壊していた
- AsyncLocal ルーティング writer を一度だけ挿し、「この実行の論理コールツリーからの書き込み」だけを capture、他スレッドは元の writer へ素通しする方式へ変更 (lock 不要)
- 検証: ローカル全ゲート green (666/666)。CI は verbosity minimal 化 (失敗詳細の可視化) 済みで、以後の再発時はログに assert が出る

### T210: [IL] 紙の演習 — lowering 例で IL の抽象度を決定 ✓ (2026-07-18)
- `doc/il-lowering-examples.md` を新設。switch 式パターン / for 変数捕捉 closure / struct 配列更新 / 文字列補間の 4 構文で IL 形と両 backend 出力を手書きし、抽象度 8 決定を確定 (パターン・式位置制御フロー・ループ構文は IL に無い、IIFE 廃止で statement 化、演算ノードは型解決済み単型、capture は変数単位、値型は place/copy 地点列挙、runtime 表面は intrinsic)
- 演習の実測で現行 transpiler の意味論バグ 2 件を発見・起票: T221 (for 変数捕捉 closure が C# `3 3 3` / 現行 `0 1 2`)、T222 (派生インスタンスへの `is Base` が C# `true` / 現行 `false`)。除算/剰余は既に idiv/irem 補正済みで il-design の「未整理」が stale と判明 → 修正
- 現行 emitter は bound tree (IOperation) でなく syntax tree + SemanticModel 走査と確認 → il-design.md の記述を実態へ修正し、M1 でも syntax 走査を維持する判断を明記
- 検証: probe を transpile + Lua 5.5 実行、同一プログラムを実 .NET (net10.0) で実行して両挙動を突き合わせ (バグ 2 件はこの差分)。idiv/irem は -7/2=-3, -7%2=-1 で C# 一致を確認
- よかったこと: コードを書く前の紙の演習で P0 バグ 2 件と設計判断 8 件が確定。IL 正本化の価値仮説 (ギャップの場当たり埋めが起きている) が実証された
- 判断: IL builder の入力は IOperation へ移行せず syntax + SemanticModel を維持 — IL 化の価値は IL 側にあり、現行走査コードは資産。IIFE 廃止は意味論でなく性能・可読性判断 (closure 割当除去)
- 残課題: T211 (仕様 v0)、バグ T221/T222 の修正時期 (M1 先行か畳むか)

### T211: [IL] 意味論仕様 v0 ✓ (2026-07-18)
- `doc/il-spec.md` を新設。il-design §2 ギャップ表の各行を「IL の条項 + backend の義務」へ分解し、意味論の正本を規範化: 厳密左→右評価・f32 再結合/縮約禁止 (§4 §6)、i32 wrap + C# 除算規則 + overflow fault (§5)、capture は変数単位 + ループ脱糖 (§7)、fault の決定性 (§12)、値型 copy 地点の列挙 (§10)、intrinsic の規範 = TinySystem dotnet facade + known-differences (§13)
- 合格基準「本文が Lua にも C にも言及せず読める」を満たし、backend 義務は付録 A/B に分離。il-design §8 の未決 6 件中 3 件 (具体形 / 例外 / interop 境界) を決着、残 3 件 + 新規 3 件を il-spec 付録 C に同期
- 検証: 文書のみの変更。規範の根拠事実は T210 の実測 (probe 2 本の Lua/.NET 突き合わせ) に依拠
- 判断: string を UTF-8 バイト列で規範化 (出荷ターゲット表現を正に置き、.NET 実行側を既知差とする — 逆だと release backend が UTF-16 を背負う)。f32→i32 変換不能と INT_MIN/-1 は fault として決定的に定義 (C# 側が unspecified / 例外なので矛盾しない)。数学関数の規範は「同一プラットフォーム内一致」に留め、プラットフォーム間ビット一致は要求しない (spike の libm 排除方針と整合)
- 残課題: gpt-5.6 second opinion review、M2 (T217) でシリアライズ形式と luo 向け入力契約

### T211 補遺: gpt-5.6 反証レビューの反映 ✓ (2026-07-18)
- 反証限定・load-bearing 6 決定に絞った second opinion (Codex 14分)。反証 4 / 生存 2 (fault 決定性、capture 脱糖)
- 採用 4 件: (1) string 規範を「任意の octet 列」へ修正 + 孤立 surrogate literal の診断化 (UTF-8 不変条件は byte slicing で破綻するため)、(2) 非網羅 switch 式の no-match を fault 種別に追加 (temp 未代入の穴。例外サブセット外なので fault が C# 実挙動と等価)、(3) f32 リテラルは IL が bit 値保持・backend は 16 進浮動小数表記 + excess precision 排除 (10 進→double→f32 の二重丸めで 1 bit ずれる反例)、(4) interface への type test は診断化で決着 (T223 起票)
- 検証: 文書のみの変更。反例は Codex 提示の C# 仕様条項 (§8.2.5 / §12.12 / §6.4.5.4 / §12.15.12.1) と Lua 5.5 luaconf.h 参照
- よかったこと: 反証限定 + 対象 6 決定の明示でニトピックゼロ、全指摘が本質だった。レビュー前に自前で付録 C へ落としていた interface 項も具体的な偽陰性反例で決断まで進んだ

### T211 補遺2: relaxed-fp 出荷オプションの追加 ✓ (2026-07-18)
- il-spec §6 に fp モードを定義: strict (既定、digest 一致ゲート適用) と relaxed-fp (出荷ビルド単位の明示 opt-in。縮約/再結合/中間精度昇格を許可)
- 判断: relaxed でも NaN/Inf/−0 の意味論は維持 (finite-math 系は不許可 — 精度は緩めても制御フローの意味は壊さない)。既定を緩めない理由は dev/release 対称性 (digest ゲート) がこの設計の商品価値であるため。Cortex-M7 は SIMD なしで fast-math の旨味は実質 fma 縮約のみ、strict のコストは spike (T212、-ffp-contract=off で測る) が定量化する
- 検証: 文書のみの変更

### T222: `is` / switch 型判定の継承対応 ✓ (2026-07-18)
- EmitTypeCheck の `getmetatable(x) == T` 完全一致を `__tcs_is(x, T)` (metatable chain 走査) へ置換。chunk-local helper (idiv と同方式、--no-runtime でも動く) + TinySystem.instanceof + module mode の _G 配線
- il-spec §9 の規範 (T またはその派生、null は偽) に準拠。多段継承・無関係型・null・designation 束縛・switch arm 順を semantic テスト 5 本で固定
- 検証: InheritanceTypeTestTests 5/5 green (Red 4 件を確認してから実装)、コミット時 pre-commit で全ゲート
- 判断: Operators.cs の overload dispatch (getmetatable 完全一致) は今回のスコープ外 — 派生型を base 引数の演算子に流す挙動は別途需要が出た時に判断

### T221: for 変数を捕捉する closure の意味論修正 ✓ (2026-07-18)
- TryEmitSimpleFor に捕捉ガードを追加: body 内の lambda が制御変数名を参照するなら numeric for を諦めて while 脱糖へ fallback (local 1 個 = 全 closure が同一 upvalue 共有、C# `3 3 3` と一致)。名前一致の過剰判定は fallback が常に正しいので許容
- foreach の反復変数が反復ごと (C# 5+ 準拠、Lua generic for と自然に一致) であることもテストでロック
- 検証: ForLoopTests 19/19 green (Red 1 件確認後に実装)、コミット時 pre-commit で全ゲート
- 判断: M1 に畳まず先行修正 — M1 は挙動不変が完了条件のため、意味論変更を分離してから内部再編に入る

### T214a: [M1] IL 核 — method body の syntax→IL→Lua 経路 ✓ (2026-07-18)
- IL ノード定義 (Il.cs: place/単型演算/構造化制御フロー、il-spec 準拠)、syntax→IL builder (IlBuild 2 ファイル、全 SemanticModel 問い合わせを集約)、IL→Lua emitter (IlEmit、Roslyn 非依存 — IL が意味を運ぶことの構造的証明)。未対応構文は method 粒度で legacy visitor へ fallback するストラングラー構成
- 対応範囲: statements 全種 (if/while/for numeric+脱糖/foreach/repeat/break/continue/return/代入/複合代入/increment)、式コア (算術 idiv/irem/fmod・比較・論理・bitwise・null 安全 concat・補間・ternary・List 生成/操作・Dict 読み・new・要素アクセス +1・facade/Math/String/Console 呼び出し)
- 計測: TranspileResult.IlBodies/LegacyBodies、TCS_IL=off で IL 経路無効化 (退行診断)。samples 実測 21/31 bodies (67%) が IL 経由
- 検証: 全テスト 671 green + IlPipelineTests 5 本 (IL 経由の確認・fallback 分離・意味論)。挙動不変は corpus semantic + differential + conformance sweep が担保
- 判断: builder は legacy と同じ helper (VisitLiteral/CompoundOperator/IsListType 等) を共有し出力一致を構成的に保証。診断を出し得る経路 (WarnUnsupported) は必ず fallback。IIFE 廃止等の出力改善は legacy 削除後に IL 上で行う (T214c 以降) — 移行中の差分を挙動不変だけに絞るため
- 残課題: T214b (lambda/pattern/?./custom property 等)、T214c (legacy 削除)

### T214b: [M1] 残構文の IL 化 — samples 100% ✓ (2026-07-18)
- 追加対応: lambda/closure (式/block body、pattern locals)、pattern 全種 (is 式/switch 式/switch 文、recursive/relational/binary/not)、型 test ノード (IlIsType/IlIsLuaType)、conditional access (?. ネスト込み)、?? (bool 厳密判定 IIFE)、??=、custom property accessor (get_/set_、compound、increment、副作用 receiver temp)、副作用 lvalue の temp 化、Dict 操作 (Add 代入形/TryGetValue/Remove/ContainsKey/Clear)、object initializer、ref-type option table、配列生成、with 式、分解代入 (宣言/tuple)、out 引数 multi-return、base 呼び出し、GetValueOrDefault、out var / is-pattern designation の前宣言
- IL ノード追加: IlIife (式位置逐次実行)、IlClosure、IlWith、IlDo、IlMultiAssign、IlForPairs、IlIsType/IlIsLuaType、IlTable NameKey。inline render (IIFE 内 1 行化) を emitter に追加
- 検証: 全テスト 676 green (fallback 例を method group 参照へ差し替え)。samples 実測 31/31 bodies (100%) が IL 経由 (T214a 時点 67%)
- 判断: IIFE 形は legacy と同形を維持 (M1 は挙動不変が契約)。statement 化 (examples 決定 2) は M1 完了後に IL 上で行う
- 残課題: T214c — ctor/accessor/operator/top-level 文の body も IL 経由へ、fallback は診断 method のみに

### T214c: [M1] ctor / accessor / operator / top-level 文の IL 経由化 — M1 完 ✓ (2026-07-18)
- TryEmitStatsViaIl / TryEmitReturnViaIl / TryEmitExprStatViaIl の共通フックを追加し、method body 以外の 4 emit 地点 (constructor body、custom property accessor body/expression body、operator body、top-level 文) を IL 経由化。origin を SyntaxNode? に統一
- samples 実測 37/37 bodies (100%、ctor/accessor 込み) が IL 経由、legacy 0。M1 の完了条件 (全テスト green・増分コンパイル session 両立・fuzz/differential/conformance ゲート不変) を充足
- 検証: 全テスト 676 green、deep fuzz 100 seeds 全一致 (生成プログラムの IL 経路を実 .NET と行単位比較)、コミット時 pre-commit 全ゲート
- 判断: legacy visitor のコード削除は見送り — fallback は診断構文・method group 参照等の残構文が使い、挙動不変の保険としても機能する。縮退は T224 (P2)、IIFE statement 化は T225 (P2) として M1 契約から分離
- 残課題: T224/T225、M2 (T217) で luo 向け入力契約 (program 構造の IL 化を含む)

### IL 経路への continue scope 修正の写像 ✓ (2026-07-18)
- origin 側 19d35c4 (一般 for lowering の continue goto が local スコープへ飛ぶ問題の legacy 修正) を rebase で取り込み、IL emitter (IlWhile) にも同条件 (incrementor あり + 直下 continue) の do..end 囲いを写像。IlWhile に ScopeBody を追加
- 検証: 回帰テスト Continue_GeneralFor_LocalDeclaredAfterContinue が IL 経路で Red → 修正後 green、全テスト 677 green

### T230: SessionExports に補完/hover クエリ API を追加 ✓ (2026-07-18)
- lub web playground の C# 補完・hover (lub docs/playground-dx.md §3) 向けに `SessionExports.Complete/Hover(epoch, path, content, offset)` を追加。エディタの現在バッファを受け取り、`IncrementalCompilationSession.ForkWithContent` (ReplaceSyntaxTree による speculative fork、session の texts/trees/revision/artifacts 不変) へ `SemanticQueries` が SemanticModel 直叩きでクエリする
- 補完は member access 文脈 (instance / 型 receiver の static / namespace) とスコープ補完を判別し、`TinyCsComplianceFacts.TryGetUnsupportedApi` で allowlist フィルタ (「補完に出る = tcs で書ける」)。overload は名前単位 1 件、上限 200 件。hover は識別子限定でシグネチャ + /// summary を返す
- 検証: SemanticQueriesTests 9 件 (instance/static/allowlist/List/scope/hover/doc/fork 不変/speculative 内容) green、run-tests.sh 全ゲート exit 0 (685/685 + skip 1)
- よかったこと: fork が Roslyn の immutable Compilation そのままなので session 不変が構造的に保証される。allowlist が ISymbol 判定 (TryGetUnsupportedApi) として既に公開されていたため補完フィルタは 1 行
- 判断: Microsoft.CodeAnalysis.Features は wasm バンドル肥大のため不使用 (SemanticModel の Lookup* で必要十分)。fork/SemanticModel はキャッシュしない (wasm ホストのメモリ膨張防止、tcs-main の指摘)。doc は source symbol の <summary> のみ (metadata 参照は XML doc を積んでいない)
- 残課題: wasm 上の実レイテンシ計測は lub 側 (playground 統合) で行い、自動発火可否を判断する → lub verify A8 で実測 complete 47ms / hover 5ms (17_flappy、swiftshader headless)、恒常観測ログ化済み
- レビュー反映 (PR #1): hover の対象を SimpleName 参照 / 宣言ノードに限定し、親式 walk による user-defined operator の誤 hover を排除

### T223: interface type test と孤立 surrogate literal の診断化 ✓ (2026-07-18)
- Shared/TinyCsComplianceFacts に 2 ルール追加: InterfaceTypeTest (is 式 / declaration・type・recursive・constant pattern の interface 対象 — 実行時表現が無く常に偽の silent wrong-code) と LoneSurrogateLiteral (string/char literal と補間テキストの孤立 surrogate — il-spec §11 の UTF-8 octet 規範へ写像不能)。対 surrogate (astral 文字) は許容
- 診断一致 (analyzer / check / transpiler) は Shared 共有により自動で、恒常ゲートが担保
- 検証: SubsetDiagnosticTests 4 本 (Red 2 → green、非対象 2 の非診断)、全テスト 692 + analyzer 47 green

### T213: LUA_32BITS ビルド整備 (lua32) ✓ (2026-07-18)
- CMake に TCS_LUA_32BITS_VARIANT (既定 ON) を追加し、LUA_32BITS 定義の liblua32/lua32 を deps/lua/lua32 へ併産。既存 lua (64bit) と共存し、run-tests の再ビルド経路でも一緒に生成される
- 検証: cmake ビルド成功、lua32 で math.maxinteger=2147483647 (int32) / 1/3=0.333333343 (float32) を確認
- 判断: 既定バイナリの切替は M4 (T216) で行う — テスト資産の移行と同時でないと挙動確認が split-brain になる

### T216: [M4] 数値モデルを i32/f32 (LUA_32BITS) へ移行 ✓ (2026-07-18)
- テスト実行の Lua を lua32 優先へ (TestHelper FindLua)、run-tests.sh/ps1 の鮮度判定にも lua32 を追加。differential の数値等価は .NET double を f32 量子化してから比較 (il-spec §6)
- 移行の爆風半径は 3 件のみ: double 精度前提テスト 2 件を f32/double 両立形へ適正化 (1e308→1e30、epsilon 1e-9→1e-6)、spec 例 VariableInitializers1 (Math.Sqrt の double 全桁表示) を known-differences へ理由付き登録
- 検証: run-tests.sh 全ゲート exit 0 (conformance sweep + differential + fuzz smoke 込み、694 tests)
- 判断: double/long の診断化 (il-design §4 の完全なサブセット外化) は分離 — T216 の完了条件 (32bit ビルドで全ゲート green + differential f32 比較) は満たしており、ソース資産の一括 f32 リテラル移行は独立タスクとして需要と合わせて判断する (tasks.md T226 起票)

### T215: digest harness — spike 3 kernel の f32 FNV digest ゲート ✓ (2026-07-18)
- ../luo/spike/CONTRACT.md と同一仕様 (LCG / 演算列 / FNV-1a) の 3 kernel を TinyC# で記述 (Transpiler.Tests/DigestKernels/、particles は M5 まで class SoA 形)。lua32 実行の出力 f32 を bit 列で FNV し、期待値を恒常ゲート化 (DigestHarnessTests、+約 2.5 秒)
- 実測 digest: sprite_update e8814b32 / spawn_churn 4e5bf016 / particles 8bf97e09。CONTRACT へ追記済みで、luo spike の全変種 (interp/aot-hash/aot-slot/native) はこれと一致すべき — 2 backend 一致検証の tcs 側正本
- 検証: 3/3 green。spawn_churn の CONTRACT 未規定分 (初期空・spawn→update 順・ring 順序) は解釈を明文化
- 副次発見: nested class は Lua 出力に emit されず実行時 nil (診断もなし)。要診断化 — T227 起票

### T217: [M2] IL 入力契約 — IlExport API + リファレンス文書 ✓ (2026-07-18)
- `IlExport.Export(sources)` を公開: class metadata (fields/型/static、auto property 込み)、migration metadata の layout hash (il-spec §14 — instance field の名前:型列 FNV、static 変更で不変・改名で変化)、method body の IL (未対応は null)。luo は assembly 参照で直接消費
- `doc/il-reference.md` を新設: 全 IL ノードのカタログ (意味論は il-spec 参照、Lua render は参考情報)、API、C backend の義務、digest 検証の接続 (DigestKernels ↔ spike CONTRACT)
- 検証: IlExportTests 3 本 green (metadata/IL body 形/layout hash 性質/未対応 null)
- 判断: シリアライズ形式は定義しない (il-spec §1 の v0 決定どおり in-memory)。class 骨格 (ctor/accessor/field initializer) の IL 本文は T224 で拡張

### T227: nested class の診断化 ✓ (2026-07-18)
- class 内の class / record class 宣言を TCS1001 (NestedTypeDeclaration) で拒否。emit されず参照時に実行時 nil になる silent wrong-code (T215 で実測) の封鎖
- 検証: SubsetDiagnosticTests 追加 1 本 green、run-tests 全ゲート green (conformance baseline 影響なし)

### T212: [spike] AOT 性能上界の PC 測定 ✓ (2026-07-18)
- ../luo/spike/ に CONTRACT 準拠の 3 kernel × 5 変種 (native/aot-hash/aot-slot/interp/jit-off) を実装 (実装は Codex 委任、検証・適用・突き合わせは当方)。全変種 digest 一致 + tcs TinyC#→Lua とも 3 kernel bit 一致 — 「手書き C と transpiler 出力が同じ計算」の初実証で、digest harness (T215) が backend 間契約として実働
- 実測 (PC): aot-slot/native = 16.8x (sprite N=1024)、interp/native ≈ 19-36x。aot-slot は interp と大差なし — boxed TValue 表現自体がボトルネック
- 合否解釈: 「aot-* のみ予算落ち、native は通る」側 → **release-lowering は IL-native 表現 (struct / 連続配列) を採る** (il-design §8 の spike 待ち 2 項が決着: release class 表現 = native、Lua 側 struct 配列表現の需要は M5 実装時に再実測)
- spawn_churn の CONTRACT 未規定分は spike 実装解釈 (開始時充填) に統一し、tcs kernel を追随 (digest 9274159d で一致確認)
- 残課題: 実機 (Playdate) 測定は SDK 導入後。luo コミット済み (未 push)

### T218 第一マイルストーン: luoc IL→C backend 骨格 ✓ (2026-07-18)
- ../luo/luoc/ (net10.0 console、Transpiler を ProjectReference) が IlExport.Export を消費して C を生成。class=calloc struct、配列=型付き連続バッファ (spike 合否解釈の IL-native 表現)、strict f32 bit literal、null/bounds/除算 fault、未対応ノードは名前付きエラー (実装は Codex 委任、適用と受入検証は当方)
- 受入: digest kernel 3 種の C 変換を指定 flags (gcc -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard) でビルド・実行し、digest が Lua backend と 3/3 一致 (e8814b32 / 9274159d / 8bf97e09) — **M3 の核である 2 backend digest 一致が初成立**
- 判断: IlExport v0 に無い宣言情報 (method 型、配列要素型/長さ、field initializer) は暫定で Roslyn 再解析により補完 → 契約拡充を T228 起票。式・制御フローは IL のみから生成
- 残課題: T218 本体の全域化 (tasks.md 更新済み)、luo コミット済み (未 push)

### T228: IlExport 契約拡充 ✓ (2026-07-18)
- IlNewArray (要素型 + 長さ、il-spec §11 の固定長配列) を IL に追加 (Lua render は legacy 互換の {})。IlTable に要素型 metadata。IlMethodInfo に ReturnType/ParameterTypes、IlFieldInfo に Init (initializer の IL)。il-reference 更新
- T218 第一実装が Roslyn 再解析で補完していた宣言情報が IL 契約に載った
- 検証: IlExportTests +1 green、run-tests 全ゲート green (Lua 出力不変)

### T219: [M5 v1] データ struct のサブセット追加 ✓ (2026-07-18)
- field のみの struct を TCS1001 解除。値意味論は il-spec §10 の copy 地点 (代入/引数/return/値文脈読み) を IL builder が IlStructCopy 挿入で実装 — Lua は __tcs_scopy (metatable 無し plain table の shallow copy)、C backend は素の値代入に写る。place への部分書き込み (arr[i].X = v) は copy なし
- 診断の再構成: struct member (method/ctor/property 等) は StructMember、nested struct は NestedTypeDeclaration、record struct は従来どおり。struct 値が legacy fallback 経路へ流れる場合は警告 (silent wrong-code 防止の安全網)
- 検証: StructSemanticsTests 4 本 (copy 3 地点 + 診断)、**particles struct 版 digest = 8bf97e09 で SoA 版と一致** (値意味論込みで同一計算の実証)、run-tests 全ゲート green (analyzer-demo/CLI/analyzer テストの期待を StructMember へ更新)
- 判断: v1 はデータ struct のみ — メソッド付き値型は metatable 無し表現と両立せず、需要も particles 型 kernel が field アクセスのみで満たされるため。残りは T219b (P2)

### T218 第二マイルストーン: luoc の string/List/instance method 対応 ✓ (2026-07-18)
- luoc が string (immutable byte 列 + concat + 値別 WriteLine)、instance method、exact type-id の型 test、List<int>/<float> (連続 growable buffer)、IlNewArray/IlTable/IlForeachList/IlTernary に対応。T228 契約のみで生成 (第一実装の Roslyn 再解析を廃止)
- 受入: collision の実行値 (hit,miss,hit) が lua32 実行と一致、追加 harness も f32 印字表記以外一致 (同一 binary32。Lua 側 %.14g が shortest でない既知課題 → tasks の T218 残りに記載)、digest 回帰 3/3 維持。実装 Codex 委任、適用と再検証は当方
- 判断 (契約ギャップとして記録): ctor 本文なし → 宣言順 positional 扱い、local 型なし → initializer 推論、IlTable の array/List 種別なし → List 扱い。これらは T224 (class 骨格 IL 化) / T228 続きで契約側に載せる

### T224 前半: class 骨格の IL 契約化 (ctor / accessor) ✓ (2026-07-18)
- IlExport に IlCtorInfo (explicit ctor の params/型/本文 IL) と custom property accessor (get_/set_ 名の IlMethodInfo) を追加。luoc の「ctor 本文なし → positional 暫定」の契約ギャップを解消
- レビュー指摘の docs 反映: il-reference に GNU statement expression の移植性制約、il-spec §12 に実装定義の資源 fault 許容を明記
- 検証: IlExportTests +1 green、run-tests 全ゲート green

### Lua 側 f32 印字の shortest round-trip 化 ✓ (2026-07-18)
- __tcs_fstr (%.6g/%.8g/%.9g の round-trip 最短、il-spec §13) を導入し、builder が float 静的型既知の出力 4 地点 (WriteLine 引数 / 補間 hole / ToString / concat operand) で適用。C backend との stdout 完全一致の最後のギャップ (%.14g の余剰桁) を解消し、.NET の表示形 (1.0f → "1") とも一致
- 付随: IlBuild.Expressions が 800 行超 → Invocations 系を分割。struct テスト期待 3 件を新表示形へ更新
- 検証: run-tests 全ゲート green。object 型経由など動的値の tostring は対象外 (静的型でのみ適用 — 既知の限界として記録)

### T226: double / long のサブセット外化 ✓ (2026-07-18)
- TCS1001 に DoubleType / LongType (predefined type keyword) と DoubleLiteral (Token.Value is double — 1.5/1e3/1d を捉え 1.5f と整数を巻き込まない) を追加、analyzer 登録同期。TinySystem facade を MathF 委譲の float シグネチャへ移行 (dotnet 側単体テストと differential が f32 で計算し Lua32 と真正 parity)
- テスト・サンプル資産を f suffix へ移行、spec 例 10 件が Diag へ (baseline 更新、VariableInitializers1 の known-difference 登録は Diag 化に伴い削除)。FuzzGenerator の 32bit overflow 回避制約を撤廃し、wrap 跨ぎ for (i != end 条件) と全域 int32 を生成 (CS0220 は片側変数化で回避)
- 検証: run-tests 全ゲート green + deep fuzz 200 seeds 全一致。着手は Codex 委任だったが指示により中断し、診断コア/FuzzGenerator/facade/baseline を精査のうえ当方で完成・検証

### T225 第一スライス: ternary の statement 化 (local 初期化 / return) ✓ (2026-07-18)
- 評価順が構造的に不変な 2 位置 (local 初期化子・return 式) の条件式を IIFE から if 文へ。closure 割当を最頻出位置で除去 (examples 決定 2 の実施開始)
- 検証: 専用テスト (IIFE 不在 + 意味論) + run-tests 全ゲート green。代入 RHS 等の残り位置は評価順条件を tasks に明記

### T225 第一スライス: ternary の statement 化 (local 初期化 / return) ✓ (2026-07-18)
- 評価順が構造的に不変な 2 位置 (local 初期化子・return 式) の条件式を IIFE から if 文へ。closure 割当を最頻出位置で除去 (examples 決定 2 の実施開始)
- 検証: 専用テスト (IIFE 不在 + 意味論) + run-tests 全ゲート green。代入 RHS 等の残り位置は評価順条件を tasks に明記

### T225 第二スライス: switch 式の statement 化 + return 経路統一 ✓ (2026-07-18)
- switch 式 IIFE の形 (前置 local 列 + return する if 連鎖) を認識し、local 初期化 / 純 local 代入 / return 位置で文へ inline (代入位置は return→代入へ書換)。return 位置の else 無し chain は「値なし return」化で print の 0 値/1 値差が出るため IIFE 維持
- return 位置の statement 化を AddReturnStat に共通化し、method 式 body (VisitMethod)・operator/accessor (TryEmitReturnViaIl)・IlExport の 3 経路を統一
- 検証: 専用テスト + run-tests 全ゲート green

### T225 第三スライス: 値返し IIFE 形状の一般化 ✓ (2026-07-18)
- 形状認識を [locals + IlIf(arm=前置文*+末尾return, else?) + 末尾return?] へ一般化し、?. / ?? / TryGetValue / GetValueOrDefault の IIFE も statement 位置で文へ。末尾 return は else へ正規化、arm 前置文 (TryGetValue の out 代入) は保存
- 検証: 専用テスト (IIFE 不在 + hit/miss/?? 意味論) + run-tests 全ゲート green

### T224 後半: IlExport 契約の完備 (top-level 文 / operator) ✓ (2026-07-18)
- IlExportResult.TopLevel (top-level 文の IL) と operator (metamethod 名 __add 等の static IlMethodInfo) を収載。program 構造の IL 契約はこれで完備 — luoc はエントリポイント込みの全体を IL のみから生成できる
- 判断: legacy visitor のコード削除は行わない — 診断構文の出力経路と挙動不変の保険として保持し、fallback 面の縮小 (残構文の IL 化/診断化) のみ継続する
- 検証: IlExportTests +1 green、run-tests 全ゲート green

### T218 第三マイルストーン: ctor 本文と top-level 文の適用 (自作) ✓ (2026-07-18)
- luoc に T224 で完備した契約を消化: ctor を __ctor 合成 instance method、top-level 文を TopLevel.Main 合成 static method として注入し、facts/prototype/EmitMethod の既存機構をそのまま通す。RenderNew は ctor 呼び出しへ置換 (positional 暫定を廃止、ctor 無し + 引数ありは明示エラー)
- __tcs_fstr intrinsic (f32 印字改善で IL に増えた分) を luoc の to-string 経路へ写像。digest モードは unwrap して f32 bit を直接投入
- 検証: digest 回帰 3/3 + ctor/string field initializer/concat/top-level 混在サンプルの stdout が lua32 と完全一致

### T218 第四マイルストーン: 継承 (自作) ✓ (2026-07-18)
- luoc に継承を実装: DFS pre-order の範囲型 ID (`is` = type_id 範囲判定)、chain flatten による prefix layout 互換 upcast (中央 RenderCoerced で明示 cast)、同名再宣言の推論による type_id switch dispatcher (契約に virtual/override フラグ不要 — hiding/overload は診断済みなので同名 = override が健全)、base.M(self,...) 形は dispatcher を通さない直呼び、ctor は chain 最寄り (連鎖は base 初期化子が契約に無いため明示エラー → 契約拡張の需要として記録)
- 受入: 仮想 dispatch / base 呼び / is (base 真・派生真・無関係偽) / 継承 field / 非仮想メソッド経由の多態を含むサンプルの stdout が lua32 と完全一致、digest 回帰 3/3、gcc エラーなし

### Dict lowering の契約クリーン化 (T218 Dict の前提) ✓ (2026-07-18)
- ContainsKey を IlCall("Dict.ContainsKey")、TryGetValue を Dict.TryGet (found, value) の multi-return intrinsic + IlMultiAssign へ変更 — 「nil 比較 = 不在判定」の Lua 方言を IL から排除 (C backend は nil を型付けできない)。runtime に Dict.TryGet を追加
- 線形 IIFE (分岐なし前置 + 末尾 return) と無条件前置 (代入/呼び出し) も statement 化対象へ拡張 → TryGetValue も statement 位置で IIFE 不要に
- 判断: TryGetValue は runtime 必須へ (Dict.Remove 等と同格 — Dictionary は言語機能でなくライブラリ)。検証: run-tests 全ゲート green
