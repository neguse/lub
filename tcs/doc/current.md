# 現在の状態

## フェーズ: Phase 0-7, T48 完了

### 完了済み (67テスト全パス)

**Phase 0**: プロジェクトセットアップ (T1-T6)
**Phase 2-4**: トランスパイラ中核 (T12-T34)
**Phase 5**: ラムダ (T33-T34)
**Phase 6**: TinySystem Luaランタイム (T35-T40)
**Phase 7**: コレクション/LINQ トランスパイル統合 (T41-T43)
**Phase 9部分**: CLI (T48)

### 実装済みの C# → Lua マッピング
| C# 構文 | Lua 出力 |
|---------|---------|
| class + new | table + metatable + .new() |
| inheritance + base() | metatable チェーン |
| static method | Class.Method() |
| instance method | obj:Method() |
| field / auto property | table field |
| custom property | get_/set_ メソッド |
| enum | 定数テーブル |
| if/else/elseif | if/elseif/else |
| for (i=0; i<n; i++) | for i=0,n-1 |
| foreach | ipairs/pairs |
| while | while do end |
| lambda | function() end |
| ternary | IIFE |
| string interpolation | tostring + .. |
| ?? | or |
| i++ (statement) | i = i + 1 |
| cast | 透過 (型消去) |
| new List<T>{...} | {…} (sequence table) |
| list[i] | list[i+1] (0→1 indexed) |
| list.Count | #list |
| list.Where/Select/... | List.Method(list, fn) |
| new Dictionary<K,V>{...} | {…} (hash table) |
| dict[key] | dict[key] |
| dict.ContainsKey(k) | (dict[k] ~= nil) |

### TinySystem ランタイム (runtime/tinysystem.lua)
- List: new, Add, Remove, Count, Contains, IndexOf
- LINQ: Where, Select, Any, All, First, FirstOrDefault, OrderBy, Min, Max, Sum, ToList
- Dict: new, Add, Remove, ContainsKey, Count, Keys, Values
- Math: Min, Max, Clamp, Abs, Floor, Ceil, Sqrt, Sin, Cos, Atan2, PI
- Random: Next, NextFloat, Range

### CLI
- `dotnet run --project Transpiler -- input.cs [-o output.lua]`

### 次のタスク
- T49: 複数ファイル・名前空間解決
- T50: エラーメッセージ改善
- T51+: lub3d Generator 統合 (TinyCSharpGen)

### コミット履歴
1. `6d02c3e` feat: T1-T6 Phase 0 プロジェクトセットアップ
2. `c0f2213` feat: T12-T34 トランスパイラ大幅拡充
3. `855ca0b` feat: T31 継承・base()コンストラクタ + ゲームスクリプト統合テスト
4. `5e897bb` refactor: LuaEmitter を3ファイルに分割
5. `b27515b` docs: Phase 2-4 完了ログ・進捗更新
6. `c4c12dd` feat: T35-T40 TinySystem Luaランタイム + テスト13件
7. `ef8f0c0` feat: T48 CLI エントリポイント + グローバルスコープ出力
