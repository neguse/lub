# Backend Consolidation — sokol 削除の段階計画

> 記録: 2026-07-07 時点の決定。前提は
> `docs/log/2026-06-22-native-backend-design.md`(DX12/WebGPU 直接実装の設計)。
> そこに書いた「旧バックエンドの削除は、新バックエンドが安定した後に別途判断」の判断がこれ。

## 決定

backend を platform ごとの直接実装 + 横断チェッカーの 3 本に整理し、sokol_gfx を削除する。

backend 名の意味論: **default は全プラットフォームで `native`** =
「このプラットフォームの最短距離実装」。

| platform | `native` の解決先 | 明示選択できる代替 |
|---|---|---|
| Windows | D3D12 直接 (`backend_dx12.cpp`) | `sdlgpu` |
| Linux | 当面 `sdlgpu` が代行 (直接実装 `backend_vulkan.c` ができたら差し替え) | `sdlgpu` |
| web | `webgpu` (指定は無視、常にこれ) | — |

`sokol` 指定は native ではエラー (黙って読み替えない)。

## 根拠

- sokol を残した理由は「Linux で動く唯一のパス」だったが、sdlgpu が Linux golden を
  全サンプル並走で通しており、既に唯一ではない。
- DX12 backend が Windows CI golden (WARP) の担い手になり、安定条件を満たした。
- sokol 固有の負債: readback が sokol private API (`_sg_lookup_image`) に依存、
  Vulkan backend の validation warning、native + web 2-in-1 で `backend_sokol.c` が
  2738 行、`third_party/sokol` 1.2MB の追従。
- web の sokol/WGPU パスは verify-headless の対象外で、実質未検証のフォールバック。

## WebGL fallback は non-goal

「特定 Android の Vulkan driver が悪く Unity ですら GL に fallback する」問題は実在する。
lub への影響は「Android Chrome は WebGPU を Vulkan 上に実装しており、driver が
ブロックリストに載った端末では WebGPU ごと無効になる」形で web にだけ現れる。

ただし WebGL 対応は sokol 温存では手に入らない:

- web build の sokol は `SOKOL_WGPU` であり、GLES3/WebGL ビルドはこのリポに存在したことがない。
- core API は compute dispatch / storage buffer / storage texture を一級で持ち
  (`backend.h`)、WebGL2 (compute なし、SSBO なし) には収まらない。
- 対応するなら「compute を落とした core API サブセット + Slang→GLSL ES ターゲット +
  新 backend」という独立案件で、sokol の有無と無関係。

よって WebGL fallback は non-goal とし (design.md に記載)、需要が出た時に
サブセット案件として再検討する。

## 実施内容 (当初は段階案だったが一括実行した)

- `backend_sokol.c` / `sokol_impl.c` / `sokol_private.h` / `third_party/sokol` /
  golden の `*_sokol.png` を削除。wasm は常に webgpu backend。
- `app.h` の `vk_*` フィールド、CMake の `find_package(Vulkan)` / `Vulkan::Vulkan` /
  `SOKOL_VULKAN` を削除。`g_backend` の定義は `app.c` へ移動。
- shader target: `SHADER_TARGET_SOKOL` → `SHADER_TARGET_WGSL` に改名
  (wasm の WGSL 経路として webgpu backend が使用)。native の sokol 用
  SPIR-V patch 分岐 (descriptor-set layout / reflection remap) を削除し、
  SPIR-V patch は sdlgpu 専用化。dead 化した `patch_spirv_descriptor_sets` も削除。
- default backend 名を `native` に (env LUB_BACKEND / `lubx.Boot` fallback /
  `Lub.config` doc / samples / tests の順で追従)。
- golden の Linux backend セットは `(sdlgpu)`、Windows は従来どおり `(native)`。
  Windows CI の Vulkan SDK セットアップを削除 (リンク依存が消えたため)。
- 将来・需要が出たら `backend_vulkan.c` を Linux 直接実装として追加し、
  Linux の `native` 解決先を差し替える。

## 注意点

- Windows の sdlgpu (SDL_GPU D3D12 driver) は Linux 機からは未検証。
  Windows 機で一度 `LUB_BACKEND=sdlgpu` の golden を通して確認する。
- wasm の reflection remap (`wasm_remap_stage_for_wgsl`) の bind 規約は
  sokol-gfx の WGPU backend 由来。`web/playground/slang-bridge.ts` と
  `backend_webgpu.c` が同じ規約を鏡写しにしている。
