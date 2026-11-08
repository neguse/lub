# 未解決の質問

開発を進める前に確認したい事項。回答がつき次第 resolved に移動する。

---

## Open

### Q13: host 組込みの API/glue 構成 — script runtime ABI をどこに置くか
- エンジンが公開する C API は 1 本とし、tier ごとに glue を生成する構図を仮置きしている: dev は tcs VM の host 関数 (`tvm_host` に名前と引数の型を付けて登録し、VM の slot から C API の引数を作る glue。struct は C の layout の塊で渡す)、release は tcs2c からの直接呼び出し (ゼロコピー)。lub3d の Generator (単一 IR から言語ごとの binding を作る) と同型で、contract-gated に整合
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
- ユーザー定義ジェネリクス (class / method) は support-matrix と objective.md で Out としているが、check を通り、tcs VM と C は閉じた型ごとに単相化した IL で動く。基準 1 / 3 / 5 のいずれにも抵触しないため残し、docs を実態に合わせる

### Q19: DU とパターンマッチの範囲 — struct の interface 格納は TCS1001、網羅性は準拠 error、位置 / list パターンは対象外 (2026-10-20)
- Q15 の「多態は interface + 型 switch」を Elm の `case` に近づけるときの論点。事実:
  - tcs VM と C の struct は実行時の型の情報を持たない値 (VM は register の並びと heap の inline 配置、C は素の struct) で、interface 型の場所に入れると `is` / 型 switch で見分けられない。この変換は TCS1001 `StructBoxing`
  - interface 型の入力を `switch (v) { case Sq a: a.S }`、`v switch { Sq a => a.S }`、`if (v is Sq a)`、`is` + 明示 cast で絞った変数の member は tcs VM と C で動く
  - 位置パターン `Hit(var d)` / `is (0, 0)` と list パターンは TCS1001。ネスト property パターン、関係 / and / or、分解 `var (x, y) = p` は動く
- 決定 (objective.md の選定基準に従う):
  1. struct から interface / object への変換は TCS1001 とする (基準 1)。DU の case は record class に限る。boxing で対応する案は確保が record class と同じで interface 経由の呼び出しに unbox が入り、struct に実行時の型の tag を持たせる案は tag の無い値という struct の表現を崩すため採らない
  2. interface 型の値に対する switch 式 / 文が default / `_` を持たず実装型を覆っていない場合は準拠 error とする (基準 1、6)。.NET は実行時に SwitchExpressionException を投げる。tcs には例外機構がないため実行時には揃えられず、C# の警告 CS8509 を error に格上げする (T265。今の tcs VM と C は、一致する arm が無いと型の既定値を返して続ける)。tcs は全プログラムが 1 つの Compilation で `--ref` 以外は閉じているため、実装型の集合は静的に求まる
  3. 位置パターン `Hit(var d)` / `is (0, 0)` は対象外のまま (基準 4、5)。型 test と property パターン、分解 `var (x, y) = p` で書け、Deconstruct の語彙を足さない
  4. list パターンは対象外のまま (基準 4)。Count と添字で書ける
- tcs2c が switch で絞った変数の member を解決できない件は、決定とは独立した不具合として修正する (T264)
- 性能 (2026-10-17、DU 200 万件を生成して分岐): C は record class 0.066 s / struct + enum 0.017 s / class + tag 0.072 s で、確保 1 個あたり約 25 ns。k 件/フレームの DU は record class でよく、熱い data は readonly record struct + enum で書く
- record class は DU の case を運べる唯一の値なので、サブセットに残す


### Q18: LINQ → TinySystem の即時評価 API にする (2026-10-17)
- `System.Linq` への束縛をやめ、`TinySystem.Linq.Enumerable` の拡張メソッドとして即時評価の LINQ 風 API を持つ (namespace を TinySystem と分けるのは、`using System;` と並べても Math / String / Random が曖昧にならないようにするため。2026-10-25)。受け手は `List<T>` だけ、列を返す演算は `List<T>` を返す。dotnet 側も同じ即時実装に束縛されるので、support-matrix §19 の「遅延評価との既知差異」は定義ごと消える。`System.Linq` は namespace ごと TCS1002 (`System.IO` と同列)
- 名前: C# と意味が一致する演算は C# の名前のまま。改名はしない (Where / Select を Filter / Map にする案は却下。C# の既知感と IDE の補完が tcs の資産)
- 集合: 残す Where / Select / Any / All / Count(pred) / Sum / Min / Max / FirstOrDefault / OrderBy / OrderByDescending。落とす First / Last / LastOrDefault / Take / Skip / ToList / Count() / ToDictionary (添字、copy constructor、範囲、foreach 2 行で書ける)。足す `List<T>.FindIndex` / `RemoveAll` (BCL の名前のまま allowlist) と MinBy / MaxBy (TinySystem)。Where で代用すると alloc か計算量で損する演算だけを足す
- 見つける原始は FindIndex (-1)。Elm の Maybe 流に `T?` を返す find は generic 1 本で書けない (struct / class 制約の overload は CS0111、制約なし `T?` は値型で default(T) と区別できない。2026-10-17 確認)。FirstOrDefault は C# の意味 (default(T)) のまま残す
- 固定する意味論: 述語 / selector は各要素ちょうど 1 回・順序どおり、結果は呼び出し時点の内容で確定 (元の Count まで添字で走る)、空列の Min / Max は fault、FirstOrDefault は default(T)、OrderBy は安定、int の Sum は checked、NaN の順序は .NET と同じ (いずれも現状の runtime の規則を、TinySystem.Linq の C# と doc comment に書く)
- Dictionary と HashSet の列挙は挿入順と定める (support-matrix §18)。tcs VM と C の runtime と、.NET の project に TinySystem.props が入れる `TinySystem/Collections.cs` の Dictionary / HashSet が同じ順になる (`tcs2c.Tests` の `DictOrder_*`)
- Dictionary 自身の演算は、結果が key と value の集合だけで決まるものに限る: Where (`Dictionary<K,V>` を返す) / Any / All / Count(述語) / RemoveAll (その場で削除) / Clear。述語は `(k, v) =>` の 2 引数 lambda とする。System.Linq の Where とは lambda の引数の数が異なるので、dotnet 側で overload が曖昧にならない。foreach も `foreach (var (k, v) in d)` の分解形に対応し、lambda と同じ形にする
- Dictionary に置かない演算: Select (Dictionary を返すと意味が mapValues になり、C# の Select と異なる。基準 2)、Sum (float の加算順で結果が変わる。基準 1)、OrderBy / First / MinBy など順序が結果に現れる演算
- Keys / Values は copy の `List<T>` のまま残す (列挙の順は挿入順)。Dictionary から List の LINQ へはここを経由する (`d.Values.Where(..).OrderBy(..)`)。dotnet 側では `Dictionary<K,V>.KeyCollection` / `ValueCollection` を receiver とする overload を TinySystem に足す。key の順が要る場合は `Keys.OrderBy(k => k)` と書く。配列は引き続き対象外
- 参照型 (class instance) の key: tcs VM は同一性で引き、列挙は挿入順で .NET と同じ。C backend は外部 handle 以外の参照 key を生成の前に止める (`Dictionary key type not supported`)。`tcs check` は通すので、C だけが断る。TCS1001 に揃えるかは未決
- 性能 (2026-10-17、PC、tcs2c gcc -O2、2000 フレーム × 1000 要素): Where と foreach + if は C で 0.019 / 0.002 s (要素あたり約 8 ns)。struct 要素の Where は readonly record struct 0.019 s、readonly なし 0.023 s (述語引数と foreach 変数の copy)。「毎フレームの一時 list は作ってよい」「data を運ぶ struct は `readonly record struct` を既定にする」の 2 つを前提として書く
- 遅延評価との比較 (2026-10-20、同条件): Where → Select → Sum の chain は C で即時 0.002 s / closure iterator の遅延 0.008 s / loop 1 本に融合 0.002 s。遅延は段ごとに要素あたり呼び出しが増えて 4 倍遅い。遅延が勝つのは `Where(q).FirstOrDefault()` のような chain 途中の早期終了だけで、述語版の FirstOrDefault / Any / Count は即時でも途中で抜ける。早期終了が profile に出たら、即時のまま emitter で chain を 1 本の loop に融合する
- 実装の形 (2026-10-25 改訂): TinySystem.Linq は tcs サブセットの C# で書き、.NET はそのまま実行し、tcs VM / C は user のコードと同じく source を変換して実行する。意味論の正本はその C# と doc comment で、il-spec §13 の intrinsic から LINQ を外す。runtime に LINQ を手書きで持たない。backend ごとの手書きの実装を突き合わせる形では、callback が受け手を変えたときなどの差が直すたびに見つかって収束しない。`System.Linq` を namespace ごと TCS1002 にすること、fuzz の「即時評価の既知差異の隔離」の撤去は従来どおり。`Linq.cs` は TinySystem.dll に入れず、.NET の project は `TinySystem/TinySystem.props` で source をコンパイルする。lub の雛形は props の Import と `using TinySystem.Linq;` (lub へは feature request)
- 前提の確認 (2026-10-25): user 定義の generic 拡張メソッドで Where / Select / Any / Count / Sum / FirstOrDefault / 安定 sort の OrderBy を書き、callback が受け手に Add する場合も含めて .NET / C の stdout が一致した。サブセットに足りないもの: overload (TCS1001 MethodOverload。Sum / Min / Max / OrderBy の型別の版)、string の ordinal 比較 (`string.CompareOrdinal` は TCS1002)、int の Sum の checked (`checked(expr)` は透過)
- 性能 (2026-10-25、PC、tcs2c gcc -O2、2000 フレーム × 1000 要素、int と readonly record struct の Where を 1 回ずつ、process 全体の時間): runtime の実装から C# の実装にすると、C は 0.064 s → 0.065 s で差がない

### Q16: 名前の写し方 → user のコードは C# の名前のまま (2026-10-12)
- snake_case 化ですべての名前を作ると、大文字小文字の情報を落とす (`Rng` / `rng` / `RNG` → `rng`、`XMLValue` → `x_m_l_value`)。host (`--ref`) の名前の写し方は `SelfHost/Compiler/Declarer.cs` の `LuaNames` にあり、lub-gen も同じ関数で写す
- user の型の member は C# の識別子をそのまま IL の名前にする (tcs VM と C の名前もここから作る)。同じ型の中の同じ綴りは C# が禁じる (CS0102 / CS0082) ので写像は一対一になり、名前の衝突の診断 (#111、#109) が要らなくなる。error と stack trace にも C# の名前が出る
- `--ref` の host 型の member は今の snake_case の規則で固定する (lub-gen と C ABI のため。user の規則の変更に巻き込まない)
- overload は、overload があるときだけ名前に引数の型を付ける。2 つ目を足すと既存の名前が変わる。呼び出し側は作り直され、名前の変わった関数を指す生きている closure (method group の値) があれば、新版に対応が無いので reload は restart になる (vm-design §12.4)。entry の callback など host が名前で呼ぶものの overload は禁じる
- entry の callback は C# の名前 (`OnFrame` 等) で host に見せる
- どの写像でも入れる診断: `__` で始まる識別子、非 ASCII の識別子、`new` / `ctor` などの予約 key、型名と namespace 名の `_` (入れ子の型と namespace を `_` で連結した平らな名前にするため)

### Q17: reload で残す状態 → `[TcsHot]` の型を tag で引き継ぐ (2026-10-12)
- reload で状態を残す型は hot、それ以外は cold。project の既定値を csproj の `<TcsHot>` に書き、型の `[TcsHot]` / `[TcsHot(false)]` で上書きする。lub と tcs は csproj を評価しないので、`<TcsHot>` は条件なしで直接書いたものだけを読み、それ以外は error にする (IDE の analyzer と既定値を食い違わせない)
- hot の型の field (static を含む) は protobuf の field 番号のような tag を必ず持つ (`[Tag(1)] int hp;`)。reload は tag で値を引き継ぐので、field と型を改名しても状態が残る。同じ型の中の tag の重複と、消した tag の再利用は error
- 推移的: hot の型の field の型は、hot の型か、中身ごと移せる型 (数値、string、enum、struct、それらの List / Dictionary、host の handle) に限る。関数値 (`Action` / `Func`、delegate) の field は持てない
- cold の型は、形 (field) が変わったら restart
- reload の前に作った関数値 (lambda、method group、event handler) は、新版に同じ形の対応する関数があればその本体に付け替え、無ければ restart にする (vm-design §12.4)
- 雛形 (lub の templates/game) は `<TcsHot>` を書いておく

### Q15: 継承 → class / record の継承は外す。多態は interface と型 switch (2026-10-12)
- Go と同じ構成にする: 多態は interface の実装と型による分岐 (`switch (e) { case Hit h: ... }`)、組み合わせは合成 (record / class が別の型を field に持つ) で書く
- class の継承 (`class D : B`、`virtual` / `override` / `base`、隠蔽) と record の継承 (`record D(...) : B(...)`) は対象外にする。lub と tcs の sample、lub の cs-lib に使う所は無い
- 名前の写し方 (Q16)、hot reload (基底の型の layout の移行)、C backend (class の vtable) が単純になり、継承をまたぐ名前の取り違えや隠蔽の不具合が書けなくなる
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
- 診断ルールは compiler (`SelfHost/Compiler` の `Compliance` と `SemanticChecks`) の 1 か所にあり、analyzer も同じ compiler を動かす (旧 `Shared/TinyCsComplianceFacts.cs` は消した)。analyzer / transpiler warning / `tcs check` でCompact C# baseline / TinySystem API / Out of scope API の判定がずれないようにする
- 残りの判断は Q12 の Rider 実機 go / no-go 確認

### Q10: 開発軸 → Compact C# baseline
- C# 側の仕様準拠度と標準ライブラリ網羅性を棚卸し軸にする
- ただし全 C# / 全 BCL 実装は目的にしない
- モダンな .NET / C# 開発体験を満たす最小ラインに留める
- C# ベースだが、Go のようなコンパクトさと説明しやすさを狙う

### Q9: 外部エンジン直接対応の範囲 → tcs 本体では前提にしない
- lub3d 側のプロジェクト方針が変わっているため、tcs 本体が lub3d を直接サポートする前提は置かない
- tcs のコアは engine agnostic な C# サブセットのコンパイラ (tcs VM と C)
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

### Q6: struct の値セマンティクス → 値型として対応 (T219b)
- ユーザー定義 `struct` / `record struct` は値型で、copy 地点は il-spec §10。tcs VM は葉の field ごとの register と heap の inline 配置、C は素の C の値型で持つ
- override は ToString だけ

### Q7: テストの粒度 → セマンティックテスト
- C#入力 → tcs VM で実行 → 結果検証 (`TCS_DIFFERENTIAL=1` なら .NET の結果とも比べる)
- 出力文字列の完全一致は使わない（壊れやすい）
- `CompileAndRunVm(input, expr)` ヘルパーでテストを書く。C と VM と .NET の一致は `tcs2c.Tests` の `Backends.AssertParity`

### Q8: TinySystem の配布 → NuGet は当面やらない
- ProjectReference のみで進める

### Q2: 名前空間マッピング → namespace 修飾名の平らな名前
- `namespace Foo.Bar` の型 `C` は IL で `Foo_Bar_C` という平らな名前になる (入れ子の型 `In` は `Foo_Bar_C_In`)。IL 契約、tcs VM の型の名前、C backend の記号 (`Tcs_Foo_Bar_C`) が平らな名前のまま揃う
- namespace 未指定の型は simple 名のまま
- `--ref` 型は namespace と入れ子の型名を全小文字で結合したパス (`Lub.Gfx` → `lub.gfx`) で呼ぶ
- 複数入力ファイルは同一の compilation で解決し、出力は入力順にまとめる

### Q3: エントリポイント → `Main` と、host が明示的に呼ぶ static method
- `static void Main()` (top-level statements を含む) を実行の入口にする。`Main` が複数あれば `--entry CLASS` で選ぶ
- class / static method は自動実行せず、host やテストが明示的に呼ぶ。tcs VM は `tcs vm --export CLASS.METHOD` (`CLASS.*` で全部) の関数を host が `tvm_find` で引いて `tvm_call` で呼ぶ。C は `tcs c --lib` が static initializer を走らせる `tcs_lib_init` と、static method ごとの `tcs_entry_<Class>_<Method>` を出す
- hot reload は tcs VM の `tvm_apply` / `tvm_apply_batch` がフレーム境界で当てる (doc/hot-reload-design.md)

### Q5: CLI 設計 → `tcs check`、`tcs vm`、`tcs c`
- `tcs check input.cs [input2.cs ...] --ref ref.cs`: 出力を作らず、compile error と TCS1001/TCS1002/TCS1003 準拠診断だけを返す (警告でも exit 1)
- `tcs vm [--entry CLASS] [--export CLASS.METHOD|CLASS.*] [--ref ref.cs] [-o out.tcsb] input.cs ...`: tcs VM の bytecode。`--watch` は入力が変わるたびに reload の batch か restart の理由を host への stream に書く。`--reload-from` は旧版から新版への reload の計画と batch を作る
- `tcs c [--entry CLASS] [--lib] [--ref ref.cs] [-o out.c] input.cs ...`: C のソース
- subcommand の無い呼び出しは usage を stderr に書いて exit 1
- `--help`, `--version`, unknown option, missing option value の UX は T117 で実装済み
