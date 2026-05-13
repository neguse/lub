# tasks

未実装と Known issues を実作業単位に分けたもの。タグは規模感:
**[S]** 数十行 / **[M]** 100〜200 行 / **[L]** 設計 + 大幅実装 / **[upstream]** 外部依存。

## 安定化

### sdlgpu backend

- **[L] Combined image sampler の multi-pair 対応**: `shader.cpp` の patcher を SPIR-V を歩いて各 `(SampledImage, Sampler)` ペアごとに `OpSampledImage` を組み直す形に拡張。テクスチャ複数枚を要求する Sample で blocker。SPIRV-Tools 依存導入の判断 (依存させない場合は 300+ 行)。
- **[upstream] swapchain texture に `TRANSFER_SRC_BIT` 未付与**: `VUID-vkCmdCopyImageToBuffer-srcImage-00186`。SDL3 upstream で usage flag を追加してもらうのが本筋。回避は offscreen color target 経由の二段 capture (副作用大、推奨しない)。
- **(削除候補) Windows での swapchain texture NULL**: `src/capture.c` の retry slip で実害消えている。Known issue から落とすか「workaround あり」と注記するかの整理。

### sokol-wgpu backend (web only)

- **[L] swapchain dimensions: devicePixelRatio 不整合**: sample 06 で MRT pass を経由した後の swapchain pass で depth attachment と color attachment のサイズが食い違って Chromium WebGPU validation が draw を弾く。Canvas は 480x360 で確保しているが Chromium は devicePixelRatio スケール後の物理サイズで surface を構成するため、`sg_pass.swapchain.{width,height}` と実 surface との整合が必要。`web/playground/player.ts` の canvas init で `devicePixelRatio` を反映するか、`backend_sokol.c::sk_begin_frame` で wgpuSurfaceGetCurrentTexture が返す実テクスチャ寸法を `app->last_w/h` に書き戻す。

## 大きい話 (要設計)

- **[L] macOS 対応**: sdlgpu 経路で `SDL_GPU_SHADERFORMAT_MSL` + Slang の MSL target を試すのが現実的。`shader.cpp` で MSL を出力する分岐、`backend_sdlgpu.c` の shader create を MSL バイナリパスに対応。実機 / CI なしでの検証手段が本質的な障壁。

## やらないこと

- **VR / OpenXR**: SDL3 の OpenXR サポートが実験的、sokol_gfx 側に multiview / VR 専用 API もなく、規模が PoC を逸脱する。
- **マルチスレッド描画**: per-thread command buffer / resource ownership 分割 / pipeline cache の lock 化など、現状の single command queue 前提の全面見直しが必要で、PoC スコープから外れる。
