// SDL3 GPU backend skeleton (Task 3).
//
// Implements the minimum needed for sample 00b_clear to render a clear-only
// frame via SDL_GPU. Buffer/shader/pipeline/draw/capture stubs return zero
// or log a "not yet implemented" message — those are filled in by Task 4-8.
//
// Sequence per frame:
//   begin_frame: AcquireGPUCommandBuffer -> AcquireGPUSwapchainTexture
//   begin_pass : BeginGPURenderPass with LOADOP_CLEAR
//   end_pass   : EndGPURenderPass
//   end_frame  : SubmitGPUCommandBuffer
#include "backend.h"
#include "app.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_gpu.h>
#include <stdlib.h>
#include <string.h>

// Render pass handle is per begin/end-pass pair. Stored in a file-static
// because the vtable's begin_pass/end_pass don't take a backend cookie.
static SDL_GPURenderPass *g_render_pass = NULL;

static bool sg_init(App *app) {
    app->gpu_device = SDL_CreateGPUDevice(SDL_GPU_SHADERFORMAT_SPIRV, true, NULL);
    if (!app->gpu_device) {
        SDL_Log("SDL_CreateGPUDevice failed: %s", SDL_GetError());
        return false;
    }
    if (!SDL_ClaimWindowForGPUDevice(app->gpu_device, app->window)) {
        SDL_Log("SDL_ClaimWindowForGPUDevice failed: %s", SDL_GetError());
        SDL_DestroyGPUDevice(app->gpu_device);
        app->gpu_device = NULL;
        return false;
    }
    return true;
}

static void sg_shutdown(App *app) {
    if (app->gpu_device) {
        SDL_ReleaseWindowFromGPUDevice(app->gpu_device, app->window);
        SDL_DestroyGPUDevice(app->gpu_device);
        app->gpu_device = NULL;
    }
}

static void sg_begin_frame(App *app, int *out_w, int *out_h) {
    app->gpu_cmd = SDL_AcquireGPUCommandBuffer(app->gpu_device);
    if (!app->gpu_cmd) {
        SDL_Log("SDL_AcquireGPUCommandBuffer failed: %s", SDL_GetError());
        if (out_w) *out_w = 0;
        if (out_h) *out_h = 0;
        return;
    }
    Uint32 sw = 0, sh = 0;
    app->gpu_swapchain_tex = NULL;
    if (!SDL_AcquireGPUSwapchainTexture(app->gpu_cmd, app->window,
                                          &app->gpu_swapchain_tex, &sw, &sh)) {
        SDL_Log("SDL_AcquireGPUSwapchainTexture failed: %s", SDL_GetError());
    }
    if (out_w) *out_w = (int)sw;
    if (out_h) *out_h = (int)sh;
}

static void sg_end_frame(App *app) {
    if (app->gpu_cmd && !SDL_SubmitGPUCommandBuffer(app->gpu_cmd)) {
        SDL_Log("SDL_SubmitGPUCommandBuffer failed: %s", SDL_GetError());
    }
    app->gpu_cmd = NULL;
    app->gpu_swapchain_tex = NULL;
}

static void sg_begin_pass(App *app, const PassBeginDesc *d) {
    if (!app->gpu_swapchain_tex) {
        g_render_pass = NULL;
        return;
    }
    SDL_GPUColorTargetInfo target = {
        .texture = app->gpu_swapchain_tex,
        .clear_color = { d->clear[0], d->clear[1], d->clear[2], d->clear[3] },
        .load_op = SDL_GPU_LOADOP_CLEAR,
        .store_op = SDL_GPU_STOREOP_STORE,
    };
    g_render_pass = SDL_BeginGPURenderPass(app->gpu_cmd, &target, 1, NULL);
}

static void sg_end_pass(App *app) {
    (void)app;
    if (g_render_pass) {
        SDL_EndGPURenderPass(g_render_pass);
        g_render_pass = NULL;
    }
}

// --- Stubs for Task 4-8 ---------------------------------------------------

static BackendBuffer sg_make_buffer(SglBufferType t, const float *d, size_t b) {
    (void)t; (void)d; (void)b;
    static bool warned = false;
    if (!warned) { SDL_Log("sdlgpu: make_buffer not yet implemented (Task 4)"); warned = true; }
    return 0;
}
static BackendImage sg_make_image(const ImageDesc *d) {
    (void)d;
    static bool warned = false;
    if (!warned) { SDL_Log("sdlgpu: make_image not yet implemented (Task 6)"); warned = true; }
    return 0;
}
static BackendShader sg_make_shader(const ShaderDesc *d) {
    (void)d;
    static bool warned = false;
    if (!warned) { SDL_Log("sdlgpu: make_shader not yet implemented (Task 4)"); warned = true; }
    return 0;
}
static BackendPipeline sg_make_pipeline(const PipelineDesc *d) {
    (void)d;
    static bool warned = false;
    if (!warned) { SDL_Log("sdlgpu: make_pipeline not yet implemented (Task 4)"); warned = true; }
    return 0;
}
static void sg_destroy_buffer(BackendBuffer h)   { (void)h; }
static void sg_destroy_image(BackendImage h)     { (void)h; }
static void sg_destroy_shader(BackendShader h)   { (void)h; }
static void sg_destroy_pipeline(BackendPipeline h){ (void)h; }
static void sg_apply_pipeline(BackendPipeline h) {
    (void)h;
    static bool warned = false;
    if (!warned) { SDL_Log("sdlgpu: apply_pipeline not yet implemented (Task 4)"); warned = true; }
}
static void sg_apply_bindings(const BindingsDesc *b) {
    (void)b;
    static bool warned = false;
    if (!warned) { SDL_Log("sdlgpu: apply_bindings not yet implemented (Task 4 / Task 6)"); warned = true; }
}
static void sg_apply_uniforms(int slot, const void *d, size_t b) {
    (void)slot; (void)d; (void)b;
    static bool warned = false;
    if (!warned) { SDL_Log("sdlgpu: apply_uniforms not yet implemented (Task 7)"); warned = true; }
}
static void sg_draw(int base, int count) {
    (void)base; (void)count;
    static bool warned = false;
    if (!warned) { SDL_Log("sdlgpu: draw not yet implemented (Task 4)"); warned = true; }
}

static bool sg_capture(App *app, const char *path) {
    (void)app; (void)path;
    SDL_Log("sdlgpu capture not yet implemented (Task 8)");
    return false;
}

static SglPixelFormat sg_swapchain_color_format(App *app) {
    (void)app;
    // PoC: assume RGBA8. Task 4+ may need to query the actual swapchain
    // format via SDL_GetGPUSwapchainTextureFormat for accurate pipeline keying.
    return SGL_PF_RGBA8;
}

const RenderBackend g_backend_sdlgpu = {
    .name = "sdlgpu",
    .init = sg_init,
    .shutdown = sg_shutdown,
    .begin_frame = sg_begin_frame,
    .end_frame = sg_end_frame,
    .make_buffer = sg_make_buffer,
    .make_image = sg_make_image,
    .make_shader = sg_make_shader,
    .make_pipeline = sg_make_pipeline,
    .destroy_buffer = sg_destroy_buffer,
    .destroy_image = sg_destroy_image,
    .destroy_shader = sg_destroy_shader,
    .destroy_pipeline = sg_destroy_pipeline,
    .begin_pass = sg_begin_pass,
    .end_pass = sg_end_pass,
    .apply_pipeline = sg_apply_pipeline,
    .apply_bindings = sg_apply_bindings,
    .apply_uniforms = sg_apply_uniforms,
    .draw = sg_draw,
    .capture = sg_capture,
    .swapchain_color_format = sg_swapchain_color_format,
};
