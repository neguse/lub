#include "ui.h"

extern "C" {
#include "api_internal.h"
#include "app.h"
#include "backend.h"
#include "pass.h"
#include "pipeline.h"
#include "shader.h"
}

#include "imgui.h"

#include <string.h>
#include <vector>

// ImGui のレンダラ実装。draw list を「頂点変換 → 単一 vbuf/ibuf アップロード →
// cmd ごとに scissor + テクスチャ切替 + indexed draw」として l_draw と同じ
// 内部 API (pipeline cache / reflection ベースの bindings) で発行する。
// 頂点は ImDrawVert (pos2f, uv2f, col u32) を 8 float に展開して、既存の
// float-only 頂点レイアウト (reflection 由来) に乗せる。ImDrawIdx は CMake の
// ImDrawIdx=unsigned 定義で 32bit (backend の ibuf は u32 固定)。

static App *g_app_ui = nullptr;
static bool g_ctx_ready = false;
static bool g_frame_open = false;
static bool g_gpu_ready = false;
static bool g_gpu_failed = false;
static BackendShader g_shader = 0;
static ShaderReflection g_refl;
static BackendImage g_font_tex = 0;
static BackendBuffer g_vbuf = 0, g_ibuf = 0;
static size_t g_vbuf_bytes = 0, g_ibuf_bytes = 0;

static const char *UI_VS = //
    "struct Uniforms {\n"
    "  float4x4 proj;\n"
    "};\n"
    "ConstantBuffer<Uniforms> u;\n"
    "struct VSIn {\n"
    "  float2 pos : POSITION;\n"
    "  float2 uv : TEXCOORD0;\n"
    "  float4 col : COLOR;\n"
    "};\n"
    "struct VSOut {\n"
    "  float2 uv : TEXCOORD0;\n"
    "  float4 col : COLOR0;\n"
    "  float4 pos : SV_Position;\n"
    "};\n"
    "[shader(\"vertex\")] VSOut vs_main(VSIn i) {\n"
    "  VSOut o;\n"
    "  o.pos = mul(u.proj, float4(i.pos, 0.0, 1.0));\n"
    "  o.uv = i.uv;\n"
    "  o.col = i.col;\n"
    "  return o;\n"
    "}\n";
static const char *UI_FS = //
    "LUB_TEXTURE2D(tex);\n"
    "struct FSIn {\n"
    "  float2 uv : TEXCOORD0;\n"
    "  float4 col : COLOR0;\n"
    "};\n"
    "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
    "  return i.col * LUB_SAMPLE(tex, i.uv);\n"
    "}\n";

static bool ui_gpu_init() {
#if defined(__EMSCRIPTEN__)
  ShaderTargetBackend tgt = SHADER_TARGET_WGSL;
#elif defined(_WIN32)
  // vulkan / sdlgpu は SDLGPU target の SPIR-V を食う。d3d12 だけ DXIL。
  ShaderTargetBackend tgt = (g_backend == &g_backend_d3d12)
                                ? SHADER_TARGET_D3D12
                                : SHADER_TARGET_SDLGPU;
#else
  ShaderTargetBackend tgt = SHADER_TARGET_SDLGPU;
#endif
  char err[1024];
  ShaderBlob vsb = {}, fsb = {};
  if (!shader_compile(UI_VS, UI_FS, tgt, &vsb, &fsb, &g_refl, err,
                      sizeof(err))) {
    SDL_Log("ui: shader compile failed: %s", err);
    shader_blob_free(&vsb);
    shader_blob_free(&fsb);
    return false;
  }
  ShaderDesc sd = {};
  sd.vs_spirv = vsb.spirv;
  sd.vs_bytes = vsb.bytes;
  sd.fs_spirv = fsb.spirv;
  sd.fs_bytes = fsb.bytes;
  sd.refl = &g_refl;
  g_shader = g_backend->make_shader(&sd);
  shader_blob_free(&vsb);
  shader_blob_free(&fsb);
  if (!g_shader) {
    SDL_Log("ui: make_shader failed");
    return false;
  }

  ImGuiIO &io = ImGui::GetIO();
  unsigned char *px = nullptr;
  int w = 0, h = 0;
  io.Fonts->GetTexDataAsRGBA32(&px, &w, &h);
  ImageDesc id = {};
  id.fmt = SGL_PF_RGBA8;
  id.w = w;
  id.h = h;
  id.data = px;
  id.data_bytes = (size_t)w * h * 4;
  id.filter = SGL_FILTER_LINEAR;
  id.wrap = SGL_WRAP_CLAMP;
  g_font_tex = g_backend->make_image(&id);
  if (!g_font_tex) {
    SDL_Log("ui: font atlas make_image failed");
    return false;
  }
  io.Fonts->SetTexID((ImTextureID)(intptr_t)g_font_tex);
  return true;
}

// l_use_buffer と同じ方針: 同サイズなら in-place 更新、違えば作り直し。
static bool ensure_buffer(BackendBuffer *buf, size_t *cap, SglBufferType type,
                          const void *data, size_t bytes) {
  if (*buf && *cap == bytes) {
    g_backend->update_buffer(*buf, data, bytes);
    return true;
  }
  if (*buf)
    g_backend->destroy_buffer(*buf);
  *buf = g_backend->make_buffer(type, data, bytes);
  *cap = bytes;
  return *buf != 0;
}

extern "C" void ui_new_frame(App *app, float dt, int fb_w, int fb_h) {
  g_app_ui = app;
  if (!g_ctx_ready) {
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO &io = ImGui::GetIO();
    io.IniFilename = nullptr; // 状態ファイルを書かない (決定論 + web)
    io.BackendPlatformName = "lub";
    io.BackendRendererName = "lub";
    // NewFrame はフォントアトラスが built であることを要求する (Release では
    // assert が消えて null font を踏む)。CPU 側の構築だけここでやり、GPU への
    // アップロードは ui_gpu_init まで遅延する。
    unsigned char *px = nullptr;
    int fw = 0, fh = 0;
    io.Fonts->GetTexDataAsRGBA32(&px, &fw, &fh);
    g_ctx_ready = true;
  }
  // 前フレームで ui_render が呼ばれなかったら捨てる
  if (g_frame_open)
    ImGui::EndFrame();

  ImGuiIO &io = ImGui::GetIO();
  int w = fb_w > 0 ? fb_w : 1280;
  int h = fb_h > 0 ? fb_h : 720;
  io.DisplaySize = ImVec2((float)w, (float)h);
  io.DeltaTime = dt > 0 ? dt : 1.0f / 60.0f;

  // マウス: window 座標 → framebuffer px (hidpi / canvas スケール補正)
  float mx = 0, my = 0;
  Uint32 buttons = SDL_GetMouseState(&mx, &my);
  int ww = 0, wh = 0;
  SDL_GetWindowSize(app->window, &ww, &wh);
  float sx = ww > 0 ? (float)w / (float)ww : 1.0f;
  float sy = wh > 0 ? (float)h / (float)wh : 1.0f;
  io.AddMousePosEvent(mx * sx, my * sy);
  io.AddMouseButtonEvent(0, (buttons & SDL_BUTTON_LMASK) != 0);
  io.AddMouseButtonEvent(1, (buttons & SDL_BUTTON_RMASK) != 0);
  io.AddMouseButtonEvent(2, (buttons & SDL_BUTTON_MMASK) != 0);
  if (app->mouse_wheel_x != 0 || app->mouse_wheel_y != 0)
    io.AddMouseWheelEvent(app->mouse_wheel_x, app->mouse_wheel_y);

  ImGui::NewFrame();
  g_frame_open = true;
}

extern "C" void ui_shutdown(void) {
  if (!g_ctx_ready)
    return;
  if (g_frame_open) {
    ImGui::EndFrame();
    g_frame_open = false;
  }
  if (g_vbuf)
    g_backend->destroy_buffer(g_vbuf);
  if (g_ibuf)
    g_backend->destroy_buffer(g_ibuf);
  if (g_font_tex)
    g_backend->destroy_image(g_font_tex);
  if (g_shader)
    g_backend->destroy_shader(g_shader);
  g_vbuf = g_ibuf = 0;
  g_font_tex = 0;
  g_shader = 0;
  g_gpu_ready = false;
  ImGui::DestroyContext();
  g_ctx_ready = false;
}

// ---- C API (include/lub/lub_api.h) -----------------------------------------

// frame が開いてない時 (new_frame 前 / render 後) は widget 呼び出しを黙って
// 既定値で返す。on_frame の書き順ミスでクラッシュさせない。
// ImGui は NUL 終端の文字列を取るので LubStr を scratch に写す。
static const char *ui_cstr(LubStr s) {
  static char buf[4][512];
  static int slot = 0;
  char *b = buf[slot];
  slot = (slot + 1) & 3;
  size_t n = s.len > 0 ? (size_t)s.len : 0;
  if (n >= sizeof(buf[0]))
    n = sizeof(buf[0]) - 1;
  if (n)
    memcpy(b, s.ptr, n);
  b[n] = '\0';
  return b;
}

extern "C" LubStatus lub_ui_render(LubContext *ctx) {
  App *app = (App *)ctx;
  if (!g_app_ui || !g_frame_open)
    return LUB_OK;
  if (!pass_state_in_pass(&app->pass))
    return lub_api_fail(app, "ui_render: must be called inside begin_pass");
  if (g_gpu_failed)
    return LUB_OK; // 一度失敗したら以降は黙って no-op
                   // (毎フレームのエラー連打回避)
  if (!g_gpu_ready) {
    if (!ui_gpu_init()) {
      g_gpu_failed = true;
      return lub_api_fail(app, "ui_render: gpu init failed (see log)");
    }
    g_gpu_ready = true;
  }

  ImGui::Render();
  g_frame_open = false;
  ImDrawData *dd = ImGui::GetDrawData();
  if (!dd || dd->TotalVtxCount <= 0)
    return LUB_OK;

  // 全 cmd list を単一の float 頂点列 + u32 インデックス列に平坦化
  static std::vector<float> vstage;
  static std::vector<unsigned int> istage;
  vstage.resize((size_t)dd->TotalVtxCount * 8);
  istage.resize((size_t)dd->TotalIdxCount);
  size_t vbase = 0, ibase = 0;
  for (int li = 0; li < dd->CmdListsCount; ++li) {
    const ImDrawList *dl = dd->CmdLists[li];
    for (int v = 0; v < dl->VtxBuffer.Size; ++v) {
      const ImDrawVert &iv = dl->VtxBuffer[v];
      float *o = &vstage[(vbase + (size_t)v) * 8];
      o[0] = iv.pos.x;
      o[1] = iv.pos.y;
      o[2] = iv.uv.x;
      o[3] = iv.uv.y;
      o[4] = (float)((iv.col >> IM_COL32_R_SHIFT) & 0xFF) / 255.0f;
      o[5] = (float)((iv.col >> IM_COL32_G_SHIFT) & 0xFF) / 255.0f;
      o[6] = (float)((iv.col >> IM_COL32_B_SHIFT) & 0xFF) / 255.0f;
      o[7] = (float)((iv.col >> IM_COL32_A_SHIFT) & 0xFF) / 255.0f;
    }
    for (int x = 0; x < dl->IdxBuffer.Size; ++x)
      istage[ibase + (size_t)x] = dl->IdxBuffer[x] + (unsigned int)vbase;
    vbase += (size_t)dl->VtxBuffer.Size;
    ibase += (size_t)dl->IdxBuffer.Size;
  }
  if (!ensure_buffer(&g_vbuf, &g_vbuf_bytes, SGL_BUFFER_VERTEX, vstage.data(),
                     vstage.size() * sizeof(float)) ||
      !ensure_buffer(&g_ibuf, &g_ibuf_bytes, SGL_BUFFER_INDEX, istage.data(),
                     istage.size() * sizeof(unsigned int)))
    return lub_api_fail(app, "ui_render: buffer upload failed");

  BackendPipeline pip = pipeline_cache_get(
      &app->pip_cache, g_shader, &g_refl, SGL_BLEND_ALPHA,
      /*depth_test=*/false, /*depth_write=*/false, SGL_CULL_NONE,
      SGL_PRIM_TRIANGLES, app->pass.current_n_color_targets,
      app->pass.current_color_fmts, app->pass.current_has_depth,
      app->pass.current_depth_fmt, /*depth_tex_mask=*/0,
      (int64_t)app->frame_index);
  g_backend->apply_pipeline(pip);

  // 2D ortho (framebuffer px, y down)。row-major で mul(u.proj, v) 形式
  // (lub.Math の Mat4 と同じ渡し方)。
  float W = dd->DisplaySize.x > 0 ? dd->DisplaySize.x : 1;
  float H = dd->DisplaySize.y > 0 ? dd->DisplaySize.y : 1;
  const float proj[16] = {
      2.0f / W, 0,         0, -1, //
      0,        -2.0f / H, 0, 1,  //
      0,        0,         1, 0,  //
      0,        0,         0, 1,
  };
  for (int i = 0; i < g_refl.ub_count; ++i) {
    const ShaderUniformBlock *ub = &g_refl.ubs[i];
    g_backend->apply_uniforms(ub->stage, ub->slot, proj, sizeof(proj));
  }

  int fbw = (int)W, fbh = (int)H;
  size_t list_idx_base = 0;
  for (int li = 0; li < dd->CmdListsCount; ++li) {
    const ImDrawList *dl = dd->CmdLists[li];
    for (int ci = 0; ci < dl->CmdBuffer.Size; ++ci) {
      const ImDrawCmd &cmd = dl->CmdBuffer[ci];
      if (cmd.UserCallback) {
        cmd.UserCallback(dl, &cmd);
        continue;
      }
      int cx = (int)cmd.ClipRect.x;
      int cy = (int)cmd.ClipRect.y;
      int cw = (int)cmd.ClipRect.z - cx;
      int ch = (int)cmd.ClipRect.w - cy;
      if (cx < 0) {
        cw += cx;
        cx = 0;
      }
      if (cy < 0) {
        ch += cy;
        cy = 0;
      }
      if (cx + cw > fbw)
        cw = fbw - cx;
      if (cy + ch > fbh)
        ch = fbh - cy;
      if (cw <= 0 || ch <= 0 || cmd.ElemCount == 0)
        continue;
      g_backend->set_scissor(cx, cy, cw, ch);

      BindingsDesc bind = {};
      bind.refl = &g_refl;
      bind.vbuf = g_vbuf;
      bind.ibuf = g_ibuf;
      bind.texture_count = 1;
      bind.textures[0].name = "tex";
      // 初回フレームは NewFrame 時点で font TexID が未設定 (0) のまま
      // draw cmd に乗るので、0 は font atlas に読み替える。
      BackendImage img = (BackendImage)(intptr_t)cmd.GetTexID();
      bind.textures[0].image = img ? img : g_font_tex;
      g_backend->apply_bindings(&bind);
      g_backend->draw((int)(list_idx_base + cmd.IdxOffset), (int)cmd.ElemCount,
                      1);
    }
    list_idx_base += (size_t)dl->IdxBuffer.Size;
  }
  g_backend->set_scissor(0, 0, fbw, fbh); // 後続の draw を巻き込まない
  return LUB_OK;
}

extern "C" bool lub_ui_begin_window(LubContext *ctx, LubStr title) {
  (void)ctx;
  if (!g_frame_open)
    return false;
  return ImGui::Begin(ui_cstr(title));
}

extern "C" void lub_ui_end_window(LubContext *ctx) {
  (void)ctx;
  if (g_frame_open)
    ImGui::End();
}

extern "C" void lub_ui_text(LubContext *ctx, LubStr text) {
  (void)ctx;
  if (!g_frame_open)
    return;
  ImGui::TextUnformatted(text.ptr, text.ptr ? text.ptr + text.len : nullptr);
}

extern "C" bool lub_ui_button(LubContext *ctx, LubStr label) {
  (void)ctx;
  if (!g_frame_open)
    return false;
  return ImGui::Button(ui_cstr(label));
}

extern "C" bool lub_ui_checkbox(LubContext *ctx, LubStr label, bool value) {
  (void)ctx;
  if (!g_frame_open)
    return value;
  ImGui::Checkbox(ui_cstr(label), &value);
  return value;
}

extern "C" float lub_ui_slider_float(LubContext *ctx, LubStr label, float value,
                                     float min, float max) {
  (void)ctx;
  if (!g_frame_open)
    return value;
  ImGui::SliderFloat(ui_cstr(label), &value, min, max);
  return value;
}

extern "C" int32_t lub_ui_slider_int(LubContext *ctx, LubStr label,
                                     int32_t value, int32_t min, int32_t max) {
  (void)ctx;
  if (!g_frame_open)
    return value;
  int v = value;
  ImGui::SliderInt(ui_cstr(label), &v, min, max);
  return v;
}

extern "C" float lub_ui_drag_float(LubContext *ctx, LubStr label, float value,
                                   const float *speed, const float *min,
                                   const float *max) {
  (void)ctx;
  if (!g_frame_open)
    return value;
  ImGui::DragFloat(ui_cstr(label), &value, speed && *speed > 0 ? *speed : 1.0f,
                   min ? *min : 0.0f, max ? *max : 0.0f);
  return value;
}

extern "C" void lub_ui_color_edit3(LubContext *ctx, LubStr label, float r,
                                   float g, float b, float *new_r, float *new_g,
                                   float *new_b) {
  (void)ctx;
  float rgb[3] = {r, g, b};
  if (g_frame_open)
    ImGui::ColorEdit3(ui_cstr(label), rgb);
  *new_r = rgb[0];
  *new_g = rgb[1];
  *new_b = rgb[2];
}

extern "C" void lub_ui_separator(LubContext *ctx) {
  (void)ctx;
  if (g_frame_open)
    ImGui::Separator();
}

extern "C" void lub_ui_same_line(LubContext *ctx) {
  (void)ctx;
  if (g_frame_open)
    ImGui::SameLine();
}

// 階層 UI。true が返ったら子を描いて tree_pop を呼ぶ。
extern "C" bool lub_ui_tree_node(LubContext *ctx, LubStr label,
                                 const bool *default_open) {
  (void)ctx;
  if (!g_frame_open)
    return false;
  return ImGui::TreeNodeEx(ui_cstr(label), default_open && *default_open
                                               ? ImGuiTreeNodeFlags_DefaultOpen
                                               : 0);
}

extern "C" void lub_ui_tree_pop(LubContext *ctx) {
  (void)ctx;
  if (g_frame_open)
    ImGui::TreePop();
}

extern "C" void lub_ui_set_next_window(LubContext *ctx, float x, float y,
                                       float w, float h) {
  (void)ctx;
  if (!g_frame_open)
    return;
  ImGui::SetNextWindowPos(ImVec2(x, y), ImGuiCond_FirstUseEver);
  ImGui::SetNextWindowSize(ImVec2(w, h), ImGuiCond_FirstUseEver);
}

// UI がマウスを取ってる間、ゲーム側はクリックを無視できる。
extern "C" bool lub_ui_want_capture_mouse(LubContext *ctx) {
  (void)ctx;
  return g_ctx_ready && ImGui::GetIO().WantCaptureMouse;
}
