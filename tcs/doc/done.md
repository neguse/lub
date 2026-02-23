# 完了ログ

### T1-T6: Phase 0 プロジェクトセットアップ ✓ (2026-02-23)
- git init、.gitignore、.NET 10 ソリューション (Transpiler / Transpiler.Tests / TinySystem)
- deps/lua submodule 追加、Lua 5.5 ビルド確認
- 最小トランスパイラ実装 (LuaEmitter): クラス、staticメソッド、リテラル、算術・比較・論理演算、if/else/elseif、while、ローカル変数、代入
- セマンティックテスト基盤 (TestHelper.TranspileAndRun): C# → トランスパイル → Lua VM実行 → 結果検証
- スモークテスト 10件、全パス
- run-tests.sh、.githooks/pre-commit
- よかったこと: セマンティックテスト方式は出力形式に依存しないため堅牢。最初から Lua VM を叩くテストにしたのは正解
- 判断: トップレベル `return` は出力しない方針にした。モジュール化は後で別途対応
- 残課題: for/foreach、クラスインスタンス化、プロパティ、enum 等は Phase 2-4 で対応
