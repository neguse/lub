# sglua (PoC)

Lua から扱える薄い 3D 描画ライブラリの PoC。SDL3 + Slang + Lua 5.5。
GPU backend は **sokol_gfx (Vulkan)** と **SDL3 GPU API** の 2 系統を持ち、
Lua の `config()` で切り替えられる (詳細は後述)。
対応プラットフォームは Linux x86_64 と Windows x86_64。

## ビルド

依存:
- CMake 3.20+
- C11 / C++17 対応コンパイラ (GCC / Clang / MSVC)
- Vulkan SDK / loader
  - Linux — Arch: `vulkan-icd-loader`、Debian/Ubuntu: `libvulkan-dev`
  - Windows — LunarG Vulkan SDK (`winget install KhronosGroup.VulkanSDK`)

Slang prebuilt (`slang.dll` / `libslang.so` 等) は configure 時に
`third_party/slang/lib/` に無ければ GitHub release から自動取得する
(`third_party/slang/{lib,bin}/` は gitignore 対象)。

Linux:

```sh
cmake -S . -B build
cmake --build build -j
```

Windows (PowerShell, MSVC + Ninja):

```powershell
$env:VULKAN_SDK = "C:\VulkanSDK\1.4.341.1"  # winget でインストールされた SDK
& 'C:\Program Files\Microsoft Visual Studio\18\Professional\VC\Auxiliary\Build\vcvars64.bat'
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
```

CMake の POST_BUILD で `SDL3.dll` と Slang ランタイム DLL 群が `sglua.exe`
の横にコピーされるので、追加の PATH 設定なしで実行できる。

## 実行

```sh
./build/sglua samples/01_triangle.lua
./build/sglua samples/02_vertex_color.lua
./build/sglua samples/03_texture.lua
./build/sglua samples/04_mvp.lua
```

(Windows は `.\build\sglua.exe samples\01_triangle.lua` 形式)

Linux ヘッドレス (Mesa lavapipe = CPU Vulkan):

```sh
# 事前: sudo pacman -S vulkan-swrast (Arch) / sudo apt install mesa-vulkan-drivers (Debian)
scripts/run-headless.sh samples/01_triangle.lua
```

`scripts/run-headless.sh` は `VK_ICD_FILENAMES` で lavapipe ICD を強制し、
`DISPLAY` / `WAYLAND_DISPLAY` が無ければ自動で `xvfb-run` でラップする。
これにより CI / SSH / コンテナ環境でも sample 01〜04 が走る (Mesa lavapipe / AMD radv 双方で動作)。
Windows 用のヘッドレス wrapper は無く、実 GPU で動かす前提。

スクリーンショット capture (PNG 出力):

```sh
# 30 フレーム描画後にキャプチャして即終了
scripts/run-headless.sh samples/01_triangle.lua --capture out.png --capture-frame 30
```

実 GPU でも `--capture` フラグはそのまま使える。Lua 側からも `capture("path.png")`
でスケジュール可能 (次フレームで実行)。BGRA8/RGBA8 のスワップチェインから RGBA に
swizzle して `stb_image_write` で PNG 出力する。

### Golden image diff (回帰テスト)

```sh
scripts/run-golden.sh             # 全 sample × 両 backend を tests/golden と cmp
scripts/run-golden.sh --update    # golden 画像を再生成 (描画意図的変更時)
scripts/run-golden.sh --sample 01_triangle --backend sokol
```

lavapipe + xvfb 環境では capture が確定的なので `cmp -s` で完全一致判定する。
実 GPU でのドリフトは想定範囲外 (tolerance 比較は別途)。

## Backend 切替

sglua は内部に 2 つの GPU backend を持つ:

- `sokol` (default) — sokol_gfx (Vulkan)
- `sdlgpu` — SDL3 GPU API (現在 Vulkan で実装、将来 Metal / D3D12 にも展開可能)

切替は Lua の `on_init` 内で `config({ backend = "sdlgpu" })` を呼ぶ。
サンプルでは `arg[1]` または環境変数 `SGLUA_BACKEND` を見るパターン:

```lua
function on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end
```

```sh
# default = sokol
./build/sglua samples/01_triangle.lua

# SDL3 GPU 経路
SGLUA_BACKEND=sdlgpu ./build/sglua samples/01_triangle.lua

# headless でも同じ
SGLUA_BACKEND=sdlgpu scripts/run-headless.sh ./build/sglua samples/01_triangle.lua
```

どちらの backend でも 4 サンプル + capture が同一 Lua API で動く。
lavapipe + xvfb 環境では、両 backend の capture PNG は **byte-identical** になる。

## Live edit (file watching)

サンプルは `samples/data/` 配下の外部ファイルから shader / 頂点データ / テクスチャを
読み込む。起動中にファイルを編集して保存すると次フレームから反映される。

仕組み:

- 各サンプル冒頭で `samples/sg_io.lua` を `dofile` で読み込み、`load_text` /
  `load_floats` / `load_png` を経由してリソースを取得する。
- helper は `path → {mtime, content_hash}` のキャッシュを持ち、毎フレームの
  `stat()` 1 回だけで「変化なし」を判定する。mtime 違い時のみ再読み込みして
  FNV-1a 64 ハッシュを取り、それを `version` として `use_*` に渡す。
- C 側は `version` 違いで in-place update (buffer / texture) または recompile
  (shader) を実施。shader recompile 時は旧 shader を参照する pipeline cache
  entry を sweep してリークを防ぐ。
- shader compile error 時は旧 shader を維持してログを出すのみで、クラッシュせず
  エディタで修正→保存すれば復帰する (初回 compile 失敗だけは loud に止める)。

例: `samples/data/01_triangle.fs.slang` の出力色をエディタで書き換えて保存すると、
起動中の `samples/01_triangle.lua` の三角形の色が即座に変わる。
PNG を別画像で上書きすればテクスチャも、`*.verts.lua` を編集すれば頂点も同様。

## WASM playground (web)

ブラウザ上で動く Vite + CodeMirror ベースの playground を `web/` 配下に同梱。
sokol-gfx の WGPU backend を target に WASM へクロスコンパイルしたバイナリを iframe
で読み込み、左ペインのエディタで `.slang` / `.lua` を編集すると 300ms debounce で右ペインの
プレイヤーに同期される (`samples/data/*` の mtime/hash hot-reload 経路を再利用)。
shader compile は [slang-wasm](https://github.com/shader-slang/slang/releases) を vendor。

### Build

```sh
# 1. WASM バイナリを生成 (Linux/macOS — emsdk が必要)
source ~/emsdk/emsdk_env.sh             # emcc / emcmake を PATH に
emcmake cmake -S . -B build/wasm        # WGPU + emdawnwebgpu port が configure される
cmake --build build/wasm -j             # sglua.{js,wasm,data} が生成

# 2. JS 側の依存と slang-wasm を取得
cd web
npm install                             # postinstall で web/scripts/fetch-slang-wasm.sh が
                                        # web/public/slang/ に slang-wasm.{js,wasm} を取得
npm run dev                             # http://localhost:5173/ で dev server 起動
npm run verify                          # 別端末: playwright + swiftshader で headless 検証
npm run build                           # web/dist/ に production bundle 生成
```

### アーキテクチャ

```
            parent (index.html / main.ts)              iframe (player.html / player.ts)
            ┌────────────────────────────┐             ┌──────────────────────────────────┐
            │ CodeMirror editor          │   setFiles  │ slang-bridge.ts                  │
            │   path -> content table    │  ────────▶  │   window.slangCompile() を export │
            │ sample dropdown / restart  │  syncFiles  │ WebGPU device 取得 → preinit     │
            │ debounce 300ms             │  ────────▶  │ sglua.js (Emscripten module)     │
            └────────────────────────────┘  ◀─player──│   ↑ EM_ASYNC_JS bridge            │
                                            Ready/log │   ↑ FS.writeFile で MEMFS overlay │
                                                       │ sokol_gfx (WGPU) — canvas へ描画 │
                                                       └──────────────────────────────────┘
```

postMessage プロトコル:

- `parent → iframe`: `setFiles {files, entry}` (初回ブート時 1 回), `syncFiles {files}` (編集毎)
- `iframe → parent`: `playerReady` (ハンドシェイク), `log {level, msg}` (console relay)

shader compile は C 側 (`src/shader.cpp`) の `EM_ASYNC_JS` shim から
`window.slangCompile(src, entry, stage)` を呼び、`{wgsl, reflectJson}` を `'\x01'`
区切りで pack して戻す。エラーは `'\x02' + msg` 形式で Slang diagnostic として
err_buf に届く。

MEMFS sync: iframe 側で Emscripten の data file package (`sglua.data`) をマウント
した直後に `FS.writeFile` でエディタ内容を上書きする (`player.ts` の `postPreload`
hook)。実行中の `syncFiles` も同じ `FS.writeFile` 経路で、C 側は次フレームの
`stat()` で mtime 違いを検知して reload する (native と同じ hot-reload コード)。

### サンプル対応状況 (web)

| Sample | Status |
|--------|--------|
| 01〜05, 07 | ✓ ブラウザで動作 |
| 06_deferred | ✗ MRT → swapchain 経路の WGPU validation で描画されない (詳細 → 後述) |

06_deferred は当初 slang-wasm が `Aborted ... unreachable` で死んでいた。
原因は per-compile に `Session` を作り捨てしていたことで、3 回目あたりの
`Session.delete()` で slang-wasm が internal abort する embind バグ
(`v2026.8.1`)。Phase 8 で `sharedSession` を 1 つ保持し続け、module 名だけ
ユニーク化する戦略に切り替えて compile は green。
残るのは swapchain pass で `depth attachment 480x360 != color attachments
base plane 1280x720` を report される WGPU validation エラーで、
これは MRT pass 後の sokol-gfx WGPU backend 側の swapchain 寸法管理の
別バグ (sample 01〜05, 07 で出る warning は描画を阻害しないのに 06 だけは
submit が無効化される)。native は影響なし、`scripts/run-golden.sh` で
22/22 PASS。

`web/scripts/verify-headless.mjs` は `KNOWN_FAILING` セットを持っており、
06 は描画失敗 (`nonBlack` ratio = 0) でも CI を落とさないようゲートしてある。

### Browser requirements

- WebGPU が利用可能なブラウザ:
  - **Chrome / Edge** (primary、137+) — 既定で WebGPU 有効。
  - **Firefox Nightly** — `dom.webgpu.enabled` を `about:config` で有効化。
- ローカル開発: Vite dev server が emdawnwebgpu に必要な CORS/MIME 設定を済ませる。
- production bundle (`npm run build`) は `web/dist/` 配下、`/wasm/`,
  `/slang/` への絶対パス前提なので site root に置く。

### Headless verification

`npm run verify` は playwright + chromium (swiftshader Vulkan) で:

1. sample 01 の初期描画 (orange triangle on dark blue clear) を pixel bucket で確認
2. fragment shader を編集 → green になる
3. lua の clear_color を編集 → 背景が red になる
4. verts を縮小編集 → green pixel 数が減る
5. sample 01〜07 を順に切替 → 各サンプルの非黒描画を確認 (KNOWN_FAILING を除く)

スクリーンショットは `/tmp/sglua-verify/` に出力される。CI 利用時は dev server を
別ジョブで立ち上げてから `SGLUA_URL=http://...` を指定すること。

### Live edit caveats

- shader に syntax error がある場合: 既存の shader を維持して Slang diagnostic を
  iframe log に流すのみ (next save で復帰)。初回 compile 失敗のみ load を止める。
- 300ms debounce: 入力後 300ms 静止してから `syncFiles` を送る。連打中は更新されない。
- サンプル切替時に dirty な編集があると `confirm()` で警告する。

### Known limitations

- **capture / golden image は native のみ**。WebGPU の `mapAsync` 経路で readback
  は可能だが capture API が同期 sync なので未実装 (`backend_sokol.c` の `sk_capture`
  が `false` を返す)。
- **sdlgpu backend は web 非対応**。WGPU backend の sokol のみ。
- **sample 06 は描画されない (上記表)**。compile は成功するが MRT → swapchain pass
  の WGPU validation で submit が無効化される。

## サンプル

| # | スクリプト              | 内容                                            |
|---|-------------------------|-------------------------------------------------|
| 1 | 01_triangle.lua         | 単色オレンジ三角形 (use_buffer / use_shader / draw / begin_pass) |
| 2 | 02_vertex_color.lua     | 頂点カラー補間された三角形                      |
| 3 | 03_texture.lua          | チェッカー柄テクスチャを貼った三角形 (use_texture) |
| 4 | 04_mvp.lua              | 回転行列を uniform で渡す三角形                 |
| 5 | 05_postprocess.lua      | offscreen render target に三角形 → 全画面 quad で色反転 + ヴィネット post process |
| 6 | 06_deferred.lua         | MRT (2 color attachments) で G-buffer 風に色をペアで書き出し → swapchain pass で左右 split-screen に表示 |
| 7 | 07_compute.lua          | compute shader で storage buffer に三角形の頂点を書き出し、同じバッファを VBO として draw |

## API

- `use_buffer(key, type, data, version)` — GPU buffer 宣言。`type` は `VERTEX` / `INDEX` / `STORAGE`。`data` は float の Lua table。`STORAGE` の場合は `data` の代わりに float 数 (integer) を渡すと中身未初期化で割り当てる (compute shader が後で埋める前提)。同 `version` なら再アップロードしない。`STORAGE` は VBO 兼用で作られるので、compute が書き出したバッファをそのまま draw の `verts` に渡せる。
- `use_texture(key, w, h, format, data, version, opts?)` — image + sampler を作成。`format` は `RGBA8` / `R8`。`data` は uint8 の Lua table (省略可、`opts.target=true` の場合は nil 必須)。`opts` (省略可) は `{ filter = LINEAR|NEAREST, wrap = REPEAT|CLAMP, target = bool }`。デフォルトは `LINEAR` / `REPEAT` / `false`。`target=true` で render-target texture (color attachment + sampler) を宣言。
- `use_shader(key, vs_src, fs_src, version)` — Slang shader を compile (`vs_main` / `fs_main` entry points)。SPIR-V を生成して reflection し、sokol_gfx (Vulkan) に渡す。
- `use_shader_compute(key, cs_src, version)` — compute shader を compile (`cs_main` entry point)。`[numthreads(...)]` で threadgroup を指定。`dispatch` で呼ぶ。
- `begin_pass({ target = main_tex | texRef, clear_color = {r,g,b,a} })` / `end_pass()` — pass 制御。`target` は `main_tex` (swapchain) または `use_texture(..., {target=true})` で宣言した texture ref。後者で offscreen render target に描画できる (Sample 5)。offscreen pass は depth/stencil 無し、swapchain pass は depth/stencil 付き。pipeline cache のキーには color format と depth 有無も含まれる。
- `begin_pass({ targets = {texRef1, texRef2, ...}, clear_colors = {{r,g,b,a}, ...} })` — MRT (Multi Render Target) 形式の offscreen pass (Sample 6)。最大 `SGL_MAX_COLOR_TARGETS` (= 4) 個まで。全 target は同サイズで、各々 `use_texture(..., {target=true})` で宣言済みであること。fragment shader 側は `SV_Target0` / `SV_Target1` / ... を出力する。
- `draw(count, resources, options)` — 描画コマンド。
  - `resources` は名前付き table: `{ verts = bufferRef, diffuse = textureRef, uniforms = { mvp = {...floats} } }`。テクスチャの名前はシェーダ側のリフレクションに突き合わせる。uniform は uniform block の最初のものに pack される。
  - `options` は `{ shader = shaderRef, blend, depth, depth_write, cull, primitive }`。`shader` だけ必須。
- `dispatch(x, y, z, resources, options)` — compute dispatch。`begin_pass`/`end_pass` の外側で呼ぶこと。`resources` は `{ buffer_name = bufferRef, uniforms = {...} }` で、shader 側 reflection の名前と突き合わせて binding を解決する。`options.shader` には compute shader ref が必須。PoC では RW storage buffer (`RWStructuredBuffer<...>`) 1〜N 個 + uniform block 1 個まで。read-only storage buffer / storage texture は未対応。
- `capture(path)` — 次フレーム終了時に swapchain image を PNG として `path` に書き出してアプリを終了する。CLI フラグ `--capture <path>` (任意で `--capture-frame N`、デフォルト 30) でも同等。

### Live edit helpers

- `file_mtime(path)` — ファイルの mtime をナノ秒単位の整数で返す。存在しなければ nil。
- `fnv1a64(s)` — 文字列の FNV-1a 64-bit ハッシュを整数で返す。`use_*` の `version` 引数に流すための content-hash 用途。
- `load_png(path)` — stb_image で PNG を RGBA8 にデコードして `(bytes_table, w, h, fmt)` を返す。失敗時は nil。
- 高レベルラッパ: `samples/sg_io.lua` (`load_text` / `load_floats` / `load_png`) — mtime fast-path + content hash キャッシュ。詳細は「Live edit」セクション。

エントリポイント: Lua 側で `on_init` / `on_frame` / `on_event` / `on_quit` の global 関数を定義すると呼ばれる。

## 未実装 (将来)

- リソース sweep (フレーム未参照の自動破棄)
- macOS 対応 (MoltenVK 経由 or SDL3 GPU の Metal backend 経由)
- sample 06 を web で描画 (sokol-gfx WGPU backend の swapchain depth 寸法問題)

## アーキテクチャ

GPU 操作は `RenderBackend` vtable に集約され、`backend_sokol` / `backend_sdlgpu` の
2 実装を切替えて使う。`pass.c` / `pipeline.c` / `resources.c` / `capture.c` は
backend を呼び出す薄い glue になっている。

```
src/
├── main.c            SDL3 main callbacks エントリ + argv (--capture)
├── app.{h,c}         App 状態 (SDL window + 選択 backend、lifecycle)
├── lua_api.{h,c}     Lua bindings (config, use_*, begin_pass, end_pass, draw, capture)
├── enums.h           SglBufferType / SglPixelFormat / ... の C-side enum
├── enums_lua.{h,c}   それらを Lua グローバルに登録
├── backend.h         RenderBackend interface (vtable + opaque handles)
├── backend_sokol.c   sokol_gfx (Vulkan) + 直叩き Vulkan 実装 (instance/device/swapchain も保持)
├── backend_sdlgpu.c  SDL3 GPU API (SDL_GPUDevice / SDL_GPUBuffer / ...) 実装
├── pass.{h,c}        現フレームの pass state (backend に begin/end を委譲)
├── resources.{h,c}   key → ResEntry のハッシュマップ (buffer/texture/shader)
├── shader.h, shader.cpp   Slang compile (SPIR-V) + reflection + sdlgpu 用 combined-sampler patcher
├── pipeline.{h,c}    pipeline state hash → backend pipeline cache
├── capture.{h,c}     swapchain texture を PNG として書き出す (backend ごとの read-back)
└── sokol_impl.c      SOKOL_GFX_IMPL の TU (SOKOL_VULKAN backend)
```

```
scripts/
├── run-headless.sh   VK_ICD_FILENAMES=lavapipe + xvfb-run wrapper
└── run-golden.sh     tests/golden/<sample>_<backend>.png と cmp
```

依存:
- `third_party/sokol/sokol_gfx.h` — single-header (vendored)、`SOKOL_VULKAN` backend
- `third_party/slang/` — Slang prebuilt (`include/` vendored、`lib/` と Windows のみ `bin/`
  は CMake configure 時に GitHub release から自動取得; gitignore 対象)、SPIR-V を target
- `third_party/stb/stb_image_write.h` — single-header (vendored)、PNG 出力 (capture)
- `third_party/stb/stb_image.h` — single-header (vendored)、PNG 入力 (`load_png`)
- SDL3 — CMake FetchContent (`SDL_WINDOW_VULKAN` + `SDL_Vulkan_*` API)
- Vulkan loader (`libvulkan.so` / `vulkan-1.dll`) — system 提供
- Lua 5.5 — CMake FetchContent (static lib build)

## Known issues

### sokol backend

- **Depth/stencil format mismatch** (`VUID-vkCmdDraw-dynamicRenderingUnusedAttachments-08914` /
  `08917`): `src/backend_sokol.c` で D24_UNORM_S8_UINT を depth attachment に選んでいるが
  sokol 内部は `SG_PIXELFORMAT_DEPTH_STENCIL` を D32_SFLOAT_S8_UINT に解決する。
  validation 警告は出るが描画自体は通る。fix は backend 側で `D32_SFLOAT_S8_UINT` を
  選ぶか pipeline.c に depth format を渡す方向。
- **Single-pair semaphore reuse** (`VUID-vkAcquireNextImageKHR-semaphore-01779`,
  `VUID-vkQueueSubmit-pSignalSemaphores-00067`): `vk_acquire_sem` / `vk_present_sem` を
  全フレームで再利用しているため、複数フレーム並列対応の前提では推奨されない。
  fix は per-image semaphore array か `VK_KHR_swapchain_maintenance1` の利用。
- **Window resize 未対応**: swapchain recreate (`VK_ERROR_OUT_OF_DATE_KHR`) を
  キャッチしていない。リサイズすると以後のフレームが broken になる可能性。

### sdlgpu backend

- **`VUID-vkCmdCopyImageToBuffer-srcImage-00186`** など SDL_GPU + lavapipe での
  capture 時 validation warning: SDL_GPU swapchain texture に
  `VK_IMAGE_USAGE_TRANSFER_SRC_BIT` が立っていないため。capture 自体は機能して
  PNG は両 backend で byte-identical。SDL3 upstream の対応待ち。
- **Combined image sampler の単一ペア制約**: sokol path は分離 `SAMPLED_IMAGE` +
  `SAMPLER` で受けるが、SDL_GPU は `COMBINED_IMAGE_SAMPLER` を要求するため、
  `src/shader.cpp` に sdlgpu 限定の SPIR-V combined-sampler 合成 patcher が入っている。
  現状は単一の texture+sampler ペアのみサポート (multi-pair fragment shader は
  patcher が bail)。将来 multi-pair / 配列 / `OpImageGather` 等への対応は拡張課題。
- **swapchain texture NULL の頻発 (Windows)**: SDL3 GPU の `SDL_AcquireGPU-`
  `SwapchainTexture` が起動直後の数十フレームに渡って NULL を返すケースがある
  (SDL3 仕様上エラーではない)。指定フレームでの単発 capture が空振りすると困るので、
  `src/capture.c` で次フレームへ最大 120 回まで自動 slip する。

## ライセンス

未定。
