// backend_webgpu.c — RenderBackend impl using webgpu.h directly.
//
// Emscripten-only for now. Uses the browser's native WebGPU API via
// emdawnwebgpu. Shaders arrive as WGSL source from slang-bridge.ts.
//
// Bind group layout (matches slang-bridge.ts remapWgslGroups):
//   @group(0): uniform buffers (b0, b1)
//   @group(1): textures, samplers, storage buffers, storage textures
#ifdef __EMSCRIPTEN__

#include "app.h"
#include "backend.h"
#include "gpu_stats.h"
#include "shader.h"

#include <SDL3/SDL.h>
#include <emscripten/emscripten.h>
#include <webgpu/webgpu.h>

#include "stb_image_write.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- EM_JS canvas helpers -------------------------------------------------
// clang-format off
EM_JS(int, wg_canvas_width, (void),
      { return (window._canvasWidth || 480) | 0; })
EM_JS(int, wg_canvas_height, (void),
      { return (window._canvasHeight || 360) | 0; })
// clang-format on

// ---- helpers ---------------------------------------------------------------

static WGPUStringView wg_sv(const char *s) {
  WGPUStringView v = {s, s ? strlen(s) : 0};
  return v;
}

static uint32_t wg_align(uint32_t v, uint32_t a) {
  return (v + a - 1u) & ~(a - 1u);
}

// ---- format tables ---------------------------------------------------------

static WGPUTextureFormat sgl_to_wgpu_fmt(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_RGBA8:
    return WGPUTextureFormat_RGBA8Unorm;
  case SGL_PF_BGRA8:
    return WGPUTextureFormat_BGRA8Unorm;
  case SGL_PF_R8:
    return WGPUTextureFormat_R8Unorm;
  case SGL_PF_RG8:
    return WGPUTextureFormat_RG8Unorm;
  case SGL_PF_R16F:
    return WGPUTextureFormat_R16Float;
  case SGL_PF_RG16F:
    return WGPUTextureFormat_RG16Float;
  case SGL_PF_R32F:
    return WGPUTextureFormat_R32Float;
  case SGL_PF_RGBA16F:
    return WGPUTextureFormat_RGBA16Float;
  case SGL_PF_RGBA32F:
    return WGPUTextureFormat_RGBA32Float;
  case SGL_PF_DEPTH16:
    return WGPUTextureFormat_Depth16Unorm;
  case SGL_PF_DEPTH32F:
    return WGPUTextureFormat_Depth32Float;
  case SGL_PF_DEPTH24_STENCIL8:
    return WGPUTextureFormat_Depth32FloatStencil8;
  default:
    return WGPUTextureFormat_RGBA8Unorm;
  }
}

static WGPUVertexFormat comp_count_to_wgpu(int n) {
  switch (n) {
  case 1:
    return WGPUVertexFormat_Float32;
  case 2:
    return WGPUVertexFormat_Float32x2;
  case 3:
    return WGPUVertexFormat_Float32x3;
  case 4:
    return WGPUVertexFormat_Float32x4;
  default:
    return WGPUVertexFormat_Float32x3;
  }
}

static WGPUPrimitiveTopology sgl_to_wgpu_prim(SglPrimitive p) {
  switch (p) {
  case SGL_PRIM_LINES:
    return WGPUPrimitiveTopology_LineList;
  case SGL_PRIM_LINE_STRIP:
    return WGPUPrimitiveTopology_LineStrip;
  case SGL_PRIM_POINTS:
    return WGPUPrimitiveTopology_PointList;
  case SGL_PRIM_TRIANGLE_STRIP:
    return WGPUPrimitiveTopology_TriangleStrip;
  default:
    return WGPUPrimitiveTopology_TriangleList;
  }
}

static WGPUCullMode sgl_to_wgpu_cull(SglCull c) {
  switch (c) {
  case SGL_CULL_BACK:
    return WGPUCullMode_Back;
  case SGL_CULL_FRONT:
    return WGPUCullMode_Front;
  default:
    return WGPUCullMode_None;
  }
}

// ---- per-resource structs --------------------------------------------------

typedef struct WgBuffer {
  WGPUBuffer buf;
  uint64_t bytes;
  SglBufferType type;
} WgBuffer;

typedef struct WgImage {
  WGPUTexture tex;
  WGPUTextureView view;
  WGPUTextureView color_att;
  WGPUTextureView depth_att;
  WGPUTextureView storage_view;
  WGPUSampler sampler;
  uint64_t stat_bytes;
  bool render_target;
  bool storage;
  SglPixelFormat fmt;
} WgImage;

typedef struct WgShader {
  WGPUShaderModule vs_mod;
  WGPUShaderModule fs_mod;
  WGPUShaderModule cs_mod;
  ShaderReflection refl;
  bool is_compute;
} WgShader;

typedef struct WgPipeline {
  WGPURenderPipeline render;
  WGPUComputePipeline compute;
  WGPUPipelineLayout layout;
  WGPUBindGroupLayout bgl0; // group 0: UBs
  WGPUBindGroupLayout bgl1; // group 1: textures/samplers/storage
  bool is_compute;
  ShaderReflection refl; // copy for bind group creation
} WgPipeline;

// ---- per-frame state -------------------------------------------------------

static WGPUDevice g_dev;
static WGPUQueue g_queue;
static WGPUCommandEncoder g_enc;
static WGPURenderPassEncoder g_rpass;

// Uniform staging: WebGPU doesn't have push constants, so we write uniform
// data to per-frame staging buffers and bind them as uniform buffers.
// We use a ring buffer approach: each draw call writes to the next 256-byte
// aligned offset in a large buffer, avoiding the problem of later draws
// overwriting earlier draws' uniform data.
#define WG_MAX_UB_SLOTS 2
#define WG_UB_ALIGN 256   // minUniformBufferOffsetAlignment
#define WG_UB_SIZE 1024   // max bytes per uniform block
#define WG_UB_STRIDE 1024 // ring stride: aligned to WG_UB_ALIGN, >= WG_UB_SIZE
#define WG_UB_RING_SIZE (WG_UB_STRIDE * 128) // 128KB ring per slot

typedef struct WgUniformState {
  WGPUBuffer bufs[WG_MAX_UB_SLOTS];
  bool dirty[WG_MAX_UB_SLOTS];
  uint8_t data[WG_MAX_UB_SLOTS][WG_UB_SIZE];
  size_t sizes[WG_MAX_UB_SLOTS];
  uint32_t ring_offset[WG_MAX_UB_SLOTS]; // current write offset in ring
} WgUniformState;

static WgUniformState g_ub;
static WgPipeline *g_cur_pipeline;
static bool g_ibuf_bound;

// ---- bring-up (surface / depth / swapchain) --------------------------------

static bool wg_recreate_depth(App *app, uint32_t w, uint32_t h) {
  if (app->wgpu_depth_view) {
    wgpuTextureViewRelease(app->wgpu_depth_view);
    gpu_stats_destroy(GPU_STAT_VIEW, 0);
    app->wgpu_depth_view = NULL;
  }
  if (app->wgpu_depth_tex) {
    wgpuTextureRelease(app->wgpu_depth_tex);
    gpu_stats_destroy(GPU_STAT_TEXTURE, 0);
    app->wgpu_depth_tex = NULL;
  }
  WGPUTextureDescriptor ds = {
      .usage = WGPUTextureUsage_RenderAttachment,
      .dimension = WGPUTextureDimension_2D,
      .size = {.width = w, .height = h, .depthOrArrayLayers = 1},
      .format = WGPUTextureFormat_Depth32FloatStencil8,
      .mipLevelCount = 1,
      .sampleCount = 1,
  };
  app->wgpu_depth_tex = wgpuDeviceCreateTexture(app->wgpu_device, &ds);
  if (!app->wgpu_depth_tex) {
    SDL_Log("[webgpu] depth texture create failed");
    return false;
  }
  gpu_stats_create(GPU_STAT_TEXTURE, 0);
  app->wgpu_depth_view = wgpuTextureCreateView(app->wgpu_depth_tex, NULL);
  if (!app->wgpu_depth_view) {
    SDL_Log("[webgpu] depth view create failed");
    wgpuTextureRelease(app->wgpu_depth_tex);
    gpu_stats_destroy(GPU_STAT_TEXTURE, 0);
    app->wgpu_depth_tex = NULL;
    return false;
  }
  gpu_stats_create(GPU_STAT_VIEW, 0);
  return true;
}

static void wg_configure_surface(App *app, uint32_t w, uint32_t h) {
  // CopySrc: wg_capture (golden test) が swapchain texture を readback する。
  WGPUSurfaceConfiguration cfg = {
      .device = app->wgpu_device,
      .format = app->wgpu_surface_format,
      .usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc,
      .width = w,
      .height = h,
      .alphaMode = WGPUCompositeAlphaMode_Opaque,
      .presentMode = WGPUPresentMode_Fifo,
  };
  wgpuSurfaceConfigure(app->wgpu_surface, &cfg);
}

// ---- init / shutdown -------------------------------------------------------

static bool wg_init(App *app) {
  app->wgpu_device = emscripten_webgpu_get_device();
  if (!app->wgpu_device) {
    SDL_Log("[webgpu] emscripten_webgpu_get_device returned NULL");
    return false;
  }

  app->wgpu_instance = wgpuCreateInstance(NULL);
  if (!app->wgpu_instance) {
    SDL_Log("[webgpu] wgpuCreateInstance failed");
    return false;
  }

  WGPUEmscriptenSurfaceSourceCanvasHTMLSelector canvas_src = {
      .chain = {.sType = WGPUSType_EmscriptenSurfaceSourceCanvasHTMLSelector},
      .selector = wg_sv("#canvas"),
  };
  WGPUSurfaceDescriptor surf_desc = {
      .nextInChain = &canvas_src.chain,
  };
  app->wgpu_surface = wgpuInstanceCreateSurface(app->wgpu_instance, &surf_desc);
  if (!app->wgpu_surface) {
    SDL_Log("[webgpu] surface create failed");
    return false;
  }

  app->wgpu_surface_format = WGPUTextureFormat_BGRA8Unorm;
  g_dev = app->wgpu_device;
  g_queue = wgpuDeviceGetQueue(g_dev);
  int cw = wg_canvas_width();
  int ch = wg_canvas_height();
  if (cw <= 0)
    cw = 480;
  if (ch <= 0)
    ch = 360;
  wg_configure_surface(app, (uint32_t)cw, (uint32_t)ch);
  if (!wg_recreate_depth(app, (uint32_t)cw, (uint32_t)ch))
    return false;

  // Create uniform staging buffers.
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i) {
    WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
    bd.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
    bd.size = WG_UB_RING_SIZE;
    g_ub.bufs[i] = wgpuDeviceCreateBuffer(app->wgpu_device, &bd);
  }

  SDL_Log("[webgpu] backend init OK: %dx%d", cw, ch);
  return true;
}

static void wg_shutdown(App *app) {
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i) {
    if (g_ub.bufs[i]) {
      wgpuBufferRelease(g_ub.bufs[i]);
      g_ub.bufs[i] = NULL;
    }
  }
  if (g_queue) {
    wgpuQueueRelease(g_queue);
    g_queue = NULL;
  }
  if (app->wgpu_swapchain_view) {
    wgpuTextureViewRelease(app->wgpu_swapchain_view);
    gpu_stats_destroy(GPU_STAT_SURFACE_VIEW, 0);
    app->wgpu_swapchain_view = NULL;
  }
  if (app->wgpu_swapchain_tex) {
    wgpuTextureRelease(app->wgpu_swapchain_tex);
    gpu_stats_destroy(GPU_STAT_SURFACE_TEXTURE, 0);
    app->wgpu_swapchain_tex = NULL;
  }
  if (app->wgpu_depth_view) {
    wgpuTextureViewRelease(app->wgpu_depth_view);
    gpu_stats_destroy(GPU_STAT_VIEW, 0);
    app->wgpu_depth_view = NULL;
  }
  if (app->wgpu_depth_tex) {
    wgpuTextureRelease(app->wgpu_depth_tex);
    gpu_stats_destroy(GPU_STAT_TEXTURE, 0);
    app->wgpu_depth_tex = NULL;
  }
  if (app->wgpu_surface) {
    wgpuSurfaceRelease(app->wgpu_surface);
    app->wgpu_surface = NULL;
  }
  if (app->wgpu_instance) {
    wgpuInstanceRelease(app->wgpu_instance);
    app->wgpu_instance = NULL;
  }
  app->wgpu_device = NULL;
}

// ---- frame begin / end -----------------------------------------------------

static void wg_begin_frame(App *app, int *out_w, int *out_h) {
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i)
    g_ub.ring_offset[i] = 0;

  int cw = wg_canvas_width();
  int ch = wg_canvas_height();
  if (cw <= 0)
    cw = 480;
  if (ch <= 0)
    ch = 360;

  bool needs_resize = app->pending_resize ||
                      (app->last_w != 0 && cw != app->last_w) ||
                      (app->last_h != 0 && ch != app->last_h);
  if (needs_resize) {
    app->pending_resize = false;
    if (app->wgpu_swapchain_view) {
      wgpuTextureViewRelease(app->wgpu_swapchain_view);
      gpu_stats_destroy(GPU_STAT_SURFACE_VIEW, 0);
      app->wgpu_swapchain_view = NULL;
    }
    if (app->wgpu_swapchain_tex) {
      wgpuTextureRelease(app->wgpu_swapchain_tex);
      gpu_stats_destroy(GPU_STAT_SURFACE_TEXTURE, 0);
      app->wgpu_swapchain_tex = NULL;
    }
    wg_configure_surface(app, (uint32_t)cw, (uint32_t)ch);
    if (!wg_recreate_depth(app, (uint32_t)cw, (uint32_t)ch)) {
      if (out_w)
        *out_w = cw;
      if (out_h)
        *out_h = ch;
      return;
    }
  }

  WGPUSurfaceTexture surf_tex = {0};
  wgpuSurfaceGetCurrentTexture(app->wgpu_surface, &surf_tex);
  bool ok =
      (surf_tex.status == WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal ||
       surf_tex.status == WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal);
  if (!ok) {
    if (surf_tex.texture) {
      wgpuTextureRelease(surf_tex.texture);
      surf_tex.texture = NULL;
    }
    if (surf_tex.status == WGPUSurfaceGetCurrentTextureStatus_Outdated ||
        surf_tex.status == WGPUSurfaceGetCurrentTextureStatus_Lost ||
        surf_tex.status == WGPUSurfaceGetCurrentTextureStatus_Timeout) {
      wg_configure_surface(app, (uint32_t)cw, (uint32_t)ch);
      if (!wg_recreate_depth(app, (uint32_t)cw, (uint32_t)ch)) {
        if (out_w)
          *out_w = cw;
        if (out_h)
          *out_h = ch;
        return;
      }
      WGPUSurfaceTexture retry = {0};
      wgpuSurfaceGetCurrentTexture(app->wgpu_surface, &retry);
      ok = (retry.status == WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal ||
            retry.status ==
                WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal);
      surf_tex = retry;
    }
  }
  if (ok && surf_tex.texture) {
    app->wgpu_swapchain_tex = surf_tex.texture;
    gpu_stats_create(GPU_STAT_SURFACE_TEXTURE, 0);
    app->wgpu_swapchain_view = wgpuTextureCreateView(surf_tex.texture, NULL);
    if (app->wgpu_swapchain_view)
      gpu_stats_create(GPU_STAT_SURFACE_VIEW, 0);
    uint32_t sw = wgpuTextureGetWidth(surf_tex.texture);
    uint32_t sh = wgpuTextureGetHeight(surf_tex.texture);
    if ((int)sw != cw || (int)sh != ch) {
      if (!wg_recreate_depth(app, sw, sh))
        SDL_Log("[webgpu] depth recreate failed for %ux%u", sw, sh);
      cw = (int)sw;
      ch = (int)sh;
    }
  } else {
    if (surf_tex.texture)
      wgpuTextureRelease(surf_tex.texture);
    SDL_Log("[webgpu] surface acquire failed (status=%d)",
            (int)surf_tex.status);
  }

  // Create the per-frame command encoder.
  WGPUCommandEncoderDescriptor enc_desc = {0};
  g_enc = wgpuDeviceCreateCommandEncoder(app->wgpu_device, &enc_desc);

  if (out_w)
    *out_w = cw;
  if (out_h)
    *out_h = ch;
}

static void wg_end_frame(App *app) {
  // Submit the command buffer.
  if (g_enc) {
    WGPUCommandBufferDescriptor cmd_desc = {0};
    WGPUCommandBuffer cmd = wgpuCommandEncoderFinish(g_enc, &cmd_desc);
    wgpuQueueSubmit(g_queue, 1, &cmd);
    wgpuCommandBufferRelease(cmd);
    wgpuCommandEncoderRelease(g_enc);
    g_enc = NULL;
  }

  if (app->wgpu_swapchain_view) {
    wgpuTextureViewRelease(app->wgpu_swapchain_view);
    gpu_stats_destroy(GPU_STAT_SURFACE_VIEW, 0);
    app->wgpu_swapchain_view = NULL;
  }
  if (app->wgpu_swapchain_tex) {
    wgpuTextureRelease(app->wgpu_swapchain_tex);
    gpu_stats_destroy(GPU_STAT_SURFACE_TEXTURE, 0);
    app->wgpu_swapchain_tex = NULL;
  }
}

// ---- make / destroy buffer -------------------------------------------------

static BackendBuffer wg_make_buffer(SglBufferType type, const void *data,
                                    size_t bytes) {
  WgBuffer *wb = (WgBuffer *)calloc(1, sizeof(WgBuffer));
  if (!wb)
    return 0;
  wb->type = type;
  wb->bytes = (uint64_t)bytes;

  WGPUBufferUsage usage = 0;
  if (type == SGL_BUFFER_VERTEX)
    usage = WGPUBufferUsage_Vertex | WGPUBufferUsage_CopyDst;
  else if (type == SGL_BUFFER_INDEX)
    usage = WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst;
  else if (type == SGL_BUFFER_STORAGE)
    usage = WGPUBufferUsage_Storage | WGPUBufferUsage_Vertex |
            WGPUBufferUsage_CopyDst;
  else
    usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;

  WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
  bd.usage = usage;
  bd.size = bytes > 0 ? bytes : 4;
  wb->buf = wgpuDeviceCreateBuffer(g_dev, &bd);
  if (!wb->buf) {
    free(wb);
    return 0;
  }
  gpu_stats_create(GPU_STAT_BUFFER, wb->bytes);
  if (data && bytes > 0) {
    wgpuQueueWriteBuffer(g_queue, wb->buf, 0, data, bytes);
  }
  return (uintptr_t)wb;
}

static void wg_destroy_buffer(BackendBuffer h) {
  WgBuffer *wb = (WgBuffer *)h;
  if (!wb)
    return;
  if (wb->buf) {
    wgpuBufferRelease(wb->buf);
    gpu_stats_destroy(GPU_STAT_BUFFER, wb->bytes);
  }
  free(wb);
}

// ---- make / destroy image --------------------------------------------------

static BackendImage wg_make_image(const ImageDesc *d) {
  WgImage *wi = (WgImage *)calloc(1, sizeof(WgImage));
  if (!wi)
    return 0;
  wi->render_target = d->render_target;
  wi->storage = d->storage;
  wi->fmt = d->fmt;
  wi->stat_bytes = gpu_stats_image_bytes(d->fmt, d->w, d->h);

  WGPUTextureFormat wfmt = sgl_to_wgpu_fmt(d->fmt);

  WGPUTextureUsage usage = WGPUTextureUsage_TextureBinding;
  if (d->render_target) {
    bool is_depth = (d->fmt == SGL_PF_DEPTH16 || d->fmt == SGL_PF_DEPTH32F ||
                     d->fmt == SGL_PF_DEPTH24_STENCIL8);
    usage |= WGPUTextureUsage_RenderAttachment;
    if (!is_depth)
      usage |= WGPUTextureUsage_CopySrc;
  }
  if (d->storage)
    usage |= WGPUTextureUsage_StorageBinding;
  if (!d->render_target && !d->storage)
    usage |= WGPUTextureUsage_CopyDst;

  WGPUTextureDescriptor td = {
      .usage = usage,
      .dimension = WGPUTextureDimension_2D,
      .size = {.width = (uint32_t)d->w,
               .height = (uint32_t)d->h,
               .depthOrArrayLayers = 1},
      .format = wfmt,
      .mipLevelCount = 1,
      .sampleCount = 1,
  };
  WGPUDevice dev = g_dev;
  wi->tex = wgpuDeviceCreateTexture(dev, &td);
  if (!wi->tex) {
    free(wi);
    return 0;
  }
  gpu_stats_create(GPU_STAT_TEXTURE, wi->stat_bytes);

  if (!d->render_target && d->data && d->data_bytes > 0) {
    WGPUTexelCopyTextureInfo dst_info = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    dst_info.texture = wi->tex;
    int bpp = 4;
    if (d->fmt == SGL_PF_R8)
      bpp = 1;
    else if (d->fmt == SGL_PF_RG8)
      bpp = 2;
    else if (d->fmt == SGL_PF_R16F)
      bpp = 2;
    else if (d->fmt == SGL_PF_RG16F)
      bpp = 4;
    else if (d->fmt == SGL_PF_RGBA16F)
      bpp = 8;
    else if (d->fmt == SGL_PF_R32F)
      bpp = 4;
    else if (d->fmt == SGL_PF_RGBA32F)
      bpp = 16;
    WGPUTexelCopyBufferLayout layout = {
        .offset = 0,
        .bytesPerRow = (uint32_t)(d->w * bpp),
        .rowsPerImage = (uint32_t)d->h,
    };
    WGPUExtent3D extent = {
        .width = (uint32_t)d->w,
        .height = (uint32_t)d->h,
        .depthOrArrayLayers = 1,
    };
    wgpuQueueWriteTexture(g_queue, &dst_info, d->data, d->data_bytes, &layout,
                          &extent);
  }

  // Sampler
  WGPUFilterMode filt = (d->filter == SGL_FILTER_NEAREST)
                            ? WGPUFilterMode_Nearest
                            : WGPUFilterMode_Linear;
  WGPUAddressMode addr = (d->wrap == SGL_WRAP_CLAMP)
                             ? WGPUAddressMode_ClampToEdge
                             : WGPUAddressMode_Repeat;
  WGPUSamplerDescriptor sd = WGPU_SAMPLER_DESCRIPTOR_INIT;
  sd.minFilter = filt;
  sd.magFilter = filt;
  sd.addressModeU = addr;
  sd.addressModeV = addr;
  wi->sampler = wgpuDeviceCreateSampler(dev, &sd);
  gpu_stats_create(GPU_STAT_SAMPLER, 0);

  // Texture view (for sampling)
  wi->view = wgpuTextureCreateView(wi->tex, NULL);
  gpu_stats_create(GPU_STAT_VIEW, 0);

  // Color attachment view
  if (d->render_target) {
    bool is_depth = (d->fmt == SGL_PF_DEPTH16 || d->fmt == SGL_PF_DEPTH32F ||
                     d->fmt == SGL_PF_DEPTH24_STENCIL8);
    if (is_depth) {
      wi->depth_att = wgpuTextureCreateView(wi->tex, NULL);
      gpu_stats_create(GPU_STAT_VIEW, 0);
    } else {
      wi->color_att = wgpuTextureCreateView(wi->tex, NULL);
      gpu_stats_create(GPU_STAT_VIEW, 0);
    }
  }

  // Storage view
  if (d->storage) {
    wi->storage_view = wgpuTextureCreateView(wi->tex, NULL);
    gpu_stats_create(GPU_STAT_VIEW, 0);
  }

  return (uintptr_t)wi;
}

static void wg_destroy_image(BackendImage h) {
  WgImage *wi = (WgImage *)h;
  if (!wi)
    return;
  if (wi->storage_view) {
    wgpuTextureViewRelease(wi->storage_view);
    gpu_stats_destroy(GPU_STAT_VIEW, 0);
  }
  if (wi->color_att) {
    wgpuTextureViewRelease(wi->color_att);
    gpu_stats_destroy(GPU_STAT_VIEW, 0);
  }
  if (wi->depth_att) {
    wgpuTextureViewRelease(wi->depth_att);
    gpu_stats_destroy(GPU_STAT_VIEW, 0);
  }
  if (wi->view) {
    wgpuTextureViewRelease(wi->view);
    gpu_stats_destroy(GPU_STAT_VIEW, 0);
  }
  if (wi->sampler) {
    wgpuSamplerRelease(wi->sampler);
    gpu_stats_destroy(GPU_STAT_SAMPLER, 0);
  }
  if (wi->tex) {
    wgpuTextureRelease(wi->tex);
    gpu_stats_destroy(GPU_STAT_TEXTURE, wi->stat_bytes);
  }
  free(wi);
}

// ---- make / destroy shader -------------------------------------------------

static BackendShader wg_make_shader(const ShaderDesc *d) {
  WgShader *ws = (WgShader *)calloc(1, sizeof(WgShader));
  if (!ws)
    return 0;
  if (d->refl)
    ws->refl = *d->refl;
  ws->is_compute = (d->cs_spirv != NULL);

  WGPUDevice dev = g_dev;

  if (ws->is_compute) {
    WGPUShaderSourceWGSL wgsl_src = {
        .chain = {.sType = WGPUSType_ShaderSourceWGSL},
        .code = wg_sv((const char *)d->cs_spirv),
    };
    WGPUShaderModuleDescriptor smd = {
        .nextInChain = &wgsl_src.chain,
    };
    ws->cs_mod = wgpuDeviceCreateShaderModule(dev, &smd);
    if (!ws->cs_mod) {
      free(ws);
      return 0;
    }
  } else {
    WGPUShaderSourceWGSL vs_src = {
        .chain = {.sType = WGPUSType_ShaderSourceWGSL},
        .code = wg_sv((const char *)d->vs_spirv),
    };
    WGPUShaderModuleDescriptor vs_desc = {.nextInChain = &vs_src.chain};
    ws->vs_mod = wgpuDeviceCreateShaderModule(dev, &vs_desc);

    WGPUShaderSourceWGSL fs_src = {
        .chain = {.sType = WGPUSType_ShaderSourceWGSL},
        .code = wg_sv((const char *)d->fs_spirv),
    };
    WGPUShaderModuleDescriptor fs_desc = {.nextInChain = &fs_src.chain};
    ws->fs_mod = wgpuDeviceCreateShaderModule(dev, &fs_desc);

    if (!ws->vs_mod || !ws->fs_mod) {
      if (ws->vs_mod)
        wgpuShaderModuleRelease(ws->vs_mod);
      if (ws->fs_mod)
        wgpuShaderModuleRelease(ws->fs_mod);
      free(ws);
      return 0;
    }
  }
  gpu_stats_create(GPU_STAT_SHADER, 0);
  return (uintptr_t)ws;
}

static void wg_destroy_shader(BackendShader h) {
  WgShader *ws = (WgShader *)h;
  if (!ws)
    return;
  if (ws->vs_mod)
    wgpuShaderModuleRelease(ws->vs_mod);
  if (ws->fs_mod)
    wgpuShaderModuleRelease(ws->fs_mod);
  if (ws->cs_mod)
    wgpuShaderModuleRelease(ws->cs_mod);
  gpu_stats_destroy(GPU_STAT_SHADER, 0);
  free(ws);
}

// ---- bind group layout building --------------------------------------------

// Build bind group layouts from shader reflection.
// Group 0: uniform buffers (slots 0..ub_count-1)
// Group 1: textures + samplers + storage_bufs + storage_textures
static void wg_build_bind_group_layouts(WGPUDevice dev,
                                        const ShaderReflection *refl,
                                        bool is_compute, uint8_t depth_tex_mask,
                                        WGPUBindGroupLayout *out_bgl0,
                                        WGPUBindGroupLayout *out_bgl1) {
  // Group 0: uniform buffers
  {
    WGPUBindGroupLayoutEntry entries[SGL_MAX_UNIFORM_BLOCKS] = {0};
    int count = 0;
    for (int i = 0; i < refl->ub_count && i < SGL_MAX_UNIFORM_BLOCKS; ++i) {
      WGPUBindGroupLayoutEntry *e = &entries[count++];
      e->binding = (uint32_t)refl->ubs[i].slot;
      e->visibility = is_compute ? WGPUShaderStage_Compute
                      : (refl->ubs[i].stage == SGL_STAGE_FRAGMENT)
                          ? WGPUShaderStage_Fragment
                          : WGPUShaderStage_Vertex;
      e->buffer.type = WGPUBufferBindingType_Uniform;
      e->buffer.hasDynamicOffset = true;
      e->buffer.minBindingSize = 0;
    }
    WGPUBindGroupLayoutDescriptor bgl_desc = {
        .entryCount = (size_t)count,
        .entries = entries,
    };
    *out_bgl0 = wgpuDeviceCreateBindGroupLayout(dev, &bgl_desc);
  }

  // Group 1: textures + samplers + storage
  {
    WGPUBindGroupLayoutEntry entries[32] = {0};
    int count = 0;

    for (int i = 0; i < refl->tex_count && i < SGL_MAX_TEXTURES; ++i) {
      WGPUShaderStage vis = is_compute ? WGPUShaderStage_Compute
                            : (refl->texs[i].stage == SGL_STAGE_VERTEX)
                                ? WGPUShaderStage_Vertex
                                : WGPUShaderStage_Fragment;
      // Depth-format textures can only be bound as unfilterable-float and
      // sampled with a non-filtering sampler (use_texture forces NEAREST on
      // depth formats, so the texture's own sampler is compatible).
      bool is_depth = (depth_tex_mask >> i) & 1;
      // Texture view
      {
        WGPUBindGroupLayoutEntry *e = &entries[count++];
        e->binding = (uint32_t)refl->texs[i].img_slot;
        e->visibility = vis;
        e->texture.sampleType = is_depth
                                    ? WGPUTextureSampleType_UnfilterableFloat
                                    : WGPUTextureSampleType_Float;
        e->texture.viewDimension = WGPUTextureViewDimension_2D;
      }
      // Sampler
      if (refl->texs[i].smp_slot >= 0) {
        WGPUBindGroupLayoutEntry *e = &entries[count++];
        e->binding = (uint32_t)refl->texs[i].smp_slot;
        e->visibility = vis;
        e->sampler.type = is_depth ? WGPUSamplerBindingType_NonFiltering
                                   : WGPUSamplerBindingType_Filtering;
      }
    }
    for (int i = 0; i < refl->storage_buf_count && i < SGL_MAX_STORAGE_BUFS;
         ++i) {
      WGPUBindGroupLayoutEntry *e = &entries[count++];
      e->binding = (uint32_t)refl->storage_bufs[i].slot;
      e->visibility =
          is_compute ? WGPUShaderStage_Compute : WGPUShaderStage_Fragment;
      e->buffer.type = refl->storage_bufs[i].readonly
                           ? WGPUBufferBindingType_ReadOnlyStorage
                           : WGPUBufferBindingType_Storage;
    }
    for (int i = 0; i < refl->storage_tex_count && i < SGL_MAX_STORAGE_TEXTURES;
         ++i) {
      WGPUBindGroupLayoutEntry *e = &entries[count++];
      e->binding = (uint32_t)refl->storage_texs[i].slot;
      e->visibility =
          is_compute ? WGPUShaderStage_Compute : WGPUShaderStage_Fragment;
      e->storageTexture.access = refl->storage_texs[i].readonly
                                     ? WGPUStorageTextureAccess_ReadOnly
                                     : WGPUStorageTextureAccess_WriteOnly;
      e->storageTexture.format =
          sgl_to_wgpu_fmt(refl->storage_texs[i].access_format);
      e->storageTexture.viewDimension = WGPUTextureViewDimension_2D;
    }

    WGPUBindGroupLayoutDescriptor bgl_desc = {
        .entryCount = (size_t)count,
        .entries = entries,
    };
    *out_bgl1 = wgpuDeviceCreateBindGroupLayout(dev, &bgl_desc);
  }
}

// ---- make / destroy pipeline -----------------------------------------------

static BackendPipeline wg_make_pipeline(const PipelineDesc *d) {
  WgShader *ws = (WgShader *)d->shader;
  if (!ws)
    return 0;

  WgPipeline *wp = (WgPipeline *)calloc(1, sizeof(WgPipeline));
  if (!wp)
    return 0;
  wp->is_compute = d->is_compute;
  if (d->refl)
    wp->refl = *d->refl;

  WGPUDevice dev = g_dev;

  wg_build_bind_group_layouts(dev, &ws->refl, d->is_compute,
                              d->is_compute ? 0 : d->depth_tex_mask, &wp->bgl0,
                              &wp->bgl1);

  WGPUBindGroupLayout bgls[2] = {wp->bgl0, wp->bgl1};
  WGPUPipelineLayoutDescriptor pl_desc = {
      .bindGroupLayoutCount = 2,
      .bindGroupLayouts = bgls,
  };
  wp->layout = wgpuDeviceCreatePipelineLayout(dev, &pl_desc);

  if (d->is_compute) {
    WGPUComputePipelineDescriptor cpd = {
        .layout = wp->layout,
        .compute =
            {
                .module = ws->cs_mod,
                .entryPoint = wg_sv("cs_main"),
            },
    };
    wp->compute = wgpuDeviceCreateComputePipeline(dev, &cpd);
    if (!wp->compute) {
      wgpuPipelineLayoutRelease(wp->layout);
      wgpuBindGroupLayoutRelease(wp->bgl0);
      wgpuBindGroupLayoutRelease(wp->bgl1);
      free(wp);
      return 0;
    }
    gpu_stats_create(GPU_STAT_PIPELINE, 0);
    return (uintptr_t)wp;
  }

  // Graphics pipeline
  // Vertex state
  WGPUVertexBufferLayout vb_layouts[SGL_MAX_VERTEX_BUFFERS] = {0};
  int vb_count = 0;

  // Per-buffer attribute collection
  WGPUVertexAttribute buf0_attrs[SGL_MAX_ATTRS];
  WGPUVertexAttribute buf1_attrs[SGL_MAX_ATTRS];
  int buf0_count = 0, buf1_count = 0;

  if (d->refl) {
    for (int i = 0; i < d->refl->attr_count && i < SGL_MAX_ATTRS; ++i) {
      WGPUVertexAttribute a = {
          .format = comp_count_to_wgpu(d->refl->attrs[i].comp_count),
          .offset =
              (uint64_t)(d->refl->attrs[i].offset_floats * (int)sizeof(float)),
          .shaderLocation = (uint32_t)d->refl->attrs[i].slot,
      };
      int bi = d->refl->attrs[i].buffer_index;
      if (bi <= 0) {
        buf0_attrs[buf0_count++] = a;
      } else {
        buf1_attrs[buf1_count++] = a;
      }
    }
    if (buf0_count > 0) {
      vb_layouts[0].arrayStride =
          (uint64_t)(d->refl->buffer_stride_floats[0] * (int)sizeof(float));
      vb_layouts[0].stepMode = WGPUVertexStepMode_Vertex;
      vb_layouts[0].attributeCount = (size_t)buf0_count;
      vb_layouts[0].attributes = buf0_attrs;
      vb_count = 1;
    }
    if (buf1_count > 0) {
      vb_layouts[1].arrayStride =
          (uint64_t)(d->refl->buffer_stride_floats[1] * (int)sizeof(float));
      vb_layouts[1].stepMode = WGPUVertexStepMode_Instance;
      vb_layouts[1].attributeCount = (size_t)buf1_count;
      vb_layouts[1].attributes = buf1_attrs;
      vb_count = 2;
    }
  }

  WGPUVertexState vs = {
      .module = ws->vs_mod,
      .entryPoint = wg_sv("vs_main"),
      .bufferCount = (size_t)vb_count,
      .buffers = vb_layouts,
  };

  // Fragment state. n_color_targets == 0 = depth-only pass: the fragment
  // stage runs with zero color targets (its SV_Target output is discarded).
  int nct = d->n_color_targets;
  if (nct < 0)
    nct = 0;
  if (nct > SGL_MAX_COLOR_TARGETS)
    nct = SGL_MAX_COLOR_TARGETS;

  WGPUColorTargetState color_targets[SGL_MAX_COLOR_TARGETS] = {0};
  WGPUBlendState blend_states[SGL_MAX_COLOR_TARGETS] = {0};

  for (int i = 0; i < nct; ++i) {
    SglPixelFormat cfmt = d->color_fmts[i];
    color_targets[i].format =
        cfmt ? sgl_to_wgpu_fmt(cfmt) : WGPUTextureFormat_BGRA8Unorm;
    color_targets[i].writeMask = WGPUColorWriteMask_All;

    if (d->blend != SGL_BLEND_NONE) {
      WGPUBlendState *bs = &blend_states[i];
      switch (d->blend) {
      case SGL_BLEND_ALPHA:
        bs->color.srcFactor = WGPUBlendFactor_SrcAlpha;
        bs->color.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
        bs->color.operation = WGPUBlendOperation_Add;
        bs->alpha.srcFactor = WGPUBlendFactor_One;
        bs->alpha.dstFactor = WGPUBlendFactor_OneMinusSrcAlpha;
        bs->alpha.operation = WGPUBlendOperation_Add;
        break;
      case SGL_BLEND_ADDITIVE:
        bs->color.srcFactor = WGPUBlendFactor_One;
        bs->color.dstFactor = WGPUBlendFactor_One;
        bs->color.operation = WGPUBlendOperation_Add;
        bs->alpha.srcFactor = WGPUBlendFactor_One;
        bs->alpha.dstFactor = WGPUBlendFactor_One;
        bs->alpha.operation = WGPUBlendOperation_Add;
        break;
      case SGL_BLEND_MULTIPLY:
        bs->color.srcFactor = WGPUBlendFactor_Dst;
        bs->color.dstFactor = WGPUBlendFactor_Zero;
        bs->color.operation = WGPUBlendOperation_Add;
        bs->alpha.srcFactor = WGPUBlendFactor_Dst;
        bs->alpha.dstFactor = WGPUBlendFactor_Zero;
        bs->alpha.operation = WGPUBlendOperation_Add;
        break;
      default:
        break;
      }
      color_targets[i].blend = bs;
    }
  }

  WGPUFragmentState fs = {
      .module = ws->fs_mod,
      .entryPoint = wg_sv("fs_main"),
      .targetCount = (size_t)nct,
      .targets = color_targets,
  };

  // Depth-only: WebGPU rejects a fragment output with no matching color
  // target, so drop the fragment stage entirely (depth still writes).
  WGPURenderPipelineDescriptor rpd = {
      .layout = wp->layout,
      .vertex = vs,
      .primitive =
          {
              .topology = sgl_to_wgpu_prim(d->primitive),
              .stripIndexFormat = WGPUIndexFormat_Undefined,
              .frontFace = WGPUFrontFace_CW,
              .cullMode = sgl_to_wgpu_cull(d->cull),
          },
      .fragment = nct > 0 ? &fs : NULL,
  };

  // Depth/stencil
  WGPUDepthStencilState dss = {0};
  if (d->has_depth) {
    dss.format = sgl_to_wgpu_fmt(d->depth_fmt);
    dss.depthWriteEnabled =
        d->depth_write ? WGPUOptionalBool_True : WGPUOptionalBool_False;
    dss.depthCompare = d->depth_test ? WGPUCompareFunction_LessEqual
                                     : WGPUCompareFunction_Always;
    rpd.depthStencil = &dss;
  }

  // Multisample
  rpd.multisample.count = 1;
  rpd.multisample.mask = 0xFFFFFFFF;

  wp->render = wgpuDeviceCreateRenderPipeline(dev, &rpd);
  if (!wp->render) {
    SDL_Log("[webgpu] render pipeline create failed");
    wgpuPipelineLayoutRelease(wp->layout);
    wgpuBindGroupLayoutRelease(wp->bgl0);
    wgpuBindGroupLayoutRelease(wp->bgl1);
    free(wp);
    return 0;
  }
  gpu_stats_create(GPU_STAT_PIPELINE, 0);
  return (uintptr_t)wp;
}

static void wg_destroy_pipeline(BackendPipeline h) {
  WgPipeline *wp = (WgPipeline *)h;
  if (!wp)
    return;
  if (wp->render)
    wgpuRenderPipelineRelease(wp->render);
  if (wp->compute)
    wgpuComputePipelineRelease(wp->compute);
  if (wp->layout)
    wgpuPipelineLayoutRelease(wp->layout);
  if (wp->bgl0)
    wgpuBindGroupLayoutRelease(wp->bgl0);
  if (wp->bgl1)
    wgpuBindGroupLayoutRelease(wp->bgl1);
  gpu_stats_destroy(GPU_STAT_PIPELINE, 0);
  free(wp);
}

// ---- update buffer / image -------------------------------------------------

static void wg_update_buffer(BackendBuffer h, const void *data, size_t bytes) {
  WgBuffer *wb = (WgBuffer *)h;
  if (!wb || !data || bytes == 0)
    return;
  wgpuQueueWriteBuffer(g_queue, wb->buf, 0, data, bytes);
}

static void wg_update_image(BackendImage h, const void *data, size_t bytes) {
  if (!h || !data || bytes == 0)
    return;
  WgImage *wi = (WgImage *)h;
  uint32_t w = wgpuTextureGetWidth(wi->tex);
  uint32_t hh = wgpuTextureGetHeight(wi->tex);

  int bpp = 4;
  switch (wi->fmt) {
  case SGL_PF_R8:
    bpp = 1;
    break;
  case SGL_PF_RG8:
    bpp = 2;
    break;
  case SGL_PF_R16F:
    bpp = 2;
    break;
  case SGL_PF_RG16F:
    bpp = 4;
    break;
  case SGL_PF_RGBA16F:
    bpp = 8;
    break;
  case SGL_PF_R32F:
    bpp = 4;
    break;
  case SGL_PF_RGBA32F:
    bpp = 16;
    break;
  default:
    bpp = 4;
    break;
  }

  WGPUTexelCopyTextureInfo dst_info = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
  dst_info.texture = wi->tex;
  WGPUTexelCopyBufferLayout layout = {
      .bytesPerRow = w * (uint32_t)bpp,
      .rowsPerImage = hh,
  };
  WGPUExtent3D extent = {w, hh, 1};
  wgpuQueueWriteTexture(g_queue, &dst_info, data, bytes, &layout, &extent);
}

// ---- pass begin / end ------------------------------------------------------

static void wg_begin_pass(App *app, const PassBeginDesc *d) {
  if (!g_enc)
    return;

  // n_color_targets == 0 with a depth target is a depth-only pass; only
  // the legacy swapchain path (targets[0] == 0, no depth target) coerces to 1.
  int nct = d->n_color_targets;
  if (nct <= 0 && !d->depth_target)
    nct = 1;
  if (nct > SGL_MAX_COLOR_TARGETS)
    nct = SGL_MAX_COLOR_TARGETS;
  WGPULoadOp load_op =
      (d->load == SGL_LOAD_LOAD) ? WGPULoadOp_Load : WGPULoadOp_Clear;

  WGPURenderPassColorAttachment colors[SGL_MAX_COLOR_TARGETS] = {0};

  bool is_offscreen = (d->targets[0] != 0 || d->depth_target != 0);

  for (int i = 0; i < nct; ++i) {
    colors[i].depthSlice = WGPU_DEPTH_SLICE_UNDEFINED;
    colors[i].loadOp = load_op;
    colors[i].storeOp = WGPUStoreOp_Store;
    colors[i].clearValue = (WGPUColor){d->clear[i][0], d->clear[i][1],
                                       d->clear[i][2], d->clear[i][3]};

    if (is_offscreen && d->targets[i]) {
      WgImage *wi = (WgImage *)d->targets[i];
      colors[i].view = wi->color_att ? wi->color_att : wi->view;
    } else {
      colors[i].view = app->wgpu_swapchain_view;
    }
  }

  WGPURenderPassDescriptor rpd = {
      .colorAttachmentCount = (size_t)nct,
      .colorAttachments = nct > 0 ? colors : NULL,
  };

  WGPURenderPassDepthStencilAttachment depth_att = {0};
  if (d->has_depth) {
    if (is_offscreen && d->depth_target) {
      WgImage *di = (WgImage *)d->depth_target;
      depth_att.view = di->depth_att ? di->depth_att : di->view;
    } else {
      depth_att.view = app->wgpu_depth_view;
    }
    depth_att.depthLoadOp = load_op;
    depth_att.depthStoreOp = WGPUStoreOp_Store;
    depth_att.depthClearValue = d->clear_depth;
    // Stencil ops are required when the format has a stencil aspect;
    // setting them on a depth-only format (e.g. Depth32Float) is a
    // validation error.
    WGPUTextureFormat depth_wfmt = sgl_to_wgpu_fmt(d->depth_fmt);
    bool has_stencil = (depth_wfmt == WGPUTextureFormat_Depth32FloatStencil8 ||
                        depth_wfmt == WGPUTextureFormat_Depth24PlusStencil8 ||
                        depth_wfmt == WGPUTextureFormat_Stencil8);
    // Swapchain depth is always Depth32FloatStencil8.
    if (!is_offscreen)
      has_stencil = true;
    if (has_stencil) {
      depth_att.stencilLoadOp = load_op;
      depth_att.stencilStoreOp = WGPUStoreOp_Store;
      depth_att.stencilClearValue = 0;
    }
    rpd.depthStencilAttachment = &depth_att;
  }

  g_rpass = wgpuCommandEncoderBeginRenderPass(g_enc, &rpd);

  // Set viewport to match target.
  if (g_rpass) {
    int w, h;
    if (is_offscreen && d->target_w > 0 && d->target_h > 0) {
      w = d->target_w;
      h = d->target_h;
    } else {
      w = app->last_w;
      h = app->last_h;
    }
    wgpuRenderPassEncoderSetViewport(g_rpass, 0, 0, (float)w, (float)h, 0.f,
                                     1.f);
    wgpuRenderPassEncoderSetScissorRect(g_rpass, 0, 0, (uint32_t)w,
                                        (uint32_t)h);
  }

  // Reset per-pass uniform state.
  g_cur_pipeline = NULL;
  g_ibuf_bound = false;
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i)
    g_ub.dirty[i] = false;
}

static void wg_end_pass(App *app) {
  (void)app;
  if (g_rpass) {
    wgpuRenderPassEncoderEnd(g_rpass);
    wgpuRenderPassEncoderRelease(g_rpass);
    g_rpass = NULL;
  }
}

// ---- apply pipeline / bindings / uniforms ----------------------------------

static void wg_apply_pipeline(BackendPipeline p) {
  WgPipeline *wp = (WgPipeline *)p;
  if (!wp || !g_rpass)
    return;
  g_cur_pipeline = wp;
  wgpuRenderPassEncoderSetPipeline(g_rpass, wp->render);
}

static void wg_apply_bindings(const BindingsDesc *b) {
  if (!g_rpass || !g_cur_pipeline)
    return;

  // Vertex / index buffers
  if (b->vbuf) {
    WgBuffer *vb = (WgBuffer *)b->vbuf;
    wgpuRenderPassEncoderSetVertexBuffer(g_rpass, 0, vb->buf, 0, vb->bytes);
  }
  if (b->instance_vbuf) {
    WgBuffer *vb = (WgBuffer *)b->instance_vbuf;
    wgpuRenderPassEncoderSetVertexBuffer(g_rpass, 1, vb->buf, 0, vb->bytes);
  }
  g_ibuf_bound = false;
  if (b->ibuf) {
    WgBuffer *ib = (WgBuffer *)b->ibuf;
    wgpuRenderPassEncoderSetIndexBuffer(g_rpass, ib->buf,
                                        WGPUIndexFormat_Uint32, 0, ib->bytes);
    g_ibuf_bound = true;
  }

  // Build bind group 1: textures + samplers + storage.
  // Always set group 1 — WebKit requires all pipeline bind groups to be bound.
  if (g_cur_pipeline->bgl1) {
    WGPUBindGroupEntry entries[32] = {0};
    int count = 0;

    if (b->refl) {
      for (int i = 0; i < b->texture_count; ++i) {
        const char *name = b->textures[i].name;
        WgImage *wi = (WgImage *)b->textures[i].image;
        if (!name || !wi)
          continue;
        for (int k = 0; k < b->refl->tex_count; ++k) {
          if (strcmp(b->refl->texs[k].name, name) == 0) {
            int img_slot = b->refl->texs[k].img_slot;
            int smp_slot = b->refl->texs[k].smp_slot;
            if (img_slot >= 0) {
              WGPUBindGroupEntry *e = &entries[count++];
              e->binding = (uint32_t)img_slot;
              e->textureView = wi->view;
            }
            if (smp_slot >= 0) {
              WGPUBindGroupEntry *e = &entries[count++];
              e->binding = (uint32_t)smp_slot;
              e->sampler = wi->sampler;
            }
            break;
          }
        }
      }
    }

    WGPUBindGroupDescriptor bgd = {
        .layout = g_cur_pipeline->bgl1,
        .entryCount = (size_t)count,
        .entries = entries,
    };
    WGPUBindGroup bg = wgpuDeviceCreateBindGroup(g_dev, &bgd);
    if (bg) {
      wgpuRenderPassEncoderSetBindGroup(g_rpass, 1, bg, 0, NULL);
      wgpuBindGroupRelease(bg);
    } else {
      SDL_Log("[webgpu] WARN: createBindGroup(group1) failed, count=%d "
              "tex_count=%d bgl1=%p",
              count, b->texture_count, (void *)g_cur_pipeline->bgl1);
    }
  }
}

static void wg_apply_uniforms(SglShaderStage stage, int ub_slot,
                              const void *data, size_t bytes) {
  (void)stage;
  if (ub_slot < 0 || ub_slot >= WG_MAX_UB_SLOTS || !data || bytes == 0)
    return;
  size_t copy = bytes < WG_UB_SIZE ? bytes : WG_UB_SIZE;
  memcpy(g_ub.data[ub_slot], data, copy);
  g_ub.sizes[ub_slot] = copy;
  g_ub.dirty[ub_slot] = true;
}

// Flush uniform data to GPU and bind group 0 before draw/dispatch.
// Uses a ring buffer with dynamic offsets so each draw gets its own
// uniform data region, preventing later draws from overwriting earlier ones.
static void wg_flush_uniforms(void) {
  if (!g_cur_pipeline)
    return;

  bool any_dirty = false;
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i) {
    if (g_ub.dirty[i]) {
      any_dirty = true;
      size_t aligned = wg_align((uint32_t)g_ub.sizes[i], 16);
      if (aligned > WG_UB_SIZE)
        aligned = WG_UB_SIZE;
      uint32_t off = g_ub.ring_offset[i];
      if (off + WG_UB_STRIDE > WG_UB_RING_SIZE)
        off = 0;
      wgpuQueueWriteBuffer(g_queue, g_ub.bufs[i], off, g_ub.data[i], aligned);
      g_ub.ring_offset[i] = off + WG_UB_STRIDE;
    }
  }
  if (!any_dirty && g_cur_pipeline->refl.ub_count == 0)
    return;

  // Build bind group 0 with the uniform buffers, using dynamic offsets.
  WGPUBindGroupEntry entries[WG_MAX_UB_SLOTS] = {0};
  uint32_t dyn_offsets[WG_MAX_UB_SLOTS] = {0};
  int count = 0;
  for (int i = 0; i < g_cur_pipeline->refl.ub_count && i < WG_MAX_UB_SLOTS;
       ++i) {
    int slot = g_cur_pipeline->refl.ubs[i].slot;
    WGPUBindGroupEntry *e = &entries[count];
    e->binding = (uint32_t)slot;
    e->buffer = g_ub.bufs[slot];
    e->offset = 0;
    e->size = WG_UB_SIZE;
    // Dynamic offset = where we last wrote for this slot.
    uint32_t off = g_ub.ring_offset[slot];
    dyn_offsets[count] = (off >= WG_UB_STRIDE) ? (off - WG_UB_STRIDE) : 0;
    count++;
  }
  if (count > 0) {
    WGPUBindGroupDescriptor bgd = {
        .layout = g_cur_pipeline->bgl0,
        .entryCount = (size_t)count,
        .entries = entries,
    };
    WGPUBindGroup bg = wgpuDeviceCreateBindGroup(g_dev, &bgd);
    if (bg) {
      wgpuRenderPassEncoderSetBindGroup(g_rpass, 0, bg, (size_t)count,
                                        dyn_offsets);
      wgpuBindGroupRelease(bg);
    }
  }

  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i)
    g_ub.dirty[i] = false;
}

// ---- draw / dispatch -------------------------------------------------------

static void wg_draw(int base, int count, int instance_count) {
  if (!g_rpass)
    return;
  wg_flush_uniforms();
  if (instance_count < 1)
    instance_count = 1;
  if (g_ibuf_bound) {
    wgpuRenderPassEncoderDrawIndexed(g_rpass, (uint32_t)count,
                                     (uint32_t)instance_count, (uint32_t)base,
                                     0, 0);
  } else {
    wgpuRenderPassEncoderDraw(g_rpass, (uint32_t)count,
                              (uint32_t)instance_count, (uint32_t)base, 0);
  }
}

static void wg_set_scissor(int x, int y, int w, int h) {
  if (!g_rpass)
    return;
  wgpuRenderPassEncoderSetScissorRect(g_rpass, (uint32_t)x, (uint32_t)y,
                                      (uint32_t)w, (uint32_t)h);
}

static void wg_dispatch(App *app, const ComputeDispatchDesc *d) {
  (void)app;
  if (!d || !d->pipeline || !d->refl || !g_enc)
    return;

  WgPipeline *wp = (WgPipeline *)d->pipeline;
  if (!wp->compute)
    return;

  // Build bind groups for compute.
  // Group 0: uniforms (ring-buffered with dynamic offsets)
  WGPUBindGroupEntry ub_entries[WG_MAX_UB_SLOTS] = {0};
  uint32_t ub_dyn_offsets[WG_MAX_UB_SLOTS] = {0};
  int ub_count = 0;
  for (int i = 0; i < d->uniform_count; ++i) {
    int slot = d->uniforms[i].slot;
    if (slot < 0 || slot >= WG_MAX_UB_SLOTS || !d->uniforms[i].data)
      continue;
    size_t aligned = wg_align((uint32_t)d->uniforms[i].bytes, 16);
    if (aligned > WG_UB_SIZE)
      aligned = WG_UB_SIZE;
    uint32_t off = g_ub.ring_offset[slot];
    if (off + WG_UB_ALIGN > WG_UB_RING_SIZE)
      off = 0;
    wgpuQueueWriteBuffer(g_queue, g_ub.bufs[slot], off, d->uniforms[i].data,
                         aligned);
    WGPUBindGroupEntry *e = &ub_entries[ub_count];
    e->binding = (uint32_t)slot;
    e->buffer = g_ub.bufs[slot];
    e->offset = 0;
    e->size = WG_UB_SIZE;
    ub_dyn_offsets[ub_count] = off;
    g_ub.ring_offset[slot] = off + WG_UB_ALIGN;
    ub_count++;
  }

  // Group 1: textures + samplers + storage buffers + storage textures
  WGPUBindGroupEntry res_entries[32] = {0};
  int res_count = 0;

  for (int i = 0; i < d->texture_count; ++i) {
    WgImage *wi = (WgImage *)d->textures[i].image;
    if (!wi || !d->textures[i].name)
      continue;
    for (int k = 0; k < d->refl->tex_count; ++k) {
      if (strcmp(d->refl->texs[k].name, d->textures[i].name) == 0) {
        int img_slot = d->refl->texs[k].img_slot;
        int smp_slot = d->refl->texs[k].smp_slot;
        if (img_slot >= 0) {
          res_entries[res_count].binding = (uint32_t)img_slot;
          res_entries[res_count].textureView = wi->view;
          res_count++;
        }
        if (smp_slot >= 0) {
          res_entries[res_count].binding = (uint32_t)smp_slot;
          res_entries[res_count].sampler = wi->sampler;
          res_count++;
        }
        break;
      }
    }
  }
  for (int i = 0; i < d->n_storage_bufs; ++i) {
    WgBuffer *wb = (WgBuffer *)d->storage_bufs[i].buf;
    if (!wb || !d->storage_bufs[i].name)
      continue;
    for (int k = 0; k < d->refl->storage_buf_count; ++k) {
      if (strcmp(d->refl->storage_bufs[k].name, d->storage_bufs[i].name) == 0) {
        int slot = d->refl->storage_bufs[k].slot;
        res_entries[res_count].binding = (uint32_t)slot;
        res_entries[res_count].buffer = wb->buf;
        res_entries[res_count].size = wb->bytes;
        res_count++;
        break;
      }
    }
  }
  for (int i = 0; i < d->n_storage_textures; ++i) {
    WgImage *wi = (WgImage *)d->storage_textures[i].image;
    if (!wi || !d->storage_textures[i].name)
      continue;
    for (int k = 0; k < d->refl->storage_tex_count; ++k) {
      if (strcmp(d->refl->storage_texs[k].name, d->storage_textures[i].name) ==
          0) {
        int slot = d->refl->storage_texs[k].slot;
        res_entries[res_count].binding = (uint32_t)slot;
        res_entries[res_count].textureView = wi->storage_view;
        res_count++;
        break;
      }
    }
  }

  WGPUBindGroup bg0 = NULL, bg1 = NULL;
  {
    WGPUBindGroupDescriptor bgd = {
        .layout = wp->bgl0,
        .entryCount = (size_t)ub_count,
        .entries = ub_entries,
    };
    bg0 = wgpuDeviceCreateBindGroup(g_dev, &bgd);
  }
  {
    WGPUBindGroupDescriptor bgd = {
        .layout = wp->bgl1,
        .entryCount = (size_t)res_count,
        .entries = res_entries,
    };
    bg1 = wgpuDeviceCreateBindGroup(g_dev, &bgd);
  }

  WGPUComputePassDescriptor cpd = {0};
  WGPUComputePassEncoder cpass =
      wgpuCommandEncoderBeginComputePass(g_enc, &cpd);
  if (cpass) {
    wgpuComputePassEncoderSetPipeline(cpass, wp->compute);
    if (bg0)
      wgpuComputePassEncoderSetBindGroup(cpass, 0, bg0, (size_t)ub_count,
                                         ub_dyn_offsets);
    if (bg1)
      wgpuComputePassEncoderSetBindGroup(cpass, 1, bg1, 0, NULL);
    wgpuComputePassEncoderDispatchWorkgroups(cpass, (uint32_t)d->groups_x,
                                             (uint32_t)d->groups_y,
                                             (uint32_t)d->groups_z);
    wgpuComputePassEncoderEnd(cpass);
    wgpuComputePassEncoderRelease(cpass);
  }
  if (bg0)
    wgpuBindGroupRelease(bg0);
  if (bg1)
    wgpuBindGroupRelease(bg1);
}

// ---- readback --------------------------------------------------------------

typedef struct WgReadbackRequest {
  WGPUBuffer buf;
  size_t map_bytes;
  uint32_t src_stride;
  int w, h, bpp;
  SglPixelFormat src_fmt;
  bool done;
  bool cancelled;
  WGPUMapAsyncStatus status;
} WgReadbackRequest;

static int wg_readback_bpp(SglPixelFormat fmt) {
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

static void wg_readback_convert_rgba8(SglPixelFormat fmt, const uint8_t *src,
                                      uint32_t stride, uint8_t *dst, int w,
                                      int h) {
  for (int y = 0; y < h; ++y) {
    const uint8_t *row = src + (size_t)y * stride;
    uint8_t *out = dst + (size_t)y * (size_t)w * 4;
    if (fmt == SGL_PF_RGBA8) {
      memcpy(out, row, (size_t)w * 4);
    } else if (fmt == SGL_PF_BGRA8) {
      for (int x = 0; x < w; ++x) {
        out[x * 4 + 0] = row[x * 4 + 2];
        out[x * 4 + 1] = row[x * 4 + 1];
        out[x * 4 + 2] = row[x * 4 + 0];
        out[x * 4 + 3] = row[x * 4 + 3];
      }
    } else if (fmt == SGL_PF_R8) {
      for (int x = 0; x < w; ++x) {
        uint8_t v = row[x];
        out[x * 4 + 0] = v;
        out[x * 4 + 1] = v;
        out[x * 4 + 2] = v;
        out[x * 4 + 3] = 255;
      }
    }
  }
}

static void wg_readback_release_buf(WgReadbackRequest *req) {
  if (!req || !req->buf)
    return;
  if (req->done && req->status == WGPUMapAsyncStatus_Success)
    wgpuBufferUnmap(req->buf);
  wgpuBufferRelease(req->buf);
  req->buf = NULL;
}

static void wg_readback_callback(WGPUMapAsyncStatus status,
                                 WGPUStringView message, void *userdata1,
                                 void *userdata2) {
  (void)message;
  (void)userdata2;
  WgReadbackRequest *req = (WgReadbackRequest *)userdata1;
  if (!req)
    return;
  req->status = status;
  req->done = true;
  if (req->cancelled) {
    wg_readback_release_buf(req);
    free(req);
  }
}

static bool wg_request_readback_image(App *app, BackendImage image, int w,
                                      int h, SglPixelFormat src_fmt,
                                      BackendReadback *out) {
  if (!out)
    return false;
  *out = 0;
  if (!app || !image || w <= 0 || h <= 0)
    return false;
  WgImage *wi = (WgImage *)image;
  int bpp = wg_readback_bpp(src_fmt);
  if (bpp == 0)
    return false;

  // Submit pending work so render-target writes are visible.
  if (g_enc) {
    WGPUCommandBufferDescriptor cmd_desc = {0};
    WGPUCommandBuffer cmd = wgpuCommandEncoderFinish(g_enc, &cmd_desc);
    wgpuQueueSubmit(g_queue, 1, &cmd);
    wgpuCommandBufferRelease(cmd);
    wgpuCommandEncoderRelease(g_enc);
    // Re-create encoder for rest of frame.
    WGPUCommandEncoderDescriptor enc_desc = {0};
    g_enc = wgpuDeviceCreateCommandEncoder(app->wgpu_device, &enc_desc);
  }

  uint32_t tight_stride = (uint32_t)w * (uint32_t)bpp;
  uint32_t src_stride = wg_align(tight_stride, 256);
  size_t map_bytes = (size_t)src_stride * (size_t)h;

  WgReadbackRequest *req =
      (WgReadbackRequest *)calloc(1, sizeof(WgReadbackRequest));
  if (!req)
    return false;
  req->map_bytes = map_bytes;
  req->src_stride = src_stride;
  req->w = w;
  req->h = h;
  req->bpp = bpp;
  req->src_fmt = src_fmt;
  req->status = WGPUMapAsyncStatus_Error;

  WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
  bd.usage = WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead;
  bd.size = (uint64_t)map_bytes;
  req->buf = wgpuDeviceCreateBuffer(app->wgpu_device, &bd);
  if (!req->buf) {
    free(req);
    return false;
  }

  WGPUCommandEncoderDescriptor enc_desc = {0};
  WGPUCommandEncoder enc =
      wgpuDeviceCreateCommandEncoder(app->wgpu_device, &enc_desc);
  if (!enc) {
    wg_readback_release_buf(req);
    free(req);
    return false;
  }

  WGPUTexelCopyTextureInfo src = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
  src.texture = wi->tex;
  src.aspect = WGPUTextureAspect_All;
  WGPUTexelCopyBufferInfo dst = WGPU_TEXEL_COPY_BUFFER_INFO_INIT;
  dst.buffer = req->buf;
  dst.layout.bytesPerRow = src_stride;
  dst.layout.rowsPerImage = (uint32_t)h;
  WGPUExtent3D extent = {(uint32_t)w, (uint32_t)h, 1};
  wgpuCommandEncoderCopyTextureToBuffer(enc, &src, &dst, &extent);

  WGPUCommandBufferDescriptor cmd_desc = {0};
  WGPUCommandBuffer cmd = wgpuCommandEncoderFinish(enc, &cmd_desc);
  wgpuCommandEncoderRelease(enc);
  if (!cmd) {
    wg_readback_release_buf(req);
    free(req);
    return false;
  }
  wgpuQueueSubmit(g_queue, 1, &cmd);
  wgpuCommandBufferRelease(cmd);

  WGPUBufferMapCallbackInfo cb = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
  cb.mode = WGPUCallbackMode_AllowSpontaneous;
  cb.callback = wg_readback_callback;
  cb.userdata1 = req;
  wgpuBufferMapAsync(req->buf, WGPUMapMode_Read, 0, map_bytes, cb);

  *out = (BackendReadback)req;
  return true;
}

static ReadbackPollStatus wg_poll_readback(BackendReadback h,
                                           ReadbackResult *out) {
  if (!h || !out)
    return READBACK_POLL_ERROR;
  WgReadbackRequest *req = (WgReadbackRequest *)h;
  if (!req->done)
    return READBACK_POLL_PENDING;
  if (req->status != WGPUMapAsyncStatus_Success)
    return READBACK_POLL_ERROR;

  const uint8_t *mapped = (const uint8_t *)wgpuBufferGetConstMappedRange(
      req->buf, 0, req->map_bytes);
  if (!mapped)
    return READBACK_POLL_ERROR;

  size_t dst_stride = (size_t)req->w * 4;
  size_t dst_bytes = dst_stride * (size_t)req->h;
  uint8_t *rgba = (uint8_t *)malloc(dst_bytes);
  if (!rgba)
    return READBACK_POLL_ERROR;
  wg_readback_convert_rgba8(req->src_fmt, mapped, req->src_stride, rgba, req->w,
                            req->h);
  wg_readback_release_buf(req);

  out->w = req->w;
  out->h = req->h;
  out->stride = (int)dst_stride;
  out->fmt = SGL_PF_RGBA8;
  out->data = rgba;
  out->data_bytes = dst_bytes;
  return READBACK_POLL_READY;
}

static void wg_destroy_readback(BackendReadback h) {
  if (!h)
    return;
  WgReadbackRequest *req = (WgReadbackRequest *)h;
  if (!req->done) {
    req->cancelled = true;
    return;
  }
  wg_readback_release_buf(req);
  free(req);
}

// ---- capture / misc --------------------------------------------------------

// Synchronous swapchain capture for golden tests (--capture). Flushes the
// frame's pending commands, copies the swapchain texture into a mappable
// buffer, and blocks on the async map via ASYNCIFY (emscripten_sleep) — the
// WebGPU equivalent of sg_capture's fence wait. Runs before end_frame
// (capture_before_end_frame) because end_frame releases the surface texture.
static bool wg_capture(App *app, const char *path) {
  if (!app || !app->wgpu_device || !app->wgpu_swapchain_tex || !path)
    return false;
  int w = (int)wgpuTextureGetWidth(app->wgpu_swapchain_tex);
  int h = (int)wgpuTextureGetHeight(app->wgpu_swapchain_tex);
  if (w <= 0 || h <= 0) {
    SDL_Log("[webgpu] capture: zero extent");
    return false;
  }

  if (g_enc) {
    WGPUCommandBufferDescriptor cmd_desc = {0};
    WGPUCommandBuffer cmd = wgpuCommandEncoderFinish(g_enc, &cmd_desc);
    wgpuQueueSubmit(g_queue, 1, &cmd);
    wgpuCommandBufferRelease(cmd);
    wgpuCommandEncoderRelease(g_enc);
    // Re-create encoder for rest of frame.
    WGPUCommandEncoderDescriptor enc_desc = {0};
    g_enc = wgpuDeviceCreateCommandEncoder(app->wgpu_device, &enc_desc);
  }

  uint32_t src_stride = wg_align((uint32_t)w * 4u, 256);
  size_t map_bytes = (size_t)src_stride * (size_t)h;

  WgReadbackRequest *req =
      (WgReadbackRequest *)calloc(1, sizeof(WgReadbackRequest));
  if (!req)
    return false;
  req->map_bytes = map_bytes;
  req->src_stride = src_stride;
  req->w = w;
  req->h = h;
  req->bpp = 4;
  // wgpu_surface_format is BGRA8Unorm (wg_init).
  req->src_fmt = SGL_PF_BGRA8;
  req->status = WGPUMapAsyncStatus_Error;

  WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
  bd.usage = WGPUBufferUsage_CopyDst | WGPUBufferUsage_MapRead;
  bd.size = (uint64_t)map_bytes;
  req->buf = wgpuDeviceCreateBuffer(app->wgpu_device, &bd);
  if (!req->buf) {
    free(req);
    return false;
  }

  WGPUCommandEncoderDescriptor enc_desc = {0};
  WGPUCommandEncoder enc =
      wgpuDeviceCreateCommandEncoder(app->wgpu_device, &enc_desc);
  if (!enc) {
    wg_readback_release_buf(req);
    free(req);
    return false;
  }
  WGPUTexelCopyTextureInfo src = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
  src.texture = app->wgpu_swapchain_tex;
  src.aspect = WGPUTextureAspect_All;
  WGPUTexelCopyBufferInfo dst = WGPU_TEXEL_COPY_BUFFER_INFO_INIT;
  dst.buffer = req->buf;
  dst.layout.bytesPerRow = src_stride;
  dst.layout.rowsPerImage = (uint32_t)h;
  WGPUExtent3D extent = {(uint32_t)w, (uint32_t)h, 1};
  wgpuCommandEncoderCopyTextureToBuffer(enc, &src, &dst, &extent);
  WGPUCommandBufferDescriptor cmd_desc = {0};
  WGPUCommandBuffer cmd = wgpuCommandEncoderFinish(enc, &cmd_desc);
  wgpuCommandEncoderRelease(enc);
  if (!cmd) {
    wg_readback_release_buf(req);
    free(req);
    return false;
  }
  wgpuQueueSubmit(g_queue, 1, &cmd);
  wgpuCommandBufferRelease(cmd);

  WGPUBufferMapCallbackInfo cb = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
  cb.mode = WGPUCallbackMode_AllowSpontaneous;
  cb.callback = wg_readback_callback;
  cb.userdata1 = req;
  wgpuBufferMapAsync(req->buf, WGPUMapMode_Read, 0, map_bytes, cb);

  for (int waited_ms = 0; !req->done && waited_ms < 10000; waited_ms += 5)
    emscripten_sleep(5);
  if (!req->done) {
    SDL_Log("[webgpu] capture: map timed out");
    req->cancelled = true; // callback frees req if it ever fires
    return false;
  }
  if (req->status != WGPUMapAsyncStatus_Success) {
    SDL_Log("[webgpu] capture: map failed (status=%d)", (int)req->status);
    wg_readback_release_buf(req);
    free(req);
    return false;
  }

  const uint8_t *mapped = (const uint8_t *)wgpuBufferGetConstMappedRange(
      req->buf, 0, req->map_bytes);
  bool ok = false;
  if (mapped) {
    size_t dst_stride = (size_t)w * 4;
    uint8_t *rgba = (uint8_t *)malloc(dst_stride * (size_t)h);
    if (rgba) {
      wg_readback_convert_rgba8(SGL_PF_BGRA8, mapped, req->src_stride, rgba, w,
                                h);
      ok = stbi_write_png(path, w, h, 4, rgba, (int)dst_stride) != 0;
      if (!ok)
        SDL_Log("[webgpu] capture: stbi_write_png failed");
      free(rgba);
    }
  }
  wg_readback_release_buf(req);
  free(req);
  return ok;
}

static SglPixelFormat wg_swapchain_color_format(App *app) {
  (void)app;
  return SGL_PF_BGRA8;
}

// ---- vtable ----------------------------------------------------------------

const RenderBackend g_backend_webgpu = {
    .name = "webgpu",
    .init = wg_init,
    .shutdown = wg_shutdown,
    .begin_frame = wg_begin_frame,
    .end_frame = wg_end_frame,
    .make_buffer = wg_make_buffer,
    .make_image = wg_make_image,
    .make_shader = wg_make_shader,
    .make_pipeline = wg_make_pipeline,
    .destroy_buffer = wg_destroy_buffer,
    .destroy_image = wg_destroy_image,
    .destroy_shader = wg_destroy_shader,
    .destroy_pipeline = wg_destroy_pipeline,
    .update_buffer = wg_update_buffer,
    .update_image = wg_update_image,
    .begin_pass = wg_begin_pass,
    .end_pass = wg_end_pass,
    .apply_pipeline = wg_apply_pipeline,
    .apply_bindings = wg_apply_bindings,
    .apply_uniforms = wg_apply_uniforms,
    .draw = wg_draw,
    .set_scissor = wg_set_scissor,
    .dispatch = wg_dispatch,
    .request_readback_image = wg_request_readback_image,
    .poll_readback = wg_poll_readback,
    .destroy_readback = wg_destroy_readback,
    .capture = wg_capture,
    .capture_before_end_frame = true,
    .swapchain_color_format = wg_swapchain_color_format,
};

#else
typedef int _wg_empty_tu;
#endif // __EMSCRIPTEN__
