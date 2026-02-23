# 現在の状態

## フェーズ: Phase 0-19 完了

### 完了済み (227テスト tcs / 477テスト lub3d)

**Phase 0**: プロジェクトセットアップ (T1-T6)
**Phase 2-4**: トランスパイラ中核 (T12-T34)
**Phase 5**: ラムダ (T33-T34)
**Phase 6**: TinySystem Luaランタイム (T35-T40)
**Phase 7**: コレクション/LINQ トランスパイル統合 (T41-T43)
**Phase 8**: switch/interface/string/null条件/is (T23,T32,T38,T46,T47)
**Phase 9**: CLI + 複数ファイル + エラーメッセージ (T48,T49,T50)
**Phase 10.5**: 標準ライブラリ セマンティックテスト補完 (T62-T69)
**Phase 11**: record型 + パターンマッチング拡充 (T70-T73)
**Phase 12**: リテラル対応拡充 (T74-T80)
**Phase 13**: Null Safety 強化 (T81-T84)
**Phase 10 (T51-T61)**: TinyCSharpGen + E2E コンパイル検証 + --ref + サンプルゲームスクリプト + ホットリロード + watch
**Phase 14**: プロパティパターン / with式 / Deconstruct / record Equals (T85-T88)
**Phase 15**: Extension methods (T89)
**Phase 16**: コレクション初期化拡充 (T90)
**Phase 17**: 文字列補間 フォーマット指定子 (T91)
**Phase 18**: Lua出力最適化 (T92-T94)
**Phase 19**: ソースマップ (T95-T96)

### 実装済みの C# → Lua マッピング
| C# 構文 | Lua 出力 |
|---------|---------|
| class + new | table + metatable + .new() |
| record | table + metatable + .new() (positional) |
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
| do-while | repeat until not(cond) |
| continue | goto _continue_N |
| static field | ClassName.field |
| const field | ClassName.CONST |
| new T[] { ... } | {…} (Lua table) |
| arr.Length | #arr |
| lambda | function() end |
| ternary | IIFE |
| string interpolation | tostring + .. |
| ?? | or |
| ??= | if x == nil then x = v end |
| ?. / ?[] | IIFE nil チェック |
| i++ (statement) | i = i + 1 |
| cast | 透過 (型消去) |
| new List<T>{...} | {…} (sequence table) |
| list[i] | list[i+1] (0→1 indexed) |
| list.Count | #list |
| list.Where/Select/... | List.Method(list, fn) |
| new Dictionary<K,V>{...} | {…} (hash table) |
| dict[key] | dict[key] |
| dict.ContainsKey(k) | (dict[k] ~= nil) |
| switch statement | if-elseif-else end |
| switch expression | IIFE if-elseif-else |
| interface | 透過 (出力なし) |
| string + string | .. (連結演算子) |
| str.Length | #str |
| str.Method() | String.Method(str) |
| is Type name | getmetatable + local 束縛 |
| is > 0 / is 1 or 2 | 比較式展開 |
| is null / is not null | == nil / ~= nil |
| int? / HasValue / Value | nil 透過 |
| default / default(T) | 型別デフォルト値 |
| 0xFF / 0b1010 / 1_000 | リテラル変換 |
| 'A' / @"..." / """...""" | 文字列変換 |
| is { X: > 0 } | プロパティ比較展開 |
| p with { X = 10 } | IIFE shallow copy |
| var (x, y) = p | local x, y = p.X, p.Y |
| record == record | __eq メタメソッド |
| obj.ExtMethod() | ExtClass.ExtMethod(obj) |
| $"{val:F2}" | string.format("%.2f", val) |

### TinySystem ランタイム (runtime/tinysystem.lua)
- List: new, Add, Remove, Count, Contains, IndexOf
- LINQ: Where, Select, Any, All, First, FirstOrDefault, OrderBy, Min, Max, Sum, ToList
- Dict: new, Add, Remove, ContainsKey, Count, Keys, Values
- Math: Min, Max, Clamp, Abs, Floor, Ceil, Sqrt, Sin, Cos, Atan2, PI
- Random: Next, NextFloat, Range
- String: Contains, Replace, StartsWith, EndsWith, Trim, Substring, Split

### CLI
- `dotnet run --project Transpiler -- input1.cs [input2.cs ...] [--ref ref.cs] [-o output.lua] [--watch]`
- `--watch` / `-w`: ファイル変更監視 + 自動再トランスパイル (FileSystemWatcher + 100msデバウンス)
- エラー時: ソース位置付きでエラーメッセージを stderr に出力
- 警告時: 未対応構文を stderr に出力

### HotReload (runtime/tinysystem.lua)
- `HotReload.swap(filepath)`: dofile → グローバルテーブルの深い更新 (既存インスタンス状態保持)
- `HotReload.watch(filepath)`: ファイル変更監視 (mtime ベース)
- `HotReload.update()`: フレームごとのポーリング (0.5秒間隔)

### TinyCSharpGen (lub3d Generator 統合)
- `lub3d/Generator/TinyCSharp/TinyCSharpGen.cs` — ModuleSpec → C# interface 生成
- BindingType → C# 型マッピング完了
- `lub3d/Generator/Program.cs` — `--tcs-output-dir` オプション追加
- テスト46件 (lub3d Generator.Tests)
- 全14モジュール C# ファイル生成・コンパイル検証済み (5,443行、0エラー)
- dep 型 stub 自動生成、C# 予約語エスケープ、完全修飾型名解決

### 次のタスク
- Phase 1 (T7-T11) ユースケース検証サンプル

### コミット履歴
1. `6d02c3e` feat: T1-T6 Phase 0 プロジェクトセットアップ
2. `c0f2213` feat: T12-T34 トランスパイラ大幅拡充
3. `855ca0b` feat: T31 継承・base()コンストラクタ + ゲームスクリプト統合テスト
4. `5e897bb` refactor: LuaEmitter を3ファイルに分割
5. `b27515b` docs: Phase 2-4 完了ログ・進捗更新
6. `c4c12dd` feat: T35-T40 TinySystem Luaランタイム + テスト13件
7. `ef8f0c0` feat: T48 CLI エントリポイント + グローバルスコープ出力
8. `9c7f3ca` docs: 全フェーズ完了ログ・進捗更新
9. `6d563b6` feat: T41-T43 List/Dictionary/LINQ トランスパイル統合
10. `6b46c0f` feat: T23,T32,T38,T46,T47 switch/interface/string/null条件/is演算子
11. `c02b3c8` feat: T49 複数ファイル・名前空間解決 + float リテラル suffix 除去
12. `440c31e` feat: T50 エラーメッセージ改善 + TranspileResult 診断API
