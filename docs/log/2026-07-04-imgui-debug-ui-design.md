# Dear ImGui debug UI — backend 抽象の一利用者として統合する

> 記録: 2026-07-04 時点の設計。現状は `src/ui.cpp` / `haxe-lib/lub/lub/Ui.hx` /
> `samples/19_sdf/` を見る。

## 目的と位置づけ

ゲーム実行中のチューニング・デバッグ用 UI。SDF ツリーのパラメータを
スライダーで叩いて即 remesh する(design doc『SDF ツリー設計』のエディタ
第一形態)、gameplay 定数の実行時調整、profiler 可視化などが用途。

**debug/dev UI 専用**。ゲーム本編の UI は従来通り自前描画(SpriteBatch の
領分)。「マテリアルアセット」を作らなかったのと同じ理由で、ImGui を
ゲーム UI の基盤にはしない。

## 統合方式: backend ごとの公式 impl を使わない

ImGui の公式 backend(sokol_imgui / imgui_impl_sdlgpu3 / imgui_impl_wgpu)を
3 つ載せる代わりに、**ImGui の draw list を lub の backend 抽象
(`g_backend`)で描く**。`l_draw` と同じ内部機構(pipeline cache /
reflection ベースの bindings)を C から直接叩くので、統合は 1 系統で
sokol / sdlgpu / webgpu / wasm 全部に効き、capture・golden・verify にも
自動で乗る。

- シェーダは内蔵 slang 文字列を `shader_compile` に通す(native=SPIR-V /
  wasm=WGSL が同一コードで出る。reflection も自動)
- 頂点は ImDrawVert (pos2f, uv2f, col u32) を 8 float に展開して既存の
  float-only 頂点レイアウトに乗せる。ImDrawIdx は 32bit(backend の
  index buffer が u32 固定のため。CMake の `ImDrawIdx=unsigned`)
- scissor だけ vtable に無かったので `set_scissor` を追加(3 backend 各
  ~10 行)。begin_pass が全面にリセットし、ui_render も終了時に戻す
- vendoring は v1.91.9b 固定(1.92 の新フォント管理
  `RendererHasTextures` を自作 backend で負担しないため)

## フレームの流れ

```
main.c: app_frame_begin → ui_new_frame(入力 + NewFrame) → onFrame(Lua)
  onFrame 内: Ui.begin/slider/... (宣言) → beginPass → 描画 → Ui.render → endPass
```

- `ui_new_frame` は毎フレーム無条件(コスト µs 級)。前フレームで
  `ui_render` が呼ばれなかったら EndFrame で捨てるので、UI を使わない
  サンプルは何も起きない
- 入力は SDL 直読み + App の latch。mouse wheel はこの統合で App +
  event 処理に新設した
- ImGui の ini 永続化は無効(決定論と web のため)

## Lua / Haxe API

厳選サブセット(全 API は縛らない。必要になったら 1 個ずつ足す):
begin/end/text/button/checkbox/slider(Float/Int)/drag/colorEdit3/
separator/sameLine/setNextWindow/wantCaptureMouse/render。

immediate mode: 返り値が新しい値で、状態は Haxe 側が持つ(state と
logic の分離はゲーム側の流儀のまま)。`wantCaptureMouse` で UI 操作中の
ゲーム入力を無視できる。

## ハマりどころ(記録)

- `ImGui::NewFrame` はフォントアトラス built が前提。Release では
  IM_ASSERT が消えて null font で segfault する。CPU 側の atlas 構築は
  コンテキスト生成時に前倒しし、GPU アップロードだけ遅延する
- wasm の `SDL_GetWindowSizeInPixels` は canvas サイズを返さない。
  DisplaySize は `app_frame_begin` の返す framebuffer サイズを使う
  (間違えると scissor が render target を超えて command buffer ごと無効)
- 初回フレームは NewFrame 時点で font TexID が 0 のまま draw cmd に乗る。
  bind 時に 0 → font atlas へ読み替える
