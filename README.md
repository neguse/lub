# lub

`lub` は、細部までこだわったゲーム体験を作るためのコード中心のゲームエンジン。
ゲームの動作を止めずにコード変更を即座に反映し、試行錯誤の速度を上げることを
コアな価値に置く。

runtime は C/C++ と既存ライブラリで組む。
上位の script layer は C# (TinyC# サブセット) で書き、tcs VM の bytecode (`.tcsb`) にして lub の player で動かし、hot reload する。
同じ C# を tcs2c で C にしてゲームごとの player に link する経路と、実 .NET で動かす経路もある。
特定のアセット形式や GUI editor に依存せず、ゲームを構成する状態、描画、
入力、物理、音、debug 情報をコードから制御できる環境を目指す。

現時点の実装は SDL3 + Slang + tcs VM を基盤にし、GPU backend は native がプラットフォームごとの直接実装 (D3D12、Vulkan、Metal) と SDL3 GPU API (`sdlgpu`)、web が webgpu.h 直接実装。
対応プラットフォームは Linux x86_64、Windows x86_64、macOS arm64、iOS arm64、Android arm64 (Meta Quest。tcs2c で C にしたゲームだけ。[docs/android.md](docs/android.md))、WebAssembly/WebGPU。

## ドキュメント

- [lub.neguse.net/docs](https://lub.neguse.net/docs): ゲームを書く人向けの
  ガイド(基礎概念)+ API reference。ガイドの原稿は `docs/manual/`、API は
  `cs-lib/lub_stub.cs` の doc comment から生成。
- [lub.neguse.net](https://lub.neguse.net): ブラウザで動くエディタ (lider)。サンプルはメニューから開ける。
- [docs/README.md](docs/README.md): ドキュメント索引と方針。
- [docs/design.md](docs/design.md): lub の why / to-be / 設計原則。
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

コンパイルする依存 (SDL3 / Box2D / Box3D / OpenXR (Linux / Android)) は `third_party/` 配下の
git submodule なので、clone 後に一度 submodule を取得する:

```sh
git submodule update --init
```

lub の configure は、これらを `third_party/CMakeLists.txt` の project として build し、build のディレクトリの `deps/` に install して使う (`cmake/LubDeps.cmake`)。
build するのは初めての configure と、submodule の commit か toolchain が変わったときだけ。
submodule の中の file を手で書き換えたときは、lub を configure し直す。

Linux:

```sh
cmake -S . -B build
cmake --build build -j
```

macOS (Xcode か Command Line Tools。iOS の app は
[docs/metal-backend.md](docs/metal-backend.md)):

```sh
cmake -S . -B build-mac -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build-mac
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
bash scripts/build-release.sh --build-dir build-mac                        # macOS
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1  # Windows
```

CMake の POST_BUILD で `SDL3.dll` と Slang ランタイム DLL 群が `lub.exe`
の横にコピーされるので、追加の PATH 設定なしで実行できる。

## コードフォーマット

各フォーマッタのデフォルト設定で整形する。既存スタイルに寄せるプロジェクト固有の設定ファイルは置かない。
例外は `.clang-format` の `ReflowComments: false` だけで、日本語のコメントを和欧の境目で折り返させないためのもの。コメントは 1 文を 1 行に書く。

- C/C++/Slang — `clang-format` (LLVM default にコメントの折り返しだけを止めたもの、Slang は HLSL 扱い)
- Lua の table の data (`*.verts.lua`) — `stylua` (`web/` で `npm install` 後に利用可)
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
- C# の変換は TinyC# (`tcs/`) が行う。
  lub がどの tcs を使うか (dotnet が要るかどうか) は [docs/manual/08-csharp.md](docs/manual/08-csharp.md) の「dotnet の無い環境 (C の tcs)」にある。

サンプルは `samples/<name>/` に 1 つずつ自己完結する形で置く
(`<Entry>.cs` + `<Entry>.csproj` + `data/`)。
ゲームの書き方(ライフサイクル、座標系、描画モデル、TinyC# の注意点)は
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
lavapipe が無いときは、その機械の Vulkan driver と `SDL_VIDEO_DRIVER=offscreen` で走らせる。この場合の capture は golden と一致しないことがある。
lavapipe があって画面も `xvfb-run` も無いときは、lavapipe と `SDL_VIDEO_DRIVER=offscreen` で走らせる。
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
`--readback-fixed-latency` は `Gfx.ReadTexture` の結果が届く frame を固定する(frame N の要求はちょうど frame N+2 に届く)。
ゲームの `ConfigOpts.ReadbackFixedLatency` が指定すればそちらが勝つ。
golden と tests/cs の runner はこれを付けて走らせる。

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
scripts/run-golden.sh             # 描画を golden で見る tests/cs の test × backend を tests/golden と cmp
scripts/run-golden.sh --update    # golden 画像を再生成 (描画意図的変更時)
scripts/run-golden.sh --test TestIndexedDraw --backend sdlgpu
```

OS ごとにどの backend をどう比べるか、byte 一致がどの条件で成り立つかは、`scripts/run-golden.sh` の先頭のコメントにある。

## Backend 切替

lub は 5 つの GPU backend を持ち、同一 API で動く:

- `d3d12` — D3D12 直接実装 (設計は [docs/d3d12-backend.md](docs/d3d12-backend.md))
- `vulkan` — Vulkan 直接実装 (`src/backend_vulkan.c`)。
  `openxr` はこの backend で OpenXR の headset に描く ([docs/manual/07-xr.md](docs/manual/07-xr.md))
- `metal` — Metal 直接実装 (設計は [docs/metal-backend.md](docs/metal-backend.md))
- `sdlgpu` — SDL3 GPU API 経由の実装 (Windows / Linux の代替 backend)
- `webgpu` — web build の実体 (webgpu.h 直接)

どの platform で選べるかと、未指定のときの既定は、API reference の `ConfigOpts.Backend` にある。

設計記録は [docs/log/2026-06-22-native-backend-design.md](docs/log/2026-06-22-native-backend-design.md)、
整理方針は [docs/log/2026-07-07-backend-consolidation.md](docs/log/2026-07-07-backend-consolidation.md)。

切替は `Config` の `Backend`。サンプルは環境変数 `LUB_BACKEND` を読んで
渡しているので CLI から切り替えられる:

```sh
LUB_BACKEND=sdlgpu ./build/lub samples/01_triangle/Triangle01.csproj
```

## web のサイト

ブラウザで動くエディタ (lider、`lider/`) を `web/` 配下に同梱し、[lub.neguse.net](https://lub.neguse.net) で公開している。
`.cs` は lider が player に組み込んだ tcs で tcs VM の bytecode に変換し、hot reload で動いているゲームに反映される。
ガイド + API reference の docs サイト (`/docs`) も同じサイトに同居する。

ビルド手順・実行時アーキテクチャ・headless 検証・制約は
[web/README.md](web/README.md) を参照。

## 外部プロジェクトから使う

別リポのゲームは、雛形の `templates/game/` (C#、tcs VM の player と .NET 実行の両方で動く) を `cp -r` して作り、サンプルと同じく `lub` で動かす。

```sh
./build/lub mygame/Game.csproj                  # 保存すると hot reload
./build/lub export web mygame/Game.csproj -o out # web の page 1 枚にする
```

## ライセンス

- lub 本体(C ランタイム / web のサイト / lider / samples / `cs-lib`)は MIT(`LICENSE`)。
- バンドル/リンクする第三者依存(SDL3 / Slang / TinyC# の runtime 等)は
  `THIRD_PARTY_LICENSES.md` を参照。
