# D3D12 Backend

Windows 用の D3D12 backend("d3d12")。`RenderBackend` vtable
(`src/backend.h`) の実装の一つで、抽象は変更していない。明示同期・
descriptor heap・resource state など D3D12 固有の概念はすべて
`src/backend_d3d12.cpp` 内に閉じる。

## 選択と配置

- 実装: `src/backend_d3d12.cpp`(C++。D3D12 は COM のため)
- 選択: `config({ backend = "d3d12" })` / `LUB_BACKEND=d3d12`。
  Windows の既定 backend(未指定時に選ばれる。Windows 以外ではエラー)。
  シンボル(`g_backend_d3d12`)とファイル名は実装 API を表すので d3d12 のまま。
- リンク: `d3d12.lib` `dxgi.lib` `dxguid.lib`(OS 標準)。CMake は `WIN32`
  のみソースを追加。
- runtime: DirectX 12 Agility SDK(NuGet `Microsoft.Direct3D.D3D12`。CMake が
  取得して `third_party/d3d12agility/` に展開)。header はこの package のものを
  Windows SDK より先に見せ、`D3D12_SDK_VERSION` が同梱する `D3D12Core.dll` と
  一致するようにする。exe は `D3D12SDKVersion` / `D3D12SDKPath` を export し、
  post-build で `D3D12/D3D12Core.dll` と debug layer の `d3d12SDKLayers.dll` を
  exe の隣に置く。配布物には `D3D12/D3D12Core.dll` だけを同梱する。exe に
  export の無い host(.NET 実行)は `ID3D12SDKConfiguration1::CreateDeviceFactory`
  で同じ SDK を求め、無ければ inbox の D3D12 に落ちる。対応 OS は
  Windows 10 1909 以降。起動時に `d3d12: runtime:` で載った runtime の path、
  `d3d12: enhanced barriers:` で feature flag を log する。
- WARP: `-DLUB_FETCH_WARP=ON` で NuGet `Microsoft.Direct3D.WARP` の
  `d3d10warp.dll` を exe の隣に置ける(新しい WARP を試すとき用)。CI と
  golden は runner の inbox WARP で、NuGet 版とは出力の LSB が違う。
- HWND は SDL3 window の `SDL_PROP_WINDOW_WIN32_HWND_POINTER`。
- Debug build または `LUB_D3D12_DEBUG=1` で debug layer を有効化。
  validation メッセージは失敗時に `ID3D12InfoQueue` から SDL_Log へ流す。

## Shader 経路: Slang → DXIL

`SHADER_TARGET_D3D12`(`shader.cpp`)が `SLANG_DXIL`(sm_6_0)でコンパイル
する。SPIR-V patching は行わず、reflection の slot がそのまま HLSL register。

- VS+FS は 1 つの slang program にリンクして compile する
  (`compile_d3d12_graphics`)。DXIL はステージ間 varying を「レジスタ位置」で
  一致させる(SPIR-V の location matching と違う)ため、別々に compile
  すると署名がずれる。リンクの副作用として b/t/s/u register は
  program 全体で一意になり、backend はそれを前提にする。
- varying 規約: VS 出力構造体の `SV_Position` は最後に置く
  (`docs/manual/04-gfx.md` 参照)。FS が VS 出力の先頭部分集合だけを
  宣言する lub のイディオムをレジスタ一致と両立させるため。
- DXIL 生成は dxcompiler.dll(Slang が動的ロード)。Slang prebuilt には
  同梱されないため CMake が DXC release から取得して
  `third_party/slang/bin` に置く(ビルド時取得、コミットしない)。
  dxil.dll は不要: DXC 1.8.2502 以降は validator hash がオープン
  ソース化され dxcompiler.dll 単体で署名済み DXIL を出力する。
  ライセンスは University of Illinois/NCSA(LLVM Release License)。
- compile 経路の smoke: `lub_shader_d3d12_smoke`(Windows のみビルド)。

## Frame model

- 単一 direct queue、frames-in-flight = 2。graphics command list は 1 本で、
  begin_frame〜end_frame の間ずっと open。pass も copy も compute も
  この list に記録する。
- per-frame リソース: command allocator / upload arena(uniform・
  buffer/texture 更新の一時メモリ)/ shader-visible CBV_SRV_UAV・sampler
  heap のリング区画 / fence 値。
- `begin_frame`: slot の fence 待ち → 遅延破棄 drain → allocator/list reset。
  `end_frame`: backbuffer を PRESENT へ遷移 → Close → Execute →
  Present(1) → Signal。
- `pending_resize` は begin_frame 冒頭で消費(全 in-flight 完了待ち →
  `ResizeBuffers`)。
- swapchain: DXGI flip-discard、buffer 3 枚、`R8G8B8A8_UNORM`。default depth
  は swapchain サイズの `D24_UNORM_S8_UINT` 1 枚
  (pass.c が swapchain pass の depth_fmt に DEPTH24_STENCIL8 を報告する
  ことと対応)。

## Binding model

- uniforms: draw 毎に upload arena から 256B align で suballocate し
  root CBV(GPU VA 直指定)。SDL_GPU の push uniform と等価。register は
  program 一意なので b 番号だけで root param が決まる。
- root signature: shader ごとに `ShaderReflection` から生成。root CBV ×
  uniform block + SRV table(t0..N)+ sampler table(s0..N)、compute は
  + UAV table(u0..N)。すべて `SHADER_VISIBILITY_ALL`。
- texture/SRV/sampler: `apply_bindings` / `dispatch` 時に per-frame
  shader-visible ring へ descriptor を直接 Create して table をセット。
  未使用 slot は null descriptor / default sampler で埋める。
  StructuredBuffer の stride は reflection の `elem_stride`。
- depth format (D24S8 等) は typeless resource + DSV/SRV format 分離で
  シャドウマップのサンプリングに対応。

## Resource 管理と同期

- buffer / texture は default heap。更新は upload arena に書いて
  `CopyBufferRegion` / `CopyTextureRegion` を frame list に記録する。
  単一 queue の in-order 実行により「copy より前に記録された draw は古い
  内容を読む」= SDL_GPU の cycle 意味論と一致。
- 同期は Enhanced Barriers。buffer は layout を持たないので、copy の前後・
  dispatch の後・pass の終わりに打つ global barrier(`SYNC_ALL` /
  `ACCESS_COMMON`)だけで順序付ける。texture は `DIRECT_QUEUE_COMMON` layout
  に置いたまま sampling・storage(UAV)・copy を行い、attachment として
  bind されている間だけ `RENDER_TARGET` / `DEPTH_STENCIL_WRITE` に移す
  (begin_pass で入り、end_pass で戻る)。swapchain の buffer は pass の外では
  `PRESENT`(capture の copy 元にもなる)。resource ごとの state は持たない。
  default depth は sampling しないので `DEPTH_STENCIL_WRITE` に置いたまま。
- resource の生成は `CreateCommittedResource3`(`ID3D12Device10`)で初期
  layout を与える。`EnhancedBarriersSupported` が無い環境では init に失敗し、
  `backend=sdlgpu` が代替。
- destroy は fence 値付きの遅延解放リストに積み、begin_frame で回収
  (GPU が最大 2 frame 参照し続けるため)。
- readback: 同期(SDL_GPU backend と同じ意味論)。frame list を
  flush → wait → readback heap から copy、list を開き直して frame 続行。
- capture: present 前に backbuffer を readback buffer へ copy して
  flush + wait(`capture_before_end_frame = true`)。end_frame は
  Present + Signal のみ行う。

## 制約 / 未対応

- graphics stage の storage buffer バインドは未対応(SDL_GPU backend と
  同等。compute 経由でのみ使用)。

## Golden test

`scripts/run-golden.sh` は Windows (git bash) では d3d12 backend を
WARP(`LUB_D3D12_WARP=1`、Microsoft のソフトウェアラスタライザ)で回し、
`tests/golden/<name>_d3d12.png` と byte 比較する。lavapipe と同じく
機材・ドライバ非依存の CPU rasterizer なので `cmp -s` の完全一致が成立する。
実 GPU での動作確認は別途 capture 目視で行う。
