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
