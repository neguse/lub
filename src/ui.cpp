#include "ui.h"

extern "C" {
#include "api_internal.h"
#include "app.h"
#include "backend.h"
#include "gpu_time.h"
#include "pass.h"
#include "pipeline.h"
#include "shader.h"
}

#include "imgui.h"

#include <string.h>
#include <vector>

// ImGui renderer implementation.
// Issues the draw list as "vertex transform → single vbuf/ibuf upload → per-cmd
// scissor + texture switch + indexed draw" through the same internal API as
// lub_gfx_draw (pipeline cache / reflection-based bindings). Vertices are
// ImDrawVert (pos2f, uv2f, col u32) expanded into a storage buffer of 8 floats
// (float2 pos, float2 uv, float4 col), which the shader reads by vertex id
// (vertex pulling). ImDrawIdx is 32-bit through the ImDrawIdx=unsigned
// definition in CMake (the backend ibuf is fixed to u32). This renderer only
// uploads the font atlas once at startup, and does not handle the mechanism of
// ImGui 1.92 and later that asks the renderer to create and update textures
// (RendererHasTextures). When upgrading ImGui, update the font texture handling
// here to match.

static App *g_app_ui = nullptr;
static bool g_ctx_ready = false;
static bool g_frame_open = false;
// false while App.Step runs an opened app (Ui.* of that app does nothing)
static bool g_enabled = true;
static bool ui_open() { return g_frame_open && g_enabled; }
extern "C" void ui_set_enabled(bool on) { g_enabled = on; }
static bool g_gpu_ready = false;
static bool g_gpu_failed = false;
static BackendShader g_shader = 0;
static ShaderReflection g_refl;
// Same formula as Blends.Alpha.
static const SglBlend kUiBlend = {1,
                                  SGL_BLEND_SRC_ALPHA,
                                  SGL_BLEND_ONE_MINUS_SRC_ALPHA,
                                  SGL_BLEND_OP_ADD,
                                  SGL_BLEND_ONE,
                                  SGL_BLEND_ONE_MINUS_SRC_ALPHA,
                                  SGL_BLEND_OP_ADD};
static BackendImage g_font_tex = 0;
static BackendBuffer g_vbuf = 0, g_ibuf = 0;
static size_t g_vbuf_bytes = 0, g_ibuf_bytes = 0;

static const char *UI_VS = //
    "struct Uniforms {\n"
    "  float4x4 proj;\n"
    "};\n"
    "ConstantBuffer<Uniforms> u;\n"
    "struct V {\n"
    "  float2 pos;\n"
    "  float2 uv;\n"
    "  float4 col;\n"
    "};\n"
    "StructuredBuffer<V> verts;\n"
    "struct VSOut {\n"
    "  float2 uv : TEXCOORD0;\n"
    "  float4 col : COLOR0;\n"
    "  float4 pos : SV_Position;\n"
    "};\n"
    "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
    "  V i = verts[vid];\n"
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
  char err[1024];
  ShaderBlob vsb = {}, fsb = {};
  if (!shader_compile_for_backend(UI_VS, UI_FS, &vsb, &fsb, &g_refl, err,
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

// Same policy as lub_gfx_use_buffer: update in place if the size is the same,
// recreate otherwise.
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
    io.IniFilename = nullptr; // Do not write the state file (determinism + web)
    io.BackendPlatformName = "lub";
    io.BackendRendererName = "lub";
    // NewFrame requires the font atlas to be built (in Release the assert is
    // gone and it hits a null font). Only the CPU-side build happens here; the
    // GPU upload is deferred to ui_gpu_init.
    unsigned char *px = nullptr;
    int fw = 0, fh = 0;
    io.Fonts->GetTexDataAsRGBA32(&px, &fw, &fh);
    g_ctx_ready = true;
  }
  // Discard if ui_render was not called in the previous frame
  if (g_frame_open)
    ImGui::EndFrame();

  ImGuiIO &io = ImGui::GetIO();
  int w = fb_w > 0 ? fb_w : 1280;
  int h = fb_h > 0 ? fb_h : 720;
  io.DisplaySize = ImVec2((float)w, (float)h);
  io.DeltaTime = dt > 0 ? dt : 1.0f / 60.0f;

  // Mouse: window coordinates → framebuffer px (hidpi / canvas scale
  // correction)
  float mx = 0, my = 0;
  Uint32 buttons = SDL_GetMouseState(&mx, &my);
  int ww = 0, wh = 0;
  SDL_GetWindowSize(app->proc->window, &ww, &wh);
  float sx = ww > 0 ? (float)w / (float)ww : 1.0f;
  float sy = wh > 0 ? (float)h / (float)wh : 1.0f;
  io.AddMousePosEvent(mx * sx, my * sy);
  io.AddMouseButtonEvent(0, (buttons & SDL_BUTTON_LMASK) != 0);
  io.AddMouseButtonEvent(1, (buttons & SDL_BUTTON_RMASK) != 0);
  io.AddMouseButtonEvent(2, (buttons & SDL_BUTTON_MMASK) != 0);
  if (app->input.mouse_wheel_x != 0 || app->input.mouse_wheel_y != 0)
    io.AddMouseWheelEvent(app->input.mouse_wheel_x, app->input.mouse_wheel_y);

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

// When no frame is open (before new_frame / after render), widget calls
// silently return default values. So that ordering mistakes in OnFrame do not
// cause a crash. ImGui takes NUL-terminated strings, so the LubStr is copied
// into scratch.
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
  if (!g_app_ui || !ui_open())
    return LUB_OK;
  if (!pass_state_in_pass(&app->pass))
    return lub_api_fail(app, "ui_render: must be called inside begin_pass");
  if (g_gpu_failed)
    return LUB_OK; // After one failure, silently no-op from then on
                   // (avoids repeating the error every frame)
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

  // Flatten all cmd lists into a single float vertex array + u32 index array
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
  if (!ensure_buffer(&g_vbuf, &g_vbuf_bytes, SGL_BUFFER_STORAGE, vstage.data(),
                     vstage.size() * sizeof(float)) ||
      !ensure_buffer(&g_ibuf, &g_ibuf_bytes, SGL_BUFFER_INDEX, istage.data(),
                     istage.size() * sizeof(unsigned int)))
    return lub_api_fail(app, "ui_render: buffer upload failed");

  BackendPipeline pip = pipeline_cache_get(
      &app->pip_cache, g_shader, &g_refl, kUiBlend,
      /*depth_test=*/false, /*depth_write=*/false, SGL_CULL_NONE,
      SGL_PRIM_TRIANGLES, app->pass.current_n_color_targets,
      app->pass.current_color_fmts, app->pass.current_has_depth,
      app->pass.current_depth_fmt, /*depth_tex_mask=*/0,
      (int64_t)app->frame_index);
  g_backend->apply_pipeline(pip);

  // 2D ortho (framebuffer px, y down).
  // Row-major in mul(u.proj, v) form (passed the same way as Mat4 in lub.Math).
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
      bind.storage_buf_count = 1;
      bind.storage_bufs[0].name = "verts";
      bind.storage_bufs[0].buf = g_vbuf;
      bind.ibuf = g_ibuf;
      bind.texture_count = 1;
      bind.textures[0].name = "tex";
      // On the first frame, the font TexID is still unset (0) at NewFrame time
      // and ends up in the draw cmds, so 0 is treated as the font atlas.
      BackendImage img = (BackendImage)(intptr_t)cmd.GetTexID();
      bind.textures[0].image = img ? img : g_font_tex;
      g_backend->apply_bindings(&bind);
      g_backend->draw((int)(list_idx_base + cmd.IdxOffset), (int)cmd.ElemCount,
                      1);
      gpu_time_draw(app);
    }
    list_idx_base += (size_t)dl->IdxBuffer.Size;
  }
  g_backend->set_scissor(0, 0, fbw, fbh); // Do not affect subsequent draws
  return LUB_OK;
}

extern "C" bool lub_ui_begin_window(LubContext *ctx, LubStr title) {
  (void)ctx;
  if (!ui_open())
    return false;
  return ImGui::Begin(ui_cstr(title));
}

extern "C" void lub_ui_end_window(LubContext *ctx) {
  (void)ctx;
  if (ui_open())
    ImGui::End();
}

extern "C" void lub_ui_text(LubContext *ctx, LubStr text) {
  (void)ctx;
  if (!ui_open())
    return;
  ImGui::TextUnformatted(text.ptr, text.ptr ? text.ptr + text.len : nullptr);
}

extern "C" bool lub_ui_button(LubContext *ctx, LubStr label) {
  (void)ctx;
  if (!ui_open())
    return false;
  return ImGui::Button(ui_cstr(label));
}

extern "C" bool lub_ui_checkbox(LubContext *ctx, LubStr label, bool value) {
  (void)ctx;
  if (!ui_open())
    return value;
  ImGui::Checkbox(ui_cstr(label), &value);
  return value;
}

extern "C" float lub_ui_slider_float(LubContext *ctx, LubStr label, float value,
                                     float min, float max) {
  (void)ctx;
  if (!ui_open())
    return value;
  ImGui::SliderFloat(ui_cstr(label), &value, min, max);
  return value;
}

extern "C" int32_t lub_ui_slider_int(LubContext *ctx, LubStr label,
                                     int32_t value, int32_t min, int32_t max) {
  (void)ctx;
  if (!ui_open())
    return value;
  int v = value;
  ImGui::SliderInt(ui_cstr(label), &v, min, max);
  return v;
}

extern "C" float lub_ui_drag_float(LubContext *ctx, LubStr label, float value,
                                   const float *speed, const float *min,
                                   const float *max) {
  (void)ctx;
  if (!ui_open())
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
  if (ui_open())
    ImGui::ColorEdit3(ui_cstr(label), rgb);
  *new_r = rgb[0];
  *new_g = rgb[1];
  *new_b = rgb[2];
}

extern "C" void lub_ui_separator(LubContext *ctx) {
  (void)ctx;
  if (ui_open())
    ImGui::Separator();
}

extern "C" void lub_ui_same_line(LubContext *ctx) {
  (void)ctx;
  if (ui_open())
    ImGui::SameLine();
}

// Hierarchical UI.
// If it returns true, draw the children and call tree_pop.
extern "C" bool lub_ui_tree_node(LubContext *ctx, LubStr label,
                                 const bool *default_open) {
  (void)ctx;
  if (!ui_open())
    return false;
  return ImGui::TreeNodeEx(ui_cstr(label), default_open && *default_open
                                               ? ImGuiTreeNodeFlags_DefaultOpen
                                               : 0);
}

extern "C" void lub_ui_tree_pop(LubContext *ctx) {
  (void)ctx;
  if (ui_open())
    ImGui::TreePop();
}

extern "C" void lub_ui_set_next_window(LubContext *ctx, float x, float y,
                                       float w, float h) {
  (void)ctx;
  if (!ui_open())
    return;
  ImGui::SetNextWindowPos(ImVec2(x, y), ImGuiCond_FirstUseEver);
  ImGui::SetNextWindowSize(ImVec2(w, h), ImGuiCond_FirstUseEver);
}

// While the UI holds the mouse, the game side can ignore clicks.
extern "C" bool lub_ui_want_capture_mouse(LubContext *ctx) {
  (void)ctx;
  return g_ctx_ready && ImGui::GetIO().WantCaptureMouse;
}
