# 未解決の質問

開発を進める前に確認したい事項。回答がつき次第 resolved に移動する。

---

### Q12: Rider 上の Roslyn Analyzer PoC は go か no-go か
- `samples/analyzer-demo/analyzer-demo.csproj` を Rider で開き、`Program.cs` 上で TCS1001/TCS1002 が通常の inspection / squiggle として表示されるか確認する
- 期待値: TCS1001 x4 (`StructDeclaration`, `LocalFunctionStatement`, `TryStatement`, `ThrowStatement`) / TCS1002 x1 (`System.IO.File.ReadAllText`)
- `.editorconfig` の `dotnet_diagnostic.TCSxxxx.severity` 変更が Rider 表示に反映されるか確認する（build への反映は `run-tests` で検証済み）
- JetBrains InspectCode 2026.1.3 headless 実行では TCS1001 x4 / TCS1002 x1 が SARIF に出ることを確認済み。再確認用 script: `samples/analyzer-demo/verify-inspectcode.sh`
- 詳細な実機確認手順は `samples/analyzer-demo/README.md` に記録済み
- 結果記録テンプレートは `samples/analyzer-demo/RIDER_VERIFICATION_TEMPLATE.md`
- go の場合: analyzer package / `tcs check` / CI を正式な準拠チェック導線として product task に分解する
- no-go の場合: Rider plugin、external tool、または CLI watcher 連携などの代替案を検討する

---

## Resolved

### Q11: tcs 準拠チェック → 標準 C# ツールキット上の linter / analyzer が必要
- C# をフロントエンドにする以上、トランスパイル時だけでなく IDE / `dotnet build` / CI 上で tcs 準拠性を確認できる必要がある
- 主目的は Rider などの IDE で書いている最中にリアルタイム警告を出すこと
- Roslyn Analyzer PoC と `tcs check` CLI は実装済み
- 診断ルールは `Shared/TinyCsComplianceFacts.cs` で analyzer / transpiler warning / `tcs check` が共有し、Compact C# baseline / TinySystem API / Out of scope API の判定がずれないようにする
- 残りの判断は Q12 の Rider 実機 go / no-go 確認

### Q10: 開発軸 → Compact C# baseline
- C# 側の仕様準拠度と標準ライブラリ網羅性を棚卸し軸にする
- ただし全 C# / 全 BCL 実装は目的にしない
- モダンな .NET / C# 開発体験を満たす最小ラインに留める
- C# ベースだが、Go のようなコンパクトさと説明しやすさを狙う

### Q9: 外部エンジン直接対応の範囲 → tcs 本体では前提にしない
- lub3d 側のプロジェクト方針が変わっているため、tcs 本体が lub3d を直接サポートする前提は置かない
- tcs のコアは engine agnostic な C# サブセット → Lua 5.5 トランスパイラ
- 外部エンジン連携は必要になった時点で stub / adapter / generator のどれが妥当かを個別に判断する

### Q1: .NET バージョン → .NET 10 / C# 14
`net10.0` / `LangVersion 14`。

### Q4: lub3d API の TinyC# 側公開範囲 → 旧方針: 全モジュール、Generator基盤に乗せる

2026-06 時点では Q9 の通り、tcs 本体の直接対応前提から外す。
以下は過去の検討メモとして残す。

- 最終的に全 lub3d モジュール (App, Gfx, Audio, Imgui, Glm, ...) の interface を生成する
- lub3d の Generator パイプラインに `TinyCSharpGen` を追加する設計
  - `ModuleSpec` (既存の言語中立IR) → `TinyCSharpGen.Generate(spec)` → `.cs` interface ファイル
  - `LuaCatsGen` と並列の出力バックエンド
  - `BindingType` → C#型名のマッピング (`Int→int`, `Float→float`, `Struct→class名`, etc.)
- 段階的に: まず App + Gfx + Glm で検証、その後全モジュールに展開

### Q6: struct の値セマンティクス → TCS1001 未対応診断
- ユーザー定義 `struct` / `record struct` は現時点では Lua へ class 相当に lowering せず、TCS1001 の未対応構文診断として扱う
- 値セマンティクスが必要なサンプルが出るまでは `class` / `record class` で代替する
- Vec2/Vec3 等は外部エンジン/数学ライブラリ側の型として提供される可能性があるため、ユーザー定義 struct の必要性はサンプルから逆算する
- 将来の最適化フェーズで必要に応じて追加

### Q7: テストの粒度 → セマンティックテスト
- C#入力 → トランスパイル → Lua VM実行 → 結果検証
- 出力文字列の完全一致は使わない（壊れやすい）
- `TranspileAndRun(input, expr)` ヘルパーでテストを書く

### Q8: TinySystem の配布 → NuGet は当面やらない
- ProjectReference のみで進める

### Q2: 名前空間マッピング → Lua table namespace
- `namespace Foo.Bar` は `Foo = Foo or {}; Foo.Bar = Foo.Bar or {}` のような nested table として表現する
- class 名は namespace table 配下へ配置し、namespace 未指定の型は従来どおり global table へ出す
- 複数入力ファイルは同一 Roslyn Compilation で解決し、出力は入力順にまとめる

### Q3: エントリポイント → Lua chunk + 明示呼び出し
- top-level statements は型定義を emit した後に Lua chunk として出力する
- class/static method は自動実行せず、host やテストが `Class.Method()` を明示的に呼ぶ
- CLI 生成 Lua は TinySystem runtime prelude をデフォルトで埋め込み、`--no-runtime` で host 供給へ戻せる
- HotReload は `HotReload.swap(path)` と host 注入 mtime による `watch/update` を使う

### Q5: CLI 設計 → 変換・watch・check・SourceMap 後処理を実装済み
- 基本形: `tcs input.cs [input2.cs ...] --ref ref.cs -o output.lua`
- `tcs check input.cs [input2.cs ...] --ref ref.cs`: Lua を出力せず、compile error と TCS1001/TCS1002/TCS1003 準拠診断だけを返す
- `--watch` / `-w`: 入力と `--ref` ファイルを監視して再トランスパイル
- `--sourcemap`: `output.lua.map` を生成し、runtime prelude offset 済みの Lua 行番号を出す
- `--map-stacktrace output.lua.map [trace.txt]`: Lua stack trace を C# ファイル:行番号で注釈する
- `--help`, `--version`, unknown option, missing option value の UX は T117 で実装済み
