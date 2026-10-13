// backend_export.c — `lub export <os>`: the export mode and its backend.
//
// The export runs the game without a GPU or a display and writes a snapshot
// to out_dir: a directory a player runs the game from. use_shader compiles
// every shader for each target the players of the export OS use, so the
// generation cache (cache/shader/) holds what a player without Slang needs.
// The files the game reads and the Lua it loads are copied to the same
// relative paths. Resources get handles and nothing else; draws, dispatches,
// captures and readbacks do nothing.
#include "app.h"
#include "backend.h"
#include "shader_cache.h"
#include <SDL3/SDL.h>
#include <string.h>

typedef struct ExportOs {
  const char *name;
  ShaderTargetBackend targets[2];
  int n_targets;
} ExportOs;

// The first target is the one use_shader hands to the backend.
static const ExportOs k_export_os[] = {
    {"web", {SHADER_TARGET_WGSL}, 1},
    {"linux", {SHADER_TARGET_SDLGPU}, 1},
};

static const ExportOs *g_os;
static char g_out_dir[1024];
static bool g_failed;
static uintptr_t g_next_handle = 1;

bool export_begin(const char *os, const char *out_dir) {
  for (size_t i = 0; i < SDL_arraysize(k_export_os); ++i) {
    if (strcmp(os, k_export_os[i].name) == 0)
      g_os = &k_export_os[i];
  }
  if (!g_os) {
    SDL_Log("export: unknown OS '%s' (expected 'web' or 'linux')", os);
    return false;
  }
  SDL_strlcpy(g_out_dir, out_dir, sizeof(g_out_dir));
  char cache[1100];
  SDL_snprintf(cache, sizeof(cache), "%s/cache/shader", out_dir);
  shader_cache_set_dir(cache);
  return true;
}

bool export_active(void) { return g_os != NULL; }

const char *export_out_dir(void) { return g_os ? g_out_dir : NULL; }

int export_targets(const ShaderTargetBackend **out) {
  *out = g_os->targets;
  return g_os->n_targets;
}

void export_fail(void) { g_failed = true; }

bool export_failed(void) { return g_failed; }

void export_record(const char *path, const uint8_t *bytes, size_t len) {
  if (!g_os)
    return;
  bool inside = path[0] != '/' && path[0] != '\\' && !strchr(path, ':');
  for (const char *seg = path; inside && *seg;) {
    size_t n = strcspn(seg, "/\\");
    inside = !(n == 2 && seg[0] == '.' && seg[1] == '.');
    seg += n + (seg[n] ? 1 : 0);
  }
  if (!inside) {
    SDL_Log("export: %s is outside the game directory and is not packed", path);
    return;
  }
  char out[2048], parent[2048];
  SDL_snprintf(out, sizeof(out), "%s/%s", g_out_dir, path);
  SDL_strlcpy(parent, out, sizeof(parent));
  *strrchr(parent, '/') = 0;
  if (!SDL_CreateDirectory(parent) || !SDL_SaveFile(out, bytes, len)) {
    SDL_Log("export: write failed: %s: %s", out, SDL_GetError());
    g_failed = true;
  }
}

void export_copy(const char *src, const char *path) {
  if (!g_os)
    return;
  size_t len = 0;
  void *bytes = SDL_LoadFile(src, &len);
  if (!bytes) {
    SDL_Log("export: read failed: %s: %s", src, SDL_GetError());
    g_failed = true;
    return;
  }
  export_record(path, (const uint8_t *)bytes, len);
  SDL_free(bytes);
}

static uintptr_t next_handle(void) { return g_next_handle++; }

static bool ex_init(App *app) {
  (void)app;
  return true;
}
static void ex_shutdown(App *app) { (void)app; }

static void ex_begin_frame(App *app, int *out_w, int *out_h) {
  SDL_GetWindowSizeInPixels(app->window, out_w, out_h);
}
static void ex_end_frame(App *app) { (void)app; }

static BackendBuffer ex_make_buffer(SglBufferType type, const void *data,
                                    size_t bytes) {
  (void)type;
  (void)data;
  (void)bytes;
  return next_handle();
}
static BackendImage ex_make_image(const ImageDesc *desc) {
  (void)desc;
  return next_handle();
}
static BackendShader ex_make_shader(const ShaderDesc *desc) {
  (void)desc;
  return next_handle();
}
static BackendPipeline ex_make_pipeline(const PipelineDesc *desc) {
  (void)desc;
  return next_handle();
}

static void ex_destroy(uintptr_t h) { (void)h; }

static void ex_update(uintptr_t h, const void *data, size_t bytes) {
  (void)h;
  (void)data;
  (void)bytes;
}

static void ex_begin_pass(App *app, const PassBeginDesc *desc) {
  (void)app;
  (void)desc;
}
static void ex_end_pass(App *app) { (void)app; }
static void ex_apply_pipeline(BackendPipeline p) { (void)p; }
static void ex_apply_bindings(const BindingsDesc *b) { (void)b; }
static void ex_apply_uniforms(SglShaderStage stage, int ub_slot,
                              const void *data, size_t bytes) {
  (void)stage;
  (void)ub_slot;
  (void)data;
  (void)bytes;
}
static void ex_draw(int base, int count, int instance_count) {
  (void)base;
  (void)count;
  (void)instance_count;
}
static void ex_set_scissor(int x, int y, int w, int h) {
  (void)x;
  (void)y;
  (void)w;
  (void)h;
}
static void ex_dispatch(App *app, const ComputeDispatchDesc *desc) {
  (void)app;
  (void)desc;
}

static bool ex_request_readback_image(App *app, BackendImage image, int w,
                                      int h, SglPixelFormat src_fmt,
                                      BackendReadback *out) {
  (void)app;
  (void)image;
  (void)w;
  (void)h;
  (void)src_fmt;
  (void)out;
  return false;
}
static ReadbackPollStatus ex_poll_readback(BackendReadback req,
                                           ReadbackResult *out) {
  (void)req;
  (void)out;
  return READBACK_POLL_ERROR;
}
static void ex_destroy_readback(BackendReadback req) { (void)req; }

static bool ex_capture(App *app, const char *path) {
  (void)app;
  (void)path;
  return false;
}

static SglPixelFormat ex_swapchain_color_format(App *app) {
  (void)app;
  return SGL_PF_BGRA8;
}

const RenderBackend g_backend_export = {
    .name = "export",
    .init = ex_init,
    .shutdown = ex_shutdown,
    .begin_frame = ex_begin_frame,
    .end_frame = ex_end_frame,
    .make_buffer = ex_make_buffer,
    .make_image = ex_make_image,
    .make_shader = ex_make_shader,
    .make_pipeline = ex_make_pipeline,
    .destroy_buffer = ex_destroy,
    .destroy_image = ex_destroy,
    .destroy_shader = ex_destroy,
    .destroy_pipeline = ex_destroy,
    .update_buffer = ex_update,
    .update_image = ex_update,
    .begin_pass = ex_begin_pass,
    .end_pass = ex_end_pass,
    .apply_pipeline = ex_apply_pipeline,
    .apply_bindings = ex_apply_bindings,
    .apply_uniforms = ex_apply_uniforms,
    .draw = ex_draw,
    .set_scissor = ex_set_scissor,
    .dispatch = ex_dispatch,
    .request_readback_image = ex_request_readback_image,
    .poll_readback = ex_poll_readback,
    .destroy_readback = ex_destroy_readback,
    .capture = ex_capture,
    .capture_before_end_frame = false,
    .swapchain_color_format = ex_swapchain_color_format,
};
