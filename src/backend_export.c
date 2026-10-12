// backend_export.c — the backend an export runs on ("export-<target>").
//
// It uses no GPU. use_shader still compiles every shader, for the export
// target instead of this machine's GPU, so the shader cache (LUB_SHADER_CACHE)
// fills with what the exported player needs. Resources get handles and
// nothing else; draws, dispatches, captures and readbacks do nothing.
#include "app.h"
#include "backend.h"
#include <SDL3/SDL.h>
#include <string.h>

static ShaderTargetBackend g_target = SHADER_TARGET_WGSL;
static uintptr_t g_next_handle = 1;

bool backend_export_select(const char *name) {
  if (strcmp(name, "export-wgsl") == 0) {
    g_target = SHADER_TARGET_WGSL;
    return true;
  }
  return false;
}

ShaderTargetBackend backend_export_target(void) { return g_target; }

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
