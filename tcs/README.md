# TinyC# (tcs)

C# サブセットから Lua 5.5 ソースコードへのトランスパイラ。ゲームスクリプティング向け。

Roslyn で C# を解析し、Lua のテーブル + メタテーブル OOP に変換する。
型チェックは C# コンパイラが行い、Lua 側は型消去で動作する。

## 必要なもの

- .NET 10 SDK
- CMake 3.10+ (Lua ビルド用)
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

## Lua 5.5 のビルド

CMake でクロスプラットフォームビルド:

```bash
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

Windows (MSVC) の場合も同じ。Visual Studio の Developer Command Prompt か PowerShell で実行する。
ビルド成果物は `deps/lua/lua` (Linux) または `deps/lua/lua.exe` (Windows) に出力される。

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

Lua が未ビルドなら自動で `cmake` を呼んでビルドしてからテストを実行する。

## 使い方

### 基本: C# → Lua 変換

```bash
# stdout に出力
dotnet run --project Transpiler -- samples/hello.cs

# ファイルに出力
dotnet run --project Transpiler -- samples/hello.cs -o out.lua
```

### 複数ファイル

```bash
dotnet run --project Transpiler -- src/Player.cs src/Enemy.cs src/Game.cs -o game.lua
```

同一 Compilation でコンパイルするので、ファイル間のクラス参照が解決される。

### ソースマップ

```bash
dotnet run --project Transpiler -- game.cs -o game.lua --sourcemap
```

`game.lua.map` が生成される。Lua 行番号 → C# ファイル:行番号 の JSON マッピング。

### watch モード

```bash
dotnet run --project Transpiler -- src/*.cs -o out.lua --watch
```

ファイル変更を検知して自動で再トランスパイルする。`Ctrl+C` で停止。

## サポートしている C# 機能

class, enum, interface(型チェックのみ), 継承, コンストラクタ, auto/custom プロパティ,
static/instance メソッド, if/else/while/for/foreach/switch, ラムダ,
List\<T\>, Dictionary\<K,V\>, LINQ (Where/Select/Any/All/First/OrderBy/Min/Max/Sum),
文字列補間, null 条件演算子 (`?.`), is パターン, switch 式 など。

詳細は [doc/support-matrix.md](doc/support-matrix.md) 参照。

## Box2D v3 ヘッドレス物理デモ

C# で書いた物理シミュレーションを Lua + Box2D v3 ネイティブバインディングで実行するデモ。

### 仕組み

1. `physics.cs` — C# でシミュレーションロジックを書く。`b2` クラスはスタブ (中身は空)
2. `tcs` で Lua に変換 → `physics.lua`
3. `main.lua` が `physics.lua` を読み込み、スタブの `b2` を本物の C モジュールで上書き
4. Lua VM 上でシミュレーション実行

### ビルド手順 (共通)

CMake で Lua と lua_b2 (Box2D 入り Lua) を一括ビルドする。

```powershell
# Lua + lua_b2 をビルド (Ninja 推奨)
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

Windows の場合は **x64 Native Tools Command Prompt** または **Developer PowerShell** (`-Arch amd64`) から実行すること。

```powershell
# C# → Lua トランスパイル
dotnet run --project Transpiler -- demos/box2d/physics.cs -o demos/box2d/physics.lua

# 実行
./demos/box2d/lua_b2 demos/box2d/main.lua        # Linux
.\demos\box2d\lua_b2.exe .\demos\box2d\main.lua  # Windows
```

### 出力例

```
=== Box2D v3 + TinyC# Headless Physics ===
3 boxes dropping under gravity (0,-10)
Simulating 3s at 60Hz...

t=0.5s: BoxA:y=8.7834  BoxB:y=18.783  BoxC:y=28.783
t=1.0s: BoxA:y=5.0584  BoxB:y=15.058  BoxC:y=25.058
t=1.5s: BoxA:y=0.5      BoxB:y=8.8259  BoxC:y=18.825
t=2.0s: BoxA:y=0.5      BoxB:y=1.5      BoxC:y=10.083
t=2.5s: BoxA:y=0.5      BoxB:y=1.5      BoxC:y=2.5
t=3.0s: BoxA:y=0.5      BoxB:y=1.5      BoxC:y=2.5

Final: BoxA:y=0.5  BoxB:y=1.5  BoxC:y=2.5
Done.
```

3つの箱が重力で落下し、地面に積み重なる。

## プロジェクト構成

```
tcs/
  Transpiler/          # トランスパイラ本体 (Roslyn → Lua)
  Transpiler.Tests/    # xUnit テスト (101件)
  TinySystem/          # C# 側の型定義 (コンパイル用)
  runtime/
    tinysystem.lua     # Lua ランタイムライブラリ (List/Dict/String/Math)
  deps/
    lua/               # Lua 5.5 ソース (git submodule)
    box2d/             # Box2D v3 (git submodule)
  demos/
    box2d/             # Box2D ヘッドレス物理デモ
  samples/             # サンプル C# コード
  doc/
    support-matrix.md  # サポートマトリクス
  CMakeLists.txt       # Lua クロスプラットフォームビルド
```

## ライセンス

MIT
