// SDL3 GPU backend (Tasks 3-4).
//
// Task 3 implemented init/shutdown + frame/pass clear-only flow.
// Task 4 fills in make_buffer/make_shader/make_pipeline + apply_pipeline /
// apply_bindings / draw so sample 01_triangle (no uniforms, no textures)
// renders end-to-end.
//
// Sequence per frame:
//   begin_frame: AcquireGPUCommandBuffer -> AcquireGPUSwapchainTexture
//   begin_pass : BeginGPURenderPass with LOADOP_CLEAR
//   end_pass   : EndGPURenderPass
//   end_frame  : SubmitGPUCommandBuffer
#include "app.h"
#include "backend.h"
#include "gpu_stats.h"
#include "stb_image_write.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_gpu.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

// Render pass handle is per begin/end-pass pair. Stored in a file-static
// because the vtable's begin_pass/end_pass don't take a backend cookie.
static SDL_GPURenderPass *g_render_pass = NULL;

// Cached App* for use by resource-creation vtable functions that don't
// receive App* as an argument (make_buffer/make_shader/make_pipeline).
// The current runtime owns exactly one App per process. Set in sg_init and
// re-confirmed each begin_frame as a paranoia measure.
static App *g_app = NULL;

// Most-recently bound pipeline. Tracked here because SDL_GPU's pipeline
// binding is per-render-pass and we need to (re)issue it after begin_pass
// if the user calls apply_pipeline before begin_pass — but for sample 01
// the order is begin_pass -> apply_pipeline -> apply_bindings -> draw, so
// this just records the current pipeline for future use.
static struct SgPipeline *g_current_pip = NULL;

// Whether the most recent apply_bindings bound an index buffer. sg_draw
// branches on this between SDL_DrawGPUIndexedPrimitives and
// SDL_DrawGPUPrimitives.
static bool g_last_indexed = false;

static SglPixelFormat sg_swapchain_color_format(App *app);

static void sg_release_depth_texture(App *app) {
  if (app->gpu_device && app->gpu_depth_tex) {
    SDL_ReleaseGPUTexture(app->gpu_device, app->gpu_depth_tex);
    gpu_stats_destroy(GPU_STAT_TEXTURE,
                      gpu_stats_image_bytes(SGL_PF_DEPTH24_STENCIL8,
                                            app->gpu_depth_w,
                                            app->gpu_depth_h));
  }
  app->gpu_depth_tex = NULL;
  app->gpu_depth_w = 0;
  app->gpu_depth_h = 0;
  app->gpu_depth_fmt = SDL_GPU_TEXTUREFORMAT_INVALID;
}

static SDL_GPUTextureFormat sg_choose_depth_format(SDL_GPUDevice *dev) {
  const SDL_GPUTextureFormat candidates[] = {
      SDL_GPU_TEXTUREFORMAT_D32_FLOAT_S8_UINT,
      SDL_GPU_TEXTUREFORMAT_D24_UNORM_S8_UINT,
      SDL_GPU_TEXTUREFORMAT_D32_FLOAT,
      SDL_GPU_TEXTUREFORMAT_D24_UNORM,
      SDL_GPU_TEXTUREFORMAT_D16_UNORM,
  };
  for (size_t i = 0; i < sizeof(candidates) / sizeof(candidates[0]); ++i) {
    if (SDL_GPUTextureSupportsFormat(
            dev, candidates[i], SDL_GPU_TEXTURETYPE_2D,
            SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET)) {
      return candidates[i];
    }
  }
  return SDL_GPU_TEXTUREFORMAT_INVALID;
}

static SDL_GPUTextureFormat sgl_to_sdl_texture_format(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_R8:
    return SDL_GPU_TEXTUREFORMAT_R8_UNORM;
  case SGL_PF_RG8:
    return SDL_GPU_TEXTUREFORMAT_R8G8_UNORM;
  case SGL_PF_R16F:
    return SDL_GPU_TEXTUREFORMAT_R16_FLOAT;
  case SGL_PF_RG16F:
    return SDL_GPU_TEXTUREFORMAT_R16G16_FLOAT;
  case SGL_PF_R32F:
    return SDL_GPU_TEXTUREFORMAT_R32_FLOAT;
  case SGL_PF_RGBA16F:
    return SDL_GPU_TEXTUREFORMAT_R16G16B16A16_FLOAT;
  case SGL_PF_RGBA32F:
    return SDL_GPU_TEXTUREFORMAT_R32G32B32A32_FLOAT;
  case SGL_PF_DEPTH16:
    return SDL_GPU_TEXTUREFORMAT_D16_UNORM;
  case SGL_PF_DEPTH32F:
    return SDL_GPU_TEXTUREFORMAT_D32_FLOAT;
  case SGL_PF_DEPTH24_STENCIL8:
    return SDL_GPU_TEXTUREFORMAT_D24_UNORM_S8_UINT;
  case SGL_PF_BGRA8:
    return SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM;
  case SGL_PF_RGBA8:
  default:
    return SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM;
  }
}

static bool sgl_is_depth_format(SglPixelFormat fmt) {
  return fmt == SGL_PF_DEPTH16 || fmt == SGL_PF_DEPTH24_STENCIL8 ||
         fmt == SGL_PF_DEPTH32F;
}

static bool sg_ensure_depth_texture(App *app, Uint32 w, Uint32 h) {
  if (!app || !app->gpu_device || w == 0 || h == 0)
    return false;
  if (app->gpu_depth_tex && app->gpu_depth_w == (int)w &&
      app->gpu_depth_h == (int)h) {
    return true;
  }

  sg_release_depth_texture(app);

  SDL_GPUTextureFormat fmt = sg_choose_depth_format(app->gpu_device);
  if (fmt == SDL_GPU_TEXTUREFORMAT_INVALID) {
    SDL_Log("sg_ensure_depth_texture: no supported depth format");
    return false;
  }

  app->gpu_depth_tex = SDL_CreateGPUTexture(
      app->gpu_device, &(SDL_GPUTextureCreateInfo){
                           .type = SDL_GPU_TEXTURETYPE_2D,
                           .format = fmt,
                           .usage = SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET,
                           .width = w,
                           .height = h,
                           .layer_count_or_depth = 1,
                           .num_levels = 1,
                           .sample_count = SDL_GPU_SAMPLECOUNT_1,
                       });
  if (!app->gpu_depth_tex) {
    SDL_Log("sg_ensure_depth_texture: SDL_CreateGPUTexture failed: %s",
            SDL_GetError());
    app->gpu_depth_fmt = SDL_GPU_TEXTUREFORMAT_INVALID;
    return false;
  }

  app->gpu_depth_w = (int)w;
  app->gpu_depth_h = (int)h;
  app->gpu_depth_fmt = fmt;
  gpu_stats_create(
      GPU_STAT_TEXTURE,
      gpu_stats_image_bytes(SGL_PF_DEPTH24_STENCIL8, (int)w, (int)h));
  return true;
}

static bool sg_acquire_command_buffer(App *app, const char *context) {
  if (app->gpu_cmd)
    return true;
  app->gpu_cmd = SDL_AcquireGPUCommandBuffer(app->gpu_device);
  if (!app->gpu_cmd) {
    SDL_Log("%s: SDL_AcquireGPUCommandBuffer failed: %s", context,
            SDL_GetError());
    return false;
  }
  return true;
}

static bool sg_acquire_swapchain_texture(App *app, const char *context) {
  if (app->gpu_swapchain_tex)
    return true;
  if (!sg_acquire_command_buffer(app, context))
    return false;
  Uint32 sw = 0, sh = 0;
  if (!SDL_AcquireGPUSwapchainTexture(app->gpu_cmd, app->window,
                                      &app->gpu_swapchain_tex, &sw, &sh)) {
    SDL_Log("%s: SDL_AcquireGPUSwapchainTexture failed: %s", context,
            SDL_GetError());
    app->gpu_swapchain_tex = NULL;
    return false;
  }
  if (app->gpu_swapchain_tex && sw > 0 && sh > 0) {
    (void)sg_ensure_depth_texture(app, sw, sh);
  }
  return app->gpu_swapchain_tex != NULL;
}

// --- per-resource backend objects ----------------------------------------

typedef struct SgBuffer {
  SDL_GPUBuffer *gpu;
  Uint32 bytes;
  SglBufferType type;
} SgBuffer;

typedef struct SgShader {
  SDL_GPUShader *vs;
  SDL_GPUShader *fs;
  // Compute pipeline (SDL_GPU collapses shader+pipeline for compute). Set
  // when the shader was created from a compute SPIR-V blob.
  SDL_GPUComputePipeline *compute_pip;
  ShaderReflection refl;
} SgShader;

typedef struct SgPipeline {
  SDL_GPUGraphicsPipeline *gpu;
  // Compute pipeline shadow — for compute, the SgShader already owns the
  // SDL_GPUComputePipeline, so the SgPipeline wrapping it just holds a
  // weak pointer and the reflection for binding resolution.
  SDL_GPUComputePipeline *compute_gpu;
  bool is_compute;
  ShaderReflection refl;
} SgPipeline;

typedef struct SgImage {
  SDL_GPUTexture *tex;
  SDL_GPUSampler *smp;
  int w, h;
  SglPixelFormat fmt;
  bool render_target;
  bool storage;
} SgImage;

// --- backend lifecycle ----------------------------------------------------

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
  g_app = app;
  return true;
}

static void sg_shutdown(App *app) {
  if (app->gpu_device) {
    sg_release_depth_texture(app);
    SDL_ReleaseWindowFromGPUDevice(app->gpu_device, app->window);
    SDL_DestroyGPUDevice(app->gpu_device);
    app->gpu_device = NULL;
  }
  g_app = NULL;
}

static void sg_begin_frame(App *app, int *out_w, int *out_h) {
  g_app = app;
  app->gpu_cmd = SDL_AcquireGPUCommandBuffer(app->gpu_device);
  if (!app->gpu_cmd) {
    SDL_Log("SDL_AcquireGPUCommandBuffer failed: %s", SDL_GetError());
    if (out_w)
      *out_w = 0;
    if (out_h)
      *out_h = 0;
    return;
  }
  Uint32 sw = 0, sh = 0;
  app->gpu_swapchain_tex = NULL;
  if (!SDL_AcquireGPUSwapchainTexture(app->gpu_cmd, app->window,
                                      &app->gpu_swapchain_tex, &sw, &sh)) {
    SDL_Log("SDL_AcquireGPUSwapchainTexture failed: %s", SDL_GetError());
  }
  if (app->gpu_swapchain_tex && sw > 0 && sh > 0) {
    if (!sg_ensure_depth_texture(app, sw, sh)) {
      SDL_Log("sg_begin_frame: depth texture unavailable; swapchain pass will "
              "be skipped");
    }
  }
  if (out_w)
    *out_w = (int)sw;
  if (out_h)
    *out_h = (int)sh;
}

static void sg_end_frame(App *app) {
  if (app->gpu_cmd && !SDL_SubmitGPUCommandBuffer(app->gpu_cmd)) {
    SDL_Log("SDL_SubmitGPUCommandBuffer failed: %s", SDL_GetError());
  }
  app->gpu_cmd = NULL;
  app->gpu_swapchain_tex = NULL;
}

static void sg_begin_pass(App *app, const PassBeginDesc *d) {
  if (!sg_acquire_command_buffer(app, "sg_begin_pass")) {
    g_render_pass = NULL;
    return;
  }
  SDL_GPULoadOp load_op =
      (d->load == SGL_LOAD_LOAD) ? SDL_GPU_LOADOP_LOAD : SDL_GPU_LOADOP_CLEAR;
  int nct = d->n_color_targets > 0 ? d->n_color_targets : 0;
  if (nct > SGL_MAX_COLOR_TARGETS)
    nct = SGL_MAX_COLOR_TARGETS;
  SDL_GPUColorTargetInfo targets[SGL_MAX_COLOR_TARGETS] = {0};
  if (d->n_color_targets == 1 && d->targets[0] == 0 && !d->depth_target) {
    // swapchain target (single)
    if (!sg_acquire_swapchain_texture(app, "sg_begin_pass")) {
      g_render_pass = NULL;
      return;
    }
    SDL_GPUTexture *tex = app->gpu_swapchain_tex;
    if (!tex) {
      g_render_pass = NULL;
      return;
    }
    targets[0].texture = tex;
    targets[0].clear_color = (SDL_FColor){d->clear[0][0], d->clear[0][1],
                                          d->clear[0][2], d->clear[0][3]};
    targets[0].load_op = load_op;
    targets[0].store_op = SDL_GPU_STOREOP_STORE;
    if (!app->gpu_depth_tex && app->last_w > 0 && app->last_h > 0) {
      (void)sg_ensure_depth_texture(app, (Uint32)app->last_w,
                                    (Uint32)app->last_h);
    }
    if (!app->gpu_depth_tex) {
      SDL_Log("sg_begin_pass: no depth texture for swapchain pass");
      g_render_pass = NULL;
      return;
    }
    SDL_GPUDepthStencilTargetInfo depth = {
        .texture = app->gpu_depth_tex,
        .clear_depth = 1.0f,
        .load_op = load_op,
        // STORE so a later swapchain pass with load = LOAD sees valid depth
        // (matches the vk / webgpu backends).
        .store_op = SDL_GPU_STOREOP_STORE,
        .stencil_load_op = SDL_GPU_LOADOP_DONT_CARE,
        .stencil_store_op = SDL_GPU_STOREOP_DONT_CARE,
    };
    g_render_pass = SDL_BeginGPURenderPass(app->gpu_cmd, targets, 1, &depth);
    return;
  }
  for (int i = 0; i < nct; ++i) {
    SgImage *im = (SgImage *)d->targets[i];
    if (!im || !im->tex) {
      g_render_pass = NULL;
      return;
    }
    targets[i].texture = im->tex;
    targets[i].clear_color = (SDL_FColor){d->clear[i][0], d->clear[i][1],
                                          d->clear[i][2], d->clear[i][3]};
    targets[i].load_op = load_op;
    targets[i].store_op = SDL_GPU_STOREOP_STORE;
  }
  SDL_GPUDepthStencilTargetInfo depth = {0};
  SDL_GPUDepthStencilTargetInfo *depth_ptr = NULL;
  if (d->depth_target) {
    SgImage *di = (SgImage *)d->depth_target;
    if (!di || !di->tex) {
      g_render_pass = NULL;
      return;
    }
    depth = (SDL_GPUDepthStencilTargetInfo){
        .texture = di->tex,
        .clear_depth = d->clear_depth,
        .load_op = load_op,
        .store_op = SDL_GPU_STOREOP_STORE,
        .stencil_load_op = SDL_GPU_LOADOP_DONT_CARE,
        .stencil_store_op = SDL_GPU_STOREOP_DONT_CARE,
    };
    depth_ptr = &depth;
  }
  g_render_pass = SDL_BeginGPURenderPass(app->gpu_cmd, nct > 0 ? targets : NULL,
                                         (Uint32)nct, depth_ptr);
}

static void sg_end_pass(App *app) {
  (void)app;
  if (g_render_pass) {
    SDL_EndGPURenderPass(g_render_pass);
    g_render_pass = NULL;
  }
  g_current_pip = NULL;
}

// --- resources ------------------------------------------------------------

// Upload helper: copies `bytes` bytes from `data` into `dst` GPU buffer.
// `cycle` should be false on first upload (make_buffer) and true on subsequent
// updates (update_buffer) to avoid in-flight resource conflicts.
static bool sg_upload_to_buffer(SDL_GPUBuffer *dst, const void *data,
                                size_t bytes, bool cycle) {
  SDL_GPUDevice *dev = g_app->gpu_device;
  SDL_GPUTransferBuffer *tbuf = SDL_CreateGPUTransferBuffer(
      dev, &(SDL_GPUTransferBufferCreateInfo){
               .usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
               .size = (Uint32)bytes,
           });
  if (!tbuf) {
    SDL_Log("sg_upload_to_buffer: tbuf: %s", SDL_GetError());
    return false;
  }
  gpu_stats_create(GPU_STAT_TRANSFER_BUFFER, bytes);

  void *map = SDL_MapGPUTransferBuffer(dev, tbuf, false);
  if (!map) {
    SDL_Log("sg_upload_to_buffer: map: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(dev, tbuf);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, bytes);
    return false;
  }
  memcpy(map, data, bytes);
  SDL_UnmapGPUTransferBuffer(dev, tbuf);

  SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(dev);
  if (!cmd) {
    SDL_Log("sg_upload_to_buffer: cmd: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(dev, tbuf);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, bytes);
    return false;
  }
  SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
  SDL_UploadToGPUBuffer(
      cp,
      &(SDL_GPUTransferBufferLocation){.transfer_buffer = tbuf, .offset = 0},
      &(SDL_GPUBufferRegion){.buffer = dst, .offset = 0, .size = (Uint32)bytes},
      cycle);
  SDL_EndGPUCopyPass(cp);
  SDL_SubmitGPUCommandBuffer(cmd);
  SDL_ReleaseGPUTransferBuffer(dev, tbuf);
  gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, bytes);
  return true;
}

// Upload helper: copies `bytes` bytes from `data` into `dst` GPU texture (w x
// h). `cycle` should be false on first upload (make_image) and true on updates.
static bool sg_upload_to_image(SDL_GPUTexture *dst, int w, int h,
                               const void *data, size_t bytes, bool cycle) {
  SDL_GPUDevice *dev = g_app->gpu_device;
  SDL_GPUTransferBuffer *tbuf = SDL_CreateGPUTransferBuffer(
      dev, &(SDL_GPUTransferBufferCreateInfo){
               .usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
               .size = (Uint32)bytes,
           });
  if (!tbuf) {
    SDL_Log("sg_upload_to_image: tbuf: %s", SDL_GetError());
    return false;
  }
  gpu_stats_create(GPU_STAT_TRANSFER_BUFFER, bytes);
  void *map = SDL_MapGPUTransferBuffer(dev, tbuf, false);
  if (!map) {
    SDL_Log("sg_upload_to_image: map: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(dev, tbuf);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, bytes);
    return false;
  }
  memcpy(map, data, bytes);
  SDL_UnmapGPUTransferBuffer(dev, tbuf);

  SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(dev);
  if (!cmd) {
    SDL_Log("sg_upload_to_image: cmd: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(dev, tbuf);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, bytes);
    return false;
  }
  SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
  SDL_UploadToGPUTexture(
      cp, &(SDL_GPUTextureTransferInfo){.transfer_buffer = tbuf, .offset = 0},
      &(SDL_GPUTextureRegion){
          .texture = dst,
          .w = (Uint32)w,
          .h = (Uint32)h,
          .d = 1,
      },
      cycle);
  SDL_EndGPUCopyPass(cp);
  SDL_SubmitGPUCommandBuffer(cmd);
  SDL_ReleaseGPUTransferBuffer(dev, tbuf);
  gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, bytes);
  return true;
}

static BackendBuffer sg_make_buffer(SglBufferType type, const void *data,
                                    size_t bytes) {
  if (!g_app || !g_app->gpu_device) {
    SDL_Log("sg_make_buffer: no GPU device");
    return 0;
  }
  SgBuffer *b = (SgBuffer *)calloc(1, sizeof(SgBuffer));
  if (!b)
    return 0;
  b->bytes = (Uint32)bytes;
  b->type = type;
  SDL_GPUBufferUsageFlags usage;
  switch (type) {
  case SGL_BUFFER_VERTEX:
    usage = SDL_GPU_BUFFERUSAGE_VERTEX;
    break;
  case SGL_BUFFER_INDEX:
    usage = SDL_GPU_BUFFERUSAGE_INDEX;
    break;
  case SGL_BUFFER_STORAGE:
    // Storage buffer for compute output + graphics vertex input.
    // The compute pass writes via COMPUTE_STORAGE_*; the same buffer
    // is rebound as a VBO in the subsequent render pass.
    usage = SDL_GPU_BUFFERUSAGE_VERTEX |
            SDL_GPU_BUFFERUSAGE_COMPUTE_STORAGE_READ |
            SDL_GPU_BUFFERUSAGE_COMPUTE_STORAGE_WRITE |
            SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ;
    break;
  default:
    SDL_Log("sg_make_buffer: unsupported buffer type %d", (int)type);
    free(b);
    return 0;
  }
  b->gpu = SDL_CreateGPUBuffer(g_app->gpu_device, &(SDL_GPUBufferCreateInfo){
                                                      .usage = usage,
                                                      .size = (Uint32)bytes,
                                                  });
  if (!b->gpu) {
    SDL_Log("SDL_CreateGPUBuffer failed: %s", SDL_GetError());
    free(b);
    return 0;
  }
  gpu_stats_create(GPU_STAT_BUFFER, b->bytes);
  if (data && bytes > 0) {
    if (!sg_upload_to_buffer(b->gpu, data, bytes, false)) {
      SDL_ReleaseGPUBuffer(g_app->gpu_device, b->gpu);
      gpu_stats_destroy(GPU_STAT_BUFFER, b->bytes);
      free(b);
      return 0;
    }
  }
  return (uintptr_t)b;
}

static void sg_destroy_buffer(BackendBuffer h) {
  SgBuffer *b = (SgBuffer *)h;
  if (!b)
    return;
  if (g_app && g_app->gpu_device && b->gpu) {
    SDL_ReleaseGPUBuffer(g_app->gpu_device, b->gpu);
    gpu_stats_destroy(GPU_STAT_BUFFER, b->bytes);
  }
  free(b);
}

static Uint32 refl_uniform_count(const ShaderReflection *refl,
                                 SglShaderStage stage) {
  int max_slot = -1;
  if (!refl)
    return 0;
  for (int i = 0; i < refl->ub_count; ++i) {
    if (refl->ubs[i].stage == stage && refl->ubs[i].slot > max_slot)
      max_slot = refl->ubs[i].slot;
  }
  return (Uint32)(max_slot + 1);
}

static Uint32 refl_sampler_count(const ShaderReflection *refl,
                                 SglShaderStage stage) {
  int max_slot = -1;
  if (!refl)
    return 0;
  for (int i = 0; i < refl->tex_count; ++i) {
    if (refl->texs[i].stage == stage && refl->texs[i].smp_slot > max_slot)
      max_slot = refl->texs[i].smp_slot;
  }
  return (Uint32)(max_slot + 1);
}

static Uint32 refl_storage_buf_count(const ShaderReflection *refl,
                                     SglShaderStage stage, bool readonly) {
  int max_slot = -1;
  if (!refl)
    return 0;
  for (int i = 0; i < refl->storage_buf_count; ++i) {
    if (refl->storage_bufs[i].stage == stage &&
        refl->storage_bufs[i].readonly == readonly &&
        refl->storage_bufs[i].slot > max_slot)
      max_slot = refl->storage_bufs[i].slot;
  }
  return (Uint32)(max_slot + 1);
}

static Uint32 refl_storage_tex_count(const ShaderReflection *refl,
                                     SglShaderStage stage, bool readonly) {
  int max_slot = -1;
  if (!refl)
    return 0;
  for (int i = 0; i < refl->storage_tex_count; ++i) {
    if (refl->storage_texs[i].stage == stage &&
        refl->storage_texs[i].readonly == readonly &&
        refl->storage_texs[i].slot > max_slot)
      max_slot = refl->storage_texs[i].slot;
  }
  return (Uint32)(max_slot + 1);
}

static BackendImage sg_make_image(const ImageDesc *d) {
  if (!g_app || !g_app->gpu_device) {
    SDL_Log("sg_make_image: no GPU device");
    return 0;
  }
  SgImage *im = (SgImage *)calloc(1, sizeof(SgImage));
  if (!im)
    return 0;
  im->w = d->w;
  im->h = d->h;
  im->fmt = d->fmt;
  im->render_target = d->render_target;
  im->storage = d->storage;

  SDL_GPUTextureFormat tfmt = sgl_to_sdl_texture_format(d->fmt);
  Uint32 usage = SDL_GPU_TEXTUREUSAGE_SAMPLER;
  if (d->render_target) {
    usage |= sgl_is_depth_format(d->fmt)
                 ? SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET
                 : SDL_GPU_TEXTUREUSAGE_COLOR_TARGET;
  }
  if (d->storage) {
    usage |= SDL_GPU_TEXTUREUSAGE_COMPUTE_STORAGE_WRITE;
  }
  im->tex = SDL_CreateGPUTexture(g_app->gpu_device,
                                 &(SDL_GPUTextureCreateInfo){
                                     .type = SDL_GPU_TEXTURETYPE_2D,
                                     .format = tfmt,
                                     .usage = usage,
                                     .width = (Uint32)d->w,
                                     .height = (Uint32)d->h,
                                     .layer_count_or_depth = 1,
                                     .num_levels = 1,
                                 });
  if (!im->tex) {
    SDL_Log("sg_make_image: SDL_CreateGPUTexture failed: %s", SDL_GetError());
    free(im);
    return 0;
  }
  gpu_stats_create(GPU_STAT_TEXTURE, gpu_stats_image_bytes(d->fmt, d->w, d->h));

  if (!d->render_target && d->data && d->data_bytes > 0) {
    if (!sg_upload_to_image(im->tex, d->w, d->h, d->data, d->data_bytes,
                            false)) {
      SDL_ReleaseGPUTexture(g_app->gpu_device, im->tex);
      gpu_stats_destroy(GPU_STAT_TEXTURE,
                        gpu_stats_image_bytes(d->fmt, d->w, d->h));
      free(im);
      return 0;
    }
  }

  SDL_GPUFilter sf = (d->filter == SGL_FILTER_NEAREST) ? SDL_GPU_FILTER_NEAREST
                                                       : SDL_GPU_FILTER_LINEAR;
  SDL_GPUSamplerAddressMode sw = (d->wrap == SGL_WRAP_CLAMP)
                                     ? SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE
                                     : SDL_GPU_SAMPLERADDRESSMODE_REPEAT;
  im->smp = SDL_CreateGPUSampler(g_app->gpu_device, &(SDL_GPUSamplerCreateInfo){
                                                        .min_filter = sf,
                                                        .mag_filter = sf,
                                                        .address_mode_u = sw,
                                                        .address_mode_v = sw,
                                                        .address_mode_w = sw,
                                                    });
  if (!im->smp) {
    SDL_Log("sg_make_image: SDL_CreateGPUSampler failed: %s", SDL_GetError());
    SDL_ReleaseGPUTexture(g_app->gpu_device, im->tex);
    gpu_stats_destroy(GPU_STAT_TEXTURE,
                      gpu_stats_image_bytes(d->fmt, d->w, d->h));
    free(im);
    return 0;
  }
  gpu_stats_create(GPU_STAT_SAMPLER, 0);
  return (uintptr_t)im;
}

static BackendShader sg_make_shader(const ShaderDesc *d) {
  if (!g_app || !g_app->gpu_device) {
    SDL_Log("sg_make_shader: no GPU device");
    return 0;
  }
  SgShader *s = (SgShader *)calloc(1, sizeof(SgShader));
  if (!s)
    return 0;
  if (d->refl)
    s->refl = *d->refl;
  // Compute path collapses shader+pipeline into SDL_GPUComputePipeline.
  if (d->cs_spirv) {
    s->compute_pip = SDL_CreateGPUComputePipeline(
        g_app->gpu_device,
        &(SDL_GPUComputePipelineCreateInfo){
            .code = (const Uint8 *)d->cs_spirv,
            .code_size = d->cs_bytes,
            .entrypoint = "main",
            .format = SDL_GPU_SHADERFORMAT_SPIRV,
            .num_samplers = refl_sampler_count(&s->refl, SGL_STAGE_COMPUTE),
            .num_readonly_storage_textures =
                refl_storage_tex_count(&s->refl, SGL_STAGE_COMPUTE, true),
            .num_readonly_storage_buffers =
                refl_storage_buf_count(&s->refl, SGL_STAGE_COMPUTE, true),
            .num_readwrite_storage_textures =
                refl_storage_tex_count(&s->refl, SGL_STAGE_COMPUTE, false),
            .num_readwrite_storage_buffers =
                refl_storage_buf_count(&s->refl, SGL_STAGE_COMPUTE, false),
            .num_uniform_buffers =
                refl_uniform_count(&s->refl, SGL_STAGE_COMPUTE),
            .threadcount_x = (Uint32)s->refl.workgroup[0],
            .threadcount_y = (Uint32)s->refl.workgroup[1],
            .threadcount_z = (Uint32)s->refl.workgroup[2],
        });
    if (!s->compute_pip) {
      SDL_Log("sg_make_shader: SDL_CreateGPUComputePipeline failed: %s",
              SDL_GetError());
      free(s);
      return 0;
    }
    gpu_stats_create(GPU_STAT_PIPELINE, 0);
    return (uintptr_t)s;
  }
  // Slang's SPIR-V emitter renames the entry-point function to "main".
  // Both vs and fs blobs each have a single "main" entry point.
  s->vs = SDL_CreateGPUShader(
      g_app->gpu_device,
      &(SDL_GPUShaderCreateInfo){
          .code = (const Uint8 *)d->vs_spirv,
          .code_size = d->vs_bytes,
          .entrypoint = "main",
          .format = SDL_GPU_SHADERFORMAT_SPIRV,
          .stage = SDL_GPU_SHADERSTAGE_VERTEX,
          .num_uniform_buffers = refl_uniform_count(d->refl, SGL_STAGE_VERTEX),
          .num_storage_buffers =
              refl_storage_buf_count(d->refl, SGL_STAGE_VERTEX, true),
          .num_storage_textures =
              refl_storage_tex_count(d->refl, SGL_STAGE_VERTEX, true),
          .num_samplers = refl_sampler_count(d->refl, SGL_STAGE_VERTEX),
      });
  s->fs = SDL_CreateGPUShader(
      g_app->gpu_device,
      &(SDL_GPUShaderCreateInfo){
          .code = (const Uint8 *)d->fs_spirv,
          .code_size = d->fs_bytes,
          .entrypoint = "main",
          .format = SDL_GPU_SHADERFORMAT_SPIRV,
          .stage = SDL_GPU_SHADERSTAGE_FRAGMENT,
          .num_uniform_buffers =
              refl_uniform_count(d->refl, SGL_STAGE_FRAGMENT),
          .num_storage_buffers =
              refl_storage_buf_count(d->refl, SGL_STAGE_FRAGMENT, true),
          .num_storage_textures =
              refl_storage_tex_count(d->refl, SGL_STAGE_FRAGMENT, true),
          .num_samplers = refl_sampler_count(d->refl, SGL_STAGE_FRAGMENT),
      });
  if (!s->vs || !s->fs) {
    SDL_Log("sg_make_shader: shader create failed (vs=%p fs=%p): %s",
            (void *)s->vs, (void *)s->fs, SDL_GetError());
    if (s->vs)
      SDL_ReleaseGPUShader(g_app->gpu_device, s->vs);
    if (s->fs)
      SDL_ReleaseGPUShader(g_app->gpu_device, s->fs);
    free(s);
    return 0;
  }
  gpu_stats_create(GPU_STAT_SHADER, 0);
  gpu_stats_create(GPU_STAT_SHADER, 0);
  return (uintptr_t)s;
}

static void sg_destroy_shader(BackendShader h) {
  SgShader *s = (SgShader *)h;
  if (!s)
    return;
  if (g_app && g_app->gpu_device) {
    if (s->vs)
      SDL_ReleaseGPUShader(g_app->gpu_device, s->vs);
    if (s->vs)
      gpu_stats_destroy(GPU_STAT_SHADER, 0);
    if (s->fs)
      SDL_ReleaseGPUShader(g_app->gpu_device, s->fs);
    if (s->fs)
      gpu_stats_destroy(GPU_STAT_SHADER, 0);
    if (s->compute_pip)
      SDL_ReleaseGPUComputePipeline(g_app->gpu_device, s->compute_pip);
    if (s->compute_pip)
      gpu_stats_destroy(GPU_STAT_PIPELINE, 0);
  }
  free(s);
}

static SDL_GPUColorTargetBlendState to_sdl_blend(SglBlend b) {
  SDL_GPUColorTargetBlendState bs = {
      .src_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE,
      .dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ZERO,
      .color_blend_op = SDL_GPU_BLENDOP_ADD,
      .src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE,
      .dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ZERO,
      .alpha_blend_op = SDL_GPU_BLENDOP_ADD,
      .color_write_mask = SDL_GPU_COLORCOMPONENT_R | SDL_GPU_COLORCOMPONENT_G |
                          SDL_GPU_COLORCOMPONENT_B | SDL_GPU_COLORCOMPONENT_A,
      .enable_color_write_mask = true,
  };

  switch (b) {
  case SGL_BLEND_ALPHA:
    bs.enable_blend = true;
    bs.src_color_blendfactor = SDL_GPU_BLENDFACTOR_SRC_ALPHA;
    bs.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
    bs.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
    bs.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA;
    break;
  case SGL_BLEND_ADDITIVE:
    bs.enable_blend = true;
    bs.src_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
    bs.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
    bs.src_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
    bs.dst_alpha_blendfactor = SDL_GPU_BLENDFACTOR_ONE;
    break;
  case SGL_BLEND_MULTIPLY:
    bs.enable_blend = true;
    bs.src_color_blendfactor = SDL_GPU_BLENDFACTOR_DST_COLOR;
    bs.dst_color_blendfactor = SDL_GPU_BLENDFACTOR_ZERO;
    break;
  default:
    break;
  }
  return bs;
}

static BackendPipeline sg_make_pipeline(const PipelineDesc *d) {
  if (!g_app || !g_app->gpu_device) {
    SDL_Log("sg_make_pipeline: no GPU device");
    return 0;
  }
  SgShader *sh = (SgShader *)d->shader;
  if (!sh) {
    SDL_Log("sg_make_pipeline: null shader");
    return 0;
  }
  if (d->is_compute) {
    if (!sh->compute_pip) {
      SDL_Log("sg_make_pipeline: shader is not compute");
      return 0;
    }
    SgPipeline *p = (SgPipeline *)calloc(1, sizeof(SgPipeline));
    if (!p)
      return 0;
    p->compute_gpu = sh->compute_pip;
    p->is_compute = true;
    if (d->refl)
      p->refl = *d->refl;
    return (uintptr_t)p;
  }
  if (!sh->vs || !sh->fs) {
    SDL_Log("sg_make_pipeline: invalid shader");
    return 0;
  }
  SgPipeline *p = (SgPipeline *)calloc(1, sizeof(SgPipeline));
  if (!p)
    return 0;
  if (d->refl)
    p->refl = *d->refl;

  SDL_GPUVertexAttribute attrs[SGL_MAX_ATTRS];
  int attr_count = d->refl ? d->refl->attr_count : 0;
  for (int i = 0; i < attr_count; ++i) {
    int buffer_index = d->refl->attrs[i].buffer_index;
    if (buffer_index < 0 || buffer_index >= SGL_MAX_VERTEX_BUFFERS)
      buffer_index = 0;
    SDL_GPUVertexElementFormat fmt;
    switch (d->refl->attrs[i].comp_count) {
    case 1:
      fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT;
      break;
    case 2:
      fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2;
      break;
    case 3:
      fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT3;
      break;
    default:
      fmt = SDL_GPU_VERTEXELEMENTFORMAT_FLOAT4;
    }
    attrs[i] = (SDL_GPUVertexAttribute){
        .location = (Uint32)d->refl->attrs[i].slot,
        .buffer_slot = (Uint32)buffer_index,
        .format = fmt,
        .offset = (Uint32)(d->refl->attrs[i].offset_floats * sizeof(float)),
    };
  }
  SDL_GPUVertexBufferDescription vbds[SGL_MAX_VERTEX_BUFFERS] = {0};
  int buffer_count = d->refl ? d->refl->buffer_count : 0;
  if (buffer_count <= 0 && attr_count > 0)
    buffer_count = 1;
  if (buffer_count > SGL_MAX_VERTEX_BUFFERS)
    buffer_count = SGL_MAX_VERTEX_BUFFERS;
  for (int i = 0; i < buffer_count; ++i) {
    vbds[i] = (SDL_GPUVertexBufferDescription){
        .slot = (Uint32)i,
        .pitch = (Uint32)((d->refl ? d->refl->buffer_stride_floats[i] : 0) *
                          sizeof(float)),
        .input_rate = (i == 0) ? SDL_GPU_VERTEXINPUTRATE_VERTEX
                               : SDL_GPU_VERTEXINPUTRATE_INSTANCE,
        .instance_step_rate = 0,
    };
  }
  int nct = d->n_color_targets > 0 ? d->n_color_targets : 0;
  if (nct > SGL_MAX_COLOR_TARGETS)
    nct = SGL_MAX_COLOR_TARGETS;
  SDL_GPUColorTargetDescription ctd[SGL_MAX_COLOR_TARGETS] = {0};
  SDL_GPUColorTargetBlendState blend = to_sdl_blend(d->blend);
  for (int i = 0; i < nct; ++i) {
    SDL_GPUTextureFormat tf = sgl_to_sdl_texture_format(d->color_fmts[i]);
    ctd[i].format = tf;
    ctd[i].blend_state = blend;
  }

  SDL_GPUPrimitiveType prim;
  switch (d->primitive) {
  case SGL_PRIM_LINES:
    prim = SDL_GPU_PRIMITIVETYPE_LINELIST;
    break;
  case SGL_PRIM_LINE_STRIP:
    prim = SDL_GPU_PRIMITIVETYPE_LINESTRIP;
    break;
  case SGL_PRIM_POINTS:
    prim = SDL_GPU_PRIMITIVETYPE_POINTLIST;
    break;
  case SGL_PRIM_TRIANGLE_STRIP:
    prim = SDL_GPU_PRIMITIVETYPE_TRIANGLESTRIP;
    break;
  case SGL_PRIM_TRIANGLES:
  default:
    prim = SDL_GPU_PRIMITIVETYPE_TRIANGLELIST;
    break;
  }

  SDL_GPUCullMode cull = (d->cull == SGL_CULL_BACK)    ? SDL_GPU_CULLMODE_BACK
                         : (d->cull == SGL_CULL_FRONT) ? SDL_GPU_CULLMODE_FRONT
                                                       : SDL_GPU_CULLMODE_NONE;

  SDL_GPUTextureFormat depth_fmt = SDL_GPU_TEXTUREFORMAT_INVALID;
  if (d->has_depth) {
    if (d->depth_fmt == SGL_PF_DEPTH24_STENCIL8) {
      depth_fmt = g_app->gpu_depth_fmt;
    }
    if (depth_fmt == SDL_GPU_TEXTUREFORMAT_INVALID &&
        d->depth_fmt != SGL_PF_DEPTH24_STENCIL8) {
      depth_fmt = sgl_to_sdl_texture_format(d->depth_fmt);
    }
    if (depth_fmt == SDL_GPU_TEXTUREFORMAT_INVALID) {
      depth_fmt = sg_choose_depth_format(g_app->gpu_device);
    }
    if (depth_fmt == SDL_GPU_TEXTUREFORMAT_INVALID) {
      SDL_Log("sg_make_pipeline: no supported depth format");
      free(p);
      return 0;
    }
  }

  p->gpu = SDL_CreateGPUGraphicsPipeline(
      g_app->gpu_device,
      &(SDL_GPUGraphicsPipelineCreateInfo){
          .vertex_shader = sh->vs,
          .fragment_shader = sh->fs,
          .vertex_input_state =
              {
                  .vertex_buffer_descriptions = vbds,
                  .num_vertex_buffers = (Uint32)buffer_count,
                  .vertex_attributes = attrs,
                  .num_vertex_attributes = (Uint32)attr_count,
              },
          .primitive_type = prim,
          .rasterizer_state =
              {
                  .fill_mode = SDL_GPU_FILLMODE_FILL,
                  .cull_mode = cull,
                  // Match the runtime's D3D-style LH examples.
                  .front_face = SDL_GPU_FRONTFACE_CLOCKWISE,
              },
          .multisample_state =
              {
                  .sample_count = SDL_GPU_SAMPLECOUNT_1,
              },
          .depth_stencil_state =
              {
                  .compare_op = (d->has_depth && d->depth_test)
                                    ? SDL_GPU_COMPAREOP_LESS_OR_EQUAL
                                    : SDL_GPU_COMPAREOP_ALWAYS,
                  .enable_depth_test = d->has_depth && d->depth_test,
                  .enable_depth_write = d->has_depth && d->depth_write,
              },
          .target_info =
              {
                  .color_target_descriptions = nct > 0 ? ctd : NULL,
                  .num_color_targets = (Uint32)nct,
                  .depth_stencil_format = depth_fmt,
                  .has_depth_stencil_target = d->has_depth,
              },
      });
  if (!p->gpu) {
    SDL_Log("sg_make_pipeline: SDL_CreateGPUGraphicsPipeline failed: %s",
            SDL_GetError());
    free(p);
    return 0;
  }
  gpu_stats_create(GPU_STAT_PIPELINE, 0);
  return (uintptr_t)p;
}

static void sg_destroy_pipeline(BackendPipeline h) {
  SgPipeline *p = (SgPipeline *)h;
  if (!p)
    return;
  if (g_app && g_app->gpu_device && p->gpu) {
    SDL_ReleaseGPUGraphicsPipeline(g_app->gpu_device, p->gpu);
    gpu_stats_destroy(GPU_STAT_PIPELINE, 0);
  }
  // p->compute_gpu is owned by SgShader, do not release here.
  free(p);
}

static void sg_update_buffer(BackendBuffer h, const void *data, size_t bytes) {
  if (!h || !data || bytes == 0)
    return;
  SgBuffer *b = (SgBuffer *)h;
  sg_upload_to_buffer(b->gpu, data, bytes, /*cycle=*/true);
}

static void sg_update_image(BackendImage h, const void *data, size_t bytes) {
  if (!h || !data || bytes == 0)
    return;
  SgImage *si = (SgImage *)h;
  sg_upload_to_image(si->tex, si->w, si->h, data, bytes, /*cycle=*/true);
}

static void sg_destroy_image(BackendImage h) {
  SgImage *im = (SgImage *)h;
  if (!im)
    return;
  if (g_app && g_app->gpu_device) {
    if (im->tex)
      SDL_ReleaseGPUTexture(g_app->gpu_device, im->tex);
    if (im->tex)
      gpu_stats_destroy(GPU_STAT_TEXTURE,
                        gpu_stats_image_bytes(im->fmt, im->w, im->h));
    if (im->smp)
      SDL_ReleaseGPUSampler(g_app->gpu_device, im->smp);
    if (im->smp)
      gpu_stats_destroy(GPU_STAT_SAMPLER, 0);
  }
  free(im);
}

// --- draw -----------------------------------------------------------------

static void sg_apply_pipeline(BackendPipeline h) {
  g_current_pip = (SgPipeline *)h;
  if (g_current_pip && g_current_pip->gpu && g_render_pass) {
    SDL_BindGPUGraphicsPipeline(g_render_pass, g_current_pip->gpu);
  }
}

static void sg_apply_bindings(const BindingsDesc *b) {
  if (!g_render_pass)
    return;
  if (b->vbuf) {
    SgBuffer *vb = (SgBuffer *)b->vbuf;
    if (vb && vb->gpu) {
      SDL_BindGPUVertexBuffers(
          g_render_pass, 0,
          &(SDL_GPUBufferBinding){.buffer = vb->gpu, .offset = 0}, 1);
    }
  }
  if (b->instance_vbuf) {
    SgBuffer *vb = (SgBuffer *)b->instance_vbuf;
    if (vb && vb->gpu) {
      SDL_BindGPUVertexBuffers(
          g_render_pass, 1,
          &(SDL_GPUBufferBinding){.buffer = vb->gpu, .offset = 0}, 1);
    }
  }
  if (b->ibuf) {
    SgBuffer *ib = (SgBuffer *)b->ibuf;
    if (ib && ib->gpu) {
      SDL_BindGPUIndexBuffer(
          g_render_pass,
          &(SDL_GPUBufferBinding){.buffer = ib->gpu, .offset = 0},
          SDL_GPU_INDEXELEMENTSIZE_32BIT);
      g_last_indexed = true;
    } else {
      g_last_indexed = false;
    }
  } else {
    g_last_indexed = false;
  }
  // Fragment-stage texture+sampler binding: resolve name->slot via reflection,
  // then issue a single SDL_BindGPUFragmentSamplers covering [0..max_slot].
  if (b->texture_count > 0 && b->refl) {
    SDL_GPUTextureSamplerBinding tsb[8] = {0};
    int max_slot = -1;
    for (int i = 0; i < b->texture_count; ++i) {
      if (!b->textures[i].name)
        continue;
      for (int j = 0; j < b->refl->tex_count; ++j) {
        if (strcmp(b->refl->texs[j].name, b->textures[i].name) != 0)
          continue;
        SgImage *im = (SgImage *)b->textures[i].image;
        if (!im || !im->tex || !im->smp)
          break;
        int slot = b->refl->texs[j].smp_slot;
        if (slot < 0 || slot >= 8)
          break;
        tsb[slot] = (SDL_GPUTextureSamplerBinding){
            .texture = im->tex,
            .sampler = im->smp,
        };
        if (slot > max_slot)
          max_slot = slot;
        break;
      }
    }
    if (max_slot >= 0) {
      SDL_BindGPUFragmentSamplers(g_render_pass, 0, tsb,
                                  (Uint32)(max_slot + 1));
    }
  }
}

static void sg_apply_uniforms(SglShaderStage stage, int slot, const void *d,
                              size_t b) {
  if (!g_app || !g_app->gpu_cmd)
    return;
  if (stage == SGL_STAGE_FRAGMENT) {
    SDL_PushGPUFragmentUniformData(g_app->gpu_cmd, (Uint32)slot, d, (Uint32)b);
  } else if (stage == SGL_STAGE_COMPUTE) {
    SDL_PushGPUComputeUniformData(g_app->gpu_cmd, (Uint32)slot, d, (Uint32)b);
  } else {
    SDL_PushGPUVertexUniformData(g_app->gpu_cmd, (Uint32)slot, d, (Uint32)b);
  }
}

static void sg_draw(int base, int count, int instance_count) {
  if (!g_render_pass)
    return;
  Uint32 instances = (Uint32)(instance_count > 0 ? instance_count : 1);
  if (g_last_indexed) {
    SDL_DrawGPUIndexedPrimitives(g_render_pass, (Uint32)count, instances,
                                 (Uint32)base, 0, 0);
  } else {
    SDL_DrawGPUPrimitives(g_render_pass, (Uint32)count, instances, (Uint32)base,
                          0);
  }
}

static void sg_set_scissor(int x, int y, int w, int h) {
  if (!g_render_pass)
    return;
  SDL_SetGPUScissor(g_render_pass, &(SDL_Rect){.x = x, .y = y, .w = w, .h = h});
}

static void sg_dispatch(App *app, const ComputeDispatchDesc *d) {
  if (!d || !d->pipeline || !d->refl)
    return;
  if (!app->gpu_cmd) {
    SDL_Log("sg_dispatch: no command buffer (called outside of frame?)");
    return;
  }
  SgPipeline *p = (SgPipeline *)d->pipeline;
  if (!p->is_compute || !p->compute_gpu) {
    SDL_Log("sg_dispatch: not a compute pipeline");
    return;
  }
  // Resolve storage buffers into ordered RW / RO arrays per the SDL_GPU
  // layout (set 1 = RW, set 0 = RO). The slot number from reflection is
  // the binding within its set; the current compute binding normally uses
  // one of each.
  SDL_GPUStorageBufferReadWriteBinding rw[SGL_MAX_STORAGE_BUFS] = {0};
  SDL_GPUBuffer *ro[SGL_MAX_STORAGE_BUFS] = {0};
  int n_rw = 0, n_ro = 0;
  for (int i = 0; i < d->n_storage_bufs; ++i) {
    SgBuffer *buf = (SgBuffer *)d->storage_bufs[i].buf;
    if (!buf || !buf->gpu || !d->storage_bufs[i].name)
      continue;
    for (int k = 0; k < d->refl->storage_buf_count; ++k) {
      if (strcmp(d->refl->storage_bufs[k].name, d->storage_bufs[i].name) != 0)
        continue;
      if (d->refl->storage_bufs[k].readonly) {
        int slot = d->refl->storage_bufs[k].slot;
        if (slot >= 0 && slot < SGL_MAX_STORAGE_BUFS) {
          ro[slot] = buf->gpu;
          if (slot + 1 > n_ro)
            n_ro = slot + 1;
        }
      } else {
        int slot = d->refl->storage_bufs[k].slot;
        if (slot >= 0 && slot < SGL_MAX_STORAGE_BUFS) {
          rw[slot].buffer = buf->gpu;
          rw[slot].cycle = true; // discard previous content
          if (slot + 1 > n_rw)
            n_rw = slot + 1;
        }
      }
      break;
    }
  }
  SDL_GPUStorageTextureReadWriteBinding rw_tex[SGL_MAX_STORAGE_TEXTURES] = {0};
  SDL_GPUTexture *ro_tex[SGL_MAX_STORAGE_TEXTURES] = {0};
  int n_rw_tex = 0, n_ro_tex = 0;
  for (int i = 0; i < d->n_storage_textures; ++i) {
    SgImage *img = (SgImage *)d->storage_textures[i].image;
    if (!img || !img->tex || !d->storage_textures[i].name)
      continue;
    for (int k = 0; k < d->refl->storage_tex_count; ++k) {
      if (strcmp(d->refl->storage_texs[k].name, d->storage_textures[i].name) !=
          0)
        continue;
      int slot = d->refl->storage_texs[k].slot;
      if (slot < 0 || slot >= SGL_MAX_STORAGE_TEXTURES)
        break;
      if (d->refl->storage_texs[k].readonly) {
        ro_tex[slot] = img->tex;
        if (slot + 1 > n_ro_tex)
          n_ro_tex = slot + 1;
      } else {
        rw_tex[slot].texture = img->tex;
        rw_tex[slot].mip_level = 0;
        rw_tex[slot].layer = 0;
        rw_tex[slot].cycle = true;
        if (slot + 1 > n_rw_tex)
          n_rw_tex = slot + 1;
      }
      break;
    }
  }
  SDL_GPUComputePass *cp = SDL_BeginGPUComputePass(
      app->gpu_cmd, rw_tex, (Uint32)n_rw_tex, rw, (Uint32)n_rw);
  if (!cp) {
    SDL_Log("sg_dispatch: SDL_BeginGPUComputePass failed: %s", SDL_GetError());
    return;
  }
  SDL_BindGPUComputePipeline(cp, p->compute_gpu);
  if (d->texture_count > 0) {
    SDL_GPUTextureSamplerBinding tsb[SGL_MAX_TEXTURES] = {0};
    int max_slot = -1;
    for (int i = 0; i < d->texture_count; ++i) {
      SgImage *img = (SgImage *)d->textures[i].image;
      if (!img || !img->tex || !img->smp || !d->textures[i].name)
        continue;
      for (int k = 0; k < d->refl->tex_count; ++k) {
        if (strcmp(d->refl->texs[k].name, d->textures[i].name) != 0)
          continue;
        int slot = d->refl->texs[k].smp_slot;
        if (slot >= 0 && slot < SGL_MAX_TEXTURES) {
          tsb[slot].texture = img->tex;
          tsb[slot].sampler = img->smp;
          if (slot > max_slot)
            max_slot = slot;
        }
        break;
      }
    }
    if (max_slot >= 0) {
      SDL_BindGPUComputeSamplers(cp, 0, tsb, (Uint32)(max_slot + 1));
    }
  }
  if (n_ro_tex > 0) {
    SDL_BindGPUComputeStorageTextures(cp, 0, ro_tex, (Uint32)n_ro_tex);
  }
  if (n_ro > 0) {
    SDL_BindGPUComputeStorageBuffers(cp, 0, ro, (Uint32)n_ro);
  }
  for (int i = 0; i < d->uniform_count; ++i) {
    if (d->uniforms[i].slot >= 0 && d->uniforms[i].data &&
        d->uniforms[i].bytes > 0) {
      SDL_PushGPUComputeUniformData(app->gpu_cmd, (Uint32)d->uniforms[i].slot,
                                    d->uniforms[i].data,
                                    (Uint32)d->uniforms[i].bytes);
    }
  }
  SDL_DispatchGPUCompute(cp, (Uint32)d->groups_x, (Uint32)d->groups_y,
                         (Uint32)d->groups_z);
  SDL_EndGPUComputePass(cp);
}

static int sg_readback_src_bpp(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_RGBA8:
  case SGL_PF_BGRA8:
    return 4;
  case SGL_PF_R8:
    return 1;
  default:
    return 0;
  }
}

static void sg_convert_readback_to_rgba8(SglPixelFormat fmt, const uint8_t *src,
                                         uint8_t *dst, int w, int h) {
  size_t pixels = (size_t)w * (size_t)h;
  if (fmt == SGL_PF_RGBA8) {
    memcpy(dst, src, pixels * 4);
    return;
  }
  if (fmt == SGL_PF_BGRA8) {
    for (size_t i = 0; i < pixels; ++i) {
      dst[i * 4 + 0] = src[i * 4 + 2];
      dst[i * 4 + 1] = src[i * 4 + 1];
      dst[i * 4 + 2] = src[i * 4 + 0];
      dst[i * 4 + 3] = src[i * 4 + 3];
    }
    return;
  }
  if (fmt == SGL_PF_R8) {
    for (size_t i = 0; i < pixels; ++i) {
      uint8_t v = src[i];
      dst[i * 4 + 0] = v;
      dst[i * 4 + 1] = v;
      dst[i * 4 + 2] = v;
      dst[i * 4 + 3] = 255;
    }
  }
}

static bool sg_submit_pending_frame_commands(App *app) {
  if (!app->gpu_cmd)
    return true;
  if (!SDL_SubmitGPUCommandBuffer(app->gpu_cmd)) {
    SDL_Log("sg_readback_image: SDL_SubmitGPUCommandBuffer failed: %s",
            SDL_GetError());
    app->gpu_cmd = NULL;
    app->gpu_swapchain_tex = NULL;
    return false;
  }
  app->gpu_swapchain_tex = NULL;
  app->gpu_cmd = NULL;
  return true;
}

static bool sg_readback_image(App *app, BackendImage image, int w, int h,
                              SglPixelFormat src_fmt, ReadbackResult *out) {
  if (!app || !app->gpu_device || !out || !image)
    return false;
  SgImage *im = (SgImage *)image;
  if (!im->tex || w <= 0 || h <= 0)
    return false;
  int bpp = sg_readback_src_bpp(src_fmt);
  if (bpp == 0) {
    SDL_Log("sg_readback_image: unsupported format %d", (int)src_fmt);
    return false;
  }
  if (!sg_submit_pending_frame_commands(app))
    return false;

  Uint32 src_stride = (Uint32)w * (Uint32)bpp;
  Uint32 src_bytes = src_stride * (Uint32)h;
  Uint32 dst_stride = (Uint32)w * 4;
  Uint32 dst_bytes = dst_stride * (Uint32)h;

  SDL_GPUTransferBuffer *tb = SDL_CreateGPUTransferBuffer(
      app->gpu_device, &(SDL_GPUTransferBufferCreateInfo){
                           .usage = SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD,
                           .size = src_bytes,
                       });
  if (!tb) {
    SDL_Log("sg_readback_image: SDL_CreateGPUTransferBuffer failed: %s",
            SDL_GetError());
    return false;
  }
  gpu_stats_create(GPU_STAT_TRANSFER_BUFFER, src_bytes);

  SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(app->gpu_device);
  if (!cmd) {
    SDL_Log("sg_readback_image: SDL_AcquireGPUCommandBuffer failed: %s",
            SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    return false;
  }
  SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
  if (!cp) {
    SDL_Log("sg_readback_image: SDL_BeginGPUCopyPass failed: %s",
            SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    SDL_SubmitGPUCommandBuffer(cmd);
    return false;
  }
  SDL_DownloadFromGPUTexture(cp,
                             &(SDL_GPUTextureRegion){
                                 .texture = im->tex,
                                 .w = (Uint32)w,
                                 .h = (Uint32)h,
                                 .d = 1,
                             },
                             &(SDL_GPUTextureTransferInfo){
                                 .transfer_buffer = tb,
                                 .offset = 0,
                                 .pixels_per_row = (Uint32)w,
                                 .rows_per_layer = (Uint32)h,
                             });
  SDL_EndGPUCopyPass(cp);

  SDL_GPUFence *fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
  if (!fence) {
    SDL_Log("sg_readback_image: SubmitAndAcquireFence failed: %s",
            SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    return false;
  }
  gpu_stats_create(GPU_STAT_FENCE, 0);
  if (!SDL_WaitForGPUFences(app->gpu_device, true, &fence, 1)) {
    SDL_Log("sg_readback_image: SDL_WaitForGPUFences failed: %s",
            SDL_GetError());
    SDL_ReleaseGPUFence(app->gpu_device, fence);
    gpu_stats_destroy(GPU_STAT_FENCE, 0);
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    return false;
  }
  SDL_ReleaseGPUFence(app->gpu_device, fence);
  gpu_stats_destroy(GPU_STAT_FENCE, 0);

  void *src = SDL_MapGPUTransferBuffer(app->gpu_device, tb, false);
  if (!src) {
    SDL_Log("sg_readback_image: SDL_MapGPUTransferBuffer failed: %s",
            SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    return false;
  }
  uint8_t *rgba = (uint8_t *)malloc(dst_bytes);
  if (!rgba) {
    SDL_Log("sg_readback_image: out of memory (%u bytes)", dst_bytes);
    SDL_UnmapGPUTransferBuffer(app->gpu_device, tb);
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    return false;
  }
  sg_convert_readback_to_rgba8(src_fmt, (const uint8_t *)src, rgba, w, h);
  SDL_UnmapGPUTransferBuffer(app->gpu_device, tb);
  SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
  gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);

  out->w = w;
  out->h = h;
  out->stride = (int)dst_stride;
  out->fmt = SGL_PF_RGBA8;
  out->data = rgba;
  out->data_bytes = dst_bytes;
  return true;
}

typedef struct SgReadbackRequest {
  ReadbackResult rb;
} SgReadbackRequest;

static bool sg_request_readback_image(App *app, BackendImage image, int w,
                                      int h, SglPixelFormat src_fmt,
                                      BackendReadback *out) {
  if (!out)
    return false;
  *out = 0;
  SgReadbackRequest *req =
      (SgReadbackRequest *)calloc(1, sizeof(SgReadbackRequest));
  if (!req)
    return false;
  if (!sg_readback_image(app, image, w, h, src_fmt, &req->rb)) {
    free(req);
    return false;
  }
  *out = (BackendReadback)req;
  return true;
}

static ReadbackPollStatus sg_poll_readback(BackendReadback h,
                                           ReadbackResult *out) {
  if (!h || !out)
    return READBACK_POLL_ERROR;
  SgReadbackRequest *req = (SgReadbackRequest *)h;
  *out = req->rb;
  memset(&req->rb, 0, sizeof(req->rb));
  return READBACK_POLL_READY;
}

static void sg_destroy_readback(BackendReadback h) {
  if (!h)
    return;
  SgReadbackRequest *req = (SgReadbackRequest *)h;
  if (req->rb.data)
    free(req->rb.data);
  free(req);
}

static bool sg_capture(App *app, const char *path) {
  if (!app || !app->gpu_device || !app->gpu_cmd || !app->gpu_swapchain_tex ||
      !path) {
    return false;
  }
  int w = app->last_w;
  int h = app->last_h;
  if (w <= 0 || h <= 0) {
    SDL_Log("sg_capture: zero extent");
    return false;
  }

  SglPixelFormat src_fmt = sg_swapchain_color_format(app);
  int bpp = sg_readback_src_bpp(src_fmt);
  if (bpp == 0) {
    SDL_Log("sg_capture: unsupported swapchain format %d", (int)src_fmt);
    return false;
  }

  Uint32 src_stride = (Uint32)w * (Uint32)bpp;
  Uint32 src_bytes = src_stride * (Uint32)h;
  Uint32 dst_stride = (Uint32)w * 4;
  Uint32 dst_bytes = dst_stride * (Uint32)h;
  SDL_GPUTransferBuffer *tb = SDL_CreateGPUTransferBuffer(
      app->gpu_device, &(SDL_GPUTransferBufferCreateInfo){
                           .usage = SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD,
                           .size = src_bytes,
                       });
  if (!tb) {
    SDL_Log("sg_capture: SDL_CreateGPUTransferBuffer failed: %s",
            SDL_GetError());
    return false;
  }
  gpu_stats_create(GPU_STAT_TRANSFER_BUFFER, src_bytes);

  SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(app->gpu_cmd);
  if (!cp) {
    SDL_Log("sg_capture: SDL_BeginGPUCopyPass failed: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    return false;
  }
  SDL_DownloadFromGPUTexture(cp,
                             &(SDL_GPUTextureRegion){
                                 .texture = app->gpu_swapchain_tex,
                                 .w = (Uint32)w,
                                 .h = (Uint32)h,
                                 .d = 1,
                             },
                             &(SDL_GPUTextureTransferInfo){
                                 .transfer_buffer = tb,
                                 .offset = 0,
                                 .pixels_per_row = (Uint32)w,
                                 .rows_per_layer = (Uint32)h,
                             });
  SDL_EndGPUCopyPass(cp);

  SDL_GPUFence *fence = SDL_SubmitGPUCommandBufferAndAcquireFence(app->gpu_cmd);
  app->gpu_cmd = NULL;
  app->gpu_swapchain_tex = NULL;
  if (!fence) {
    SDL_Log("sg_capture: SubmitAndAcquireFence failed: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    return false;
  }
  gpu_stats_create(GPU_STAT_FENCE, 0);
  if (!SDL_WaitForGPUFences(app->gpu_device, true, &fence, 1)) {
    SDL_Log("sg_capture: SDL_WaitForGPUFences failed: %s", SDL_GetError());
    SDL_ReleaseGPUFence(app->gpu_device, fence);
    gpu_stats_destroy(GPU_STAT_FENCE, 0);
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    return false;
  }
  SDL_ReleaseGPUFence(app->gpu_device, fence);
  gpu_stats_destroy(GPU_STAT_FENCE, 0);

  void *src = SDL_MapGPUTransferBuffer(app->gpu_device, tb, false);
  if (!src) {
    SDL_Log("sg_capture: SDL_MapGPUTransferBuffer failed: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    return false;
  }
  uint8_t *rgba = (uint8_t *)malloc(dst_bytes);
  if (!rgba) {
    SDL_Log("sg_capture: out of memory (%u bytes)", dst_bytes);
    SDL_UnmapGPUTransferBuffer(app->gpu_device, tb);
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);
    return false;
  }
  sg_convert_readback_to_rgba8(src_fmt, (const uint8_t *)src, rgba, w, h);
  SDL_UnmapGPUTransferBuffer(app->gpu_device, tb);
  SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
  gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, src_bytes);

  int ok = stbi_write_png(path, w, h, 4, rgba, (int)dst_stride);
  free(rgba);
  if (!ok) {
    SDL_Log("sg_capture: stbi_write_png failed");
    return false;
  }
  return true;
}

static SglPixelFormat sg_swapchain_color_format(App *app) {
  SDL_GPUTextureFormat fmt =
      SDL_GetGPUSwapchainTextureFormat(app->gpu_device, app->window);
  switch (fmt) {
  case SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM:
  case SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM_SRGB:
    return SGL_PF_RGBA8;
  case SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM:
  case SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM_SRGB:
    return SGL_PF_BGRA8;
  default:
    return SGL_PF_BGRA8;
  }
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
    .update_buffer = sg_update_buffer,
    .update_image = sg_update_image,
    .begin_pass = sg_begin_pass,
    .end_pass = sg_end_pass,
    .apply_pipeline = sg_apply_pipeline,
    .apply_bindings = sg_apply_bindings,
    .apply_uniforms = sg_apply_uniforms,
    .draw = sg_draw,
    .set_scissor = sg_set_scissor,
    .dispatch = sg_dispatch,
    .request_readback_image = sg_request_readback_image,
    .poll_readback = sg_poll_readback,
    .destroy_readback = sg_destroy_readback,
    .capture = sg_capture,
    .capture_before_end_frame = true,
    .swapchain_color_format = sg_swapchain_color_format,
};
