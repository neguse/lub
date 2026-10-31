# TinyC# (tcs)

C# サブセットから Lua 5.5 ソースコードへのトランスパイラ。ゲームスクリプティング向け。

Roslyn で C# を解析し、Lua のテーブル + メタテーブル OOP に変換する。
型チェックは C# コンパイラが行い、Lua 側は型消去で動作する。

## 必要なもの

- .NET 10 SDK (版は `global.json` で固定。導入は `dotnet-install.sh --jsonfile global.json` を推奨 — distro package は workload と版がずれるため混ぜない)
- CMake 3.12+ (Lua ビルド用)
- C コンパイラ (MSVC / GCC / Clang)

## セットアップ

```bash
git clone --recursive https://github.com/neguse/tcs.git
cd tcs
```

サブモジュールを取得し忘れた場合:

```bash
git submodule update --init --recursive
```

`deps/lua` は Lua 5.5 ソースの git submodule として固定する。
更新する場合は submodule commit を明示的に進め、`deps/lua/lua -v` が `Lua 5.5` で始まることを `run-tests` で確認する。

## Lua 5.5 のビルド

CMake でクロスプラットフォームビルド:

```bash
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

Windows (MSVC) の場合も同じ。Visual Studio の Developer Command Prompt か PowerShell で実行する。
CMake は Linux / Windows / macOS / iOS-family / Emscripten / BSD / generic Unix で Lua の compile definitions と system libs を分岐する。
ビルド成果物は `deps/lua/lua` (Unix) または `deps/lua/lua.exe` (Windows) に出力される。

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

Lua が未ビルド、CMake 入力より古い、または `Lua 5.5` ではない場合は自動で `cmake` を呼んでビルドしてからテストを実行する。
テスト時は `deps/lua/lua -v` が `Lua 5.5` で始まることも検証する。
`run-tests` は `dotnet test` に加え、代表 sample の `tcs check`、Rider helper script、analyzer-demo build の expected diagnostics、analyzer nupkg の PackageReference consumer と `.editorconfig` severity override を検査する。
GitHub Actions (`.github/workflows/ci.yml`) でも同じ gate を実行する。

## 依存と配布

NuGet package は floating version を使わず `.csproj` に明示 version を pin し、`packages.lock.json` で transitive dependency も固定する。
依存を更新する場合は対象 package version と lock file を同時に更新する。

CLI publish:

```bash
dotnet publish Transpiler/Transpiler.csproj -c Release -o publish/tcs
```

publish 出力には `runtime/tinysystem.lua` と `runtime/module_registry.lua` が `runtime/` 配下に同梱される。
CLI の出力はこの runtime を読み込んで埋め込むため、publish ディレクトリ単体で変換できる。
runtime はバイナリの隣だけから読み、無ければ変換は失敗する。

## 使い方

### 基本: C# → Lua 変換

```bash
# ヘルプ / バージョン
dotnet run --project Transpiler -- --help
dotnet run --project Transpiler -- --version

# stdout に出力
dotnet run --project Transpiler -- samples/hello.cs

# ファイルに出力
dotnet run --project Transpiler -- samples/hello.cs -o out.lua
```

`-o`の出力と`--sourcemap`のmapは、input / `--ref` / `--prelude`と
同じパスにはできない。Windows / Linux / macOSではsymlinkやhardlink等で
同じ実体を指す場合も含め、衝突時は書き込み前にエラーとなり、入力を変更しない。
衝突しない既存の出力linkはlink先へ書かず、出力entryを通常ファイルへ置換する。
既存出力が書き込み不可なら従来どおりエラーにし、Linux / macOSでは置換前の
Unix permission bitsを維持する。

CLI 生成 Lua はデフォルトで TinySystem runtime prelude を埋め込む。
`List` / `Dict` / `Math` / `String` / `Random` は生成物だけで利用できる。
エンジン側で runtime を供給する場合は `--no-runtime` を付ける。

`Math` の超越関数 (Sin / Cos / Tan / Exp / Log / Atan2 / Pow) は、Lua の `math.*` ではなく global の `tcs_math` を呼ぶ (tcs2c と .NET と bit 単位で同じ結果にするため)。生成 Lua を動かす host は、`runtime/lua/tcs_math_lua.c` と `runtime/math/*.c` を build して VM に登録する (`luaL_requiref(L, "tcs_math", luaopen_tcs_math, 1)`)。このリポジトリの `deps/lua/lua` / `lua32` (CMake) は登録済み。

float の文字列化 (連結・補間 (書式つきも)・ToString・Console.WriteLine) と string の順序 (Sort / OrderBy / Min / Max) は global の `tcs_rt` を呼ぶ (.NET の `float.ToString()` と同じ文字列にし、Lua の `<` は locale で変わるので .NET の ordinal と同じ順序にするため)。host は `runtime/lua/tcs_rt_lua.c` と `runtime/fmt/f2s.c` を build して VM に登録する (`luaL_requiref(L, "tcs_rt", luaopen_tcs_rt, 1)`)。`deps/lua/lua` / `lua32` は登録済み。

```bash
dotnet run --project Transpiler -- samples/hello.cs -o out.lua --no-runtime
```

`--entry <Class>` を付けると出力末尾に emitted 名の `return` を追記し、
`require` / `dofile` が class table を返す Lua module として使える
(host が module の callback を呼ぶ engine 組み込み向け)。
指定は metadata 名 (`Game.App`) と一意な simple 名 (`App`) の両方を解決し、
interface / `--ref` 型 / 曖昧な simple 名はエラーになる。
`--module` を付けると出力末尾に定義した型 (`--ref` を除く) の table を返す
`return { Counter = Counter, ... }` を追記し、ライブラリを
`local m = require("lib")` で読む Lua module として使える (`--entry` /
`--snapshot` とは併用しない)。key は C# の simple 名で、namespace の中の型も
`m.Color` で引ける (simple 名が出力内で重複するときだけ Lua の global 名)。

namespace の中の型は Lua では namespace 修飾名の global になり
(`Game.Gfx.Color` → `Game_Gfx_Color`)、入れ子の型は外側の型の名前をつなぎ
(`Outer.Inner` → `Outer_Inner`)、どちらでもない型は simple 名の
global になる。
`--prelude <shim.lua>` は任意のユーザー Lua (host API を tcs stub の形に
橋渡しする shim など) を出力の先頭に前置する。

output / source map のパスが入力・`--ref`・`--prelude` と同じ実体
(symlink / hardlink 含む) になる場合は、書き込み前にエラーにして
入力破壊を防ぐ (watch の各 rebuild でも再検証)。

```bash
dotnet run --project Transpiler -- game.cs -o game.lua --entry Game --prelude engine-shim.lua
```

lub エンジンで動かす実例は `samples/lub/` (`run-lub.sh`) と
`doc/lub-gap-analysis.md` を参照。

### 準拠チェック

Lua を出力せず、C# compile error と TinyC# 準拠診断だけを返す。
警告またはエラーがあれば exit 1、問題がなければ exit 0。
未対応の構文 (TCS1001) と API (TCS1002) は error で、build (`-o` / `--watch` / 増分 session) も Lua を書かずに exit 1 になる。TCS1003 は warning で、build は止めない (`--warnaserror` で止められる)。
BCL の型は既定で通さない。[support-matrix](doc/support-matrix.md) の一覧に無い型は TCS1002 になる。
TinyC#固有の例外はenumと数値整数 (`char`を除く) の変換・等値比較、および
互換public fieldによるinterface property facadeだけで、同じC#エラーIDの
通常の型不一致は失敗する。
user の型の member 名は C# の識別子のまま Lua / IL の名前になる (`OnFrame` → `OnFrame`、
enum メンバ `BigValue` → `BigValue`)。`--ref` 型
(host) の member だけ host の wire format に写す (`BeginPass` → `begin_pass`、enum
メンバ `DontCare` → `DONT_CARE`、static アクセス `Lub.Gfx` → `lub.gfx`) ので、host の
wire format が snake_case でも C# 側は C# の通常の名前で書ける (規則は
`doc/support-matrix.md` の「Lua 出力の名前規則」)。Lua の予約語と同名の member、`__` で始まる識別子、非 ASCII の識別子、`new` / `ctor` と同名の member、型名と namespace 名の `_` は TCS1001。名前は C# の識別子のまま Lua に出るので、tcs は命名の流儀 (PascalCase / camelCase) を検査しない。

```bash
dotnet run --project Transpiler -- check samples/hello.cs
dotnet run --project Transpiler -- check game.cs --ref engine-stub.cs
```

### 複数ファイル

```bash
dotnet run --project Transpiler -- src/Player.cs src/Enemy.cs src/Game.cs -o game.lua
```

同一 Compilation でコンパイルするので、ファイル間のクラス参照が解決される。

### 参照専用 stub / facade

Lua に出力しない型チェック専用ファイルは `--ref` で渡す。
外部エンジン API や host 提供 API の最小 stub を置く用途。

```bash
dotnet run --project Transpiler -- game.cs --ref engine-stub.cs -o game.lua
```

stub の型は namespace と入れ子の型名を全小文字で `.` 結合した Lua の
パスで呼ぶ (`namespace HostApi { static class Screen }` の `Screen.Width()` は
`hostapi.screen.width()`)。

この repo では engine 固有名に依存しない例として、
`samples/host_api_game.cs` と `samples/host_api_stub.cs` を用意している。

```bash
dotnet run --project Transpiler -- samples/host_api_game.cs --ref samples/host_api_stub.cs -o host_api_game.lua
```

### ソースマップ

```bash
dotnet run --project Transpiler -- game.cs -o game.lua --sourcemap
```

`game.lua.map` が生成される。Lua 行番号 → C# ファイル:行番号 の JSON マッピング。
runtime prelude を埋め込む通常出力でも、map の Lua 行番号は生成された `.lua` ファイル上の行番号に合わせて offset 済み。

`.lua.map` の形式:

```json
{
  "version": 1,
  "mappings": {
    "42": {"file": "game.cs", "line": 12}
  }
}
```

Lua runtime error の stack trace は SourceMap で注釈できる:

```bash
deps/lua/lua game.lua 2> trace.txt
dotnet run --project Transpiler -- --map-stacktrace game.lua.map trace.txt
```

`trace.txt` を省略した場合は stdin から読む。SourceMap の exact 行が無い場合は直前の mapping を使う。

### 実行 trace (`--trace`)

```bash
dotnet run --project Transpiler -- samples/game/Game.cs cs-lib/Text.cs -o game.lua --trace samples/game/
```

ゲームを動かしながら、どの C# の行を実行したかと、host API を呼んだときの C# の呼び出し元を知るための開発用の道具。
入力 path (`/` 区切りに正規化) が `--trace` の prefix のどれかに path の segment 単位で一致するファイルの文だけが対象で、prefix は繰り返して指定できる。prefix はファイルの path そのものか directory で、directory はその下のファイルに一致する (`samples/game` は `samples/game/Game.cs` に一致し、`samples/gameover/Over.cs` には一致しない)。prefix と入力 path の先頭の `./` と、prefix の末尾の `/` は比べる前に外す (`./` はすべての入力に一致する)。
対象の文の直前に `__tcs_at = "<file>:<line>"; __tcs_trace[__tcs_at] = true` が入る (`<file>` は入力に与えた path、`<line>` は C# の 1 始まりの行)。1 つの文につき 1 回で、生成コードで複数の Lua 文に分かれる文 (pattern の変数の前宣言など) も最初に 1 回だけ入る。
`else if` と `while` / `do` ... `while` の条件は、評価するたびにその条件の行で同じ記録をしてから評価する (反復ごとに戻る)。
`--trace` を付けなければ出力は変わらない。`--snapshot` とは併用できない。

host との契約:

- `__tcs_trace` は global の table で、出力と reload chunk は `__tcs_trace = __tcs_trace or {}` で既存のものを使う。host は key `"<file>:<line>"` を読み、消すときは table を置き換えず in place で key を消す (frame ごとに消せば、その frame で実行した行の集合になる)。
- `__tcs_at` は対象の文を持つ Lua 関数 (method / ctor / accessor / operator / block 本体の lambda) ごとの local で、関数本体の先頭で宣言される。host は各 Lua frame の `__tcs_at` という名前の local (`lua_getlocal` / `debug.getlocal`) を読むと、その frame の現在の C# 行が分かり、並べると C# の呼び出し元の列になる。
- `__tcs_trace_classes` は global の table で、対象ファイルの型 table (class / record / struct。enum は状態を持たないので入らない) を Lua 名の key で持つ (`__tcs_trace_classes["Game"] = Game`)。型 table を作った直後に登録し、出力と reload chunk は `__tcs_trace_classes = __tcs_trace_classes or {}` で既存のものを使う。host はここから static field を辿って game の状態を snapshot / restore する。reload chunk は型 table の identity を保つので、登録済みの型は同じ table のまま、chunk が足した型は新しく登録される。
- `return f(x)` は Lua の tail call で、その frame は stack に残らない。式本体の lambda、IIFE に畳まれた文、top-level 文、ctor 先頭の field 代入 (table constructor に畳まれる) には trace が入らない。host は `__tcs_at` の無い frame を飛ばす。

### watch モード

```bash
dotnet run --project Transpiler -- src/*.cs -o out.lua --watch
```

`src/*.cs` は shell が展開する例。shell が glob 展開しない環境ではファイルを個別に渡す。
ファイル変更を検知して自動で再トランスパイルする。`Ctrl+C` で停止。

実行中の VM の状態を保ったまま変更を当てるホスト (lub など) は `--reload-chunks` を付ける。
以後の rebuild ごとに、`-o` の出力を書いた後に、前の build から今回の build への reload chunk を標準出力へ `@@tcs_reload_chunk <UTF-8 の byte 数>` の行と本文の組で順に書く (ログは標準エラー)。
初回の出力を読んだ VM で、届いた chunk を届いた順にすべて実行すると、method の差し替えと field・static の移行が行われる (instance と static の値は保持)。
出力と chunk は VM の build 番号 (`__tcs_build`) を書き、chunk は自分の元の build の VM にだけ当たる (既に含む VM では何もせず、途中が抜けた VM では何も変えずに失敗する)。
chunk で移行できない変更 (既存 static の initializer と型、const の値、base class、型の削除・種類の変更、enum member の削除・値の変更) では chunk を書かず、出力を書いた後に `@@tcs_restart_required <UTF-8 の byte 数>` の行と理由の行 (1 行 1 理由、改行終端) を書く。ホストは出力を読み直して VM を起動し直す。以後の chunk はその build からの差分になる。
chunk の規則は `doc/hot-reload-design.md`。`--instance-registry` を含意する。
`--trace` を付けたときは、reload chunk の v2 定義にも出力と同じ trace が (同じ入力 path の key で) 入る。

### tcs analyzer PoC

Rider などの C# IDE 上で tcs 非準拠コードを警告するための Roslyn Analyzer PoC。
現時点では struct の indexer、`partial` 型, `lock`, `nameof`, `try/catch`, `throw`, local function, list pattern, 名前付き引数, runtime の global と同名の型 (`class Math` 等), caller info 属性 (`[CallerMemberName]` 等)、未対応 BCL API / 未対応 core library member、collection への null 保存を `TCS1001` / `TCS1002` / `TCS1003` として報告する。
analyzer だけが、.NET で動かすと .NET の実装が動く `System.Math` / `System.MathF` の超越関数を `TCS1004` として報告する。TinySystem を参照する project は `TinySystem/TinySystem.props` を Import すると、`Math` / `MathF` が `TinySystem.Math` (tcs2c / Lua と同じ実装) になる。
props は `InvariantGlobalization` も有効にし、string の既定の比較 (Sort / OrderBy / Min / Max) を tcs2c / Lua と同じ ordinal にする。
同じ共有ルールを `tcs check` と transpiler でも使う (transpiler では TCS1001 / TCS1002 は error だが、analyzer では warning。severity は `.editorconfig` で上げられる)。
変換に `--warnaserror` を付けると、`tcs check` と同じく警告が 1 つでもあれば出力を書かずに失敗する (check と変換を 1 回の compile で済ませる)。

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
List\<T\>, Dictionary\<K,V\>, LINQ (`using TinySystem.Linq;` の Where/Select/Any/All/Count/FirstOrDefault/OrderBy/OrderByDescending/Min/Max/MinBy/MaxBy/Sum。Dictionary の Keys / Values も受け手。.NET の project は `TinySystem/TinySystem.props` で同じ実装をコンパイルする),
文字列補間, null 条件演算子 (`?.`), is パターン, switch 式 など。

詳細は [doc/support-matrix.md](doc/support-matrix.md) 参照。

## プロジェクト構成

```
tcs/
  Transpiler/          # トランスパイラ本体 (Roslyn → Lua)
  Transpiler.Tests/    # xUnit テスト
  TinyCs.Analyzers/    # Roslyn Analyzer PoC
  TinyCs.Analyzers.Tests/
  TinySystem/          # C# 側の型定義/facade (コンパイル・補完用)
  runtime/
    tinysystem.lua     # Lua ランタイムライブラリ (List/Dict/String/Math)
    math/              # Math の超越関数の C 実装 (tcs2c が埋め込み、Lua の module が使う)
    fmt/               # float の文字列化の C 実装 (同上)
    lua/               # tcs_math / tcs_rt の Lua module と、それを登録した lua / lua32
  deps/
    lua/               # Lua 5.5 ソース (git submodule)
  samples/             # サンプル C# コード
  doc/
    support-matrix.md  # サポートマトリクス
  CMakeLists.txt       # Lua クロスプラットフォームビルド
```

## ライセンス

MIT。`runtime/math` と `TinySystem/TcsMath.*.cs` は CORE-MATH と musl (Arm) の MIT のコードを、`runtime/fmt` は Ryu の Boost Software License 1.0 のコードを元にしている (`THIRD_PARTY_LICENSES.md`)。
