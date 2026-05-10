# sglua: SDL3 GPU backend との両立 設計

**日付:** 2026-05-10
**状態:** ドラフト

## 目的

現行 sglua PoC (sokol_gfx + 直叩き Vulkan) に加え、SDL3 GPU backend
を Lua 側スイッチで選べるようにする。Lua の `config({backend = ...})` を
`on_init` の中で呼ぶことで sokol / sdlgpu のどちらを使うかを決定し、
サンプル 01〜04 と `capture` 機能を両 backend で動かす。

スコープ外:
- ホットリロード、post process、MRT、multi-platform 対応
- フレーム並列度 > 1
- 既存の Vulkan validation 警告 (depth format 等) の解決

## 動機

- SDL3 GPU は SDL3 自体に同梱され、cross-platform (Vulkan / Metal / D3D12)
  を SDL3 のレイヤで吸収できる。将来の macOS / Windows 対応の足掛かりになる
- sokol_gfx を続ける場合と SDL3 GPU に移る場合の両比較が PoC の中で取れる
- 「Lua API を変えずに backend を差し替えられる」ことが、抽象が機能している
  ことの検証になる

## 全体方針

backend 抽象を関数ポインタの vtable で導入する。`RenderBackend` struct
を 1 個定義し、`backend_sokol.c` と `backend_sdlgpu.c` が impl を提供。
`g_backend` 経由で全描画コードがディスパッチする。

Lua からの backend 選択は `config({backend = "sokol"|"sdlgpu"})` のみ。
`on_init` の中で 1 度だけ呼ぶ。default は `sokol`。

## モジュール構成

### 新規ファイル

- `src/backend.h` — `RenderBackend` struct + opaque ハンドル型 + 共通
  desc 構造体 + `extern const RenderBackend *g_backend`
- `src/backend_sokol.c` — 現行コードの GPU 関連部分 (sokol_gfx 呼び出し +
  自前 Vulkan instance/device/swapchain + capture) を移設したもの
- `src/backend_sdlgpu.c` — SDL3 GPU API 版を新規に実装

### 既存ファイル変更

- `src/app.c` — Vulkan instance / device / swapchain 作成は
  `backend_sokol.c` に移動。`app.c` は SDL window 作成、Lua VM ライフサイクル、
  `g_backend` の選択 + 初期化呼び出しまでに縮める
- `src/lua_api.c` — `config(table)` 関数を追加。`config()` を呼べるのは
  `on_init` の中だけ。`pre_frame` 以降に呼ばれたら error
- `src/main.c` — `--capture` フラグなど既存挙動は維持
- `src/pass.c` / `src/pipeline.c` / `src/resources.c` / `src/capture.c` —
  内部実装を backend impl に委譲する。残るのは
  - `pipeline.c`: pipeline state hash → opaque handle の cache (backend
    非依存ロジック)。cache miss 時に `g_backend->make_pipeline()` を呼ぶ
  - `resources.c`: key → ResEntry のハッシュマップは現行のまま。
    `ResEntry.handle` は `uintptr_t` に統一
  - `pass.c`: 現フレームの pass state (target / pass-active フラグ) のみ。
    実装は `g_backend->begin_pass / end_pass` を呼ぶ薄い glue
  - `capture.c`: pending capture path の保持と `g_backend->capture()` 呼び出し
- `src/shader.cpp` — Slang→SPIR-V compile + reflection は backend 非依存。
  reflection の出力は `ShaderReflection` という backend 共通 struct に整える

## RenderBackend interface

```c
// backend.h

typedef uintptr_t BackendBuffer;
typedef uintptr_t BackendImage;
typedef uintptr_t BackendShader;
typedef uintptr_t BackendPipeline;

typedef struct {
    SglPixelFormat fmt;
    int w, h;
    const uint8_t *data;  // null OK (将来 RT 用に空けておく)
} ImageDesc;

typedef struct {
    const uint32_t *vs_spirv;  size_t vs_bytes;
    const uint32_t *fs_spirv;  size_t fs_bytes;
    const ShaderReflection *refl;
} ShaderDesc;

typedef struct {
    BackendShader shader;
    SglBlendMode blend;
    bool depth_test, depth_write;
    SglCullMode cull;
    SglPrimitive primitive;
    SglPixelFormat color_format;
} PipelineDesc;

typedef struct {
    BackendImage target;       // 現状は SENTINEL_MAIN_TEX 固定
    float clear[4];
} PassBeginDesc;

typedef struct {
    BackendBuffer vbuf;
    struct {
        const char *name;      // reflection 名で対応付け
        BackendImage image;
    } textures[8];
    int texture_count;
} BindingsDesc;

typedef struct RenderBackend {
    const char *name;

    bool (*init)(struct App *app);
    void (*shutdown)(struct App *app);

    void (*begin_frame)(struct App *app);
    void (*end_frame)(struct App *app);

    BackendBuffer   (*make_buffer)(SglBufferType type, const float *data, size_t bytes);
    BackendImage    (*make_image)(const ImageDesc *desc);
    BackendShader   (*make_shader)(const ShaderDesc *desc);
    BackendPipeline (*make_pipeline)(const PipelineDesc *desc);

    void (*destroy_buffer)(BackendBuffer);
    void (*destroy_image)(BackendImage);
    void (*destroy_shader)(BackendShader);
    void (*destroy_pipeline)(BackendPipeline);

    void (*begin_pass)(const PassBeginDesc *);
    void (*end_pass)(void);

    void (*apply_pipeline)(BackendPipeline);
    void (*apply_bindings)(const BindingsDesc *);
    void (*apply_uniforms)(const void *data, size_t bytes);
    void (*draw)(int base, int count);

    bool (*capture)(const char *path);  // 次フレーム終了時に書き出し
} RenderBackend;

extern const RenderBackend *g_backend;
```

opaque ハンドルは `uintptr_t` 一律。各 backend が integer ID か pointer
を cast して格納する。`ResEntry` も `uintptr_t handle` 1 個に簡素化。

## 初期化フロー

```
SDL_AppInit
  ├─ SDL_Init(VIDEO)
  ├─ SDL_CreateWindow(SDL_WINDOW_VULKAN)   ← SDL_GPU の Vulkan path とも互換
  ├─ Lua VM 起動 + script ロード
  ├─ Lua の on_init() を呼ぶ                ← この中で config({backend=...})
  ├─ config 結果から g_backend を選択 (default: sokol)
  └─ g_backend->init(app)                   ← Vulkan instance / SDL_CreateGPUDevice 等

SDL_AppIterate
  ├─ g_backend->begin_frame(app)
  ├─ Lua の on_frame() (use_*, begin_pass, draw)
  └─ g_backend->end_frame(app)

SDL_AppQuit
  └─ g_backend->shutdown(app)
```

`config()` の制約:
- `on_init` の中で 1 度だけ有効
- `on_init` を抜けた後は変更不可、エラー
- 引数 backend は `"sokol"` または `"sdlgpu"`、それ以外はエラー
- 呼ばなければ default = sokol

## 機能 → backend マッピング

| 機能 | sokol path | sdlgpu path |
|---|---|---|
| device 初期化 | 自前 vk instance/device + `sg_setup` | `SDL_CreateGPUDevice(SPIRV)` + `SDL_ClaimWindowForGPUDevice` |
| swapchain | 自前 `vkSwapchainKHR`、`vkAcquireNextImageKHR` | `SDL_AcquireGPUSwapchainTexture` |
| begin_frame | image acquire + layout transition | `SDL_AcquireGPUCommandBuffer` |
| begin_pass | `sg_begin_pass(swapchain)` | `SDL_BeginGPURenderPass(color attachment)` |
| make_buffer | `sg_make_buffer(immutable)` | `SDL_CreateGPUBuffer` + transfer buffer で upload |
| make_image | `sg_make_image` + `sg_make_sampler` | `SDL_CreateGPUTexture` + transfer buffer + `SDL_CreateGPUSampler` |
| make_shader | SPIR-V → `sg_make_shader` (reflection 渡し) | SPIR-V → `SDL_CreateGPUShader` x2 |
| make_pipeline | `sg_make_pipeline` (vtx layout + blend + cull) | `SDL_CreateGPUGraphicsPipeline` |
| apply_bindings | `sg_apply_bindings` | `SDL_BindGPUVertexBuffers` + `SDL_BindGPUFragmentSamplers` |
| apply_uniforms | `sg_apply_uniforms(stage, slot, data)` | `SDL_PushGPUVertexUniformData` / `SDL_PushGPUFragmentUniformData` |
| draw | `sg_draw(0, count, 1)` | `SDL_DrawGPUPrimitives(count, 1, 0, 0)` |
| end_pass | `sg_end_pass` | `SDL_EndGPURenderPass` |
| end_frame | `sg_commit` + `vkQueueSubmit/Present` | `SDL_SubmitGPUCommandBuffer` (present は claim 済み) |
| capture | 既存 `vkCmdCopyImageToBuffer` 経路 (backend_sokol.c に移設) | `SDL_DownloadFromGPUTexture` + transfer buffer mapping |

## 段階実装 (各ステップを 1 コミット単位)

1. **抽象抽出 (sokol 側のみ)**: `backend.h` を定義、現行コードの GPU 呼び出し部分を `backend_sokol.c` へ移設。`g_backend = &SOKOL` 固定。`pass.c` / `pipeline.c` / `resources.c` / `capture.c` は薄い glue に縮める。サンプル 01〜04 + capture が現状通り動くことを確認 (リグレッションゼロが目標)
2. **Lua config() API 追加**: `config({backend="sokol"})` を実装。on_init 後に `g_backend->init` を呼ぶフローへ書き換え。default も sokol。サンプル変更なしで通ることを確認
3. **sdlgpu skeleton**: `backend_sdlgpu.c` の init / shutdown / begin_frame / end_frame / begin_pass / end_pass (clear のみ) を実装。`samples/00b_clear.lua` に `config({backend="sdlgpu"})` を仕込んで黒画面 + clear color が出ることを確認
4. **sdlgpu draw**: make_buffer + make_shader + make_pipeline + apply_pipeline + apply_bindings + apply_uniforms + draw を実装し `samples/01_triangle.lua` を sdlgpu で動かす
5. **multi-attribute layout**: sample 02 (vertex color)
6. **texture + sampler**: sample 03 (texture)
7. **uniform block**: sample 04 (mvp)
8. **sdlgpu capture**: `SDL_DownloadFromGPUTexture` 経由で PNG 出力。`--capture` が両 backend で機能する
9. **README 更新**: backend 切替方法、両 backend のサンプル動作確認手順、known limitations を追記

## テスト戦略

- 各ステップ完了時に既存 4 サンプル + capture を sokol / sdlgpu 両方で
  `scripts/run-headless.sh` (lavapipe) 経由で実行
- ステップ 4 以降、サンプル冒頭に
  `config({backend = arg[1] or os.getenv("SGLUA_BACKEND") or "sokol"})`
  形式を仕込み、CLI 引数 / 環境変数で backend を切り替えてテスト可能にする
- capture 出力は `out_sokol.png` / `out_sdlgpu.png` を目視で確認。bit-exact
  比較は本 PoC では要求しない (将来の golden image diff タスクのスコープ)
- 各ステップで「sokol 側がリグレッションしていないこと」を毎回確認することで、
  抽象抽出による副作用を早期に発見する

## オープン論点 / リスク

- SDL3 GPU の SPIR-V descriptor binding 規約と Slang 出力の食い違い
  (現状 sokol 側で set 0 → 1 patching を入れている)。sdlgpu 側でも同種の
  patching が必要か、別の調整で済むかは実装時に確認する
- `SDL_WINDOW_VULKAN` フラグを sdlgpu でも使える前提にしているが、もし
  問題があれば「backend 確定後に window を作り直す」フォールバックを検討
- sokol 側の depth/stencil format 不一致 validation 警告は本タスクでは触らない
  (移設に留める)。sdlgpu 側の depth attachment 設計で同種問題が出たら個別対応

## 完了条件

- `scripts/run-headless.sh ./build/sglua samples/01_triangle.lua` 等で
  `config({backend="sokol"})` / `config({backend="sdlgpu"})` の両方が
  正しく描画される (lavapipe で目視確認)
- `--capture` フラグで両 backend がそれぞれ妥当な PNG を出力する
- 既存サンプルを変更せずに動かしたとき、default の sokol path が
  リグレッションしない
- README に backend 切替方法と既知の制約が反映されている
