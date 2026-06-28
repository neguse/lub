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
  samples/             # サンプル C# コード
  doc/
    support-matrix.md  # サポートマトリクス
  CMakeLists.txt       # Lua クロスプラットフォームビルド
```

## ライセンス

MIT
