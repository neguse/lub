# TinyC# (tcs)

C# サブセットから Lua 5.5 ソースコードへのトランスパイラ。ゲームスクリプティング向け。

Roslyn で C# を解析し、Lua のテーブル + メタテーブル OOP に変換する。
型チェックは C# コンパイラが行い、Lua 側は型消去で動作する。

## 必要なもの

- .NET 10 SDK
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
`run-tests` は `dotnet test` に加え、代表 sample の `tcs check`、analyzer-demo build の expected diagnostics、analyzer nupkg の PackageReference consumer と `.editorconfig` severity override を検査する。
GitHub Actions (`.github/workflows/ci.yml`) でも同じ gate を実行する。

## 依存と配布

NuGet package は floating version を使わず `.csproj` に明示 version を pin し、`packages.lock.json` で transitive dependency も固定する。
依存を更新する場合は対象 package version と lock file を同時に更新する。

CLI publish:

```bash
dotnet publish Transpiler/Transpiler.csproj -c Release -o publish/tcs
```

publish 出力には `runtime/tinysystem.lua` が `runtime/` 配下に同梱される。
通常の CLI 出力はこの runtime を読み込んで Lua prelude に埋め込むため、publish ディレクトリ単体で変換できる。

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

CLI 生成 Lua はデフォルトで TinySystem runtime prelude を埋め込む。
`List` / `Dict` / `Math` / `String` / `Random` / `HotReload` は生成物だけで利用できる。
エンジン側で runtime を供給する場合は `--no-runtime` を付ける。

```bash
dotnet run --project Transpiler -- samples/hello.cs -o out.lua --no-runtime
```

### 準拠チェック

Lua を出力せず、C# compile error と TinyC# 準拠診断だけを返す。
警告またはエラーがあれば exit 1、問題がなければ exit 0。

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

### watch モード

```bash
dotnet run --project Transpiler -- src/*.cs -o out.lua --watch
```

`src/*.cs` は shell が展開する例。shell が glob 展開しない環境ではファイルを個別に渡す。
ファイル変更を検知して自動で再トランスパイルする。`Ctrl+C` で停止。

### HotReload runtime

`HotReload.swap(path)` は `dofile` した Lua を既存 table へ反映する。
`HotReload.watch(path)` / `HotReload.update()` で runtime 側の監視もできるが、
組み込み環境で shell に依存しないよう default `HotReload.mtime` は `nil` を返す。
エンジン側の file API がある場合だけ mtime 関数を注入する。

```lua
HotReload.mtime = function(path)
  return fs.mtime(path)
end
```

### tcs analyzer PoC

Rider などの C# IDE 上で tcs 非準拠コードを警告するための Roslyn Analyzer PoC。
現時点では `struct`, `record struct`, `try/catch`, `throw`, local function, list pattern、未対応 BCL API / 未対応 core library member、collection への null 保存を `TCS1001` / `TCS1002` / `TCS1003` として報告する。
同じ共有ルールを `tcs check` と transpiler warning でも使う。

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

1. 必要なら `samples/analyzer-demo/open-rider-demo.sh` で pre-check 後に demo project を Rider で開く。Rider command を自動検出できない場合は `TCS_RIDER_COMMAND=/path/to/rider.sh` を指定する
1. `samples/analyzer-demo/analyzer-demo.csproj` を含む solution/project を Rider で開く
1. Restore 後、`samples/analyzer-demo/Program.cs` を開く
1. `struct`, local function, `try`, `throw`, `values is [1, 2]`, `System.IO.File.ReadAllText`, `List<string?> { null }` に Roslyn inspection の squiggle が出ることを確認する
1. Build tool window で `TCS1001` が5件、`TCS1002` が1件、`TCS1003` が1件出ることを確認する
1. `.editorconfig` の `dotnet_diagnostic.TCS1001/TCS1002/TCS1003.severity` を `error` へ変え、Rider 表示が追従するか確認する（build 上の severity override は `run-tests` で検証済み）
1. [`samples/analyzer-demo/RIDER_VERIFICATION_TEMPLATE.md`](samples/analyzer-demo/RIDER_VERIFICATION_TEMPLATE.md) に沿って、結果を `q.md` に go / no-go として記録する

## サポートしている C# 機能

class, enum, interface(型チェックのみ), 継承, コンストラクタ, auto/custom プロパティ,
static/instance メソッド, if/else/while/for/foreach/switch, ラムダ,
List\<T\>, Dictionary\<K,V\>, LINQ (Where/Select/Any/All/First/Last/OrderBy/OrderByDescending/Take/Skip/Min/Max/Sum/Count/ToDictionary),
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
  deps/
    lua/               # Lua 5.5 ソース (git submodule)
  samples/             # サンプル C# コード
  doc/
    support-matrix.md  # サポートマトリクス
  CMakeLists.txt       # Lua クロスプラットフォームビルド
```

## ライセンス

MIT
