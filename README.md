# sglua (PoC)

Lua から扱える薄い 3D 描画ライブラリの PoC。SDL3 + sokol_gfx (Vulkan) + Slang + Lua 5.5。

## ビルド

依存:
- CMake 3.20+
- C11 / C++17 対応コンパイラ (GCC / Clang)
- Vulkan loader (`libvulkan.so` — Arch: `vulkan-icd-loader`、Debian/Ubuntu: `libvulkan-dev`)
- Linux x86_64 (現状)

```sh
# Slang prebuilt は third_party/slang/lib に配置済み (gitignore 対象)
cmake -S . -B build
cmake --build build -j
```

## 実行

通常 (実 GPU 経由):

```sh
./build/sglua samples/01_triangle.lua
./build/sglua samples/02_vertex_color.lua
./build/sglua samples/03_texture.lua
./build/sglua samples/04_mvp.lua
```

ヘッドレス (Mesa lavapipe = CPU Vulkan):

```sh
# 事前: sudo pacman -S vulkan-swrast (Arch) / sudo apt install mesa-vulkan-drivers (Debian)
scripts/run-headless.sh samples/01_triangle.lua
```

`scripts/run-headless.sh` は `VK_ICD_FILENAMES` で lavapipe ICD を強制し、
`DISPLAY` / `WAYLAND_DISPLAY` が無ければ自動で `xvfb-run` でラップする。
CI / SSH / コンテナ環境でも動くことを確認している (Mesa lavapipe + AMD radv の両方で
sample 01〜04 が pass)。

スクリーンショット capture (PNG 出力):

```sh
# 30 フレーム描画後にキャプチャして即終了
scripts/run-headless.sh samples/01_triangle.lua --capture out.png --capture-frame 30
```

実 GPU でも `--capture` フラグはそのまま使える。Lua 側からも `capture("path.png")`
でスケジュール可能 (次フレームで実行)。BGRA8/RGBA8 のスワップチェインから RGBA に
swizzle して `stb_image_write` で PNG 出力する。

## サンプル

| # | スクリプト              | 内容                                            |
|---|-------------------------|-------------------------------------------------|
| 1 | 01_triangle.lua         | 単色オレンジ三角形 (use_buffer / use_shader / draw / begin_pass) |
| 2 | 02_vertex_color.lua     | 頂点カラー補間された三角形                      |
| 3 | 03_texture.lua          | チェッカー柄テクスチャを貼った三角形 (use_texture) |
| 4 | 04_mvp.lua              | 回転行列を uniform で渡す三角形                 |

## API

- `use_buffer(key, type, data, version)` — GPU buffer 宣言。`type` は `VERTEX` / `INDEX`。`data` は float の Lua table。同 `version` なら再アップロードしない。
- `use_texture(key, w, h, format, data, version)` — image + sampler を作成。`format` は `RGBA8` / `R8`。`data` は uint8 の Lua table (省略可)。sampler は LINEAR / REPEAT 固定。
- `use_shader(key, vs_src, fs_src, version)` — Slang shader を compile (`vs_main` / `fs_main` entry points)。SPIR-V を生成して reflection し、sokol_gfx (Vulkan) に渡す。
- `begin_pass({ target = main_tex, clear_color = {r,g,b,a} })` / `end_pass()` — pass 制御。`target` は今のところ `main_tex` のみ。
- `draw(count, resources, options)` — 描画コマンド。
  - `resources` は名前付き table: `{ verts = bufferRef, diffuse = textureRef, uniforms = { mvp = {...floats} } }`。テクスチャの名前はシェーダ側のリフレクションに突き合わせる。uniform は uniform block の最初のものに pack される。
  - `options` は `{ shader = shaderRef, blend, depth, depth_write, cull, primitive }`。`shader` だけ必須。
- `capture(path)` — 次フレーム終了時に swapchain image を PNG として `path` に書き出してアプリを終了する。CLI フラグ `--capture <path>` (任意で `--capture-frame N`、デフォルト 30) でも同等。

エントリポイント: Lua 側で `on_init` / `on_frame` / `on_event` / `on_quit` の global 関数を定義すると呼ばれる。

詳細は `tasks.md` 参照。

## 未実装 (将来)

- Sample 5: post process (offscreen render target を渡せるように `use_texture(..., data=nil)` を render target にする)
- Sample 6: deferred shading (MRT、複数 color attachment)
- Sample 7: ホットリロード (use_* の version 引数は対応済み、Lua 側ファイル監視は未実装)
- Golden image diff 回帰テスト (`capture` 機能を活かした自動 visual 比較)
- リソース sweep (フレーム未参照の自動破棄)
- SDL3 GPU backend (Phase 3、cross-platform)
- macOS / Windows 対応 (MoltenVK / dxvk 経由 or SDL3 GPU 経由)
- compute shader / VR / マルチスレッド描画
- Lua 側からの sampler 設定 (filter / wrap)

## アーキテクチャ

```
src/
├── main.c            SDL3 main callbacks エントリ + argv (--capture)
├── app.{h,c}         App 状態 (SDL_Vulkan window + Vulkan instance/device/swapchain, sokol env, lifecycle)
├── lua_api.{h,c}     Lua bindings (use_*, begin_pass, end_pass, draw, capture)
├── enums.h           SglBufferType / SglPixelFormat / ... の C-side enum
├── enums_lua.{h,c}   それらを Lua グローバルに登録
├── pass.{h,c}        現フレームの pass state
├── resources.{h,c}   key → ResEntry のハッシュマップ (buffer/texture/shader)
├── shader.h, shader.cpp   Slang compile (target = SPIR-V) + reflection
├── pipeline.{h,c}    pipeline state hash → sg_pipeline cache
├── capture.{h,c}     swapchain image を PNG として書き出す (vkCmdCopyImageToBuffer)
└── sokol_impl.c      SOKOL_GFX_IMPL の TU (SOKOL_VULKAN backend)
```

```
scripts/
└── run-headless.sh   VK_ICD_FILENAMES=lavapipe + xvfb-run wrapper
```

依存:
- `third_party/sokol/sokol_gfx.h` — single-header (vendored)、`SOKOL_VULKAN` backend
- `third_party/slang/` — Slang 2026.x prebuilt (`include/`, `lib/`)、SPIR-V を target
- `third_party/stb/stb_image_write.h` — single-header (vendored)、PNG 出力
- SDL3 — CMake FetchContent (`SDL_WINDOW_VULKAN` + `SDL_Vulkan_*` API)
- Vulkan loader (`libvulkan.so`) — system 提供
- Lua 5.5 — CMake FetchContent (static lib build)

## Known issues

Phase 2 (Vulkan 移行) で残っている既知の Vulkan validation warning / 制限:

- **Depth/stencil format mismatch** (`VUID-vkCmdDraw-dynamicRenderingUnusedAttachments-08914` /
  `08917`): `src/app.c` で D24_UNORM_S8_UINT を depth attachment に選んでいるが
  sokol 内部は `SG_PIXELFORMAT_DEPTH_STENCIL` を D32_SFLOAT_S8_UINT に解決する。
  validation 警告は出るが描画自体は通る。fix は `app.c` 側で `D32_SFLOAT_S8_UINT` を
  選ぶか pipeline.c に depth format を渡す方向。
- **Single-pair semaphore reuse** (`VUID-vkAcquireNextImageKHR-semaphore-01779`,
  `VUID-vkQueueSubmit-pSignalSemaphores-00067`): `src/app.c` の `vk_acquire_sem` /
  `vk_present_sem` を全フレームで再利用しているため、複数フレーム並列対応の前提では
  推奨されない。fix は per-image semaphore array か `VK_KHR_swapchain_maintenance1`
  の利用。
- **Window resize 未対応**: swapchain recreate (`VK_ERROR_OUT_OF_DATE_KHR`) を
  キャッチしていない。リサイズすると以後のフレームが broken になる可能性。
- **lavapipe での 03_texture**: 解決済み (Phase 2 Task 18 で SPIR-V descriptor set
  patch 適用)。両 driver で動作。

これらはいずれも実用上の致命的問題ではなく、Phase 2 のスコープ外として残置。
将来 Phase 3 (SDL3 GPU backend / multi-platform) と合わせて整理する。

## ライセンス

未定。
