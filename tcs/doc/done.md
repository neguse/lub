# 完了ログ

### T1-T6: Phase 0 プロジェクトセットアップ ✓ (2026-02-23)
- git init、.gitignore、.NET 10 ソリューション (Transpiler / Transpiler.Tests / TinySystem)
- deps/lua submodule 追加、Lua 5.5 ビルド確認
- 最小トランスパイラ実装 (LuaEmitter): クラス、staticメソッド、リテラル、算術・比較・論理演算、if/else/elseif、while、ローカル変数、代入
- セマンティックテスト基盤 (TestHelper.TranspileAndRun): C# → トランスパイル → Lua VM実行 → 結果検証
- スモークテスト 10件、全パス
- run-tests.sh、.githooks/pre-commit
- よかったこと: セマンティックテスト方式は出力形式に依存しないため堅牢
- 判断: トップレベル `return` は出力しない方針にした。モジュール化は後で別途対応
- 残課題: for/foreach、クラスインスタンス化、プロパティ、enum 等は Phase 2-4 で対応

### T12-T34: Phase 2-4 トランスパイラ大幅拡充 ✓ (2026-02-23)
- for ループ (simple for → Lua numeric for 最適化付き)
- foreach ループ (ipairs / pairs 自動判定)
- クラスインスタンス化 (new, コンストラクタ, フィールド初期化)
- auto property → フィールド、カスタム getter/setter
- enum 定義 (明示的値指定対応)
- ラムダ式 (simple/parenthesized, expression/block body)
- 三項演算子 (IIFE で falsy 安全)
- 文字列補間 (tostring + .. 連結)
- 継承 + base() コンストラクタチェーン
- 同クラス内メソッド呼び出し/暗黙 this の自動解決
- instance method → `:` 呼び出し自動判定
- LuaEmitter を3ファイルに partial class 分割 (各200行台)
- ゲームスクリプト統合テスト: Entity/HP、状態遷移、衝突判定
- テスト 10 → 40
- よかったこと: Roslyn の SemanticModel でメソッドの static/instance 判定ができるので `:` と `.` の切り替えが正確
- 判断: syntax tree ベースで走査。IOperation ベースへの移行は規模が大きくなったら検討
- 残課題: switch式、record、コレクション初期化、LINQ
