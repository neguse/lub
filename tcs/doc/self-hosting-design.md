# tcs のセルフホスト設計

> Status: 提案 (2026-10-31)。実装未着手。§14 の決定を待つ
>
> Scope: tcs の compiler を TinyC# のサブセットで書き、tcs 自身で Lua / C に変換して動かす構成。
> `../lub` は利用側の文脈として扱う (lub 側の変更は feature request)。

## 1. 結論

tcs の compiler のうち Roslyn に依存しない後半 (IL と IL→Lua) をまずサブセットの C# に書き直して共有し、
その上に Roslyn を使わない前半 (lexer / parser / binder / 準拠検査 / IL 構築) をサブセットの C# で足す。
これを tcs 自身で Lua (と tcs2c で C) に変換したものを「セルフホストの compiler」とする。

- Roslyn の経路は参照実装として残す。IDE (Rider の analyzer)、`tcs check`、CI、release build の判定は Roslyn が正のまま (objective の基準 6 を保つ)
- セルフホストの前半は「参照実装と同じ IL を出すか、未対応と明示して出さないか」のどちらかしか許さない。違う IL を出すことは不具合で、gate で止める
- 後半 (IL のモデル、IL→Lua、prelude、hot reload の metadata) は 1 つのソースを .NET と Lua と C で共有する。二重になるのは前半だけ
- compiler のソースは常に Roslyn の tcs でも変換できるので、bootstrap 用の seed をリポジトリに置く必要がない
- 着手前に 2 つの情報を買う。Lua 上の lexer + parser の速度と、サブセットで compiler を書いたときの書きにくさ (§11 の S0)。結果次第で binder 以降に進むかを決める

## 2. 何のためにやるか

北極星は live-first (objective)。今の開発ループは .NET が前提になっていて、そこが重い。

| 場面 | 今 | セルフホスト後 |
|---|---|---|
| web playground | Roslyn + tcs の .NET wasm bundle (AOT で 25MiB 超、分割して配信)。cold は prebuilt の Lua で隠し、warm の body edit は p95 0.45s 級。CPU の 6 割強は Mono の interpreter | player と同じ Lua 5.5 の VM (worker) で compiler が動く。.NET runtime が要らない。prebuilt snapshot、`gen-tcs --publish`、wasm-tools workload が不要になる |
| lub native の dev | `lub X.csproj` に dotnet SDK と `third_party/tcs` が要る | lub の binary だけで C# を書いて動かせる (compiler は Lua か tcs2c の C で lub に同梱) |
| 実機の dev build (il-design §5) | source push の先で変換できないので、PC で変換した Lua を送る | 実機の上で変換できる。ゲーム内コンソールや mod のように、利用者が C# を書く用途も開ける |
| サブセットの検証 | 最大の TinyC# のプログラムは cs-lib とサンプル (約 2 万行) | compiler 自身 (数万行) が最大の利用例になり、サブセットの書き味、3 経路の一致、tcs2c の GC を大きな実プログラムで試す |

やらない場合の代替は「.NET wasm の AOT とサイズ削減を続ける」で、web の warm edit は既に SLO を満たしている。
したがって主な利得は速度ではなく、.NET への依存を開発ループから外すこと (配布の軽さ、cold 起動、実機上の変換) と dogfooding にある。
この利得が数万行の実装と前半の二重保守に見合うかは §14 の D0 で決める。

## 3. objective との関係

objective は「Roslyn を パーサー兼型チェッカー として利用し、自前実装を最小化する」と書き、選定基準 6 は「Roslyn をそのまま型検査器として使い、tcs 独自の緩和を入れない」と書く。
セルフホストは自前の型検査器を作るので、前者の方針は変わる。後者は次の読み方で保つ。

- TinyC# のプログラムの正否は、引き続き Roslyn (と TCS100x の準拠検査) が決める
- セルフホストの前半は Roslyn の判定に従う下位の実装で、受理する集合は Roslyn が受理する集合の部分集合に限る。独自の緩和は入れない
- Roslyn が通さないものをセルフホストが通してしまう差は、検査の取りこぼしとして扱い、§9 の gate で見つけて直す。ただし §6.6 の「後回しにしてよい検査」は、意味 (IL) に影響しないことを条件に、Roslyn だけが判定する
- release build (tcs2c) と CI は Roslyn の経路を通す。セルフホストは開発ループの速い経路という位置付け

objective の該当箇所は D0 が決まったら書き換える。

## 4. 全体構成

```text
                 ┌───────────── 参照実装 (full C#、.NET のみ) ─────────────┐
C# source ──┬──→ │ Roslyn → syntax + SemanticModel → IL 構築 (現行 IlBuild) │ ──┐
            │    └──────────────────────────────────────────────────────────┘   │
            │    ┌──── セルフホストの前半 (サブセット C#、.NET / Lua / C) ───┐   ├→ IL ─→ 共有の後半 ─→ Lua
            └──→ │ lexer → parser → 宣言 → binder → 準拠検査 → IL 構築       │ ──┘        (サブセット C#)
                 └──────────────────────────────────────────────────────────┘
                                                                    IL ─→ IlExport ─→ tcs2c ─→ C
```

置き場所 (名前は仮。tcs 系に揃える):

```text
TcsCore/                 サブセットの C#。.NET では普通の library、Lua と C には tcs で変換する
  Text/                  ソース、位置、行の表、診断
  Il/                    IL のモデルと正規のテキスト表記 (§9 の比較に使う)
  Lua/                   IL→Lua (lowering、emit、命名、prelude、static 初期化、struct / record、hot reload の metadata、source map)
  Syntax/                lexer、parser、構文木
  Semantics/             symbol、BCL の面の読み込み、binder、定数、フロー解析
  Compliance/            TCS1001-1003 の判定 (bound tree の上)
  Lower/                 bound tree → IL
  Session/               増分の driver、reload の分類
Transpiler/              参照実装。Roslyn → IL (TcsCore.Il) → TcsCore.Lua。CLI と WasmCompiler もここから
tools/BclSurfaceGen      Roslyn の metadata と allowlist から BCL の宣言だけの C# を生成する (§6.4)
tools/SemanticDump       Roslyn から比較用の dump を出す (§9 の G1 / G2)
```

## 5. 共有する部分と二重になる部分

| 部分 | 規模 (現行) | 扱い |
|---|---:|---|
| IL のモデル | 約 0.3k 行 | TcsCore に移して共有。`abstract record IlNode` の継承は interface (`IIlExpr` / `IIlStat`) と sealed record に、`ImmutableArray` は `List` に、`SyntaxNode? Origin` は `Span` (ファイル id、開始、長さ) にする。IlExport と tcs2c はこの型を読む |
| IL→Lua (IlEmit、IlLower、Prelude、StaticInit の emit、Structs / Records の emit、Naming、HotReload、SourceMap、LuaLocalRenamer) | 約 4〜5k 行 | TcsCore に移して共有。今も Roslyn に依存しないので、書き換えはサブセットへの移植だけ |
| IL 構築 (LuaEmitter.IlBuild.*) | 約 6k 行 | 二重。参照実装は Roslyn の上で現行のまま。セルフホストは bound tree の上で書き直す |
| 準拠検査 (Shared の facts) | 約 2.7k 行 | 二重。判定は symbol の API に強く依存するので共有しない。診断の文言の表だけ共有する |
| 構文解析、型検査 | Roslyn | セルフホストで新規 |
| static 初期化の順序 (StaticInitOrder の依存グラフ) | 約 0.3k 行 | 依存の収集は二重 (Roslyn の symbol / bound tree)、順序の決定は共有 |
| 増分 session、reload の分類 | 約 1k 行 | 二重。Roslyn の差し替え単位 (SyntaxTree) とセルフホストの差し替え単位が違うため |

後半を先に共有にすると、セルフホストを途中で止めても「後半がサブセットで書かれ、3 経路で動き、参照実装と同じ出力を出す」という価値が残る (§11 の S1)。

## 6. セルフホストの前半

### 6.1 ソースと位置

- ソースは `Utf8Bytes` で読む (添字が O(1)、Slice は複製しない)。サブセットの string は位置の API を持たないので、lexer は byte の列を直接走査する
- 位置は byte の offset で持ち、診断と source map に出すときだけ行と列に直す。Roslyn は UTF-16 の列で数えるので、比較 (§9) と IDE 連携の地点で変換する。ASCII の行では両者が一致する

### 6.2 lexer

- C# の字句のうち、サブセットが使うものと、使っていないことを TCS1001 で伝えるのに要るもの (`try`、`goto`、`unsafe` など) を読む
- 数値リテラル: 10 進 / 16 進 / 2 進の整数、`f` suffix の実数、suffix の無い実数 (TCS1001 `DoubleLiteral` を出すため読む)、`_` 区切り。float は `float.TryParse` (3 経路で同じ丸め) で f32 にし、IL には .NET と同じ最短の文字列で渡す
- 文字列: 通常、verbatim、raw、補間 (入れ子の補間と書式指定子を含む)。補間は lexer が token の列に割り、parser が組み立てる
- preprocessor は support-matrix §9 の範囲 (対応しない指令は TCS1001)
- trivia (空白とコメント) は token に付けず捨てる。IDE 機能 (S7) で必要になったら span だけ持つ

### 6.3 parser と構文木

- 再帰下降。式は優先順位の表で読む (precedence climbing)。C# の曖昧さ (generic の `<` と比較、cast と括弧式、lambda と括弧式、pattern の `and` / `or`) は Roslyn と同じ規則で先読みして決める
- 構文木のノードは種類ごとの sealed class で、`ISyntax` (`Kind` と `Span` を持つ) を実装する。継承はサブセットに無いので、走査は `switch` と型 pattern で書く。種類の網羅は T265 (interface の switch の網羅性を準拠 error に) があると漏れを compile 時に見つけられるので、S2 の前提にする
- 誤りの回復は「足りない token を補ったノードを作り、診断を積んで続ける」。サブセットに例外は無いので、parser は失敗を値 (診断の list と、欠けを表すノード) で返す
- Roslyn の構文のうち、サブセットで TCS1001 にするもの (try / throw、goto、local function、named argument、`params` など) も木にする。TCS1001 の位置と文言を参照実装と揃えるため

### 6.4 宣言と BCL の面

Roslyn は参照 assembly の metadata を読むが、セルフホストは DLL を読まない。代わりに宣言だけの C# を読む。

- BCL: `tools/BclSurfaceGen` が Roslyn で参照 assembly を開き、Shared の allowlist (TCS1002 の許可表) に載る型と member だけを、本体の無い C# の宣言として `TcsCore/Surface/bcl.cs` に書き出す。許可表がそのまま「セルフホストが知っている BCL」になり、載っていない member は名前の解決で失敗して TCS1002 になる。生成物は手で編集せず、CI が再生成して差分を検査する (lub の `gen-api.sh --check` と同じ形)
- 許可表に無い BCL の member の診断文言 (置き換え先の案内) は、同じ tool が名前と案内文だけの表として書き出す
- TinySystem (`Linq.cs`、`Utf8.cs` など) はサブセットの C# のソースなので、そのまま読む。ただし .NET 用の本体 (`Encoding.UTF8` を呼ぶ部分など) はセルフホストが受理しない書き方を含むので、同じ tool が宣言だけを抜き出して `TcsCore/Surface/tinysystem.cs` にする
- `--ref` の host の宣言 (lub の `lub_stub.cs`) は C# のソースなので、そのまま読む
- collection (List / Queue / HashSet / Dictionary / 配列) と string、`Utf8Bytes`、`Rune` は、binder が型の種類として特別に知る。foreach、collection の初期化、添字は `GetEnumerator` などの pattern を探さず、種類ごとの規則で束縛する。サブセットの collection は閉じた集合なのでこれで足り、IL 構築も同じ区別をしている
- 宣言の表は、型 / member の名前から symbol への `Dictionary` と、宣言順の `List` の 2 つで持つ。出力に影響する走査は必ず `List` の順で行う (§7 の決定性)

### 6.5 binder

method の本体ごとに、型の付いた bound tree を作る。サブセットに無い機能 (`params`、named argument、`using static`、alias、`global using`、継承、例外、`ref` / `in`、async、LINQ のクエリ構文、dynamic) は束縛の対象外で、これが binder を Roslyn より大幅に小さくする。対象は次の通り。

| 項目 | 内容 |
|---|---|
| 名前の解決 | local、parameter、member、型、namespace (file-scoped と block)、`using`、入れ子の型、拡張メソッド (`TinySystem.Linq` と user の static class)。`Color Color` の規則 |
| 変換 | 恒等、暗黙の数値変換 (int / char / byte / uint → float など)、定数の暗黙変換 (`byte b = 1`)、null、`T` → `T?`、interface への参照変換、lambda / method group → delegate、tuple、target-typed `new()` と collection 式、明示の cast (il-spec §5 / §6 の契約に繋がる区別を含む) |
| 演算子 | 組み込みの演算子の表 (int / uint / float / char / byte / bool / string / enum)、lifted 演算子、user の `+ - * / %` と単項 `-`、record の `==` / `!=` |
| overload 解決 | C# §12.6.4 の「より良い関数 member」と「より良い変換」。省略引数あり。generic と非 generic の同点の解消。拡張メソッドは instance member で見つからないときだけ |
| generic の型推論 | 2 段階の推論 (lambda の parameter 型を固定してから戻り型で推論する)。LINQ の `Select(x => x.A)`、`OrderBy`、`MinBy` が必要とする範囲。推論が既定の手順で決まらない形は「セルフホスト未対応」にする |
| lambda | 対象の delegate 型からの束縛、戻り型の推論、自然型 (`var f = (int x) => x;`)、変数の capture (il-spec §7) |
| 定数 | C# の定数式の評価。int の定数の overflow は compile error (CS0220)。float の定数は f32 で畳む。`const` と enum の member 値 (基底型の 2 の補数への写し) |
| pattern | 型、定数、関係 (`< 3`)、`and` / `or` / `not`、property pattern、`var`、switch 式と switch 文。網羅性は C# の規則 (switch 式の CS8509 は警告なので、準拠 error の T265 に従う) |
| record | 合成 member (positional の constructor と property、`with`、値の等価、`Deconstruct`) |
| nullable | 値型の `T?` は型として扱う。参照型の注釈 (`string?`) は型に影響させず、nullable の警告 (フロー解析) は出さない (Roslyn だけが出す) |

bound tree の各ノードは、IL 構築が Roslyn に問い合わせている情報 (`GetTypeInfo` の型と変換後の型、`GetSymbolInfo` の symbol、`GetConstantValue`、変換の種類) を持つ。現行の IL 構築はこれらの問い合わせを約 380 箇所で使っていて、その全てが bound tree の field で答えられることが S4 の完了条件になる。

### 6.6 フロー解析と後回しにしてよい検査

意味に影響する検査は必ずセルフホストでも行う。

- 確実な代入 (CS0165)、値を返さない経路 (CS0161)、switch の section の fall through (CS0163)、到達しない文 (警告なので出さない)
- 変数の capture の範囲 (il-spec §7 の scope 構造をここで決める)

意味 (IL) に影響しない検査は、Roslyn だけが判定してよい。開発ループでは通り、IDE と CI で止まる。

- nullable の警告、未使用の変数の警告、accessibility (`private` を外から読む CS0122。生成する Lua は同じになる)、`readonly` の field への代入 (CS0191) などのうち、§9 の G5 で一覧にしたもの
- この一覧は文書にして固定し、一覧に無い Roslyn の error をセルフホストが通したら gate で落とす

### 6.7 準拠検査

- TCS1001-1003 は bound tree の上で判定する。Shared の facts と同じ規則を、Roslyn の symbol ではなくセルフホストの symbol で書き直す
- 診断の id、位置、文言の表は共有する (`TcsCore/Compliance/Messages.cs`)。文言の組み立てだけを共有し、判定は二重
- 一致は既存の「analyzer / `tcs check` / transpiler の診断一致」の gate に 4 番目の参加者として入れる

### 6.8 IL の構築

- 現行の IlBuild が Roslyn の syntax と SemanticModel から作る IL を、bound tree から作る
- 一時変数の名前の付け方、評価順のための退避 (EvalOrder)、struct の copy 地点の判定、`T?` のノード、文字列補間の分解は、参照実装と同じ IL になるように同じ順序で決める。名前の振り方まで揃えると §9 の比較が byte の一致で済む。揃わない部分は比較の前に一時変数の名前を出現順に振り直して比べる

### 6.9 増分 session

- 単位は incremental-module-compilation-design と同じ「1 source file = 1 module」
- ファイルごとに宣言の要約 (型と member の signature の hash) を持つ。編集が本体だけなら、そのファイルの本体だけを束縛し直して emit する。要約が変わったら、そのファイルの名前を参照する module を束縛し直す (依存は束縛の時に記録する)
- reload の分類 (static 初期化の変更、shape の変更などで restart にする) は ReloadClassifier の規則をセルフホストの symbol で書き直す
- BCL と TinySystem と `--ref` の宣言は session の開始時に 1 度だけ読む。宣言の表を直列化して保存し、次の起動で読み直すことで cold 起動を縮める余地がある (S7 で測ってから決める)

## 7. サブセットで compiler を書くことの帰結

compiler はサブセットで書く最大のプログラムになる。書きにくさが選定基準の見直しの材料になるので、S0 で実際に書いて記録する。今の時点で見えているもの:

| サブセットの制約 | compiler での書き方 | 決定が要るか |
|---|---|---|
| 継承が無い (Q15) | 構文木 / bound tree / IL のノードは sealed class と interface、走査は型 switch。網羅は T265 に頼る | T265 を前提に置く |
| 例外が無い | parser と binder は診断を積んで続ける形で書く (Roslyn も同じ形)。compiler 自身の不具合は fault になるので、host は compiler を保護された呼び出し (Lua なら `pcall`) で動かす | 無し |
| string に位置の API が無い | lexer は `Utf8Bytes` を走査する。token の文字列は `Slice(...).ToString()` | 無し |
| StringBuilder が無い (support-matrix §20 で `-`) | 出力は `List<string>` に積んで `string.Join("", parts)`。Lua は `table.concat` になる | D4: 測って足りなければ `StringBuilder` を BCL の名前で足す (基準 5) |
| Dictionary の列挙順が未規定 | 出力に効く走査は `List` で順序を持つ。辞書は引くだけに使う。違反は §10 の 3 経路の bootstrap の一致で必ず露見する | D8: compiler のソースに Dictionary の foreach を禁じる検査を置くか |
| double / long が無い | int の定数式の overflow 検出は手で書く (`checked` は fault するだけで捕まえられない)。uint の 16 進リテラルは 32 bit の 2 の補数で持つ | 無し |
| Dictionary の key に使える型 | symbol は class の同一性、名前は string で引く。record を key にしない | 無し |
| switch の分岐が多い (式の種類は 60 前後) | Lua の型 switch と enum の switch はどちらも if の連鎖になる。頻度順に並べるか、種類ごとの関数の表で引く | S0 で測ってから決める |

## 8. 実行の形

### 8.1 Lua

- compiler はゲームとは別の Lua の state で動かす。tcs は namespace を global の名前 (`A_B_C`) に写すので、同じ state に入れると compiler の型がゲームの global と混ざり、hot reload の対象にもなってしまう
- web は compiler 用の worker に Lua 5.5 の wasm (数百 KB) と compiler の Lua を置く。ゲームの描画は止まらない
- native の lub は別スレッドの Lua の state で動かし、結果の Lua の文字列だけをゲームの state に渡す
- Lua は `LUA_32BITS` の build (il-design §4)。compiler も i32 / f32 だけで書くので問題は無い

### 8.2 C (tcs2c)

- tcs2c で compiler を C にすると、.NET を使わずに native の速度で動く。lub に link するか、wasm にして web で使う
- tcs2c の GC は host が entry から戻った後の frame の境界で回収する (T252)。compiler を 1 回の Main で走らせると、変換の全体が 1 frame になり、その間の確保が回収されない。そこで compiler の API は「session に仕事を積み、host が `Step()` を繰り返し呼ぶ」形にする。1 回の Step は 1 ファイルの parse か 1 module の束縛と emit で、Step の間が frame の境界になる。session の object は host が frame を跨いで持つ slot (`tcs_gc_holds`) で保持する
- この形は Lua でも都合が良い。web の worker は Step の合間に新しい編集を受け取って古い仕事を捨てられる

### 8.3 .NET

- TcsCore は普通の .NET の library としても build する。参照実装の Transpiler は TcsCore の IL と後半を使う
- セルフホストの前半も .NET で動くので、§9 の比較は xUnit の中で Roslyn と並べて実行できる。Lua と C で同じ結果になることは §10 の bootstrap で別に確かめる

## 9. 正しさの担保

原則は「参照実装と一致するか、未対応と明示して出さないか。食い違いは無い」。未対応は `TCS1900 SelfHostUnsupported(機能名)` のような専用の診断で返す。食い違いは不具合として gate で落とす。

| gate | 比べるもの | 母集団 |
|---|---|---|
| G1 構文 | 構文木の正規の dump (ノードの種類、span、子)。参照側は `tools/SemanticDump` が Roslyn の木をサブセットのノードの種類に写して出す | Transpiler.Tests の C# 断片、samples、cs-lib、lub の samples、TinySystem、compiler 自身 |
| G2 束縛 | 式ごとの (span、型、変換後の型、symbol、定数、変換の種類)。参照側は SemanticModel から出す | G1 と同じ |
| G3 IL | IL の正規のテキスト表記 (一時変数の名前を出現順に振り直したもの) | G1 と同じ + FuzzGenerator の program |
| G4 Lua | 最終の Lua。後半を共有するので、G3 が一致すれば一致する。確認のために残す | G3 と同じ |
| G5 拒否 | 参照実装が拒否する program を、セルフホストも拒否するか (何らかの error を出すか)。id と行の一致率は指標として記録する。§6.6 の後回しにしてよい検査の一覧に載る id だけは例外 | Transpiler.Tests の診断の test、fuzz の program に変異 (型の取り違え、未代入、名前の誤り) を入れたもの |
| G6 準拠 | TCS1001-1003 の id、位置、文言 | 既存の診断一致の gate の母集団 |
| G7 性能 | 行 / 秒 (Lua、C、.NET)、warm の body edit の edit-stop → commit ACK の p95 | cs-lib + サンプル、`bench/chrome-e2e-ack.mjs` |

既存の test を母集団にする仕掛け:

- `TranspileAndRun` などの test helper が、環境変数 (`TCS_SELFHOST_DIFF=1`) のあるときだけ、同じ入力をセルフホストの前半にも通して G3 で比べる
- 結果は「一致 / 未対応 / 食い違い」の 3 つに分けて数える。一致の割合をセルフホストの進み具合の指標にし、CI は「食い違い 0」と「一致の割合が下がらない」を検査する
- 参照実装に機能を足す PR は、セルフホストにも足すか、未対応として数えるかのどちらかになる。未対応のままでも食い違いにはならないので、参照実装の開発は止まらない

## 10. bootstrap

compiler のソース (TcsCore) は常に Roslyn の tcs でも変換できるので、seed の binary は要らない。

```text
ref    = 参照実装の tcs (Roslyn、.NET)
L1     = ref で TcsCore を Lua にしたもの
L2     = L1 (Lua の上) で TcsCore を Lua にしたもの
C1     = tcs2c で TcsCore を C にしたもの
N1     = TcsCore を .NET で build したもの
```

- 固定点: `L1 == L2` (byte の一致)。ref の前半とセルフホストの前半が TcsCore の入力に対して同じ IL を出し、後半が共有なので、一致するはず
- 3 経路の一致: L1 (Lua 上)、C1 (C)、N1 (.NET) のそれぞれで TcsCore を変換した Lua が byte で一致する。数万行の実プログラムでの differential test になり、Dictionary の列挙順への依存などはここで必ず見つかる
- この 2 つを CI の gate にする。`L2` で `L3` を作って比べる従来の固定点の検査は、上の 2 つから従うので要らない

配布:

- 変換済みの compiler (`tcsc.lua`、C の source) は CI の成果物にし、git には入れない
- lub は release の build で tcs の submodule から `tcsc.lua` を作って同梱する。lub を開発する側は dotnet SDK を持っている前提のままで、lub を使う側は dotnet が要らなくなる

## 11. 段階導入

各段階で独立に価値が残る順に並べる。完了条件は全て既存の test が通ることを含む。

| 段階 | 内容 | 完了条件 |
|---|---|---|
| S0 情報を買う | (a) 式と文の一部を読む lexer + parser をサブセットで書き、Lua と tcs2c で cs-lib (約 310KB、9k 行) を parse する速度を測る。(b) 書きにくさを §7 の表に追記する。(c) IL 構築が Roslyn に問う情報の一覧 (約 380 箇所) を分類する | 測定値と記録が文書にある。Lua の parse が 1MB/s 未満なら、Lua を主経路にせず C (tcs2c) を主経路にする方針に切り替える (D5)。S1 以降に進むかを決める |
| S1 後半の共有 | IL のモデルと IL→Lua を TcsCore (サブセット) に移し、参照実装がそれを使う | 出力の Lua が移行前と byte で一致 (全 test と samples)。TcsCore の後半が tcs で Lua と C に変換でき、3 経路で同じ Lua を出す |
| S2 IL の正規表記 | IL のテキスト表記を決めて書き出す (il-spec の「シリアライズ形式は M2」もここで決着) | 参照実装の IL を書き出して読み戻すと同じ Lua を出す |
| S3 構文 | lexer と parser の全体 | G1 が母集団の全てで一致 |
| S4 宣言と束縛 | BclSurfaceGen、宣言の表、binder、定数、フロー解析 | G2 が母集団の全てで一致か未対応。§6.5 の問い合わせの一覧が全て bound tree で答えられる |
| S5 IL と準拠 | bound tree → IL、TCS1001-1003 | G3 / G5 / G6 で食い違い 0。一致の割合が Transpiler.Tests の 95% 以上 |
| S6 bootstrap | compiler 自身を通す | §10 の固定点と 3 経路の一致が CI の gate になる |
| S7 統合 | 増分 session、Step の driver、web の worker、lub への同梱 (lub 側は feature request) | web の warm の body edit の p95 が現行 (0.45s) 以下。cold の compiler ready が 1s 以下。lub が dotnet なしで `samples/` を動かせる |
| S8 IDE 機能 (任意) | 誤りのある途中のコードでも束縛を続け、playground の補完と hover をセルフホストで出す | playground の A8 がセルフホストで通る |

S1 と S2 は、セルフホストに進まなかったとしても価値が残る (後半がサブセットで書かれて 3 経路で動く、IL の直列化が決まる)。S3 以降に進むかは S0 の結果で決める。

## 12. 規模と性能の見積もり

規模 (サブセットの C# の行数、目安):

| 部分 | 行数 |
|---|---:|
| Text、診断 | 1k |
| lexer | 1k |
| parser と構文木 | 5〜6k |
| 宣言、symbol、BCL の面の読み込み | 2〜3k |
| binder (変換、演算子、overload、推論、lambda、pattern、定数) | 8〜12k |
| フロー解析 | 1k |
| 準拠検査 | 2〜3k |
| bound tree → IL | 5〜6k |
| session、reload の分類 | 1〜2k |
| 前半の計 | 26〜35k |
| 後半 (S1 で移植、二重にならない) | 5k |

現行の Transpiler (Roslyn を除く) が約 15k 行なので、前半はその 2 倍前後になる。

性能の見当 (S0 で測って置き換える):

- Lua 5.5 の interpreter で、lexer が 1 byte あたり数十の命令を使うとすると、数 MB/s の lex が見込める。parse と束縛は byte あたりその数倍
- warm の body edit は 1 ファイル (数百〜数千行) の束縛と emit だけで済むので、Lua でも 100ms 前後に収まる見込み。現行の SLO (edit-stop → commit ACK の p95 < 500ms) には十分な余裕がある
- cold は BCL / TinySystem / `--ref` の宣言 (数千行) の読み込みが支配する。Lua で 1s を超えるなら、宣言の表の直列化か C (tcs2c) の build を使う
- tcs2c の C は .NET の JIT に近い速度が見込め、bootstrap (数万行) の時間を短くする

## 13. リスク

| リスク | 対策 |
|---|---|
| 2 つの前半の意味がずれる | §9 の「一致か未対応か」の gate。食い違いは CI で落ちる。後半は 1 つなので、ずれうるのは前半だけ |
| サブセットの機能を足すたびに前半を 2 回書く | 未対応で逃げられるので参照実装の開発は止まらない。機能は可能な限り TinySystem (サブセットの C# のソース) の側で足し、compiler の変更を減らす |
| Lua では遅すぎる | S0 で測る。C (tcs2c) の build に切り替える道を残す (D5) |
| サブセットで compiler を書くのがつらい | S0 で記録し、選定基準に照らして機能を足すか決める (基準は「他の書き方で書けるから要らない」を理由にしない) |
| tcs2c の GC が長い 1 frame に耐えない | §8.2 の Step の driver |
| generic の推論や overload の細部で Roslyn と合わない | 未対応に倒す。G2 は式ごとに比べるので、ずれの位置がすぐに分かる |
| 一人 + AI での保守の負担 | 前半の正しさは参照実装との比較で機械的に検査され、人が仕様を読み比べる必要が少ない。それでも D0 で費用を見て決める |

## 14. 決定キュー

| id | 問い | 推奨 |
|---|---|---|
| D0 | セルフホストに取り組むか。objective の「自前実装を最小化する」を改めるか | S0〜S2 までは進める (止めても価値が残る)。S3 以降は S0 の結果で決める |
| D1 | Roslyn を正否の判定の正として残すか | 残す。セルフホストは下位の実装 (§3) |
| D2 | 後半 (IL と IL→Lua) をサブセットに移して参照実装と共有するか | 共有する (§5)。二重の後半は保守の負担に見合わない |
| D3 | セルフホストが未対応を返したときの振る舞い | dotnet がある環境 (lub native の開発機) では参照実装に自動で切り替える。web と dotnet の無い環境では診断を出して last-good の Lua を動かし続ける |
| D4 | StringBuilder を足すか | まず `List<string>` と `string.Join` で書き、S0 / S6 で emit の時間を測ってから決める |
| D5 | 主経路を Lua にするか C (tcs2c) にするか | 両方を build できるようにし、S0 の測定で既定を決める。web は Lua を先に試す (worker が軽い) |
| D6 | 診断の一致をどこまで求めるか | TCS100x は id、位置、文言まで一致。C# の CSxxxx は「拒否すること」を必須、id と行の一致は指標 |
| D7 | compiler を別の Lua の state で動かすか | 別の state (§8.1) |
| D8 | compiler のソースに Dictionary の foreach を禁じる検査を置くか | 置く (TcsCore の project だけに効く検査)。§10 の 3 経路の一致でも見つかるが、見つかるのが遅い |
| D9 | `tcsc.lua` を git に入れるか | 入れない。CI の成果物と lub の release に同梱 (§10) |

## 15. 採らなかった案

- Roslyn 自体を tcs で変換する: Roslyn は full C# (継承、例外、遅延 LINQ、unsafe) で書かれていて、サブセットに入らない
- 後半だけをセルフホストにして、前半は Roslyn の server に問い合わせる: 編集のたびに .NET が要るので、開発ループから .NET を外すという目的を満たさない
- 型を見ずに構文だけで Lua にする: struct の copy 地点、整数の除算、`T?`、overload の選択など、出力が型に依存する箇所が多く、成り立たない
- 前半を C や Rust で書く: 速いが、dogfooding の価値と「compiler 自身が 3 経路の一致を示す」検査を失う。速度は tcs2c の build で得られる
- 前半を参照実装と置き換える (Roslyn を捨てる): IDE の analyzer と C# の厳密な判定 (基準 6) を失う

## 16. 関連文書

- `objective.md` — 目的、選定基準 (特に基準 6)
- `doc/il-design.md`、`doc/il-spec.md` — IL が意味論の正本であること。セルフホストの前半はこの IL を出す
- `doc/incremental-module-compilation-design.md` — 増分 session の構成と SLO
- `doc/support-matrix.md` — セルフホストの前半が扱う範囲。実装が進んだら「セルフホスト」の列を足す
- `doc/spec-conformance-design.md` — fuzz の母集団
- `tcs2c/README.md` — C backend と GC の frame
