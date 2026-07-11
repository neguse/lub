# 現在の状態

## フェーズ: Phase 0-19 完了 / Analyzer PoC go 確定 (T122) / lub Haxe 代替検証へ (T125-T127)

### 完了済み (303テスト tcs / 10テスト analyzer / 477テスト lub3d)

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
**Docs**: Compact C# baseline / support matrix 分類整理 (T103,T115)
**T97**: 未対応構文を黙殺しない診断へ統一
**T101-T102**: CLI runtime prelude 埋め込み + Lua 5.5 バージョン検証
**T98-T100,T104-T105**: Core 正しさ穴埋め (top-level/base/null条件/default field/general for)
**T106-T107**: lambda block SourceMap 修正 + watch `--ref` 監視
**T108**: TinySystem C# facade と runtime mapping 同期
**T7**: samples/hello.cs, game.cs, inventory.cs E2E 検証
**T8**: samples/entity.cs 追加 + E2E 検証
**T9**: samples/statemachine.cs 追加 + switch default 順序修正
**T10**: samples/inventory.cs Dictionary 使用例へ拡張
**T11**: samples/collision.cs 追加 + struct 方針同期
**T109**: Dictionary `TryGetValue` out 代入 + default 値
**T110**: LINQ `Count()` / `ToDictionary()` Core 実装
**T111**: List/Dictionary null 保存の禁止診断
**T112**: HotReload mtime の shell 非依存化
**T113**: Lua CMake ビルドの platform 分岐 + stale rebuild
**T114**: Lua stack trace の SourceMap 注釈 CLI
**T116**: README/objective/q/current 同期
**T117**: CLI 引数 UX (`--help`, `--version`, unknown/missing option)
**T118**: dependency pin / lock file / publish runtime 同梱
**T119**: 標準ライブラリ小拡張 (`IndexOf`/`Join`/`Pow`/`Sort`/LINQ 追加)
**T120**: ユーザー定義 `struct` / `record struct` を TCS1001 未対応診断として確定
**T121**: 外部エンジン連携サンプルを engine agnostic な `--ref` 例へ置換
**T122**: Rider リアルタイム警告向け Roslyn Analyzer PoC (`tcs check` / 共有診断化 / core API allowlist / CI / nupkg consumer / InspectCode headless / Rider 実機確認 go)

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
| field / auto property default | 型別 default (`0`/`false`/`nil`) |
| enum | 定数テーブル |
| top-level statements | 型定義後に Lua chunk へ出力 |
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
| s?.Method / list?.Method / dict?.Method | 型別 runtime mapping + nil チェック |
| i++ (statement) | i = i + 1 |
| cast | 透過 (型消去) |
| new List<T>{...} | {…} (sequence table) |
| list[i] | list[i+1] (0→1 indexed) |
| list.Count | #list |
| list.Where/Select/Count/ToDictionary/... | List.Method(list, fn) |
| new Dictionary<K,V>{...} | {…} (hash table) |
| dict[key] | dict[key] |
| dict.ContainsKey(k) | (dict[k] ~= nil) |
| dict.TryGetValue(k, out v) | out 代入 + bool |
| collection null storage | TCS1003 warning |
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
| new T { X = v } | IIFE .new() + field 代入 |
| new RefT { key = v } (--ref 型) | plain table `{key = v}` |
| var (x, y) = p | local x, y = p.X, p.Y |
| record == record | __eq メタメソッド |
| obj.ExtMethod() | ExtClass.ExtMethod(obj) |
| base.Method() | Base.Method(self, ...) |
| $"{val:F2}" | string.format("%.2f", val) |

### TinySystem ランタイム (runtime/tinysystem.lua)
- List: new, Add, Remove, Count, Contains, IndexOf, Sort
- LINQ: Where, Select, Any, All, First, FirstOrDefault, Last, LastOrDefault, OrderBy, OrderByDescending, Take, Skip, Min, Max, Sum, Count, ToList, ToDictionary
- Dict: new, Add, Remove, ContainsKey, Count, Keys, Values
- Math: Min, Max, Clamp, Abs, Floor, Ceil, Sqrt, Sin, Cos, Atan2, Pow, PI
- Random: Next, NextFloat, Range
- String: Contains, Replace, StartsWith, EndsWith, Trim, Substring, Split, IndexOf, Join

### TinySystem C# facade (TinySystem/)
- `TinySystem.Random`, `TinySystem.Math`, `TinySystem.String`, `TinySystem.List`, `TinySystem.Dict`
- Transpiler の Roslyn compilation は TinySystem.dll を参照し、facade static call を Lua runtime global (`Random.*`, `Math.*`, etc.) へ変換する
- `System.Action` / `System.Func` は標準 BCL 型をそのまま使い、TinySystem facade の delegate 引数にも利用する

### CLI
- `dotnet run --project Transpiler -- input1.cs [input2.cs ...] [--ref ref.cs] [-o output.lua] [--entry Class] [--watch] [--no-runtime]`
- `--entry <Class>`: 出力末尾に `return <Class>` を追記し、require/dofile で class table を返す Lua module にする
- `--no-naming-check`: C# naming convention warning を抑制する (host の wire format が lowerCamel/snake_case の場合)。transpile / check 両対応
- `dotnet run --project Transpiler -- check input1.cs [input2.cs ...] [--ref ref.cs]`: Lua を出力せず、診断だけを返す CI 向けチェック
- `--help` / `--version`
- 生成 Lua はデフォルトで TinySystem runtime prelude を埋め込み、`--no-runtime` で bare 出力に戻せる
- `--sourcemap`: runtime prelude 埋め込み時も Lua 行番号を offset 済みで JSON 出力
- `--map-stacktrace out.lua.map [trace.txt]`: Lua stack trace の `file.lua:line:` を C# `file.cs:line` で注釈する
- `--watch` / `-w`: 入力/`--ref` ファイル変更監視 + 自動再トランスパイル (FileSystemWatcher + 100msデバウンス)
- エラー時: ソース位置付きでエラーメッセージを stderr に出力
- 警告時: TCS1001/TCS1002/TCS1003 などの準拠診断を stderr に出力

### Lua 5.5 build
- CMake は Linux / Windows / macOS / iOS-family / Emscripten / BSD / generic Unix で Lua compile definitions と system libs を分岐
- `run-tests.sh` / `run-tests.ps1` は Lua binary が未生成、CMake 入力より古い、または `Lua 5.5` でない場合に再ビルドし、dotnet tests、sample `tcs check`、analyzer-demo expected diagnostics、analyzer nupkg consumer / severity override 検証を実行する
- `.github/workflows/ci.yml` は submodule checkout → .NET 10 setup → `run-tests.sh` を実行する

### 依存・配布
- `deps/lua` は git submodule commit で固定し、更新時は `Lua 5.5` version check を必須にする
- NuGet package は `.csproj` に明示 version を pin し、`packages.lock.json` で transitive dependency も固定する
- `dotnet publish Transpiler/Transpiler.csproj -c Release -o <dir>` は `runtime/tinysystem.lua` を publish 出力の `runtime/` 配下へ同梱する

### HotReload (runtime/tinysystem.lua)
- `HotReload.swap(filepath)`: dofile → グローバルテーブルの深い更新 (既存インスタンス状態保持)
- `HotReload.watch(filepath)`: ファイル変更監視 (host が `HotReload.mtime` を注入した場合のみ)
- `HotReload.update()`: フレームごとのポーリング (0.5秒間隔)
- `HotReload.mtime(filepath)`: デフォルトは shell 非依存の no-op (`nil`)、engine 側 `fs.mtime()` 等を代入する

### 外部 API / --ref サンプル
- `samples/host_api_game.cs` + `samples/host_api_stub.cs` を engine agnostic な参照専用 stub 例として追加
- `--ref` source は型チェックだけに使い、Lua 出力には含めない。実行時は host / engine 側が同名 Lua table を注入する
- 旧 `samples/lub3d_hello.cs` は削除し、lub3d 直対応前提の sample は tcs 本体から外した

### TinyCSharpGen (lub3d Generator 参考実装)
- 現方針では lub3d 直対応を tcs 本体の主目的にせず、外部 API facade / `--ref` の参考実装として扱う
- `lub3d/Generator/TinyCSharp/TinyCSharpGen.cs` — ModuleSpec → C# interface 生成
- BindingType → C# 型マッピング完了
- `lub3d/Generator/Program.cs` — `--tcs-output-dir` オプション追加
- テスト46件 (lub3d Generator.Tests)
- 全14モジュール C# ファイル生成・コンパイル検証済み (5,443行、0エラー)
- dep 型 stub 自動生成、C# 予約語エスケープ、完全修飾型名解決

### Analyzer PoC (T122, go 確定)
- Rider 実機 (Windows) で TCS1001 x5 / TCS1002 x1 / TCS1003 x1 の editor inspection 表示と `.editorconfig` severity=error の表示反映を確認済み (`q.md` Q12 → Resolved)
- `samples/analyzer-demo/Program.cs` は analyzer build と `tcs check` の両方で TCS1001 x5 / TCS1002 x1 / TCS1003 x1 を検出する
- `verify-inspectcode.sh` / `.ps1` で InspectCode 2026.1.3 headless の ProjectReference / local nupkg `PackageReference` consumer / severity override を再確認できる
- `verify-rider-prechecks.sh` / `.ps1` で pre-check summary、`open-rider-demo.sh` / `.ps1` (`-NoPrecheck` あり) で Rider 起動
- `.ps1` は pwsh 7 の read-only 自動変数 (`$IsWindows` / `$IsMacOS`) と衝突する変数名を使わない。`verify-rider-scripts.sh` の検証は `run-tests.sh` (Linux/CI) のみで行い、`run-tests.ps1` は bash を使わない
- `Math` / `string` / `List<T>` / `Dictionary<K,V>` / LINQ は supported member allowlist を持ち、`Math.Log`, `List.Reverse`, `Enumerable.Single` などを TCS1002 として検出する

### 次のタスク
- `doc/tasks.md` の推奨着手順に従い、タスク番号順には進めない
- P0: lub (`../lub`, readonly) の Haxe 代替検証。ギャップ分析は `doc/lub-gap-analysis.md` (T125 完了)
- 着手順: T126 (00_hello 相当) → T127 (hot reload) → T131 (multi-return)。T128/T129/T130 は完了
- P2: T123 analyzer release 手順の README 化 (縮小)
- T124 はクローズ: 診断一致は run-tests の恒常ゲート (analyzer-demo expected diagnostics / nupkg consumer 検証) で守る

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
