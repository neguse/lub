# 現在の状態

## フェーズ: Phase 2-4 完了 → Phase 6-7 (TinySystem + コレクション) へ

### 完了済み
- T1-T6: プロジェクトセットアップ
- T12-T34: トランスパイラ中核機能
  - リテラル、演算子、変数、if/else/elseif、while、for、foreach
  - クラス、コンストラクタ、継承、enum、プロパティ
  - ラムダ、三項演算子、文字列補間
  - instance/static メソッド自動判定
- テスト 40件パス
- LuaEmitter 3ファイル分割済み

### 実装済みの C# → Lua マッピング
| C# 構文 | Lua 出力 | テスト |
|---------|---------|-------|
| class + new | table + metatable + .new() | ✓ |
| inheritance + base() | metatable チェーン | ✓ |
| static method | Class.Method() | ✓ |
| instance method | obj:Method() | ✓ |
| field / auto property | table field | ✓ |
| enum | 定数テーブル | ✓ |
| if/else/elseif | if/elseif/else | ✓ |
| for (i=0; i<n; i++) | for i=0,n-1 | ✓ |
| foreach | ipairs/pairs | ✓ |
| while | while do end | ✓ |
| lambda | function() end | ✓ |
| ternary | IIFE | ✓ |
| string interpolation | tostring + .. | ✓ |
| ?? | or | ✓ |

### 次のタスク
- T35-T40: TinySystem 標準ライブラリ
- T41-T43: コレクション・LINQ トランスパイル
- T48: CLI エントリポイント
