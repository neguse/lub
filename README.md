# lub

`lub` は、細部までこだわったゲーム体験を作るためのコード中心のゲーム開発環境。
ゲームの動作を止めずにコード変更を即座に反映し、試行錯誤の速度を上げることを
コアな価値に置く。

runtime は C/C++ と既存ライブラリで組み、Lua を通して API を呼び出す。
上位の script layer は C# (TinyC# サブセット) で書き、Lua に transpile して
hot reload する。同じ C# を実 .NET で動かす経路もある。
特定のアセット形式や GUI editor に依存せず、ゲームを構成する状態、描画、
入力、物理、音、debug 情報をコードから制御できる環境を目指す。

現時点の実装は SDL3 + Slang + Lua 5.5 を基盤にし、GPU backend は
native がプラットフォーム直接実装 (`native`、default — Windows: D3D12、
Linux: Vulkan) と SDL3 GPU API (`sdlgpu`)、web が webgpu.h 直接実装。
対応プラットフォームは Linux x86_64、Windows x86_64、WebAssembly/WebGPU。

## ドキュメント

- [lub.neguse.net/docs](https://lub.neguse.net/docs): ゲームを書く人向けの
  ガイド(基礎概念)+ API reference。ガイドの原稿は `docs/manual/`、API は
  `cs-lib/lub_stub.cs` の doc comment から生成。
- [lub.neguse.net](https://lub.neguse.net): ブラウザで動く playground。
- [docs/README.md](docs/README.md): ドキュメント索引と方針。
- [docs/design.md](docs/design.md): lub の why / to-be / 設計原則。
- [docs/roadmap.md](docs/roadmap.md): phase ごとの達成目標と状態。
- [docs/serve.md](docs/serve.md): 外部リポのゲームを web で開発する `--serve` モード。
- [docs/profile.md](docs/profile.md): Release 計測と汎用 CPU profiler。

## ビルド

依存:
- CMake 3.22+
- C11 / C++17 対応コンパイラ (GCC / Clang / MSVC)
- Vulkan loader と開発 header (Linux 必須) — Arch: `vulkan-icd-loader`、Debian/Ubuntu: `libvulkan-dev`
- Vulkan SDK (Windows 任意) — SDK が見つかった場合だけ `vulkan` backend を組み込む

Slang prebuilt (`slang.dll` / `libslang.so` 等) は configure 時に
`third_party/slang/lib/` に無ければ GitHub release から自動取得する
(`third_party/slang/{lib,bin}/` は gitignore 対象)。

コンパイルする依存 (SDL3 / Lua / Box2D / Box3D) は `third_party/` 配下の
git submodule なので、clone 後に一度 submodule を取得する:

```sh
git submodule update --init
```

Linux:

```sh
cmake -S . -B build
cmake --build build -j
```

Windows (PowerShell, MSVC + Ninja):

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat'
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
```

Release build は手順を固定するため、通常は script 経由で行う
(詳細は [docs/release-build.md](docs/release-build.md)):

```sh
bash scripts/build-release.sh                                              # Linux
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1  # Windows
```

CMake の POST_BUILD で `SDL3.dll` と Slang ランタイム DLL 群が `lub.exe`
の横にコピーされるので、追加の PATH 設定なしで実行できる。

## コードフォーマット

各フォーマッタの デフォルト設定 で整形する。既存スタイルに寄せる
プロジェクト固有の設定ファイル (`.clang-format` / `.prettierrc`) は意図的に置かず、ツール標準のスタイルに従う。

- C/C++/Slang — `clang-format` (LLVM default、Slang は HLSL 扱い)
- Lua — `stylua` (`web/` で `npm install` 後に利用可)
- C# — `dotnet format whitespace` (dotnet SDK 付属)
- Web TS — `prettier` (`web/` で `npm install` 後に利用可)

```sh
scripts/format.sh            # 全ソースを整形
scripts/format.sh --check    # 整形が必要か確認のみ (CI 向け / 非ゼロ終了で失敗)
```

## 実行

```sh
./build/lub samples/01_triangle/Triangle01.csproj
```

(Windows は `.\build\lub.exe samples\01_triangle\Triangle01.csproj` 形式)

`.cs` を編集して保存すると、走っている game に即反映される(hot reload)。
`.slang` / PNG / `*.verts.lua` などの data ファイルも同様に保存で即反映。

依存:
- dotnet SDK。C# → Lua の transpiler(TinyC#、`third_party/tcs` submodule)を
  lub が dotnet で動かす。

raw Lua で書いたゲームは transpile なしで起動できる
(`./build/lub samples/27_lua_triangle/27_lua_triangle.lua`)。

サンプルは `samples/<name>/` に 1 つずつ自己完結する形で置く
(`<Entry>.cs` + `<Entry>.csproj` + `data/`)。
ゲームの書き方(ライフサイクル、座標系、描画モデル、C#→Lua の注意点)は
[lub.neguse.net/docs](https://lub.neguse.net/docs) のガイドを参照。

Linux ヘッドレス (Mesa lavapipe = CPU Vulkan):

```sh
# 事前: sudo pacman -S vulkan-swrast (Arch) / sudo apt install mesa-vulkan-drivers (Debian)
scripts/run-headless.sh samples/01_triangle/Triangle01.csproj
```

`scripts/run-headless.sh` は `VK_ICD_FILENAMES` で lavapipe ICD を強制し、
`DISPLAY` / `WAYLAND_DISPLAY` が無ければ自動で `xvfb-run` でラップする。
これにより CI / SSH / コンテナ環境でも native sample を走らせられる
(Mesa lavapipe / AMD radv 双方で動作)。
Windows 用のヘッドレス wrapper は無く、実 GPU で動かす前提。

スクリーンショット capture (PNG 出力、native のみ):

```sh
# 30 フレーム描画後にキャプチャして即終了
scripts/run-headless.sh samples/01_triangle/Triangle01.csproj --capture out.png --capture-frame 30

# golden test 用: 各 render frame の dt も 1/60 秒に固定
scripts/run-headless.sh samples/01_triangle/Triangle01.csproj --capture out.png --capture-frame 30 --fixed-dt 0.0166666666666667
```

通常の `OnFrame(dt)` と UI には実測のフレーム間隔が渡る。`--capture-frame` は
capture する render frame 番号だけを固定し、経過時間は固定しない。
`--fixed-dt <seconds>` は golden / replay テスト専用で、指定すると実測値の代わりに
同じ `dt` を毎フレーム渡す(有限かつ `0 < dt <= 0.25` の値のみ)。通常プレイの
速度制限や FPS 制限には使わない。

ゲーム側から任意の render target を保存する場合は `Gfx.Readback()` を使う
([API reference](https://lub.neguse.net/docs) 参照)。

### Sprite benchmark (Release)

```sh
bash scripts/run-sprites-bench.sh                                              # Linux
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-sprites-bench.ps1  # Windows
```

score の見方や `-NoBuild` / backend 切替は [docs/sprites-bench.md](docs/sprites-bench.md) を参照。

### Golden image diff (回帰テスト)

```sh
scripts/run-golden.sh             # 全 sample × backend を tests/golden と cmp
scripts/run-golden.sh --update    # golden 画像を再生成 (描画意図的変更時)
scripts/run-golden.sh --sample 01_triangle --backend sdlgpu
```

プラットフォームごとに機材非依存の CPU rasterizer を強制するので capture が
確定的になり、`cmp -s` で完全一致判定する。Linux は lavapipe + xvfb で
sdlgpu と vulkan を、Windows (git bash) は WARP (`LUB_D3D12_WARP=1`) で
d3d12 をチェックする。実 GPU でのドリフトは想定範囲外
(tolerance 比較は別途)。

## Backend 切替

lub は 4 つの GPU backend を持ち、同一 API で動く:

- `d3d12` — Windows の既定。D3D12 直接実装
  (設計は [docs/d3d12-backend.md](docs/d3d12-backend.md))
- `vulkan` — Linux の既定。Vulkan 直接実装 (`src/backend_vulkan.c`)。Windows でも
  Vulkan SDK が見つかる build では選べる
- `sdlgpu` — SDL3 GPU API 経由の実装 (native 全般の代替 backend)
- `webgpu` — web build の実体 (webgpu.h 直接)。web では backend 指定は無視される

設計記録は [docs/log/2026-06-22-native-backend-design.md](docs/log/2026-06-22-native-backend-design.md)、
整理方針は [docs/log/2026-07-07-backend-consolidation.md](docs/log/2026-07-07-backend-consolidation.md)。

切替は `Config` の `Backend`。サンプルは環境変数 `LUB_BACKEND` を読んで
渡しているので CLI から切り替えられる:

```sh
LUB_BACKEND=sdlgpu ./build/lub samples/01_triangle/Triangle01.csproj
```

## WASM playground (web)

ブラウザ上で動く playground を `web/` 配下に同梱し、
[lub.neguse.net](https://lub.neguse.net) で公開している。`.cs` は .NET wasm 化した
TinyC# コンパイラでブラウザ内で Lua に compile され、native と
同じ hot reload 経路で player に反映される。ガイド + API reference の
docs サイト (`/docs`) も同じサイトに同居する。

ビルド手順・実行時アーキテクチャ・headless 検証・制約は
[web/README.md](web/README.md) を参照。

## 外部プロジェクトから使う (--serve)

lub を別リポのゲームから使うための Web 開発モード。native の `lub Game.csproj` と
対称に、ブラウザをレンダリング先として `.cs` / `.slang` / `data/` のホットリロード
開発ができる:

```sh
./build/lub --serve mygame/Game.csproj   # http://localhost:8080 (--port N で変更)
```

雛形は `templates/game/` (C#、tcs→Lua と .NET 実行の両方で動く) を `cp -r` して使う。詳細は [docs/serve.md](docs/serve.md)。

## ライセンス

- lub 本体(C ランタイム / web playground / samples / `cs-lib`)は MIT(`LICENSE`)。
- バンドル/リンクする第三者依存(SDL3 / Lua / Slang 等)は
  `THIRD_PARTY_LICENSES.md` を参照。
