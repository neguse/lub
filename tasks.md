# tasks

未実装と Known issues を実作業単位に分けたもの。タグは規模感:
**[S]** 数十行 / **[M]** 100〜200 行 / **[L]** 設計 + 大幅実装 / **[upstream]** 外部依存。

## テスト / CI

- **[S] Golden image diff (lavapipe 限定)**: `tests/golden/<sample>_<backend>.png` を置き、`scripts/run-golden.sh` で `--capture` 後に `cmp` で完全一致を判定。lavapipe + xvfb 環境で確定的。実 GPU 用には tolerance 比較 (PSNR / max diff) を別途、後回しで良い。

## サンプル / API 拡張

- **[S] Lua 側 sampler 設定**: `use_texture(key, w, h, fmt, data, version, {filter='nearest'|'linear', wrap='repeat'|'clamp'})` を追加。`enums_lua.c` に `NEAREST` / `CLAMP` を登録、`backend_sokol` / `backend_sdlgpu` の sampler 生成箇所で desc に流す。
- **[M] Sample 5: post process (offscreen render target)**: `use_texture(key, w, h, fmt, nil, version, {target=true})` で render-target texture を宣言。`begin_pass({target = texRef})` で main_tex 以外も受ける。pipeline cache のキーに color format を含める。
- **[M] Sample 6: deferred shading (MRT)**: `begin_pass({targets = {t1, t2, ...}, clear_colors = {...}})` 形式に拡張、pipeline.c の color attachment 配列化、両 backend の pass begin を多色 attachment 対応に。Sample 5 と同時にやるのが筋。
- **[M] compute shader**: `dispatch(x, y, z, resources, {shader})` を `lua_api.c` に追加、`use_buffer` に `STORAGE` type 追加、`shader.cpp` で `[shader("compute")]` entry point を拾う。両 backend に `dispatch` vtable 追加。

## リソース管理

- **[S] フレーム未参照リソースの sweep**: `res_table_touch` / `e->last_seen_frame` のインフラは既にある (`resources.c:74`)。`app_frame_end` で `current_frame - last_seen_frame > N` のエントリを `res_entry_release` に流す関数を追加するだけ。pipeline cache も同方式。閾値 N は config 化推奨。

## 安定化

### sokol backend

- **[S] Depth/stencil format mismatch**: `backend_sokol.c:190` の第一候補を `VK_FORMAT_D32_SFLOAT_S8_UINT` に統一 (sokol 内部の `SG_PIXELFORMAT_DEPTH_STENCIL` 解決に合わせる)。`VUID-vkCmdDraw-...-08914 / 08917` が消える。
- **[M] Semaphore array 化**: `vk_acquire_sem` / `vk_present_sem` を swapchain image 数分確保し `frame_index % N` で回す。`VUID-vkAcquireNextImageKHR-semaphore-01779`, `vkQueueSubmit-pSignalSemaphores-00067` 解消。`VK_KHR_swapchain_maintenance1` 採用なら更にクリーン (任意)。
- **[M] Window resize (swapchain recreate)**: `vkAcquireNextImageKHR` / `vkQueuePresentKHR` の `VK_ERROR_OUT_OF_DATE_KHR` / `VK_SUBOPTIMAL_KHR` を捕捉、swapchain / depth / image views を作り直す関数に分離。`SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED` をトリガにする。

### sdlgpu backend

- **[S] SPIR-V version を 1.0 に下げる試行**: `shader.cpp:52` の `findProfile("spirv_1_5")` を `"spirv_1_0"` にして 4 sample が通るか確認 (現状未使用機能なら通るはず)。NG なら SPIR-V 生成後に header (`words[1]`) を `0x00010000` にパッチする手も可。`VUID-VkShaderModuleCreateInfo-pCode-08737` 解消。
- **[L] Combined image sampler の multi-pair 対応**: `shader.cpp` の patcher を SPIR-V を歩いて各 `(SampledImage, Sampler)` ペアごとに `OpSampledImage` を組み直す形に拡張。テクスチャ複数枚を要求する Sample で blocker。SPIRV-Tools 依存導入の判断 (依存させない場合は 300+ 行)。
- **[upstream] swapchain texture に `TRANSFER_SRC_BIT` 未付与**: `VUID-vkCmdCopyImageToBuffer-srcImage-00186`。SDL3 upstream で usage flag を追加してもらうのが本筋。回避は offscreen color target 経由の二段 capture (副作用大、推奨しない)。
- **(削除候補) Windows での swapchain texture NULL**: `src/capture.c` の retry slip で実害消えている。Known issue から落とすか「workaround あり」と注記するかの整理。

## 大きい話 (要設計)

- **[L] macOS 対応**: sdlgpu 経路で `SDL_GPU_SHADERFORMAT_MSL` + Slang の MSL target を試すのが現実的。`shader.cpp` で MSL を出力する分岐、`backend_sdlgpu.c` の shader create を MSL バイナリパスに対応。実機 / CI なしでの検証手段が本質的な障壁。

## やらないこと

- **VR / OpenXR**: SDL3 の OpenXR サポートが実験的、sokol_gfx 側に multiview / VR 専用 API もなく、規模が PoC を逸脱する。
- **マルチスレッド描画**: per-thread command buffer / resource ownership 分割 / pipeline cache の lock 化など、現状の single command queue 前提の全面見直しが必要で、PoC スコープから外れる。
