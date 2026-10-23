# Native Backend Design — DX12 + WebGPU 直接実装

> 記録: 2026-06 時点の設計。WebGPU backend は実装済み(`src/backend_webgpu.c`、web の default)。
> DX12 backend は未着手。現状の backend 構成は README の「Backend 切替」を参照。

## 動機

プラットフォーム API を直接叩くバックエンドを追加する。既存の sokol / SDL-GPU バックエンドは残し、並行稼働させる。

新バックエンドを足す理由:

1. DX12 / HLSL、WebGPU / WGSL をそれぞれ直接ターゲットすれば、Slang → ネイティブ言語の 1 段で済む。SPIR-V パッチや sokol バインディング記述の変換レイヤーが要らない
2. lub のバインディングモデル (UB 2, tex 8, storage buf 4, storage tex 4) は固定で小さく、汎用抽象層を経由する意味が薄い
3. Win desktop (DX12) と Web (WebGPU) という priority platform にそれぞれネイティブで最短距離のパスを持てる

既存バックエンドを残す理由:

- sokol (Vulkan) は Linux で動く唯一のパス
- SDL-GPU は安定しており、移行中のリグレッション検証に使える
- 新バックエンドが全サンプルで安定するまで、フォールバックとして必要

## ターゲット

| バックエンド | プラットフォーム | GPU API | シェーダー言語 |
|---|---|---|---|
| `backend_dx12.c` | Windows desktop | D3D12 直接 | Slang → HLSL (DXIL) |
| `backend_webgpu.c` | ブラウザ | webgpu.h (ブラウザ内蔵) | Slang → WGSL |

外部 GPU ライブラリへの依存なし。dawn も wgpu-native も使わない。
SDL はウィンドウ / 入力 / オーディオ用に残す。Slang はシェーダーコンパイラとして残す。

将来 Linux が必要になれば `backend_vulkan.c` を足す。今は不要。

## backend.h は変えない

既存の `RenderBackend` vtable (14 関数 + readback 3 + capture 1) はそのまま使う。
lua_api.c / pass.c / pipeline.c / resources.c は変更なし。

```
lua_api.c → g_backend->xxx() → backend_dx12.c   (Windows)
                               → backend_webgpu.c (Web)
```

## DX12 バックエンド

### 初期化 (`init`)

```
SDL_Window → HWND (SDL_GetPointerProperty)
HWND → IDXGIFactory4 → IDXGIAdapter → ID3D12Device
ID3D12CommandQueue (DIRECT)
IDXGISwapChain3 (2 buffers, FLIP_DISCARD)
descriptor heap: CBV/SRV/UAV (GPU-visible, ring)
descriptor heap: Sampler (GPU-visible)
descriptor heap: RTV (CPU-only)
descriptor heap: DSV (CPU-only)
ID3D12Fence + event (per-frame sync)
ID3D12CommandAllocator × 2 (double buffer)
upload heap: ID3D12Resource (UPLOAD, ring buffer)
```

### リソース

| lub 概念 | DX12 マッピング |
|---|---|
| `BackendBuffer` (vertex/index) | `ID3D12Resource` DEFAULT heap + staging upload |
| `BackendBuffer` (storage) | `ID3D12Resource` DEFAULT heap, UAV descriptor |
| `BackendImage` | `ID3D12Resource` DEFAULT heap, SRV + optional RTV/DSV/UAV descriptor |
| `BackendShader` | DXIL bytecode blob (`IDxcBlob`) + root signature |
| `BackendPipeline` | `ID3D12PipelineState` (graphics or compute) |

### Root Signature 設計

lub のバインディングモデルは固定で小さい (UB 最大 2, テクスチャ最大 8, storage buf 最大 4, storage tex 最大 4)。
シェーダーごとに root signature を動的生成せず、静的な root signature を 2 つ (graphics / compute) 用意する。

```
Graphics root signature:
  [0] Root CBV  — b0 (uniform block 0, VS or FS)
  [1] Root CBV  — b1 (uniform block 1, VS or FS)
  [2] Descriptor table — t0..t7  (SRV: textures)
  [3] Descriptor table — s0..s7  (Sampler)

Compute root signature:
  [0] Root CBV  — b0
  [1] Root CBV  — b1
  [2] Descriptor table — t0..t7  (SRV: textures)
  [3] Descriptor table — s0..s7  (Sampler)
  [4] Descriptor table — u0..u3  (UAV: storage buffers)
  [5] Descriptor table — u4..u7  (UAV: storage textures)
```

Root CBV はヒープ不要で apply_uniforms が SetGraphicsRootConstantBufferView 1 呼び出しで済む。

### シェーダーコンパイル

```
Slang → HLSL source → IDxcCompiler3 (DXC) → DXIL bytecode
```

shader.cpp の変更:
- `SHADER_TARGET_DX12` を追加
- `configure_spirv_target()` の代わりに `configure_hlsl_target()` — Slang の `SLANG_HLSL` ターゲット
- SPIR-V パッチ関数群 (`patch_spirv_descriptor_sets`, `patch_spirv_bindings_from_reflection`, `patch_spirv_storage_image_formats`) は DX12 パスでは不要。HLSL の `register()` が直接正しいスロットを指す
- ShaderBlob にはコンパイル済み DXIL bytecode を格納。フィールド名 `spirv` は `bytecode` にリネーム

リフレクション:
- Slang 側のリフレクションは現行と同じ `ShaderReflection` 構造体に詰める (変更なし)
- DXC の `ID3D12ShaderReflection` は使わない。Slang のリフレクションで十分

### 描画パス

```
begin_pass:
  swapchain → ResourceBarrier(PRESENT → RENDER_TARGET)
  OMSetRenderTargets (RTV + DSV)
  ClearRenderTargetView / ClearDepthStencilView
  RSSetViewports / RSSetScissorRects

apply_pipeline:
  SetPipelineState
  SetGraphicsRootSignature
  IASetPrimitiveTopology

apply_bindings:
  IASetVertexBuffers (slot 0 + optional slot 1 for instancing)
  IASetIndexBuffer (optional)
  descriptor heap に SRV/Sampler をコピー → SetDescriptorHeaps + SetGraphicsRootDescriptorTable

apply_uniforms:
  upload ring に memcpy → SetGraphicsRootConstantBufferView

draw:
  DrawInstanced or DrawIndexedInstanced

end_pass:
  swapchain → ResourceBarrier(RENDER_TARGET → PRESENT)

end_frame:
  Close → ExecuteCommandLists → Present → Signal fence
```

### Readback

```
request_readback:
  ID3D12Resource (READBACK heap) を作成
  CopyTextureRegion (GPU tex → readback buffer)
  fence value を記録

poll_readback:
  fence.GetCompletedValue >= 記録した value なら READY
  Map → memcpy → Unmap → RGBA8 変換
```

### Capture

`request_readback` と同じパス。readback 完了後 stb_image_write で PNG。

## WebGPU バックエンド

既存の `backend_sokol.c` の `#ifdef __EMSCRIPTEN__` パス (~400 行) をベースにする。
sokol の `sg_*` 呼び出しを `wgpu*` 呼び出しに置き換える。

### 変更のスコープ

sokol 経由で間接的にやっていたことを直接やる:

| 操作 | sokol 経由 (現状) | webgpu.h 直接 |
|---|---|---|
| shader 生成 | 130 行の sg_shader_desc 組立 | `wgpuDeviceCreateShaderModule` (WGSL source) |
| pipeline 生成 | sg_pipeline_desc 組立 | `wgpuDeviceCreateRenderPipeline` |
| buffer 生成 | sg_make_buffer | `wgpuDeviceCreateBuffer` |
| image 生成 | sg_make_image + sg_make_sampler + sg_make_view | `wgpuDeviceCreateTexture` + `wgpuDeviceCreateSampler` |
| draw | sg_apply_* + sg_draw | `wgpuRenderPassEncoder*` |
| compute | sg_begin_pass(compute) + sg_dispatch | `wgpuComputePassEncoder*` |

bind group layout は Slang リフレクションから直接構築。WGSL の `@group/@binding` が正しいスロットを指すので SPIR-V パッチ不要。

### シェーダーコンパイル

```
Slang (slang-wasm, 既存の EM_ASYNC_JS ブリッジ) → WGSL source
```

現行と同じ。変更なし。

## shader.cpp の変更

| 追加 | 内容 |
|---|---|
| `SHADER_TARGET_DX12` | Slang → HLSL → DXIL パス追加 |
| `SHADER_TARGET_WEBGPU` | 既存の WGSL パス (EM_ASYNC_JS) をターゲットとして独立 |
| DX12 用 prelude | separate texture/sampler 形式 (sokol prelude と同等) |

既存の SPIR-V パッチコード (`patch_spirv_*`) は sokol / SDL-GPU バックエンドが使い続けるので残す。
DX12 / WebGPU パスではこれらを通らない。

## 増えるもの

| ファイル | 推定行数 |
|---|---|
| `backend_dx12.c` | ~1200 |
| `backend_webgpu.c` | ~800 |
| `app.h` の DX12 フィールド群 | ~20 |
| shader.cpp の HLSL ターゲット追加 | ~100 |

推定合計増加: ~2100 行

## 進め方

1. shader.cpp に `SHADER_TARGET_DX12` (Slang → HLSL → DXIL) パスを追加
2. `backend_dx12.c` を新規作成。`backend_name = "dx12"` で選択可能にする。Windows で 16 サンプル golden test パス
3. `backend_webgpu.c` を sokol の EMSCRIPTEN パスから抽出。sokol の `sg_*` を `wgpu*` 直接呼び出しに置換。Web playground で 16 サンプル動作確認

既存バックエンド (sokol, sdlgpu) には触れない。4 バックエンド並行の状態にする。
旧バックエンドの削除は、新バックエンドが安定した後に別途判断する。
