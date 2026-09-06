# NoGraphicsAPI と lub の gfx 層の比較

> 記録: 2026-09-06 時点の調査と試作。Sebastian Aaltonen の blog「No Graphics API」と
> その Vulkan 実装 [NoGraphicsAPI](https://github.com/sebbbi/NoGraphicsAPI) を
> lub の gfx 層と突き合わせ、思想として取り入れる点と踏んでいる anti-pattern を洗い、
> 各項目の実現可能性と効果を測った。現状の backend 構成は `docs/design.md` と
> `docs/d3d12-backend.md` を見る。

## 対象

- blog: [No Graphics API](https://www.sebastianaaltonen.com/blog/no-graphics-api)
- 実装: NoGraphicsAPI(Vulkan 1.4 + `VK_EXT_descriptor_heap` 等 4 拡張、C++20、
  header 768 行 + 実装 4601 行、Win32 present のみ)
- lub: `cs-lib/lub_stub.cs` の `Gfx` 面、`src/backend.h` の `RenderBackend`、
  `src/backend_vulkan.c` / `backend_d3d12.cpp` / `backend_sdlgpu.c` / `backend_webgpu.c`

比べる高さに注意する。NoGraphicsAPI は lub の `RenderBackend` と同じかそれより下の
層で、lub の `Gfx` 面(key + version の宣言、名前による束縛、自動 sweep)とは
比較対象にならない。両者は同じ問題(descriptor set / layout / binding の煩雑さ)を
逆向きに解いている。NoGraphicsAPI は所有権をアプリに寄せて API からオブジェクトを消し、
lub は所有権を runtime に寄せてユーザーから判断を消す。

## blog の主張と lub の現状

| blog の主張 | lub の現状 | 評価 |
|---|---|---|
| 頂点入力レイアウトは legacy。shader が pointer / buffer から自分で読む | reflection から attribute layout を導き、`instances` 予約名と頂点 buffer 2 本の上限を持つ。graphics stage で storage buffer を読めない | 取り入れる余地あり(項目 5) |
| PSO は shader + attachment format だけ。blend / depth は分離 | `PipelineKey` に blend / depth / cull / primitive / `depth_tex_mask` / `is_indexed` を焼く | 測ると permutation はほぼ起きていない(項目 1, 3) |
| 同期は resource list ではなく hazard。layout 遷移は不要 | vulkan は global memory barrier だが image layout を 8 種類 per-image で追跡し、bind 時に遷移して pass を suspend する。d3d12 は legacy の per-resource state 追跡 | vulkan は統一できる(項目 2)。d3d12 は Agility SDK 依存(項目 4) |
| sampler object は要らない | texture に filter / wrap を焼く | 一致 |
| command buffer は transient、完了は timeline | 毎フレーム記録し直し、timeline semaphore と fence 値付きの遅延破棄 | 一致 |
| root は小さな struct を push | d3d12 は root CBV、他は uniform block を名前で pack | Lua 面では struct 共有が不可能なので意図的な乖離 |
| binding API は消えるべき | draw ごとに名前照合で descriptor set を確保 | hot reload の核なので意図的。CPU コストは lub の規模では出ない |

## 検証した 5 項目

### 1. `is_indexed` を pipeline key から外す

- 事実: `PipelineDesc.is_indexed` はどの backend も読んでいなかった。
  webgpu の strip topology は `stripIndexFormat` を Undefined にしていて、
  indexed の strip draw は validation に落ちる状態だった。
- 変更: key と desc から外し、webgpu は strip なら `Uint32` 固定
  (lub の index は常に u32)。ブランチ `agent/gfx-pipeline-key`。
- 効果: 0。golden 20 サンプル + 3 テストの trace で、同じ shader を indexed と
  非 indexed の両方で使う draw は無かった(下の表)。master と変更後で
  `LUB_GPU_STATS` の `pipelines_created` は全 entry で同じ。
- 検証: lavapipe golden 46 / 46 PASS(vulkan + sdlgpu)、web golden 20 / 20 PASS
  (swiftshader、strip の `stripIndexFormat` 変更を含む)。

### 2. vulkan: image layout を GENERAL に統一

- 事実: `vkb_transition` の呼び出しが 6 箇所、layout 定数が 8 種類。texture を
  bind するたびに shader-read へ遷移し、その際 render pass を suspend / resume
  していた。upload / readback / dispatch も前後で遷移。
- 変更: image は生成直後に一度だけ GENERAL へ移し、以後は attachment / sampling /
  storage / copy すべて GENERAL。遷移の代わりに pass 終了時・copy 前後・
  dispatch 前後の global memory barrier で hazard を順序付ける。swapchain
  image だけ present 用の遷移を残す。`VkbImage.layout` と `g.depth_layout`
  を削除。差分は +51 / -104 行。ブランチ `agent/gfx-vulkan-general-layout`。
- 効果: bind が pass を中断しなくなり、layout の状態機械が消える。描画結果は
  同一で、性能は lub の non-goal なので測っていない。
- 検証: lavapipe golden vulkan 23 / 23 PASS。`LUB_VK_DEBUG=1` の validation は
  変更前後で同じ(26_renderer3d と test_depth_sample にある 10 件は depth-only
  pass で fragment が color を書くという既存の警告で、layout とは無関係)。
- 実機: 手元の radv も `VK_KHR_unified_image_layouts` を持つが未実行。
  GENERAL 統一は Vulkan 1.3 core の範囲で合法なので、拡張は最適化にしか関係しない。

### 3. pipeline key の粒度を backend 別にする

- 想定: vulkan 1.3 core の dynamic state(cull、depth test / write / compare、
  topology は core で必須。`VkPhysicalDeviceVulkan13Features` に feature bit が
  無いことで確認)で key から外し、`depth_tex_mask` は webgpu だけの key にする。
- 測定: 全 entry の draw を trace し、key の項目を落としたときの unique 数を数えた。

| entry | shader 数 | 現状 | cull/depth/prim を外す | depth_tex_mask も外す | blend も外す |
|---|---|---|---|---|---|
| 12_sfb | 16 | 16 | 16 | 16 | 16 |
| 18_coin_pusher | 8 | 10 | 10 | 10 | 8 |
| 19_sdf | 8 | 9 | 9 | 9 | 8 |
| 26_renderer3d | 10 | 12 | 12 | 12 | 10 |
| 他 19 entry | 19 | 19 | 19 | 19 | 19 |
| 合計 | 61 | 66 | 66 | 66 | 61 |

- 結論: lub の内容では permutation は blend 由来の 5 本しか無く、それを消すには
  `VK_EXT_extended_dynamic_state3`(任意拡張)が要る。webgpu と d3d12 は blend を
  PSO に焼くしかない。効果が無いので見送り。

### 4. d3d12 を Enhanced Barriers にする

- 実現可能性: inbox の D3D12 では `EnhancedBarriersSupported` は常に FALSE で、
  Agility SDK 1.7 以降が必須([D3D12_FEATURE_DATA_D3D12_OPTIONS12](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_feature_data_d3d12_options12)、
  [Windows 11 22H2 の SDK 記事](https://walbourn.github.io/windows-sdk-for-windows-11,-version-22h2/))。
  retail で最初に入ったのは [Agility SDK 1.608.0](https://devblogs.microsoft.com/directx/agility-sdk-1-608-0/)。
  対応 OS は Windows 10 1909 以降で、`D3D12SDKVersion` / `D3D12SDKPath` を
  export し `D3D12Core.dll` を同梱して出荷する
  ([Getting started](https://devblogs.microsoft.com/directx/gettingstarted-dx12agility/))。
  CI の WARP(`windows-latest` = Windows Server 2025)は NuGet の
  `Microsoft.Direct3D.WARP` の `d3d10warp.dll` を exe の隣に置く形になる。
- 結論: 技術的には可能だが、build 時の NuGet 取得と出荷物への DLL 同梱が増える。
  `docs/d3d12-backend.md` が「対応 GPU の幅優先」で見送った判断は今も妥当で、
  Phase 2 の出荷パイプラインと一緒に決める。Linux からは検証できない。
- その後: 配布方法(app-local の `D3D12/D3D12Core.dll`)を確認した上で進めた。
  Agility SDK の同梱は `agent/d3d12-agility-sdk`、Enhanced Barriers への
  書き換えは `agent/d3d12-enhanced-barriers`。CI の windows job で、Agility
  runtime + runner の inbox WARP で `EnhancedBarriersSupported` が TRUE に
  なり、書き換え後も WARP golden 23 件が一致した。NuGet 版 WARP は出力の LSB
  が inbox 版と違い golden と一致しないので CI では使わない。.NET 実行の host
  (export を持てない apphost)は `ID3D12SDKConfiguration1::CreateDeviceFactory`
  に lub.dll 隣の絶対 path を渡す形で同じ runtime に載る。

### 5. vertex pulling(graphics stage の storage buffer 束縛)

- 事実: reflection は vertex stage の `StructuredBuffer` を拾い、vulkan / sdlgpu /
  d3d12 の layout 構築もそれを数えているが、`BindingsDesc` に storage buffer が
  無く、draw では束縛されなかった。
- 試作: `BindingsDesc` に読み取り専用 storage buffer を足し、shader が
  `StructuredBuffer` として宣言した名前の STORAGE buffer はそこへ束縛する。
  backend 4 本の `apply_bindings` に対応を足した。ブランチ
  `agent/gfx-vertex-pull-proto`。d3d12 は手元で走らせられないので PR の
  windows job(WARP golden)で確認する。
- 検証: `tests/lua/test_vertex_pull` は頂点 buffer と入力レイアウトを使わずに
  `test_indexed_draw` と byte 一致の絵を出す(lavapipe、vulkan と sdlgpu)。
  validation は vulkan で 0 件、sdlgpu は master にもある capture 経路の 2 件のみ。
  既存の golden も 46 / 46 PASS で退行なし。
- 落とし穴: Slang は SPIR-V の `SV_VertexID` を `VertexIndex - BaseVertex` に
  落とし `DrawParameters` capability を要求する
  ([Slang の SPIR-V 固有事項](https://docs.shader-slang.org/en/stable/external/slang/docs/user-guide/a2-01-spirv-target-specific.html))。
  lub の SPIR-V target は `spirv_1_0` で OpExtension が要り、SDL_GPU は
  `shaderDrawParameters` を有効にしない(`pEnabledFeatures` に 1.0 の features しか
  渡さない)。lub は常に base 0 で draw するので `SV_VulkanVertexID` で足りる。
  採用時は shader.cpp が target ごとに vertex id の semantic を与える形にする。
- web: WebGPU core は vertex stage の read-only storage buffer を
  `maxStorageBuffersInVertexStage` 既定 8 まで許す(compat mode は 0。lub は
  core 前提)。wasm では未実行。
- 効果と費用: attribute reflection、backend 4 本の vertex input state、
  `instances` 予約名、頂点 buffer 2 本上限が不要になる。一方で頂点 attribute を
  使う shader は約 98 ファイル(12_sfb 26、14_sponza 22 など)と .cs / .lua 内の
  6 本が書き換え対象で、authoring の規約が変わる。

## 判断

| 項目 | 実現可能性 | 効果 | 提案 |
|---|---|---|---|
| 1 is_indexed | 済 | 0(dead field の除去) | PR にする |
| 2 GENERAL 統一 | 試作で確認 | layout 状態機械と pass 中断が消える | PR にする |
| 3 key の粒度 | 可能 | 66 本中 5 本、要任意拡張 | 見送り |
| 4 Enhanced Barriers | Agility SDK 必須。CI で確認 | resource ごとの state 追跡が消える。出荷物に DLL が一つ増える | PR にした |
| 5 vertex pulling | 試作で確認(vulkan / sdlgpu。d3d12 は CI) | 入力レイアウト機構が消える。移行 100 ファイル | 設計判断。採用なら shader 規約を先に決める |

## 再現

- trace: `LUB_PIPE_TRACE`(一時 patch、commit していない)で draw ごとに key を
  出し、entry ごとに unique 数を数えた。`LUB_GPU_STATS=1` の `pipelines_created`
  と一致することを確認。
- golden: `BINARY=./build-release-linux/lub scripts/run-golden.sh [--backend vulkan]`
- validation: `LUB_VK_DEBUG=1 LUB_BACKEND=vulkan scripts/run-headless.sh ...`
