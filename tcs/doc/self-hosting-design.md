# tcs のセルフホスト設計

> Status: 実装 (2026-10-31)。セルフホストの compiler は `SelfHost/` にあり、bootstrap の固定点と既存の test の全体での差分の一致を gate にした。lub の samples と tests/cs も同じ結果で動き、lub の native (dotnet なし) と web の playground から使える (§17.7)。
> 実装は §1〜§16 の提案と違う形を取った (IL を共有せず、Lua を直接出す)。実装した形と提案からの差は §17 にある。§1〜§16 は提案のときのまま残す
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

## 17. 実装した形 (2026-10-31)

§1〜§16 の提案のうち、gate で正しさを機械的に確かめるという骨格は残し、構成は小さくした。
compiler は 1 本の branch で bootstrap まで作り、既存の test の全体で参照実装と同じ結果を出すことを gate にした。

### 17.1 置き場所

```text
SelfHost/
  Compiler/        サブセットの C# で書いた compiler (namespace TcsSelf、約 1.7 万行)
    Text.cs          ソース (Utf8Bytes)、行の表、診断
    Lexer.cs         字句 (補間、verbatim、raw、raw の補間を含む)
    Syntax.cs        構文木 (種類の enum と、slot を持つ 1 つの Node class)
    Parser.cs        再帰下降の parser
    Types.cs         型、symbol、定数
    Declarer.cs      宣言の表、型の解決、Lua の名前 (overload は Name__sig)
    Builtins.cs      BCL と TinySystem の member の表
    Compliance.cs    構文だけで決まる準拠検査 (TCS1001。参照実装の Shared の規則の写し)
    DefiniteAssignment.cs  確実な代入 (CS0165。束縛した後の本体を走査する)
    Binder.cs        名前の解決、変換、overload 解決、型推論、lambda、pattern、定数
    Emitter.cs       Lua の出力 (struct の copy、評価順の退避、static 初期化の順序)
    EnumSort.cs      enum の名前の表を .NET と同じ順に並べる (ArraySortHelper の introsort の写し)
    Session.cs       増分の session と Step の driver (1 回きりの compile も同じ段を通る)
    Compiler.cs      入口 (Compile、CompileWithLibraries、ParseOnly)
  Host/tcsc.lua    Lua の上で compiler を動かす driver
  Host/bench-session.lua  Lua の上で session の時間を測る (cold、変更なし、1 ファイルの編集)
  Web/             WebAssembly 版 (Emscripten)。tcsc_web.c (Lua 5.5 と tcs の runtime の入口)、web.lua (Session を JSON で包む glue)、tcsc.mjs (JS の入口)、build.sh、test.mjs (node)
  Native/          C の build (tcs2c の --lib)。TcscMain.cs (Start と Step の entry)、HostStub.cs (host の関数の宣言)、host.c (その実装と、Step を繰り返す main)
  build.sh         stage1〜3 と C の build を作り、固定点と C の出力の一致を確かめる (出力は SelfHost/out/、git に入れない)
  diff-report.sh   差分の検査の結果を集計する
  SelfHost.csproj  .NET の library としての build (差分の検査が使う)
```

### 17.2 提案からの差

| 提案 | 実装 | 理由 |
|---|---|---|
| 後半 (IL と IL→Lua) をサブセットに移して共有する (S1、S2) | 共有しない。セルフホストの compiler は bound tree から Lua を直接出す。runtime (`runtime/tinysystem.lua` と `__tcs_*` の helper) は参照実装の出力から取った前置きを共有する | 参照実装の後半は Roslyn に依存しないが、`ImmutableArray`、継承、例外を使っていて、移すと参照実装の全体に手が入る。前置きを共有すれば、出力の形 (class の table、struct の `__copy`、generic の記述子など) を合わせるだけで同じ runtime の上で動く |
| 構文木は種類ごとの sealed class と interface (§6.3) | 種類の enum と、slot (A〜D、List、List2、List3) を持つ 1 つの `Node` class。束縛の結果 (型、symbol、定数) も同じ Node の field に書く | 継承の無いサブセットでは、種類ごとの class にすると走査のたびに型 switch が要る。1 つの class なら走査が素直に書け、Lua でも table 1 つになる |
| G1〜G4 (構文木、束縛、IL、Lua のテキストを比べる) | 実行の結果を比べる (§17.3)。テキストは比べない | IL を共有しないので、テキストを揃える意味が薄い。実行の結果が揃えば利用者に見える違いは無い |
| BCL の面を `tools/BclSurfaceGen` で生成する (§6.4) | `Builtins.cs` に表として手で書く。`TinySystem/Linq.cs` はソースのまま読む (user が `using TinySystem.Linq` するときだけ) | 差分の検査の母集団が使う member だけを足していけば足りた |
| 準拠検査 (TCS1001〜1003) とフロー解析 (§6.6、§6.7) | 構文で決まる TCS1001 は `Compliance.cs` が参照実装の Shared の規則を写して判定する。型で決まるもの (TCS1002、TCS1003、多次元配列の型) は Declarer と Binder が判定する。フロー解析は到達可能性 (CS0161、CS1643、CS0163) と確実な代入 (CS0165) | 確実な代入は C# の規則を写した (条件式の真偽ごとの状態、loop と break / continue、switch、lambda、到達できない場所)。追うのは初期化子なしで宣言した local だけで、pattern と out var の local は宣言した時点で代入済み、struct と tuple の local は field ごとの代入を追わずに見ない |
| 固定点は `L1 == L2` (§10) | stage2 (stage1 が作った Lua) と stage3 (stage2 が作った Lua) の一致 | 出力の形が参照実装と違う (同じ runtime の上で動くが、名前や一時変数が違う) ので、stage1 と stage2 は一致しない |
| 3 経路は Lua / C / .NET (§10) | 提案の通り 3 経路。.NET で動かした出力と、tcs2c で C にした build の出力が、どちらも stage2 と byte で一致する | compiler は object を key にする Dictionary を使わない (tcs2c の Dictionary の key は int と string)。型の scope は TypeDeclSym の field に、`?.` の受け手は Node の field に持つ |

### 17.3 gate

- 差分の検査: `TCS_SELFHOST_DIFF=1` のとき、同じ入力をセルフホストの compiler (.NET で動かす) にも通し、参照実装と比べる。`run-tests.sh` が全てを有効にして走らせる
  - 実行の比較: 参照実装が変換して実行した入力は、セルフホストの出力も実行して結果を比べる。母集団は `TranspileAndRun` 系の test helper (Transpiler.Tests)、tcs2c.Tests の Lua の実行、fuzz の program (`TCS_FUZZ=1`)、C# の仕様の例のうち実行するもの (`TCS_SPEC_CONFORMANCE=1`)
  - 受理と拒否の比較: C# の仕様の例のうち実行しないものは、参照実装が受理した例 (InCompile) をセルフホストも受理し、拒否した例 (Diag、CsErr) をセルフホストも拒否するかを比べる。拒否には `tcs check` と同じく準拠の警告 (TCS1003) を含める
- 結果は 一致 (equal) / 受理が一致 (compiles) / 拒否が一致 (rejects) / Roslyn だけが判定する検査 (deferred、§17.6) / 未対応 (unsupported、TCS1900) / 厳しい (stricter、セルフホストだけがサブセットの違反を報告した) / 拒否 (rejected、参照実装が受理した入力をセルフホストが拒否した) / 見逃し (missed、参照実装が拒否した入力をセルフホストが受理した) / 食い違い (mismatch) / crash (compiler 自身が落ちた) / 比べられない (test の側が参照実装の overload の名前を直接呼ぶ) に分ける。拒否、見逃し、食い違い、crash は test の失敗にする。厳しいは参照実装の検査の取りこぼしで、見つけたら参照実装を直す
- `TCS_SELFHOST_REPORT=path` で結果を 1 行ずつ書き出し、`SelfHost/diff-report.sh path` で集計する。`TCS_SELFHOST_DIFF_RECORD_ONLY=1` は失敗を記録だけして test を落とさない (母集団の全体を 1 回で集める)
- bootstrap: `SelfHostBootstrapTests` が stage1〜3 を作り、stage2 と stage3 の一致と、.NET で動かした出力と stage2 の一致を確かめる。`SelfHost/build.sh` は同じ固定点に加えて、tcs2c で C にした build の出力と stage2 の一致を確かめる。`run-tests.sh` は C compiler のある環境で build.sh を走らせる
- 実装した時点の結果: `run-tests.sh` と同じ設定で、実行を比べた 575 件は全て一致した。受理と拒否を比べた仕様の例は、受理の一致 99 件、拒否の一致 448 件、deferred 7 件、未対応 1 件 (名前が同じで型引数の数が違う型)。拒否、見逃し、食い違い、crash、厳しいは 0。fuzz を 300 seed に増やしても全て一致した。固定点に達する
- 差分の検査で見つけた参照実装の不具合は直した。`float.IsNaN` などの数値の static method (`tcs check` を通るが、Lua は `math.IsNaN` (nil) を呼んで止まった) は TCS1002 に、多次元配列 (`tcs check` を通るが、Lua は添字で範囲外の error になった) は TCS1001 にした (T272、T276)。多次元配列の 2 件は、参照実装も拒否するようになって比べる対象から外れた

### 17.4 測定

- compiler 自身 (約 1.7 万行、13 ファイル) の変換は、lua32 の上で約 1.3 秒 (`build.sh` が出す compiled の時間。準拠検査とフロー解析を足す前は 0.75〜1.1 秒)、tcs2c で C にした build で約 0.1 秒 (process の起動を含む)。1 ファイルの body edit が 100ms 前後という §12 の見当と矛盾しない
- C の build は、生成した C が約 3.5MB、gcc -O2 の build に約 11 秒、実行ファイルが約 550KB
- サブセットで compiler を書くことは、§7 の表の通りに書けた。継承が無いことは 1 つの Node class で、例外が無いことは診断を積んで続けることで、StringBuilder が無いことは `List<string>` と `string.Join` で済んだ
- 書いていて手間がかかったのは、string の位置の API が無いことで、lexer と書式指定子の解析は `Utf8Bytes` の byte を読む形にした

### 17.5 増分の session と Step の driver

- `Session` はファイルを足し (`SetSource`、`SetReference`、`SetLibrary`)、差し替え、外し (`Remove`)、compile する。parse は内容が変わったファイルだけをやり直し、parse したままの木を持つ。束縛は木を書き換えるので、compile のたびに木の複製 (`Node.CloneTree`) を組み立て、宣言、束縛、出力は全体をやり直す
- compile は Step に分かれる: 変わったファイルの parse を 1 つずつ、組み立てと準拠検査、宣言、束縛、出力。`Start` は途中の compile を捨てて始め直すので、host は Step の合間に新しい編集を受け取れる
- C の build (tcs2c) は Start と Step を別の entry にし、host が Step を終わりまで呼ぶ。entry から戻るたびに GC の frame の境界になり (§8.2)、session は static field に置いて段をまたぐ。compiler 自身の変換は 18 段で、段の間の managed heap は最大で約 51MB (`TCSC_HEAP=1`)
- `Compiler.CompileWithLibraries` は session を 1 回回すだけの入口にした。経路は 1 つで、bootstrap の 3 経路の一致がそのまま session の出力を確かめる
- 測定 (lua32、compiler 自身の約 1.7 万行、`Host/bench-session.lua`): cold 約 1.7 秒、変更なし約 0.6〜0.75 秒、小さいファイル (Text.cs) の編集の後 約 0.6 秒、大きいファイル (Binder.cs、全体の 4 分の 1) の編集の後 約 1.3 秒。変更なしの時間は、全体の宣言、束縛、出力の時間
- 束縛を変わったファイルの分だけにする (宣言の要約を比べ、本体だけの編集なら他のファイルの束縛と出力を持ち越す) のは、束縛が木を書き換えることと、出力が program 全体 (static 初期化の順序) で決まることから、今はやらない。ゲームの規模 (数千行) では全体をやり直しても §12 の見当 (100ms 前後) に収まる見込みで、測ってから決める

### 17.6 Roslyn だけが判定する検査

出力の Lua に影響しない C# の検査は、セルフホストでは判定せず、IDE と `tcs check` と CI の参照実装 (Roslyn) に任せる (§6.6)。
差分の検査は、参照実装の診断がこの一覧の id だけなら、セルフホストが受理しても見逃しにしない (deferred)。
一覧に無い id をセルフホストが見逃したら test が落ちる。

| id | 内容 |
|---|---|
| CS0050 | accessibility の不整合 (public な method が internal な型を返すなど) |
| CS0454、CS0455、CS0456、CS0701、CS0425 | 型引数の制約の形 (制約の循環、struct 制約の型引数を制約に使う、sealed な型を制約に使う、制約の不一致) |
| CS1961、CS8427 | variance (in / out) の妥当性と、variant な interface の中の型 |
| CS8903 | 修飾子の組み合わせ (init accessor の readonly) |
| CS8125 | tuple の要素の名前 (Item2 を別の位置に使う) |

### 17.7 lub での利用

lub の C# のプログラム (cs-lib、`lub_stub.cs` を `--ref` にした samples と tests/cs) をセルフホストの compiler で変換できるようにし、lub の上で参照実装の出力と同じに動くことを確かめた。

- host (`--ref`) の規則を参照実装 (Shared/LuaNaming.cs) と同じにした: 型は namespace と入れ子の型を小文字で `.` でつなぐ (`Lub.Gfx` → `lub.gfx`)、member は snake_case、enum の member は UPPER_SNAKE で、値は host が持つので定数に畳まない (switch の label には使える)。property は host の table の field、instance の method は `:` で呼ぶ。data class の `new` は host の名前を key にした table。host の class の継承 (`GlyphMesh : MeshData`) と属性の宣言 (`: Attribute`) を受ける (`SelfHostHostApiTests`)
- 確かめた方法: lub を Linux で build し (SDL の offscreen と mesa の lavapipe)、samples 31 本を参照実装の出力とセルフホストの出力の両方で 30 frame 動かした。frame ごとの digest (C API の呼び出しの構造) と capture の PNG が全て byte で一致した。tests/cs の runtime テストは 48 本が両方で通り、意図した Lua の error の数も同じ。残る 1 本 (TestManyDraws) は両方で同じく segfault する (lavapipe の環境)
- `Host/tcsc.lua` は tcs の CLI のうち lub が使う形 (`check`、`-o`、`--entry`、`--ref`、`--watch`、`--reload-chunks`、`--warnaserror`) を受ける。`--watch` は入力を 250ms ごとに読み直し、変わったファイルだけ parse し直して出力を書き直し、`@@tcs_restart_required` を書く (reload chunk は作らない)
- lub の側 (branch `tcs-selfhost`): PATH に dotnet が無いとき (`LUB_TCS_SELFHOST=1` なら常に)、lub は自分を `lub tcsc` で起動し、セルフホストの compiler を lub の Lua (LUA_32BITS) の別の state で動かす。`scripts/build-tcs-selfhost.sh` が lub の Lua で bootstrap し (stage2 == stage3。tcs の lua32 で作った stage2 と byte で同じ)、release の一式の `bin/tcs/selfhost` に入れる。dotnet の無い環境で `lub <sample>.csproj` を動かし、参照実装の出力と同じ digest と絵になること、保存で VM が作り直されることを確かめた
- web: `Web/build.sh` が Lua 5.5 (LUA_32BITS) と tcs の runtime の C と glue を Emscripten で 1 つの ES module にする。`Web/test.mjs` (node) で、compiler 自身の変換が stage2.lua と byte で同じこと (5 番目の経路) と、1 ファイルの編集の後の結果が 1 から変換した結果と同じことを確かめる。compiler 自身の変換は cold で約 2.4 秒、最大のファイルの編集の後で約 1.7 秒
- lub の playground (lub の branch `tcs-selfhost`): `?compiler=selfhost` で、.NET wasm の worker の代わりにこの wasm を worker で動かす (同じ要求 open / update / linkSnapshot を受ける。編集はいつも fresh player、補完と hover は無い)。headless Chromium で playground の 30 サンプルが描画され、C# の編集で player が作り直されることを確かめた (`npm run verify-tcs-selfhost`)

## 18. 単一の実装へ移す (2026-11-01)

17 節の実装は、参照実装の後半 (IL と Lua の出力) を共有せず、セルフホストの compiler が Lua を直接出す形にした (17.2)。
その結果、セルフホストの出力は参照実装と byte では一致せず、意味の違いも残った。
例は次の通り。
- float の定数: 参照実装は 32bit に丸めた値を double として往復できる最短の表記で出す (`19.4f` → `19.399999618530273`)。セルフホストは丸めずに出していた (`19.4`)。畳み込みも 64bit で計算していた
- `--ref` の enum の名前の表 (`String.FormatEnum` の第 2 引数): セルフホストは値を引けず `"Index:0"` のようにしていた
- 文字列の制御文字の escape: 参照実装は `\xXX`、セルフホストは `\ddd`

実行の結果を比べる検査 (17.3) は、入力が通った所の違いしか見えず、これらを見逃した。
実装を 2 つ持つ意味は無いので、tcs の実装を TinyC# で書いた 1 つにまとめる。
役割は次の通り。
- compiler: TinyC# のサブセットで書いた 1 つの実装。dotnet、Lua、C (tcs2c)、wasm のどこでも同じソースが動く。以後は Roslyn や参照実装の形に縛られずに伸ばす
- Roslyn と .NET: lub と tcs の外の道具と検証役に回す。IDE の analyzer、準拠の検査の linter、受理と拒否の判定 (正しい C# かどうかの正解)、同じソースを .NET で動かした結果を意味の正解にする比較

移す順序は次の通り。
1. IL の型と、IL を Lua にする処理 (lowering と emit) をサブセットに写す (`SelfHost/Compiler/Il.cs`、`SelfHost/Compiler/LuaGen.cs`)。参照実装の IL を写した IL に変換して同じ入口で描き、出力と状態 (Lua の行、source map、continue の label、lowering の temp) が一致することを確かめる (`TCS_LUAGEN_CHECK=1`、`Transpiler/LuaGenCheck.cs`)
2. IL の組み立て (Roslyn の IOperation から IL を作る部分) と、型の出力 (class の table、constructor、struct、record、static の初期化、型の local) を、セルフホストの束縛済みの木から作る形でサブセットに写す。規則は参照実装のものを写し、独自の規則は入れない。移す間は参照実装と出力を byte で比べ、全ての母集団で一致させる
3. tcs の CLI (変換、check、`--watch` の reload chunk、増分の session、tcs2c、wasm) をサブセットの実装だけで動かし、Roslyn を部品として使う参照実装を消す。17 節の独自の後半 (`SelfHost/Compiler/Emitter.cs`) も消す。比べる相手は .NET での実行と Roslyn の受理の判定に切り替える
4. lub を単一の実装に合わせる

### 18.1 手順 1 の結果

- `Il.cs`: 参照実装の IL を、継承の代わりに interface (`IlExpr` / `IlStat`) とそれを実装する record で表した。ImmutableArray は List、if の腕と with の上書きは record (`IlArm` / `IlOverride`) にした。Origin は positional parameter の最後に置き、同じ構文から作った IL は同じ `IlPos` を共有する (--trace は同一性で比べる)
- `LuaGen.cs`: 参照実装の `LuaEmitter.IlEmit.cs` と `LuaEmitter.IlLower.cs` と、それが使う出力の状態 (行、字下げ、source map、continue の label、--trace、文字列の escape) を写した。サブセットに無いもの (例外、partial class、ローカル関数、範囲、リストパターン、StringBuilder) は書き換えた。到達しないはずの分岐は例外の代わりに `InternalErrors` に記録する
- 検査: `TCS_LUAGEN_CHECK=1` で Transpiler.Tests の全体 (1273 件)、fuzz と仕様の例の sweep、compiler 自身、lub の Baseball24 (764 回の比較) が一致した。`run-tests.sh` はこの検査を常に入れる
- セルフホストの bootstrap は、record と interface を使う新しいファイルを含めても固定点 (stage2 == stage3) と C の build の一致を保つ

### 18.2 手順 2 の結果

- IL の組み立てを `SelfHost/Compiler/IlBuild.cs` に、型の出力 (class / struct / record struct / record、constructor、property の accessor、演算子、interface の一覧、static の pre-zero と初期化子) を `SelfHost/Compiler/LuaEmit.cs` に、chunk の前置きを `SelfHost/Compiler/LuaPrelude.cs` に写した。参照実装の規則 (local の改名、overload の名前、型引数の既定値、struct の copy 地点、引数の copy の省略、副作用のある代入先の固定、?.、タプルと分解、書式、enum の名前の表) を、Roslyn の IOperation の代わりにセルフホストの束縛済みの木から作る
- Roslyn の情報に合わせるため、束縛の側も直した。System.Math は double、TinySystem の facade (Math / String / List / Dict / Random) は自分の表を持つ。null と値型の腕の条件式は変換先の型へ各腕を変換する。uint の範囲の 10 進の literal を受ける。--ref の enum の値も決める
- static の初期化の順は参照実装の StaticInitDependencies を `SelfHost/Compiler/StaticInitDeps.cs` に写した。enum の名前の表の並びは .NET の Array.Sort (introsort) と同じ手順にした (17 個以上の member で同じ値が並ぶとき)
- タプルは参照実装と同じく `TinySystem/ValueTuple.cs` を library として読み、`System_ValueTuple2` などの struct として出す (タプルの構文がある時だけ)
- 準拠の検査のうち型を見る規則 (out の引数、CompareOrdinal、checked、struct と値の boxing、interface と generic な型の検査、値で比べる key、overload の名前の衝突、host の interface、interface の member の実装、byte の丸め、string.Join の要素など) を `SelfHost/Compiler/SemanticChecks.cs` に写した
- 検査: Transpiler.Tests (794 件) と tcs2c.Tests (335 件) の入力と lub の 31 の sample が参照実装と byte で一致する。参照実装が拒否する入力 (1009 件) はすべて拒否する (診断の code は Roslyn の CSxxxx と違うものがある)。bootstrap は参照実装の出力 (stage1)、セルフホストの出力 (stage2、stage3)、C の build の出力がすべて同じ
- 見つけた tcs2c の不具合: switch の section ごとに同じ名前の pattern 変数 (`case A x: ... case B x:`) を書くと、C の出力で全ての section が最後の `x` (型も最後のもの) を参照する。参照実装の IL は pattern 変数を名前で持つので、C の emitter が名前で引くと取り違える。セルフホストのソースは名前を分けて避けた (tasks.md の T283)


### 18.3 手順 3 の途中 (変換と check の切り替え)

- `Transpiler.TranspileWithDiagnostics` (CLI の変換と `tcs check`、テストの入口) はセルフホストの `Session` で変換する。Roslyn を部品にした旧い実装は `TCS_SELFHOST_PRIMARY=0` で選べる (比べる相手として残し、手順 3 の終わりに消す)
- `--module` (定義した型の table を返す)、`--instance-registry`、`--trace`、型の local を置かない形、source map を `Session` の選択肢と結果に通した
- 診断の文言と並びを参照実装に合わせた。テストが利用者に見える振る舞いを決めているので、セルフホストの診断もそれに従う
  - 構文の名前は Roslyn の SyntaxKind の名前 (`TryStatement`、`DelegateDeclaration` など)。出力の段で見つかる式は `expression: BitwiseAndExpression` の形
  - 並びはファイルごとに TCS1001、TCS1003、TCS1002 をそれぞれ位置の順。C# の誤り (CSxxxx) があればそれだけを報告する
  - サブセットの外の構文 (TCS1001) を読み飛ばした後も宣言と束縛を続け、中の API と構文を報告する。読み飛ばしの巻き添えで出うる C# の誤り (名前の解決、overload、変換など) は、サブセットの診断があるときは報告しない
  - 型の表示は C# の表示 (`System.Collections.Generic.List<int>`、namespace で修飾した user の型)
- 「.NET に有るがサブセットに無い API」(TCS1002) を見分けるため、BCL の型と member の名前の表 `SelfHost/Compiler/BclCatalog.cs` を参照アセンブリから生成して持つ (`Transpiler.Tests/BclCatalogTests.cs`、`TCS_UPDATE_BCL=1` で作り直す)。表に無い名前は C# の誤り (CS0103 など) になる。組み込みの method の overload が合わないときも、.NET に同じ名前があれば TCS1002 にする
- 準拠検査を足した: collection の null (TCS1003) を参照実装の規則のまま `SemanticChecks` に写した。ほかに bool の `& | ^`、Enum.ToString の書式、補間の揃えと書式、instance の method group、ToString を持たない型の文字列化、object の member、delegate の型の検査と結合、`ref` の戻り値と local、query 式、top-level の `args` など
- static の初期化の依存の解析が、組み込みの表の Symbol (compile をまたいで共有する) にも通し番号を振っていた。並列に compile すると出力が揺れたので、組み込みの method は辿らない
- 検査: 3 つのテストプロジェクト (Transpiler.Tests、tcs2c.Tests、Analyzers) はセルフホストを既定にして通る (root 権限で動かすと落ちる既知の 1 件を除く)。母集団 (794 件、335 件、lub の 31) の出力は Roslyn の旧い実装と byte で一致し、旧い実装が拒否する入力はすべて拒否する。bootstrap は固定点と C の build の一致を保つ

### 18.4 手順 3 の途中 (C backend の入力の切り替え)

- `IlExport.Export` (tcs2c と hot reload の判定の入力) はセルフホストの `Session` の `ExportOnly` で作る (`SelfHost/Compiler/IlExport.cs`)。セルフホストの IL を参照実装の IL の record へ写すのは `Transpiler/SelfHostIlExport.cs`。旧い実装は `IlExport.ExportRoslyn` として比べる相手に残す
- generic の単相化 (`SelfHost/Compiler/Specialize.cs`) を木の上で写した。閉じた型と method の copy を作り、型引数を置き換えて束縛し直す。名前 (`TcsClosedN_Name`) の番号は参照実装と同じく見つけた順で、interface の method を実装する copy の名前も数に入れる。走査の順は元のソースの位置で、足した copy は足した順に後ろへ置く
- 単相化した `ValueTuple` の struct の分解は、タプルと同じく `Item1` からの field を読む。型で修飾した入れ子の型 (`Outer.Box<int>`) の copy は外側の型に置くので、修飾を残す
- --ref の型を field や parameter の型の構文だけで使っても、外部の class として出す。--ref の本文のサブセットの外の構文は報告しない (本文は使わない)
- static の初期化子の hash は空白と comment を除いた token の列で決める
- 修飾しない BCL と TinySystem の型 (`List`、`Action`、`Console` など) は、その namespace を using するか、その namespace の中に書いたときだけ見える。using の無い入力を通していた
- IL の出力で受け付けていて Lua の出力で拒否していた入力 (配列への cast、bool の `&` など) は、どちらの backend でも拒否する。受け付ける言語を backend で揃える
- 検査: 母集団の IL の構造は、単相化なしで 798 件 (残る 6 件は上の拒否)、単相化ありで 335 件の全てが一致する。tcs2c.Tests (541 件) と tcs2c の verify の 5 本、bootstrap が通る

### 18.5 手順 3 の途中 (増分 session と hot reload の切り替え)

- `IncrementalCompilationSession` (`--snapshot` と playground の増分 compile) はセルフホストの `Session` で compile する。parse は内容の変わったファイルだけをやり直し、宣言、束縛、出力は毎回全体で行う。出力はファイルごとの module (`Session.ModuleArtifacts`、`CompileResult.Modules`) で、型ごとの記録 (宣言の行と static の初期化の行の範囲、instance の形、static field、初期化の依存) を持つ
- fast path の判定 (編集が method の本文に収まり、本文の外の token が変わらない) は `SelfHost/Compiler/Surface.cs`。fast path では編集した module だけを差し替え、それ以外は全 module を差し替えて restart の要否 (ReloadRules) を判定する。method 単位の splice は持たない (全体を出し直しても速い)
- 補完と hover は Roslyn をエディタの道具として使う。session の今のソースから Roslyn の compilation をその都度作る (compile の結果には関わらない)
- reload chunk (`--watch --reload-chunks`) の field の初期化子の Lua はセルフホストの `LuaGen` で書く
- using の無いファイルが library の型を修飾した名前で書く場合も library を読む
- 検査: Transpiler.Tests (fuzz の 2 つを 200 件で回したものを含む) が通る。WasmCompiler の build と bootstrap も通る

### 18.6 手順 3 の終わり (Roslyn を部品にした実装を消す)

- Roslyn を部品にした変換 (`LuaEmitter.*`、`IlExport` の Roslyn 版、`IlSpecialization`、static の初期化の依存の解析、診断の方針) と、セルフホストの旧い出力 (`SelfHost/Compiler/Emitter.cs`) を消した。`TCS_SELFHOST_PRIMARY`、`TCS_LUAGEN_CHECK`、`TCS_SELFHOST_DIFF` (同じ実装どうしの比較になった) も消した
- 検証の oracle は Roslyn と .NET の実行にした。仕様の例は Roslyn が C# として受け付けるかを判定し、受け付けた例を tcs が C# の誤り (CSxxxx) で拒否したら compiler の不具合とする。生成と仕様の例の実行は .NET での実行と出力を比べる (`TCS_DIFFERENTIAL`、`TCS_FUZZ`)
- この oracle で、サブセットの外の正しい C# にセルフホストがサブセットの診断 (TCS100x) ではなく構文や型の誤りを出す 70 件が見つかった (以前の比較は受理か拒否かだけを見ていた)。label の付いた文、`ref struct`、属性の対象 (`[return: X]`)、多次元の配列の初期化子、型引数を持つ interface の明示的な実装、event の accessor、throw 式、stackalloc、tuple の pattern、switch の arm の `when b =>`、using の別名で修飾した名前、型引数を書かない generic の型 (`typeof(X<>)`)、generic な local function などを読み飛ばして TCS1001 にした。サブセットの外の構文の巻き添えになる C# の誤り (CS0021、CS0023、CS0030、CS0305、CS0815、CS1579) も、サブセットの診断があるときは報告しない
- 受理の後退も直した: delegate の生成式 (`new Action<int>(M)`、宣言した delegate の `new D(M)`) と、式本体の分解の代入 (`=> (X, Y) = (x, y);`)。`new object()` と `new string(...)` は TCS1002 にした (旧い実装は `new object()` を受け付けたが、出力は runtime に無い `Object.new()` を呼んでいた)
- セルフホストだけの診断 TCS1900 は TCS1001 にした。同じ名前で型引数の数だけが違う型は TCS1001 (GenericArityOverload)。旧い実装は受け付けたが、両方の型が同じ Lua の名前になり後の型が前の型を上書きしていた (仕様の例の分類が InCompile から Diag へ 1 件動いた)
- `global using` は --ref で与えたときに全てのファイルに効く (仕様の例の暗黙の using)。user のソースでは今までどおり TCS1001
- BCL の名前の表に System.Threading.Thread、System.Linq.Expressions、System.Net.Sockets の型を足した (TCS1002 と報告するため)

## 19. ゴール: tcs 全体を TinyC# で書き、tcs2c でネイティブにする (2026-11-01)

セルフホストの到達点は、compiler だけでなく tcs 全体を TinyC# で書き、tcs2c で C にしたものを唯一の配布形にすることである。
.NET は開発の道具 (bootstrap の最初の段、テスト、Roslyn の analyzer と補完、検証の oracle) だけに残る。

測った速さ (compiler 自身の約 2.9 万行を Lua にする。出力は 3 つとも同じ):

| 経路 | 変換 | 小さなファイル 1 つ |
|---|---|---|
| tcs2c で C にした tcsc | 2.8 秒 | 0.005 秒 |
| .NET (Debug、JIT を含む) | 6.5 秒 | 0.17 秒 |
| Lua の上のセルフホスト | 12.5 秒 | - |

これで片づくこと:

- NativeAOT の tcs が要らなくなる。tcs2c の出力は素の C なので、lub と同じ C の toolchain (MSVC、clang、emscripten) で全ての platform に作れる
- lub は compiler を library として組み込み、別の process を起動せずに変換できる。dotnet の無い環境の経路と速さの問題も同時に消える
- playground の変換は同じ C を emscripten で wasm にしたものにでき、.NET の wasm の bundle (と .NET 10 の wasm AOT の GC の不具合) から外れる

### 19.1 手順

- G1: tcs の host (今の `Transpiler/` の CLI、`--watch`、reload chunk、増分 session と module の snapshot、source map) を TinyC# に写す。OS に触る所 (ファイルの読み書きと原子的な置き換え、ファイルの同一性、変更の監視、時刻) は `--ref` の stub と C の host 関数 (`SelfHost/Native/host.c`) にする。.NET の `Transpiler/` は同じ TinyC# の host を呼ぶ薄い入口になる
- G1 の配布: tcs2c で C にした tcs を CI で Linux、macOS、Windows に作り、出力が Lua と .NET の経路と同じことを確かめる。release は NativeAOT の代わりにこれを配る
- G1 の lub: lub が C の compiler を library として組み込む。playground の変換も同じ C の wasm にする
- G2: tcs2c (C# で約 9,600 行、Roslyn は使わない) を TinyC# に写し、tcs2c が自分自身を C にできるようにする。tool chain 全体が .NET 無しで自分を組み立てられる

### 19.2 G1 の host を TinyC# に写した結果 (T284、2026-11-01)

tcs の CLI の全体を TinyC# にした。.NET、Lua、C の 3 つの host が同じ TinyC# の CLI を動かす。

```
SelfHost/
  Compiler/        compiler (字句から Lua の出力、IL、単相化まで)
  Tool/            CLI の本体 (TinyC#)
    Cli.cs         引数の解釈、check、1 回の変換、--watch (reload chunk と restart の判定、host への message)、--map-stacktrace
    Modules.cs     module の artifact、bridge snapshot (--snapshot と playground)、増分 session (ModuleSession)
    Reload.cs      reload chunk の組み立てと、live に当てられない変更の規則 (ReloadRules)
    SourceMapText.cs  source map の JSON の読み書きと stack trace の注釈
    TcsHost.cs     host の関数の宣言 (--ref の stub。ファイル、標準入出力、環境変数、時刻、待つ、終了コード)
  Native/host.c    C の host (tcs2c の --lib の出力を include し、TcsHost を実装して Cli.Start と Cli.Step を呼ぶ)
  Host/tcsc.lua    Lua の host (`lua tcsc.lua <tcs.lua> <tcs の引数>...`。TcsHost を global の tcshost として実装する)
Transpiler/
  DotnetHost.cs    .NET の host (TcsHost の実装と、Cli を動かす TcsHost.Run)
  Program.cs       Main は TcsHost.Run を呼ぶだけ
  IncrementalCompilationSession.cs、ModuleArtifacts.cs、HotReload.cs
                   WasmCompiler とテストのための薄い入口 (Tool の ModuleSession、ModuleLinker、ReloadChunk を呼ぶ)
```

- host は `Cli.Start` を 1 回、`TcsHost.SetDone` まで `Cli.Step` を呼ぶ。compile は Session の段ごとに 1 回の Step で、tcs2c の GC は entry から戻るたびに回収する。`--watch` の待ちも Step の中の `TcsHost.Sleep` で、host の loop は同じ形のまま
- tcs と一緒に配るファイル (`runtime/tinysystem.lua`、`runtime/module_registry.lua`、`lib/TinySystem/*.cs`) は `TcsHost.ToolFile` で読む。.NET は今までと同じ (lib は埋め込み、runtime は実行ファイルの隣)。C と Lua は環境変数 `TCS_HOME` の dir か、実行ファイル (Lua は tcs.lua) の dir から読む
- tcs2c に合わせた書き方: CLI の仕事 (CheckRun、OnceRun、WatchRun、SnapshotRun) は継承を使わず、Cli が型ごとの field に持って分ける。定数の配列は `"a b".Split(' ')` で作る
- Lua の host は stat を持たないので、ファイルの同一性は調べず (`-`)、変わったかの印はファイルの内容で比べる
- bootstrap (`SelfHost/build.sh`): stage1 は .NET の tcs が tcs (Compiler と Tool、TcsHost.cs は --ref) を Lua にしたもの、stage2 と stage3 は Lua の host の上の tcs が作ったもの。stage1 == stage2 == stage3 で、tcs2c で C にした `out/tcs` の出力も同じ。`SelfHostBootstrapTests` は .NET と Lua の経路を確かめる
- `--snapshot`、`--sourcemap`、`check`、`--watch --reload-chunks --instance-registry` を C の tcs で動かし、.NET と同じ出力と host への message になることを確かめた
- 速さ (tcs 自身の約 3.2 万行を Lua にする): C の tcs で約 1 秒 (managed heap の最大は約 117MB)、.NET (Debug) で約 1.9 秒、Lua の host で約 10 秒。小さなファイル 1 つは C の tcs で約 0.01 秒

### 19.3 配布と web (T285、T286 の一部、2026-11-01)

- `SelfHost/native.sh` が C の tcs を作り、.NET の tcs と出力を比べる。CI は Linux (cc)、macOS (clang)、Windows (clang) で動かし、tcs と一緒に配るファイル (runtime/、lib/) と一緒に成果物にする。Windows は GNU C の文の式を使うので MSVC ではなく clang で build し、標準出力を binary mode にして .NET と同じ改行にする
- lub の release の一式は NativeAOT の tcs の代わりに C の tcs を `bin/tcs/tcs` に入れる。lub は実行ファイルの隣の `tcs/tcs` を最初に起動する。lub は C の tcs を library として link しない (link すると lub の build に tcs2c と dotnet が要る)。同じ CLI の実行ファイルを起動する形で、dotnet の無い環境の問題も消えた
- web: `SelfHost/Web/build.sh` が tcs2c の C と `tcs_web.c` を emcc で wasm にする。JS (`tcs.mjs`) は要求を引数に並べて `Playground.Call` を呼び、応答を受け取る (open、update、link。応答の形は WasmCompiler の SessionExports と同じ)。ファイルは無く、runtime と同梱の library は JS が先に渡す
- lub の playground の変換はこの wasm を worker で動かす (変換と hot reload は .NET 版と同じ)。補完と hover は .NET の wasm の Roslyn が、compile しない session (`EditorSession`、テキストだけを持ち Roslyn の compilation を作る) で答える。`?compiler=dotnet` なら変換も .NET の wasm で行う
- これで G1 は終わり。.NET は bootstrap の最初の段 (tcs2c が tcs を C にする)、テスト、Roslyn の analyzer と補完と hover、検証の oracle だけに使う。残りは G2 (tcs2c 自身を TinyC# にする、T287)
