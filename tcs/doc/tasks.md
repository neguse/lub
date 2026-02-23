# タスクリスト

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
