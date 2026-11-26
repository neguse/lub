# tcs のセルフホスト設計

> Status: 実装。tcs の compiler、CLI、C backend、tcs VM の backend は TinyC# で書いた 1 つの実装 (`src/tcs/` の `Compiler/`、`CBackend/`、`VmBackend/`、`Tool/`) で、.NET、C (tcs2c で tcs 自身を C にしたもの)、wasm で同じソースが動く。gate は bootstrap の固定点と、.NET の tcs と C の tcs の出力の一致 (§9.1)、Roslyn の受理の判定と .NET での実行を正解にした比較 (§9.2)。
>
> Scope: tcs 自身を TinyC# のサブセットで書き、tcs 自身で C にして動かす構成。

## 1. 結論

- tcs の実装は TinyC# で書いた 1 つ。字句、構文、宣言、束縛、準拠検査、IL の組み立て、generic の単相化 (`Compiler/`)、IL を C にする backend (`CBackend/`)、IL を tcs VM の `.tcsb` にする backend (`VmBackend/`)、CLI (`Tool/`) のどれも同じサブセットで書く
- 同じソースが 3 つの host で動く。.NET (`src/tcs/Dotnet/` の host。出力は `tcs.dll`)、C (tcs2c で tcs 自身を C にした native の tcs)、wasm (同じ C を emcc で build したもの)
- Roslyn と .NET は compiler の外の道具として使う。IDE の Roslyn Analyzer (`src/tcs-analyzers` は `Compiler` を netstandard2.0 で取り込み、`tcs check` と同じ診断を出す)、playground の補完と hover、検証の正解 (C# として正しいかの判定と、同じソースを .NET で動かした結果) の 3 つ
- bootstrap の最初の段は .NET の tcs が tcs 自身を C にすること。compiler のソースは常に .NET の build で動くので、seed の binary をリポジトリに置かない

## 2. 何のためにやるか

- 開発ループと配布から .NET を外す。配る tcs は C にした実行ファイルと、一緒に配るファイル (`runtime/` の C、`lib/` の TinySystem のソース) で、dotnet の無い環境でも変換できる (§10)
- compiler 自身が最大の TinyC# のプログラムになる。サブセットの書き味、tcs VM と C と .NET の一致、tcs2c の GC を大きな実プログラムで試せる
- 実装が 1 つなので、受理する言語、診断、IL が host ごとに変わらない

## 3. objective との関係

objective の選定基準 6 は「C# コンパイラ (Roslyn) が受理しないものを tcs は受理せず、tcs 独自の緩和を入れない」と書く。
tcs の compiler は自前の型検査器を持ち、基準 6 を次の形で保つ。

- tcs の compiler が受理する集合は、Roslyn が受理する集合の部分集合に限る。サブセットの外の C# はサブセットの診断 (TCS1001〜1003) で報告し、Roslyn が受理する C# を C# の誤り (CSxxxx) で拒否したら compiler の不具合とする (§9.2)
- 意味 (IL) に影響しない C# の検査の一部は tcs の compiler では判定せず、IDE の C# compiler (Roslyn) に任せる (§9.3)

## 4. 全体構成

```text
tcs/
  Compiler/            compiler (namespace Tcs)
    Text.cs              ソース (Utf8Bytes)、行の表、診断
    Lexer.cs             字句 (補間、verbatim、raw、raw の補間を含む)
    Syntax.cs            構文木 (種類の enum と、slot を持つ 1 つの Node class)
    Parser.cs            再帰下降の parser
    Types.cs             型、symbol、定数
    Declarer.cs          宣言の表、型の解決、host (--ref) の名前
    Builtins.cs          BCL と TinySystem の member の表
    BclCatalog.cs        .NET の参照アセンブリにある BCL の型と member の名前 (生成物)
    Compliance.cs        構文だけで決まる準拠検査 (TCS1001)
    SemanticChecks.cs    型の決まった木の上の準拠検査
    Binder.cs            名前の解決、変換、overload 解決、型推論、lambda、pattern、定数、到達可能性
    DefiniteAssignment.cs  確実な代入 (CS0165)
    Il.cs、IlBuild.cs    IL の型と、束縛済みの木からの組み立て (IlContext、IlLiterals、IlNames、IlTree、IlTypes、LocalNames は道具)
    IlExport.cs          tcs c と tcs vm への入力 (型ごとの metadata と本文の IL)
    Specialize.cs        generic の単相化
    EnumSort.cs          enum の名前の表を .NET と同じ順に並べる (ArraySortHelper の introsort の写し)
    Session.cs           増分の session と Step の driver
    Compiler.cs          compile の結果 (CompileResult)
  CBackend/            IL を C にする (tcs c)。CModel.cs (C の型と名前、最初の誤りの記録 CFail)、CFacts.cs (IL から引く型と method の事実)、CEmitter.cs (本体)
  VmBackend/           IL を tcs VM の .tcsb にする (tcs vm)。VModel.cs、ModuleBuilder.cs (型 / 定数 / static / 関数の表と .tcsb の書き出し)、FunctionCompiler.cs (関数を命令列にする)、VmEmitter.cs (module と reload の組み立て)、ReloadPlan.cs、ReloadRules.cs (hot reload で live に当てられない編集の規則)、IlNodes.cs (gen-ilnodes.py が Il.cs から作る)、IlWalk.cs、VBytes.cs、Op.cs (src/tcs-vm/gen-ops.py が tcsvm_ops.h から作る)
  Tool/                CLI
    Cli.cs               引数の解釈と tcs check
    CRun.cs              tcs c
    VmRun.cs             tcs vm
    VmSession.cs         VM の増分 session
    Sessions.cs          名前で持つ複数の VM の増分 session の入口 (open、update、close。結果は host が指す path に書く)
    HostMessage.cs       --watch の host への message
    TcsHost.cs           host の関数の宣言 (C の build では --ref の stub)
    ToolText.cs          数の読み取り
  Native/host.c        C の host
  Dotnet/              .NET の host (DotnetHost.cs、Program.cs)
  sources.sh           tcs 自身の source を渡す引数の一覧
  to-c.sh              tcs 自身を C にする
  cflags.sh            tcs c が作る C を compile する flag
  shipped.txt、ship.sh  tcs と一緒に配る file の一覧と、それを置く script
  build-native.sh      native の tcs の build
  native.sh            native の tcs の build と、.NET の tcs との比較、固定点 (§9.1)
  tcs.csproj           .NET の build (tcs.dll)
tests/tcs/Entry/       テストが compiler と VM の backend を文字列で動かす入口 (Checker.cs、IlExport.cs、SelfHostVm.cs、VmSession.cs)
tests/tcs-backends/SelfHostC.cs  テストが C の backend を文字列で動かす入口
```

## 5. compiler の段

### 5.1 ソースと位置

- ソースは `Utf8Bytes` で読み、lexer は byte の列を直接走査する (サブセットの string は位置の API を持たない)
- 位置は UTF-8 の byte offset で持ち、診断に出すときだけ行と列に直す

### 5.2 構文

- 再帰下降の parser。generic な method の型引数の `<` と比較、lambda と括弧式などの曖昧さは先読みで決める
- 誤りは診断に積んで読み進める (例外は無い)
- 構文木は種類の enum と slot (`A`〜`D`、`List`、`List2`、`List3`) を持つ 1 つの `Node` class で、束縛の結果 (型、symbol、定数) も同じ Node の field に書く。継承の無いサブセットでは、種類ごとの class にすると走査のたびに型 switch が要る
- サブセットの外の構文 (try / throw、goto、local function など) も読み、TCS1001 で報告する。読み飛ばした後も宣言と束縛を続けて中の API と構文を報告し、読み飛ばしの巻き添えで出うる C# の誤りは、サブセットの診断があるときは報告しない

### 5.3 宣言と BCL の面

- BCL と TinySystem の member は `Builtins.cs` の表で持つ。.NET に有るがサブセットに無い API を TCS1002 と報告するため、BCL の型と member の名前の表 `BclCatalog.cs` を参照アセンブリから生成して持つ (`tests/tcs/BclCatalogTests.cs`、`TCS_UPDATE_BCL=1` で作り直す)。どちらの表にも無い名前は C# の誤り (CS0103 など)
- TinySystem のうち source で配る部分 (`TinySystem/Linq.cs`、`TinySystem/ValueTuple.cs`) は tcs と一緒に配る library として読む。`--ref` の host の stub は宣言だけを読み、本文は使わない
- collection (List / Queue / HashSet / Dictionary / 配列)、string、`Utf8Bytes` は binder が型の種類として知り、foreach、collection の初期化、添字を種類ごとの規則で束縛する

### 5.4 束縛と検査

- binder は method の本体ごとに名前の解決、変換、overload 解決、generic の型推論、lambda、pattern、定数を束縛し、到達可能性 (CS0161、CS1643、CS0163) を見る。確実な代入 (CS0165) は束縛した後の本体を走査して判定する
- 準拠検査: 構文で決まる TCS1001 は `Compliance.cs`、型で決まる規則 (TCS1002、TCS1003、型を見る TCS1001) は `SemanticChecks.cs` と Declarer / Binder。準拠の規則はこの compiler の 1 か所にあり、analyzer も同じ compiler を動かす

### 5.5 IL

- 束縛済みの木から IL (`docs/tcs/il-spec.md`) を組み立てる (`IlBuild.cs`)。tcs c と tcs vm への入力は `IlExport.cs` が作る
- generic は単相化する (`Specialize.cs`)。閉じた generic の型と、generic な method の閉じた呼び出しごとに、型引数を置き換えた copy (`TcsClosed0_Box` の形の名前) を作って参照をその名前に書き換える。copy の中に新しい閉じた型が現れるので、増えなくなるまで束縛し直す (最大 128 回)。C backend と tcs VM はこの IL を読む

## 6. host と Step の driver

- CLI (`Cli`) は OS に触る所を `TcsHost` の関数 (引数、ファイルの読み書きと原子的な置き換え、標準出力と標準エラー、ファイルの更新の印、tcs と一緒に配るファイル、待つ、終了コード、終わりの印) にし、host が実装する
  - .NET: `Dotnet/DotnetHost.cs`。`lib/` は埋め込んだ resource から、`runtime/` は実行ファイルの隣から読む
  - C: `Native/host.c`。tcs と一緒に配るファイルは環境変数 `TCS_HOME` の dir か、実行ファイルの dir から読む。Windows では標準入出力を binary mode にし、.NET と同じ改行を書く
- host は `Cli.Start` を 1 回、`TcsHost.SetDone` が来るまで `Cli.Step` を繰り返し呼ぶ。C の build (tcs2c) では entry から戻るたびに GC の frame の境界になるので、compile は Step ごとに session の 1 段だけ進め、仕事の状態は static field に置いて段をまたぐ。`tcs vm --watch` の待ちも Step の中の `TcsHost.Sleep` で、host の loop は同じ形のまま
- 仕事は継承を使わずに型ごとの field (`CheckRun`、`CRun`、`VmRun`) に持って分ける

## 7. 増分 session

- `Session` はファイルを足し (`SetSource`、`SetReference`、`SetLibrary`)、差し替え、外し (`Remove`)、compile する。parse は内容の変わったファイルだけをやり直し、parse したままの木を持つ。束縛は木を書き換えるので、compile のたびに木の複製 (`Node.CloneTree`) を組み立て、宣言、束縛、出力は全体をやり直す
- compile は Step に分かれる: 変わったファイルの parse を 1 つずつ、組み立て (構文で決まる準拠検査を含む)、宣言、束縛と型で決まる準拠検査、出力 (C backend と tcs VM のための単相化は回ごとに 1 段)。`Start` は途中の compile を捨てて始め直すので、host は Step の合間に新しい編集を受け取れる
- 出力は mode で決まる。`ExportOnly` は tcs c と tcs vm への IL、`CheckOnly` は `tcs check` と analyzer の検査 (IL を組み立てて、組み立てられない本文も診断にする)、どちらでもなければ束縛と準拠の診断だけ
- VM の増分 session (`VmSession`、`Tool/VmRun.cs`) は `Session` の上で、最後に通った build を持ち、編集ごとに reload の batch か restart を返す。診断のある編集は最後の build を変えない。`tcs vm --watch` と、名前で持つ複数の session (`Sessions.Call`。lub の lider の player が組み込む) が同じものを使う (`docs/tcs/vm-design.md`)

## 8. サブセットで compiler を書くことの帰結

compiler はサブセットで書く最大のプログラムで、書きにくさは選定基準を見直す材料になる。

| サブセットの制約 | compiler での書き方 |
|---|---|
| 継承が無い | 構文木は種類の enum を持つ 1 つの `Node` class。IL のノードは interface (`IlExpr` / `IlStat`) とそれを実装する record で、走査は型 switch |
| 例外が無い | parser と binder は診断を積んで続ける。C と VM の backend は最初の誤りを `CFail` に記録して既定の値を返し、出力は誤りが無いときだけ使う (CLI は誤りを出して 1 で終わる)。compiler 自身の不具合は fault になる |
| string に位置の API が無い | lexer と書式指定子の解析は `Utf8Bytes` の byte を読む。token の文字列は `Slice(...).ToString()` |
| StringBuilder が無い (support-matrix §20) | 出力は `List<string>` に積んで `string.Join` |
| reflection が無い | IL のノードの子の列挙と診断用のテキスト (lambda の本文の指紋に使う) は `gen-ilnodes.py` が `Il.cs` の record から生成する |
| C backend の Dictionary の key は int と string と host の class | compiler は int と string を key にする Dictionary だけを使い、symbol に付ける情報は symbol や Node の field に持つ |
| double / long が無い | int の定数式の overflow の検出は手で書く (`checked` は fault するだけで捕まえられない)。uint の 16 進リテラルは 32 bit の 2 の補数で持つ |

## 9. 正しさの担保

### 9.1 bootstrap と host の一致

- `src/tcs/native.sh` (CI の native job が Linux、macOS、Windows で走らせる): .NET の tcs2c で C にした native の tcs を作り、.NET の tcs と比べる。samples の `tcs check` と `tcs c` と `tcs vm`、誤りのある入力の診断、tcs 自身の `tcs check` と `tcs vm`、digest kernel の `.tcsb`、reload の batch (`tcs vm --reload-from`)、tcs 自身の C (固定点)

### 9.2 検証の正解

- C# として正しいかは Roslyn が決める。C# の仕様の例 (`third_party/csharpstandard`、`TCS_SPEC_CONFORMANCE=1`) は Roslyn で受理を判定し、Roslyn が受理した例を tcs が C# の誤り (CSxxxx) で拒否したら compiler の不具合とする (`SpecConformanceClassifier`)
- 意味の正解は同じソースを .NET で動かした結果。semantic テスト (`TestHelper.CompileAndRunVm` と `TCS_DIFFERENTIAL=1`)、生成した program (`TCS_FUZZ`)、仕様の例の実行は tcs VM と .NET の stdout を比べる。`tests/tcs-backends` は C (通常と GC stress)、tcs VM (通常と GC stress)、.NET の stdout を比べる (`Backends.AssertParity`、`FuzzDifferentialTests`)

### 9.3 Roslyn だけが判定する検査

意味 (IL) に影響しない C# の検査の一部は、tcs の compiler では判定しない。
`tcs check` はこれらを通し、IDE の C# compiler と .NET の build が報告する。

| id | 内容 |
|---|---|
| CS0122 | accessibility (`private` の member を外から読む) |
| CS0050 | accessibility の不整合 (public な method が internal な型を返すなど) |
| CS0454、CS0701 | 型引数の制約の形 (制約の循環、sealed な型を制約に使う) |
| CS1961 | variance (in / out) の妥当性 |
| CS8125 | tuple の要素の名前 (`Item2` を別の位置に使う) |

## 10. 配布

- CI の native job が Linux (cc)、macOS (clang)、Windows (clang) で `src/tcs/native.sh` を走らせ、tcs の実行ファイルと一緒に配るファイル (`runtime/` の C、`lib/`) を成果物 (`tcs-linux-x64` など) にする。生成する C は GNU C の文の式を使うので MSVC の cl では build できず、Windows は clang で build する
- 変換した tcs (C のソースと実行ファイル) は git に入れない (`src/tcs/out/`)

## 11. 採らなかった案

- Roslyn 自体を tcs で変換する: Roslyn は full C# (継承、例外、遅延 LINQ、unsafe) で書かれていて、サブセットに入らない
- Roslyn を部品にした compiler を別に持つ: 2 つの実装の出力 (定数の表記、名前の表、文字列の escape など) を揃え続ける費用がかかり、.NET の無い環境で動かない
- 型を見ずに構文だけで IL にする: struct の copy 地点、整数の除算、`T?`、overload の選択など、出力が型に依存する箇所が多く、成り立たない
- compiler を C や Rust で書く: 速いが、dogfooding の価値と、compiler 自身が tcs VM と C と .NET の一致を示す検査を失う。速さは tcs2c の build で得られる
- Roslyn を使わない: IDE の analyzer と補完、C# として正しいかの判定 (基準 6) を失う

## 12. 関連文書

- `docs/tcs/il-design.md`、`docs/tcs/il-spec.md` — IL が意味論の正本であること
- `docs/tcs/vm-design.md` — tcs VM と VM の増分 session
- `docs/tcs/support-matrix.md` — compiler が扱う範囲
- `tcs2c/README.md` — C backend と GC の frame
