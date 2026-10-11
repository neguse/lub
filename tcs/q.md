# 未解決の質問

開発を進める前に確認したい事項。回答がつき次第 resolved に移動する。

---

## Open

### Q13: host 組込みの API/glue 構成 — script runtime ABI をどこに置くか
- エンジンが公開する C API は 1 本とし、tier ごとに glue を生成する構図を
  仮置きしている: dev は Lua binding (スタック marshalling、struct は
  table ↔ C struct 詰め替え)、release は tcs2c からの直接呼び出し
  (ゼロコピー)。lub3d の Generator (単一 IR → CBindingGen / LuaCatsGen)
  と同型で、contract-gated に整合
- host → script 方向 (init/update/reload 駆動) を tier 非依存にする薄い
  「script runtime ABI」(entrypoint + callback 登録) を定義するか、それは
  tcs 本体でなく host 側 (lub 等) の責務か — 実利用 (UC1) の接続時に判断
- 判断材料: hot-reload-design.md §1 の UC 表、Q9 (エンジン直接対応は
  前提にしない)

## Resolved

### Q16: 名前の写し方 → user のコードは C# の名前のまま (2026-10-12)
- 今は `Shared/LuaNaming.cs` の snake_case 化がすべての名前を作り、大文字小文字の情報を落とす (`Rng` / `rng` / `RNG` → `rng`、`XMLValue` → `x_m_l_value`)。IL に文字列で載り、tcs2c の C 名と lub-gen の host 名も同じ関数を使う
- user の型の member は C# の識別子をそのまま Lua / C の名前にする。同じ型の中の同じ綴りは C# が禁じる (CS0102 / CS0082) ので写像は一対一になり、名前の衝突の診断 (#111、#109) が要らなくなる。error と stack trace にも C# の名前が出る
- `--ref` の host 型の member は今の snake_case の規則で固定する (lub-gen と C ABI のため。user の規則の変更に巻き込まない)
- overload は、overload があるときだけ名前に引数の型を付ける。2 つ目を足すと既存の名前が変わるが、呼び出し側は作り直され、reload 前に作った関数値は Q17 で保証しないので転送はしない。entry の callback など host が名前で呼ぶものの overload は禁じる
- entry の callback は C# の名前 (`OnFrame` 等) で host に見せる
- Lua の層は利用者に開かない (lub の `docs/log/2026-10-12-close-lua-layer-plan.md`)。raw Lua から見た読みやすさは基準にしない
- どの写像でも入れる診断: `__` で始まる識別子、非 ASCII の識別子、`new` / `ctor` などの予約 key、型名と namespace 名の `_` (入れ子の型を `_` で連結した平らな global にするため)
- 既知のリスク: overload の追加で名前が変わるとき、増分 session と reload chunk が呼び出し側の作り直しと旧 key の削除をどう分類するか (live か restart か) は実装で確かめる

### Q17: reload で残す状態 → `[TcsHot]` の型を tag で引き継ぐ (2026-10-12)
- reload で状態を残す型は hot、それ以外は cold。project の既定値を csproj の `<TcsHot>` に書き、型の `[TcsHot]` / `[TcsHot(false)]` で上書きする。lub と tcs は csproj を評価しないので、`<TcsHot>` は条件なしで直接書いたものだけを読み、それ以外は error にする (IDE の analyzer と既定値を食い違わせない)
- hot の型の field (static を含む) は protobuf の field 番号のような tag を必ず持つ (`[Tag(1)] int hp;`)。reload は tag で値を引き継ぐので、field と型を改名しても状態が残る。同じ型の中の tag の重複と、消した tag の再利用は error
- 推移的: hot の型の field の型は、hot の型か、中身ごと移せる型 (数値、string、enum、struct、それらの List / Dictionary、host の handle) に限る。関数値 (`Action` / `Func`、delegate) の field は持てない
- cold の型は、形 (field) が変わったら restart
- reload の前に作った関数値 (lambda、method group、event handler) は reload に耐えることを保証しない。古い本体のまま動き、改名・削除された member を呼べば落ちる
- 雛形 (lub の templates/game) は `<TcsHot>` を書いておく

### Q15: 継承 → class / record の継承は外す。多態は interface と型 switch (2026-10-12)
- Go と同じ構成にする: 多態は interface の実装と型による分岐 (`switch (e) { case Hit h: ... }`)、組み合わせは合成 (record / class が別の型を field に持つ) で書く
- class の継承 (`class D : B`、`virtual` / `override` / `base`、隠蔽) と record の継承 (`record D(...) : B(...)`) は対象外にする。lub と tcs の sample、lub の cs-lib に使う所は無い
- 名前の写し方 (Q16)、hot reload (metatable の連鎖と基底の後張り)、C backend (class の vtable) が単純になり、継承をまたぐ名前の取り違えや隠蔽の不具合が書けなくなる
- まず build を止める TCS1001 にする (実装はそのあと消す)
- interface は default method (本体) も static member も持たない。共通の処理は interface に対する拡張メソッドか、static class に置く

### Q14: 開発の優先 → hot reload (2026-10-12)
- 機能は hot reload と揃うものを優先し、揃わないもの (スカラー置換・inline 展開など、実行中の closure が古い呼び出し規約を持つもの) は優先度を落とす
- 新しい言語機能 (overload、入れ子の型、out、tuple) の前に、名前の写し方 (Q16) を決める

### Q12: Rider 上の Roslyn Analyzer PoC → go (2026-07-12)
- Windows 10.0.26200 / .NET SDK 10.0.301 / JetBrains Toolbox の Rider で `samples/analyzer-demo/analyzer-demo.csproj` を実機確認
- editor inspection に期待どおり TCS1001 x5 (`Unsupported TinyC# syntax`) / TCS1002 x1 (`Unsupported TinyC# API`) / TCS1003 x1 (`Unsupported TinyC# collection null`) が表示された
- `.editorconfig` の `dotnet_diagnostic.TCSxxxx.severity = error` 変更が Rider 表示にも error として反映された
- InspectCode headless 検証（ProjectReference / PackageReference consumer / severity override）は `verify-inspectcode.sh` / `.ps1` で再確認できる
- go に伴い T123 (analyzer package 正式導線) / T124 (診断一致の継続検証) を `doc/tasks.md` に追加

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

### Q2: 名前空間マッピング → namespace 修飾名の global
- `namespace Foo.Bar` の型 `C` は `Foo_Bar_C` の global として出す (nested table にはしない。registry / hot reload の type id、IL 契約、C backend の記号が平らな名前のまま揃う)
- namespace 未指定の型は従来どおり simple 名の global へ出す
- `--ref` 型は namespace と入れ子の型名を全小文字で結合したパス (`Lub.Gfx` → `lub.gfx`) で呼ぶ
- 複数入力ファイルは同一 Roslyn Compilation で解決し、出力は入力順にまとめる

### Q3: エントリポイント → Lua chunk + 明示呼び出し
- top-level statements は型定義を emit した後に Lua chunk として出力する
- class/static method は自動実行せず、host やテストが `Class.Method()` を明示的に呼ぶ
- CLI 生成 Lua は TinySystem runtime prelude をデフォルトで埋め込み、`--no-runtime` で host 供給へ戻せる
- hot reload は host 側 (lub の lume.hotswap / browser の module registry) が担い、tcs runtime は reload 機構を持たない

### Q5: CLI 設計 → 変換・watch・check・SourceMap 後処理を実装済み
- 基本形: `tcs input.cs [input2.cs ...] --ref ref.cs -o output.lua`
- `tcs check input.cs [input2.cs ...] --ref ref.cs`: Lua を出力せず、compile error と TCS1001/TCS1002/TCS1003 準拠診断だけを返す
- `--watch` / `-w`: 入力と `--ref` ファイルを監視して再トランスパイル
- `--sourcemap`: `output.lua.map` を生成し、runtime prelude offset 済みの Lua 行番号を出す
- `--map-stacktrace output.lua.map [trace.txt]`: Lua stack trace を C# ファイル:行番号で注釈する
- `--help`, `--version`, unknown option, missing option value の UX は T117 で実装済み
