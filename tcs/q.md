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

### Q20: サブセットの選定基準を 6 つの基準に改め、基準で確定した 3 件 (2026-10-20)
- objective.md の選定基準を 6 つの基準 (全 backend で同じ結果 / 名前と意味を C# から変えない / 暗黙のコストなし / 書き方は 1 つ / 語彙を増やさない / C# コンパイラが受理するものだけ) に改めた。Q15 / Q18 / Q19 はこの基準の適用例で、以後の足す / 切るもこの基準で判断する
- `Transpiler/CompilationDiagnosticPolicy.cs` の緩和 (CS0266 / CS0029 / CS0019 の enum と int の暗黙変換、CS0535 の field で interface member を満たす facade) は撤去する (基準 6)
- 未対応の書式指定子 (`N2` / `C` / `P0` など) は現状では既定の文字列化に黙って置き換わる (`{x:N2}` が `1234.5`)。TCS1001 とする (基準 1)
- ユーザー定義ジェネリクス (class / method) は support-matrix と objective.md で Out としているが、check を通り、Lua では型消去でそのまま動く。tcs2c にも generics の host テストがある。基準 1 / 3 / 5 のいずれにも抵触しないため残し、docs を実態に合わせる

### Q19: DU とパターンマッチの範囲 — struct の interface 格納は TCS1001、網羅性は準拠 error、位置 / list パターンは対象外 (2026-10-20)
- Q15 の「多態は interface + 型 switch」を Elm の `case` に近づけるときの論点。2026-10-17 の probe (master d0165bf) で分かった事実:
  - record struct を interface 型の場所に入れると、Lua では `is` / 型 switch が silent に false になる (struct は metatable の無い plain table で実行時タグを持たない)。tcs2c は `IlIsType target is not a class` で明示 error。issue 未登録
  - tcs2c は interface 型の入力を `switch (v) { case Sq a: a.S }` と `v switch { Sq a => a.S }` で絞った変数の member を `unknown field: IShape.S` で解決できない。`if (v is Sq a)` と `is` + 明示 cast は通る。Lua は 4 形とも正しい。issue 未登録
  - 位置パターン `Hit(var d)` / `is (0, 0)` と list パターンは TCS1001。ネスト property パターン、関係 / and / or、分解 `var (x, y) = p` は動く
- 決定 (objective.md の選定基準に従う):
  1. struct から interface / object への変換は TCS1001 とする (基準 1)。DU の case は record class に限る。boxing で対応する案は確保が record class と同じで interface 経由の呼び出しに unbox が入り、struct に metatable を付ける案は plain table の設計と migration を崩すため採らない
  2. interface 型の値に対する switch 式 / 文が default / `_` を持たず実装型を覆っていない場合は準拠 error とする (基準 1、6)。.NET は実行時に SwitchExpressionException を投げ、Lua は nil を返して使用箇所で停止する。tcs には例外機構がないため実行時には揃えられず、C# の警告 CS8509 を error に格上げする。tcs は全プログラムが 1 つの Compilation で `--ref` 以外は閉じているため、実装型の集合は静的に求まる
  3. 位置パターン `Hit(var d)` / `is (0, 0)` は対象外のまま (基準 4、5)。型 test と property パターン、分解 `var (x, y) = p` で書け、Deconstruct の語彙を足さない
  4. list パターンは対象外のまま (基準 4)。Count と添字で書ける
- tcs2c が switch で絞った変数の member を解決できない件は、決定とは独立した不具合として修正する (T264)
- 性能 (2026-10-17、同条件、DU 200 万件を生成して分岐): Lua は record class 0.75 s / readonly record struct + enum 0.63 s / class + int tag 0.63 s で確保コストは同じ (`__tcs_is` の metatable 連鎖歩きが約 20%、`getmetatable(x) == T` の直接比較で 0.67 s)。C は record class 0.066 s / struct + enum 0.017 s / class + tag 0.072 s で、確保 1 個あたり約 25 ns。k 件/フレームの DU は record class でよく、熱い data は readonly record struct + enum で書く
- record class は DU の case を運べる唯一の値なので、サブセットに残す


### Q18: LINQ → TinySystem の即時評価 API にする (2026-10-17)
- `System.Linq` への束縛をやめ、`TinySystem.List` の拡張メソッドとして即時評価の LINQ 風 API を持つ。受け手は `List<T>` だけ、列を返す演算は `List<T>` を返す。dotnet 側も同じ即時実装に束縛されるので、support-matrix §19 の「遅延評価との既知差異」は定義ごと消える。`System.Linq` は namespace ごと TCS1002 (`System.IO` と同列)
- 名前: C# と意味が一致する演算は C# の名前のまま。改名はしない (Where / Select を Filter / Map にする案は却下。C# の既知感と IDE の補完が tcs の資産)
- 集合: 残す Where / Select / Any / All / Count(pred) / Sum / Min / Max / FirstOrDefault / OrderBy / OrderByDescending。落とす First / Last / LastOrDefault / Take / Skip / ToList / Count() / ToDictionary (添字、copy constructor、範囲、foreach 2 行で書ける)。足す `List<T>.FindIndex` / `RemoveAll` (BCL の名前のまま allowlist) と MinBy / MaxBy (TinySystem)。Where で代用すると alloc か計算量で損する演算だけを足す
- 見つける原始は FindIndex (-1)。Elm の Maybe 流に `T?` を返す find は generic 1 本で書けない (struct / class 制約の overload は CS0111、制約なし `T?` は値型で default(T) と区別できない。2026-10-17 確認)。FirstOrDefault は C# の意味 (default(T)) のまま残す
- 固定する意味論: 述語 / selector は各要素ちょうど 1 回・順序どおり、結果は呼び出し時点の内容で確定 (元の Count まで添字で走る)、空列の Min / Max は fault、FirstOrDefault は default(T)、OrderBy は安定、int の Sum は checked、NaN の順序は .NET と同じ (いずれも現状の runtime の規則を仕様にする)
- Dictionary (2026-10-20 追記): 列挙順は未規定とする。Lua は string key の hash seed が process ごとに異なり、tcs2c は chained hash の順、.NET は挿入順で、backend ごとに順序が異なる。差分テストは keys / values を sort して比較し、fuzz は Dictionary の列挙を生成しない。この扱いを support-matrix §18 に明記する
- Dictionary 自身の演算は、結果が key と value の集合だけで決まるものに限る: Where (`Dictionary<K,V>` を返す) / Any / All / Count(述語) / RemoveAll (その場で削除) / Clear。述語は `(k, v) =>` の 2 引数 lambda とする。KeyValuePair を値として扱うと Lua で要素ごとに table を確保するため、値としては持たない。System.Linq の Where とは lambda の引数の数が異なるので、dotnet 側で overload が曖昧にならない。foreach も `foreach (var (k, v) in d)` の分解形に対応し、lambda と同じ形にする
- Dictionary に置かない演算: Select (Dictionary を返すと意味が mapValues になり、C# の Select と異なる。基準 2)、Sum (float の加算順で結果が変わる。基準 1)、OrderBy / First / MinBy など順序が結果に現れる演算。Count は pairs 走査の O(n) のまま (wire format 契約)
- Keys / Values は copy の `List<T>` のまま残し、順序未規定と明記する。Dictionary から List の LINQ へはここを経由する (`d.Values.Where(..).OrderBy(..)`)。dotnet 側では `Dictionary<K,V>.KeyCollection` / `ValueCollection` を receiver とする overload を TinySystem に足す。順序が必要な場合は `Keys.OrderBy(k => k)` と明示する。配列は引き続き対象外
- seed: tcs のテストと perf が使う interpreter (`runtime/lua/tcs_lua.c`) は、環境変数 `TCS_LUA_SEED` があるときだけ固定 seed で `lua_newstate` する。設定するのはテストの harness だけで、perf / samples / lub は Lua 既定の seed のまま動く。固定 seed は失敗した fuzz や差分テストを同じ状態で再現するためのもので、テストの expected に列挙順を書く根拠にはしない。差分テストの sort と fuzz の除外はそのまま残す
- 参照型 (class instance) の key は Lua では address で hash されるため、seed を固定しても順序は再現しない。tcs2c は外部 handle 以外の参照 key を生成前に止めている。Lua 側も TCS1001 に揃えるかは未決
- 性能 (2026-10-17、PC、lua32 / tcs2c gcc -O2、2000 フレーム × 1000 要素): Where と foreach + if は Lua 0.089 / 0.090 s、C 0.019 / 0.002 s (C で要素あたり約 8 ns)。struct 要素の Where は readonly record struct 0.089 s、readonly なし 0.715 s (Lua。述語引数と foreach 変数の copy)。C は 0.019 / 0.023 s。「毎フレームの一時 list は作ってよい」「data を運ぶ struct は `readonly record struct` を既定にする」の 2 つを前提として書く
- 遅延評価との比較 (2026-10-20、同条件): Where → Select → Sum の chain は Lua で即時 0.071 s / closure iterator の遅延 0.118 s / loop 1 本に融合 0.066 s、C で 0.002 / 0.008 / 0.002 s。遅延は段ごとに要素あたり呼び出しが増えて Lua で 1.7 倍、C で 4 倍遅い。遅延が勝つのは `Where(q).FirstOrDefault()` のような chain 途中の早期終了だけ (一致が 10% の位置で即時 0.051 s / 遅延 0.009 s) で、述語版の FirstOrDefault / Any / Count は即時でも途中で抜ける。早期終了が profile に出たら、即時のまま emitter で chain を 1 本の loop に融合する
- 実装の形: `TinySystem/RuntimeFacades.cs` の `List` facade (即時版が static 形で既にある) を拡張メソッドに、`LuaEmitter.IlBuild.Invocations` の束縛判定を `System.Linq.Enumerable` から `TinySystem.List` に、allowlist をシグネチャ列挙から namespace 規則に。Lua runtime の `List.*` と tcs2c の `CEmitter.Linq.cs` は流用。fuzz の「即時評価の既知差異の隔離」は撤去。lub の雛形 csproj には `<Using Include="TinySystem" />` (lub へは feature request)

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
