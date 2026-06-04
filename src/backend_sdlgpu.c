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

static void sg_release_depth_texture(App *app) {
  if (app->gpu_device && app->gpu_depth_tex) {
    SDL_ReleaseGPUTexture(app->gpu_device, app->gpu_depth_tex);
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
  return true;
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
  // Snapshot for sg_capture: capture_state_drain runs AFTER this function,
  // so gpu_swapchain_tex is about to be cleared. Stash it for the capture
  // path. The submit above ensures the GPU has begun consuming it; capture
  // additionally fences its own copy to wait for that work.
  app->gpu_last_swapchain_tex = app->gpu_swapchain_tex;
  app->gpu_cmd = NULL;
  app->gpu_swapchain_tex = NULL;
}

static void sg_begin_pass(App *app, const PassBeginDesc *d) {
  int nct = d->n_color_targets > 0 ? d->n_color_targets : 0;
  if (nct > SGL_MAX_COLOR_TARGETS)
    nct = SGL_MAX_COLOR_TARGETS;
  SDL_GPUColorTargetInfo targets[SGL_MAX_COLOR_TARGETS] = {0};
  if (d->n_color_targets == 1 && d->targets[0] == 0 && !d->depth_target) {
    // swapchain target (single)
    SDL_GPUTexture *tex = app->gpu_swapchain_tex;
    if (!tex || !app->gpu_cmd) {
      g_render_pass = NULL;
      return;
    }
    targets[0].texture = tex;
    targets[0].clear_color = (SDL_FColor){d->clear[0][0], d->clear[0][1],
                                          d->clear[0][2], d->clear[0][3]};
    targets[0].load_op = SDL_GPU_LOADOP_CLEAR;
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
        .load_op = SDL_GPU_LOADOP_CLEAR,
        .store_op = SDL_GPU_STOREOP_DONT_CARE,
        .stencil_load_op = SDL_GPU_LOADOP_DONT_CARE,
        .stencil_store_op = SDL_GPU_STOREOP_DONT_CARE,
    };
    g_render_pass = SDL_BeginGPURenderPass(app->gpu_cmd, targets, 1, &depth);
    return;
  }
  if (!app->gpu_cmd) {
    g_render_pass = NULL;
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
    targets[i].load_op = SDL_GPU_LOADOP_CLEAR;
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
        .load_op = SDL_GPU_LOADOP_CLEAR,
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

  void *map = SDL_MapGPUTransferBuffer(dev, tbuf, false);
  if (!map) {
    SDL_Log("sg_upload_to_buffer: map: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(dev, tbuf);
    return false;
  }
  memcpy(map, data, bytes);
  SDL_UnmapGPUTransferBuffer(dev, tbuf);

  SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(dev);
  if (!cmd) {
    SDL_Log("sg_upload_to_buffer: cmd: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(dev, tbuf);
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
  void *map = SDL_MapGPUTransferBuffer(dev, tbuf, false);
  if (!map) {
    SDL_Log("sg_upload_to_image: map: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(dev, tbuf);
    return false;
  }
  memcpy(map, data, bytes);
  SDL_UnmapGPUTransferBuffer(dev, tbuf);

  SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(dev);
  if (!cmd) {
    SDL_Log("sg_upload_to_image: cmd: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(dev, tbuf);
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
  if (data && bytes > 0) {
    if (!sg_upload_to_buffer(b->gpu, data, bytes, false)) {
      SDL_ReleaseGPUBuffer(g_app->gpu_device, b->gpu);
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
  }
  free(b);
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

  SDL_GPUTextureFormat tfmt = sgl_to_sdl_texture_format(d->fmt);
  Uint32 usage = SDL_GPU_TEXTUREUSAGE_SAMPLER;
  if (d->render_target) {
    usage |= sgl_is_depth_format(d->fmt)
                 ? SDL_GPU_TEXTUREUSAGE_DEPTH_STENCIL_TARGET
                 : SDL_GPU_TEXTUREUSAGE_COLOR_TARGET;
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

  if (!d->render_target && d->data && d->data_bytes > 0) {
    if (!sg_upload_to_image(im->tex, d->w, d->h, d->data, d->data_bytes,
                            false)) {
      SDL_ReleaseGPUTexture(g_app->gpu_device, im->tex);
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
    free(im);
    return 0;
  }
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
    Uint32 n_rw_buf = 0, n_ro_buf = 0;
    for (int i = 0; i < s->refl.storage_buf_count; ++i) {
      if (s->refl.storage_bufs[i].readonly)
        n_ro_buf++;
      else
        n_rw_buf++;
    }
    s->compute_pip = SDL_CreateGPUComputePipeline(
        g_app->gpu_device, &(SDL_GPUComputePipelineCreateInfo){
                               .code = (const Uint8 *)d->cs_spirv,
                               .code_size = d->cs_bytes,
                               .entrypoint = "main",
                               .format = SDL_GPU_SHADERFORMAT_SPIRV,
                               .num_samplers = 0,
                               .num_readonly_storage_textures = 0,
                               .num_readonly_storage_buffers = n_ro_buf,
                               .num_readwrite_storage_textures = 0,
                               .num_readwrite_storage_buffers = n_rw_buf,
                               .num_uniform_buffers = (Uint32)s->refl.ub_count,
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
    return (uintptr_t)s;
  }
  // Slang's SPIR-V emitter renames the entry-point function to "main"
  // (same convention used by the sokol backend). Both vs and fs blobs
  // each have a single "main" entry point.
  s->vs = SDL_CreateGPUShader(
      g_app->gpu_device,
      &(SDL_GPUShaderCreateInfo){
          .code = (const Uint8 *)d->vs_spirv,
          .code_size = d->vs_bytes,
          .entrypoint = "main",
          .format = SDL_GPU_SHADERFORMAT_SPIRV,
          .stage = SDL_GPU_SHADERSTAGE_VERTEX,
          .num_uniform_buffers = (Uint32)(d->refl ? d->refl->ub_count : 0),
          .num_storage_buffers = 0,
          .num_storage_textures = 0,
          .num_samplers = 0,
      });
  s->fs = SDL_CreateGPUShader(
      g_app->gpu_device,
      &(SDL_GPUShaderCreateInfo){
          .code = (const Uint8 *)d->fs_spirv,
          .code_size = d->fs_bytes,
          .entrypoint = "main",
          .format = SDL_GPU_SHADERFORMAT_SPIRV,
          .stage = SDL_GPU_SHADERSTAGE_FRAGMENT,
          .num_uniform_buffers = 0,
          .num_storage_buffers = 0,
          .num_storage_textures = 0,
          .num_samplers = (Uint32)(d->refl ? d->refl->tex_count : 0),
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
  return (uintptr_t)s;
}

static void sg_destroy_shader(BackendShader h) {
  SgShader *s = (SgShader *)h;
  if (!s)
    return;
  if (g_app && g_app->gpu_device) {
    if (s->vs)
      SDL_ReleaseGPUShader(g_app->gpu_device, s->vs);
    if (s->fs)
      SDL_ReleaseGPUShader(g_app->gpu_device, s->fs);
    if (s->compute_pip)
      SDL_ReleaseGPUComputePipeline(g_app->gpu_device, s->compute_pip);
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
                  // Match sokol's default and the runtime's D3D-style LH
                  // examples.
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
  return (uintptr_t)p;
}

static void sg_destroy_pipeline(BackendPipeline h) {
  SgPipeline *p = (SgPipeline *)h;
  if (!p)
    return;
  if (g_app && g_app->gpu_device && p->gpu) {
    SDL_ReleaseGPUGraphicsPipeline(g_app->gpu_device, p->gpu);
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
    if (im->smp)
      SDL_ReleaseGPUSampler(g_app->gpu_device, im->smp);
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

static void sg_apply_uniforms(int slot, const void *d, size_t b) {
  if (!g_app || !g_app->gpu_cmd)
    return;
  // Current binding exposes vertex-stage uniform blocks. SDL_GPU per-stage
  // layout maps VS uniform buffers to descriptor set 1; the slot here is the
  // binding index within set 1 (matches ShaderUniformBlock.slot from
  // reflection).
  SDL_PushGPUVertexUniformData(g_app->gpu_cmd, (Uint32)slot, d, (Uint32)b);
}

static void sg_draw(int base, int count, int instance_count) {
  if (!g_render_pass)
    return;
  Uint32 instances = (Uint32)(instance_count > 0 ? instance_count : 1);
  if (g_last_indexed) {
    SDL_DrawGPUIndexedPrimitives(g_render_pass, (Uint32)count, instances,
                                 (Uint32)base, 0, 0);
  } else {
    SDL_DrawGPUPrimitives(g_render_pass, (Uint32)count, instances,
                          (Uint32)base, 0);
  }
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
        if (n_ro < SGL_MAX_STORAGE_BUFS)
          ro[n_ro++] = buf->gpu;
      } else {
        if (n_rw < SGL_MAX_STORAGE_BUFS) {
          rw[n_rw].buffer = buf->gpu;
          rw[n_rw].cycle = true; // discard previous content
          n_rw++;
        }
      }
      break;
    }
  }
  SDL_GPUComputePass *cp =
      SDL_BeginGPUComputePass(app->gpu_cmd, NULL, 0, rw, (Uint32)n_rw);
  if (!cp) {
    SDL_Log("sg_dispatch: SDL_BeginGPUComputePass failed: %s", SDL_GetError());
    return;
  }
  SDL_BindGPUComputePipeline(cp, p->compute_gpu);
  if (n_ro > 0) {
    SDL_BindGPUComputeStorageBuffers(cp, 0, ro, (Uint32)n_ro);
  }
  if (d->uniform_slot >= 0 && d->uniform_data && d->uniform_bytes > 0) {
    SDL_PushGPUComputeUniformData(app->gpu_cmd, (Uint32)d->uniform_slot,
                                  d->uniform_data, (Uint32)d->uniform_bytes);
  }
  SDL_DispatchGPUCompute(cp, (Uint32)d->groups_x, (Uint32)d->groups_y,
                         (Uint32)d->groups_z);
  SDL_EndGPUComputePass(cp);
}

// --- capture: SDL_DownloadFromGPUTexture + stb_image_write ---------------
//
// Called from app_frame_end -> capture_state_drain, AFTER sg_end_frame has
// submitted the frame's command buffer and snapshotted the swapchain texture
// into app->gpu_last_swapchain_tex. We:
//   1. Allocate a DOWNLOAD-usage transfer buffer sized w*h*4 bytes.
//   2. Run a one-shot copy pass: SDL_DownloadFromGPUTexture(swapchain -> tb).
//   3. Submit + acquire fence + wait, so the host map below is safe.
//   4. SDL_MapGPUTransferBuffer, memcpy out, unmap+release.
//   5. If the swapchain format is BGRA8(_SRGB), swap R/B per pixel so the PNG
//      is RGBA. (sokol's sk_capture does the same swizzle.)
//   6. stbi_write_png with stride=w*4.
static bool sg_capture(App *app, const char *path) {
  SDL_GPUTexture *src_tex = app->gpu_last_swapchain_tex;
  if (!src_tex) {
    SDL_Log("sg_capture: no last swapchain texture (frame skipped?)");
    return false;
  }
  int w = app->last_w, h = app->last_h;
  if (w <= 0 || h <= 0) {
    SDL_Log("sg_capture: invalid size %dx%d", w, h);
    return false;
  }
  if (!app->gpu_device || !app->window) {
    SDL_Log("sg_capture: backend not initialized");
    return false;
  }
  Uint32 stride = (Uint32)w * 4;
  Uint32 bytes = stride * (Uint32)h;

  SDL_GPUTransferBuffer *tb = SDL_CreateGPUTransferBuffer(
      app->gpu_device, &(SDL_GPUTransferBufferCreateInfo){
                           .usage = SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD,
                           .size = bytes,
                       });
  if (!tb) {
    SDL_Log("sg_capture: SDL_CreateGPUTransferBuffer failed: %s",
            SDL_GetError());
    return false;
  }

  SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(app->gpu_device);
  if (!cmd) {
    SDL_Log("sg_capture: SDL_AcquireGPUCommandBuffer failed: %s",
            SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    return false;
  }
  SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
  if (!cp) {
    SDL_Log("sg_capture: SDL_BeginGPUCopyPass failed: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    // cmd buffer ownership is unclear after BeginGPUCopyPass failure; submit it
    // empty rather than leaking. Fence is not used since download didn't run.
    SDL_SubmitGPUCommandBuffer(cmd);
    return false;
  }
  SDL_DownloadFromGPUTexture(cp,
                             &(SDL_GPUTextureRegion){
                                 .texture = src_tex,
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
    SDL_Log("sg_capture: SubmitAndAcquireFence failed: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    return false;
  }
  if (!SDL_WaitForGPUFences(app->gpu_device, true, &fence, 1)) {
    SDL_Log("sg_capture: SDL_WaitForGPUFences failed: %s", SDL_GetError());
    SDL_ReleaseGPUFence(app->gpu_device, fence);
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    return false;
  }
  SDL_ReleaseGPUFence(app->gpu_device, fence);

  void *src = SDL_MapGPUTransferBuffer(app->gpu_device, tb, false);
  if (!src) {
    SDL_Log("sg_capture: SDL_MapGPUTransferBuffer failed: %s", SDL_GetError());
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    return false;
  }
  uint8_t *rgba = (uint8_t *)malloc(bytes);
  if (!rgba) {
    SDL_Log("sg_capture: out of memory (%u bytes)", bytes);
    SDL_UnmapGPUTransferBuffer(app->gpu_device, tb);
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);
    return false;
  }
  memcpy(rgba, src, bytes);
  SDL_UnmapGPUTransferBuffer(app->gpu_device, tb);
  SDL_ReleaseGPUTransferBuffer(app->gpu_device, tb);

  SDL_GPUTextureFormat fmt =
      SDL_GetGPUSwapchainTextureFormat(app->gpu_device, app->window);
  if (fmt == SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM ||
      fmt == SDL_GPU_TEXTUREFORMAT_B8G8R8A8_UNORM_SRGB) {
    for (Uint32 i = 0; i < bytes; i += 4) {
      uint8_t t = rgba[i];
      rgba[i] = rgba[i + 2];
      rgba[i + 2] = t;
    }
  }

  int ok = stbi_write_png(path, w, h, 4, rgba, (int)stride);
  free(rgba);
  if (!ok) {
    SDL_Log("sg_capture: stbi_write_png failed for %s", path);
    return false;
  }
  SDL_Log("sg_capture: wrote %s (%dx%d)", path, w, h);
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
    .dispatch = sg_dispatch,
    .capture = sg_capture,
    .swapchain_color_format = sg_swapchain_color_format,
};
