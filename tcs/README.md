# TinyC# (tcs)

C# サブセットのコンパイラ。ゲームスクリプティング向け。
出力は tcs VM の bytecode (`tcs vm`、VM は `tcsvm/`。開発と hot reload の実行系) と C のソース (`tcs c`、`tcs2c/`。release の実行系) の 2 つ。

compiler は TinyC# で書いたセルフホストの実装 1 つ (`SelfHost/`) で、.NET、C (tcs2c で C にした tcs)、wasm のどれでも同じ実装が動く。
字句解析から型チェックと準拠検査、IL の組み立てまでを compiler が行い、tcs VM と C の backend は同じ IL を読む。
Roslyn は compiler の外の道具 (IDE の Roslyn Analyzer、playground の補完と hover、検証の oracle) にだけ使う。

## 必要なもの

- .NET 10 SDK (版は `global.json` で固定。導入は `dotnet-install.sh --jsonfile global.json` を推奨 — distro package は workload と版がずれるため混ぜない)
- CMake 3.12+ (tcsvm のビルド用)
- C コンパイラ (MSVC / GCC / Clang)

## セットアップ

tcs は [lub](https://github.com/neguse/lub) の repo の `tcs/` にある。以下のコマンドは `tcs/` で実行する。

```bash
git clone --recursive https://github.com/neguse/lub.git
cd lub/tcs
```

サブモジュールを取得し忘れた場合:

```bash
git submodule update --init --recursive
```

`deps/csharpstandard` は C# の仕様の例 (spec conformance のテストの入力) の git submodule。

## tcsvm のビルド

CMake でクロスプラットフォームビルド:

```bash
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release --target tcsvm
```

ビルド成果物は `build/tcsvm/tcsvm` に出力される。

## テスト実行

```bash
dotnet test
```

Linux:
```bash
./run-tests.sh
```

Windows (PowerShell):
```powershell
.\run-tests.ps1
```

`run-tests` は `dotnet test` に加え、代表 sample の `tcs check`、Rider helper script、analyzer-demo build の expected diagnostics、analyzer nupkg の PackageReference consumer と `.editorconfig` severity override を検査する。
GitHub Actions (repo の root の `.github/workflows/ci.yml` の `tcs-test`) でも同じ gate を実行する。

## 依存と配布

NuGet package は floating version を使わず `.csproj` に明示 version を pin し、`packages.lock.json` で transitive dependency も固定する。
依存を更新する場合は対象 package version と lock file を同時に更新する。

CLI publish:

```bash
dotnet publish Transpiler/Transpiler.csproj -c Release -o publish/tcs
```

publish 出力には `tcs c` が生成 C に埋め込む runtime の C (`runtime/core`、`math`、`fmt`、`model`、`utf8`) が `runtime/` 配下に同梱される。
同梱の library (`TinySystem/Linq.cs` など) は CLI に埋め込まれるので、publish ディレクトリ単体で変換できる。
runtime はバイナリの隣だけから読み、無ければ `tcs c` は失敗する。

## 使い方

### コマンド

```bash
# ヘルプ / バージョン
dotnet run --project Transpiler -- --help
dotnet run --project Transpiler -- --version

# 準拠チェック
dotnet run --project Transpiler -- check samples/hello.cs

# tcs VM の bytecode (.tcsb) にして VM で動かす (entry は static Main を持つ class)
dotnet run --project Transpiler -- vm --entry Game game.cs -o game.tcsb
build/tcsvm/tcsvm game.tcsb

# C にする
dotnet run --project Transpiler -- c --entry Game game.cs -o game.c
```

subcommand (`check` / `vm` / `c`) の無い呼び出しは usage を stderr に書いて exit 1 になる。
`tcs vm` の option (`--export`、`--watch`、`--reload-from`、`--trace`、`--dump-il` など) は `tcs vm --help` と [`tcsvm/README.md`](tcsvm/README.md)、`tcs c` の option (`--lib`、`--digest-f32`、`--vm-host` など) は `tcs c --help` と [`tcs2c/README.md`](tcs2c/README.md)。

`Math` の超越関数 (Sin / Cos / Tan / Exp / Log / Atan2 / Pow) は `runtime/math` の C 実装で、tcs VM、C、.NET (`TinySystem/TinySystem.props`) で bit 単位で同じ結果になる。
float の文字列化は `runtime/fmt`、`TinySystem.Text` (`RuneCount` / `RuneSubstring` / `Utf8Bytes`) と `EnumerateRunes` は `runtime/utf8` の実装で、.NET と同じ結果を返す。

### 準拠チェック

出力を書かず、C# compile error と TinyC# 準拠診断だけを返す。警告またはエラーがあれば exit 1、問題がなければ exit 0。未対応の構文 (TCS1001) と API (TCS1002) は error で、`tcs vm` / `tcs c` (`--watch` と増分 session も) も出力を書かずに失敗する。TCS1003 は warning で、`tcs vm` / `tcs c` は止めない。BCL の型は既定で通さない。[support-matrix](doc/support-matrix.md) の一覧に無い型は TCS1002 になる。TinyC#固有の例外はenumと数値整数 (`char`を除く) の変換・等値比較だけで、同じC#エラーIDの通常の型不一致は失敗する。user の型の member 名は C# の識別子のまま IL の名前になる (`OnFrame` → `OnFrame`、enum メンバ `BigValue` → `BigValue`)。`--ref` 型 (host) の member だけ host の wire format に写す (`BeginPass` → `begin_pass`、enum メンバ `DontCare` → `DONT_CARE`、static アクセス `Lub.Gfx` → `lub.gfx`) ので、host の wire format が snake_case でも C# 側は C# の通常の名前で書ける (規則は `doc/support-matrix.md` の「名前規則」)。`self` と同名の宣言、`__` で始まる識別子、非 ASCII の識別子、`new` / `ctor` と同名の member、型名と namespace 名の `_` は TCS1001。名前は C# の識別子のまま IL に出るので、tcs は命名の流儀 (PascalCase / camelCase) を検査しない。

```bash
dotnet run --project Transpiler -- check samples/hello.cs
dotnet run --project Transpiler -- check game.cs --ref engine-stub.cs
```

`tcs check` は束縛と準拠の検査の後に tcs VM と C の backend が読む IL を組み立てる。IL にできない本文と式も TCS1001 (`unsupported syntax: <構文の種類>`) と `internal:` の理由で止める。

### 複数ファイル

```bash
dotnet run --project Transpiler -- check src/Player.cs src/Enemy.cs src/Game.cs
dotnet run --project Transpiler -- vm --entry Game src/Player.cs src/Enemy.cs src/Game.cs -o game.tcsb
```

同一 Compilation でコンパイルするので、ファイル間のクラス参照が解決される。

### 参照専用 stub / facade

IL に出さない型チェック専用ファイルは `--ref` で渡す。
外部エンジン API や host 提供 API の最小 stub を置く用途。

```bash
dotnet run --project Transpiler -- check game.cs --ref engine-stub.cs
dotnet run --project Transpiler -- vm --entry Game game.cs --ref engine-stub.cs -o game.tcsb
```

stub の型の呼び出しは host の関数になる。名前は namespace と入れ子の型名を全小文字で `.` 結合したパス
(`namespace HostApi { static class Screen }` の `Screen.Width()` は `hostapi.screen.width`)。
C の host 関数の名前と VM への登録は `tcs2c/README.md` と `tcsvm/README.md`。

この repo では engine 固有名に依存しない例として、
`samples/host_api_game.cs` と `samples/host_api_stub.cs` を用意している。

```bash
dotnet run --project Transpiler -- check samples/host_api_game.cs --ref samples/host_api_stub.cs
```

### 実行 trace と watch (`tcs vm --trace`、`tcs vm --watch`)

`tcs vm --trace <path-prefix>` は、入力 path (`/` 区切りに正規化) が prefix のどれかに path の segment 単位で一致するファイルの文と代入を、VM が記録できる形で module に入れる (prefix は繰り返して指定できる。`samples/game` は `samples/game/Game.cs` に一致し、`samples/gameover/Over.cs` には一致しない)。

`tcs vm --watch -o out.tcsb` は入力 (と `--inputs-from` の一覧) を見張り、最初に通った build を `-o` に書く。
以後の編集ごとに `-o` を書き直してから、前の build からの reload を標準出力へ `@@tcs_<kind> <UTF-8 の byte 数>` の行と本文の組で書く (kind は `vm_batch`、`restart_required`、`reload_notes`、`build_failed`)。
live に当てられない変更 (既存 static の initializer と型、const の値、型の削除・種類の変更、enum member の削除・値の変更) は `restart_required` と理由の行になる。
規則は `doc/hot-reload-design.md`、VM の reload は `doc/vm-design.md` の §12 と §13.3。

### tcs analyzer PoC

Rider などの C# IDE 上で tcs 非準拠コードを警告するための Roslyn Analyzer PoC。
現時点では struct の indexer、`partial` 型, `lock`, `nameof`, `try/catch`, `throw`, local function, list pattern, 名前付き引数, runtime の global と同名の型 (`class Math` 等), caller info 属性 (`[CallerMemberName]` 等)、未対応 BCL API / 未対応 core library member、配列と Dictionary への null 保存を `TCS1001` / `TCS1002` / `TCS1003` として報告する。
analyzer だけが、.NET で動かすと .NET の実装が動く `System.Math` / `System.MathF` の超越関数を `TCS1004` として報告する。TinySystem を参照する project は `TinySystem/TinySystem.props` を Import すると、`Math` / `MathF` が `TinySystem.Math` (tcs の C と VM と同じ実装) になる。
props は `InvariantGlobalization` も有効にし、string の既定の比較 (Sort / OrderBy / Min / Max) を tcs の C と VM と同じ ordinal にする。
TCS1001 / TCS1002 / TCS1003 は、analyzer が tcs の compiler (`SelfHost/Compiler`、`tcs check` と同じ実装) を project のソースで動かして出す。規則は compiler の 1 か所にあり、analyzer と `tcs check` と `tcs vm` / `tcs c` の判定は同じになる (`tcs check` などでは TCS1001 / TCS1002 は error だが、analyzer では warning。severity は `.editorconfig` で上げられる)。
`--ref` の stub (host の API の宣言) は、AdditionalFiles か Compile の項目に `TinyCsRef="true"` を付けて analyzer に渡す (Roslyn の型の検査にも stub を使うなら Compile)。

```xml
<ItemGroup>
  <AdditionalFiles Include="engine-stub.cs" TinyCsRef="true" />
  <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="TinyCsRef" />
  <!-- stub を Compile に入れるなら: <Compile Update="engine-stub.cs" TinyCsRef="true" /> と
       <CompilerVisibleItemMetadata Include="Compile" MetadataName="TinyCsRef" /> -->
</ItemGroup>
```
```bash
dotnet test TinyCs.Analyzers.Tests
dotnet build samples/analyzer-demo/analyzer-demo.csproj
```

demo project 固有の expected diagnostics と Rider 確認手順は
[`samples/analyzer-demo/README.md`](samples/analyzer-demo/README.md) にも残している。
`run-tests` は analyzer nupkg を pack し、一時 project から `PackageReference` で参照して同じ診断が出ることと、`.editorconfig` で `TCS1001` / `TCS1002` / `TCS1003` を error にした build が失敗することも検証する。
[`samples/analyzer-demo/verify-inspectcode.sh`](samples/analyzer-demo/verify-inspectcode.sh) で JetBrains InspectCode 2026.1.3 の headless 実行でも、ProjectReference と local nupkg `PackageReference` consumer の両方で `TCS1001` x5 / `TCS1002` x1 / `TCS1003` x1 が SARIF に出ることを確認できる。さらに PackageReference consumer の `.editorconfig` で `TCS1001` / `TCS1002` / `TCS1003` を error にした場合、InspectCode が同じ件数の error を返すことも検証する。

通常の C# project から package として参照する場合:

```bash
dotnet pack TinyCs.Analyzers/TinyCs.Analyzers.csproj -c Release -o .nupkgs
dotnet restore your-project.csproj --source .nupkgs --source https://api.nuget.org/v3/index.json
```

```xml
<PackageReference Include="TinyCs.Analyzers"
                  Version="0.1.0"
                  PrivateAssets="all" />
```

#### analyzer package の release 手順

NuGet 公開は当面行わず、local nupkg の配布のみとする。

1. `TinyCs.Analyzers/TinyCs.Analyzers.csproj` の `PackageVersion` を上げる
2. `run-tests.sh` 内の consumer 検証と本 README の `PackageReference` 例の `Version` を同じ値に更新する
3. `dotnet pack TinyCs.Analyzers/TinyCs.Analyzers.csproj -c Release -o .nupkgs` で再生成する
4. `bash run-tests.sh` を実行し、nupkg consumer build と `.editorconfig` severity override の検証が通ることを確認する

repository 内で直接参照する場合:

```xml
<ProjectReference Include="..\TinyCs.Analyzers\TinyCs.Analyzers.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false"
                  PrivateAssets="all" />
```

`.editorconfig` で重要度を変更できる:

```ini
dotnet_diagnostic.TCS1001.severity = warning
dotnet_diagnostic.TCS1002.severity = error
dotnet_diagnostic.TCS1003.severity = warning
```

Rider 実機確認の手順:

1. 必要なら `samples/analyzer-demo/open-rider-demo.sh` で pre-check 後に demo project を Rider で開く。Windows では `.\samples\analyzer-demo\open-rider-demo.ps1` を使う。Rider command を自動検出できない場合は `TCS_RIDER_COMMAND=/path/to/rider.sh`、Windows では `$env:TCS_RIDER_COMMAND = "C:\path\to\rider64.exe"` を指定する
1. `samples/analyzer-demo/analyzer-demo.csproj` を含む solution/project を Rider で開く
1. Restore 後、`samples/analyzer-demo/Program.cs` を開く
1. struct の indexer, local function, `try`, `throw`, `values is [1, 2]`, `System.IO.File.ReadAllText`, `List<string?> { null }` に Roslyn inspection の squiggle が出ることを確認する
1. Build tool window で `TCS1001` が5件、`TCS1002` が1件、`TCS1003` が1件出ることを確認する
1. `.editorconfig` の `dotnet_diagnostic.TCS1001/TCS1002/TCS1003.severity` を `error` へ変え、Rider 表示が追従するか確認する（build 上の severity override は `run-tests` で検証済み）
1. [`samples/analyzer-demo/RIDER_VERIFICATION_TEMPLATE.md`](samples/analyzer-demo/RIDER_VERIFICATION_TEMPLATE.md) に沿って、結果を `q.md` に go / no-go として記録する

## サポートしている C# 機能

class, enum, interface(class の `is I` / `as I` / 型 switch も。class / record の継承は対象外で、多態は interface と型 switch), コンストラクタ, auto/custom プロパティ,
static/instance メソッド, if/else/while/for/foreach/switch, ラムダ,
List\<T\>, Queue\<T\>, HashSet\<T\>, Dictionary\<K,V\>, LINQ (`using TinySystem.Linq;` の Where/Select/Any/All/Count/FirstOrDefault/OrderBy/OrderByDescending/Min/Max/MinBy/MaxBy/Sum。Dictionary の Keys / Values も受け手。.NET の project は `TinySystem/TinySystem.props` で同じ実装をコンパイルする),
文字列の文字と byte の位置 (`using TinySystem.Text;` の `s.RuneCount()` / `s.RuneSubstring(start, count)` / `Utf8Bytes`),
文字列補間, null 条件演算子 (`?.`), is パターン, switch 式 など。

詳細は [doc/support-matrix.md](doc/support-matrix.md) 参照。

## 性能

同じプログラムを tcs の各経路と他の言語で動かした比較 (測り方は [perf/compare/METHODOLOGY.md](perf/compare/METHODOLOGY.md)):

- [Ryzen 7 PRO 5750GE (ローカル、専有コア)](perf/compare/results/2026-11-04-ryzen-5750ge.md)

## プロジェクト構成

```
tcs/
  SelfHost/            # tcs の本体 (TinyC# で書いた compiler、C と tcs VM の backend、CLI、C / wasm の host)
  Transpiler/          # .NET の host と CLI の入口 (CLI の本体は SelfHost/Tool)
  Transpiler.Tests/    # xUnit テスト
  tcsvm/               # tcs VM (`tcs vm` が作る .tcsb を動かす register VM)
  tcs2c/               # tcs c (C backend) を .NET で動かす入口と、C の出力のテスト用のスクリプト
  tcs2c.Tests/         # C backend、tcs VM、.NET の stdout の差分テスト
  WasmCompiler/        # tcs を .NET の wasm でブラウザに公開する入口
  TinyCs.Analyzers/    # Roslyn Analyzer PoC
  TinyCs.Analyzers.Tests/
  Shared/              # 診断の id
  TinySystem/          # C# 側の型定義/facade (コンパイル・補完用)
  runtime/
    core/              # tcs c の C runtime (GC を含む)
    model/             # tcs VM と tcs2c が共有する型記述子
    sort/              # List.Sort の C 実装 (tcs VM が使う)
    math/              # Math の超越関数の C 実装 (tcs2c が埋め込み、tcs VM が link する)
    fmt/               # float の文字列化の C 実装 (同上)
    utf8/              # 文字列の操作の C 実装 (同上)
  perf/                # 性能実測ハーネス
  deps/
    csharpstandard/    # C# の仕様 (spec conformance の例、git submodule)
  samples/             # サンプル C# コード
  doc/
    support-matrix.md  # サポートマトリクス
  CMakeLists.txt       # tcsvm のクロスプラットフォームビルド
```

## ライセンス

MIT。`runtime/math` と `TinySystem/TcsMath.*.cs` は CORE-MATH と musl (Arm) の MIT のコードを、`runtime/fmt` は Ryu の Boost Software License 1.0 のコードを元にしている (`THIRD_PARTY_LICENSES.md`)。
