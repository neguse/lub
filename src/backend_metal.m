// Metal backend (macOS / iOS).
//
// Sequence per frame:
//   begin_frame: commandBuffer -> nextDrawable
//   begin_pass : renderCommandEncoder
//   end_pass   : endEncoding
//   end_frame  : presentDrawable -> commit
//
// One command buffer carries the whole frame, so passes, compute dispatches
// and blits run in the order they were recorded. Shaders arrive as MSL source
// (SHADER_TARGET_METAL) and the reflection slots are the [[buffer]] /
// [[texture]] / [[sampler]] indices of each stage, bound as-is.
#include "app.h"
#include "backend.h"
#include "gpu_stats.h"
#include "host_api.h"
#include "stb_image_write.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_metal.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>

// D24 does not exist on Apple GPUs and lub has no stencil operations, so both
// the default depth buffer and DEPTH24_STENCIL8 images are Depth32Float.
#define MT_DEFAULT_DEPTH_FORMAT MTLPixelFormatDepth32Float

// setVertexBytes and friends take single-use data smaller than 4 KB.
#define MT_INLINE_UNIFORM_MAX 4096

// --- per-resource backend objects ----------------------------------------
// Handles are retained Objective-C objects; destroy_* releases them. The GPU
// objects inside stay alive while a command buffer still references them.

@interface MtBuffer : NSObject {
@public
  id<MTLBuffer> gpu;
  size_t bytes;
}
@end
@implementation MtBuffer
@end

@interface MtImage : NSObject {
@public
  id<MTLTexture> tex;
  id<MTLSamplerState> smp;
  int w, h;
  SglPixelFormat fmt;
  bool render_target;
  bool storage;
}
@end
@implementation MtImage
@end

@interface MtShader : NSObject {
@public
  id<MTLFunction> vs;
  id<MTLFunction> fs;
  id<MTLComputePipelineState> compute;
  ShaderReflection refl;
}
@end
@implementation MtShader
@end

@interface MtPipeline : NSObject {
@public
  id<MTLRenderPipelineState> gpu;
  id<MTLDepthStencilState> depth;
  MTLCullMode cull;
  MTLPrimitiveType primitive;
  id<MTLComputePipelineState> compute;
  ShaderReflection refl;
}
@end
@implementation MtPipeline
@end

@interface MtReadback : NSObject {
@public
  ReadbackResult rb;
}
@end
@implementation MtReadback
- (void)dealloc {
  free(rb.data);
}
@end

#define MT_HANDLE(obj) ((uintptr_t)(__bridge_retained void *)(obj))
#define MT_OBJ(type, h) ((__bridge type *)(void *)(h))
#define MT_RELEASE(h) ((void)(__bridge_transfer id)(void *)(h))

// --- backend state --------------------------------------------------------

static id<MTLDevice> g_device;
static id<MTLCommandQueue> g_queue;
static SDL_MetalView g_view;
static CAMetalLayer *g_layer;
static id<MTLCommandBuffer> g_cmd;     // current frame
static id<CAMetalDrawable> g_drawable; // current frame; nil while occluded
static id<MTLTexture> g_depth;         // drawable-sized default depth

// The part of the drawable the game draws to (lub_host_main_rect): the safe
// area on a notched device, everything otherwise. Swapchain passes clear the
// whole drawable and set their viewport to this rect.
static int g_main_x, g_main_y, g_main_w, g_main_h;

// Render pass state is per begin/end-pass pair. g_pass_x/y/w/h is the
// viewport, which scissor rects are relative to. The index buffer of the
// most recent apply_bindings decides between indexed and non-indexed draws.
static id<MTLRenderCommandEncoder> g_pass;
static int g_pass_x, g_pass_y, g_pass_w, g_pass_h;
static MtPipeline *g_pip;
static id<MTLBuffer> g_ibuf;
static bool g_scissor_empty;

static MTLPixelFormat mt_pixel_format(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_R8:
    return MTLPixelFormatR8Unorm;
  case SGL_PF_RG8:
    return MTLPixelFormatRG8Unorm;
  case SGL_PF_R16F:
    return MTLPixelFormatR16Float;
  case SGL_PF_RG16F:
    return MTLPixelFormatRG16Float;
  case SGL_PF_R32F:
    return MTLPixelFormatR32Float;
  case SGL_PF_RGBA16F:
    return MTLPixelFormatRGBA16Float;
  case SGL_PF_RGBA32F:
    return MTLPixelFormatRGBA32Float;
  case SGL_PF_DEPTH16:
    return MTLPixelFormatDepth16Unorm;
  case SGL_PF_DEPTH32F:
  case SGL_PF_DEPTH24_STENCIL8:
    return MT_DEFAULT_DEPTH_FORMAT;
  case SGL_PF_BGRA8:
    return MTLPixelFormatBGRA8Unorm;
  case SGL_PF_RGBA8:
  default:
    return MTLPixelFormatRGBA8Unorm;
  }
}

static int mt_bytes_per_pixel(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_R8:
    return 1;
  case SGL_PF_RG8:
  case SGL_PF_R16F:
  case SGL_PF_DEPTH16:
    return 2;
  case SGL_PF_RGBA16F:
    return 8;
  case SGL_PF_RGBA32F:
    return 16;
  default:
    return 4;
  }
}

static bool mt_is_depth_format(SglPixelFormat fmt) {
  return fmt == SGL_PF_DEPTH16 || fmt == SGL_PF_DEPTH24_STENCIL8 ||
         fmt == SGL_PF_DEPTH32F;
}

static void mt_ensure_command_buffer(void) {
  if (!g_cmd)
    g_cmd = [g_queue commandBuffer];
}

static void mt_ensure_depth(int w, int h) {
  if (g_depth && (int)g_depth.width == w && (int)g_depth.height == h)
    return;
  if (g_depth)
    gpu_stats_destroy(GPU_STAT_TEXTURE,
                      gpu_stats_image_bytes(SGL_PF_DEPTH24_STENCIL8,
                                            (int)g_depth.width,
                                            (int)g_depth.height));
  MTLTextureDescriptor *td = [MTLTextureDescriptor
      texture2DDescriptorWithPixelFormat:MT_DEFAULT_DEPTH_FORMAT
                                   width:(NSUInteger)w
                                  height:(NSUInteger)h
                               mipmapped:NO];
  td.usage = MTLTextureUsageRenderTarget;
  td.storageMode = MTLStorageModePrivate;
  g_depth = [g_device newTextureWithDescriptor:td];
  if (g_depth)
    gpu_stats_create(GPU_STAT_TEXTURE,
                     gpu_stats_image_bytes(SGL_PF_DEPTH24_STENCIL8, w, h));
}

// --- backend lifecycle ----------------------------------------------------

static bool mt_init(App *app) {
  @autoreleasepool {
    g_device = MTLCreateSystemDefaultDevice();
    if (!g_device) {
      SDL_Log("metal: no Metal device");
      return false;
    }
    g_queue = [g_device newCommandQueue];
    g_view = SDL_Metal_CreateView(app->window);
    if (!g_view) {
      SDL_Log("metal: SDL_Metal_CreateView failed: %s", SDL_GetError());
      g_queue = nil;
      g_device = nil;
      return false;
    }
    g_layer = (__bridge CAMetalLayer *)SDL_Metal_GetLayer(g_view);
    g_layer.device = g_device;
    g_layer.pixelFormat = MTLPixelFormatBGRA8Unorm;
    // capture copies the drawable back, which a framebuffer-only one refuses.
    g_layer.framebufferOnly = NO;
    // A capture run draws at the requested size, not at what the window got:
    // the drawable is ours to size, and the layer scales it for display.
    if (app->capture.pending) {
      app->fixed_w = app->cfg_w > 0 ? app->cfg_w : LUB_DEFAULT_WINDOW_W;
      app->fixed_h = app->cfg_h > 0 ? app->cfg_h : LUB_DEFAULT_WINDOW_H;
    }
    SDL_Log("metal: device: %s", g_device.name.UTF8String);
    return true;
  }
}

static void mt_shutdown(App *app) {
  (void)app;
  @autoreleasepool {
    if (g_cmd) {
      [g_cmd commit];
      [g_cmd waitUntilCompleted];
    }
    if (g_depth)
      gpu_stats_destroy(GPU_STAT_TEXTURE,
                        gpu_stats_image_bytes(SGL_PF_DEPTH24_STENCIL8,
                                              (int)g_depth.width,
                                              (int)g_depth.height));
    g_pass = nil;
    g_pip = nil;
    g_ibuf = nil;
    g_cmd = nil;
    g_drawable = nil;
    g_depth = nil;
    g_layer = nil;
    if (g_view)
      SDL_Metal_DestroyView(g_view);
    g_view = NULL;
    g_queue = nil;
    g_device = nil;
  }
}

static void mt_begin_frame(App *app, int *out_w, int *out_h) {
  @autoreleasepool {
    int w = 0, h = 0;
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
    if (app->fixed_w > 0 && app->fixed_h > 0) {
      w = app->fixed_w;
      h = app->fixed_h;
    }
    if (w > 0 && h > 0) {
      CGSize size = g_layer.drawableSize;
      if ((int)size.width != w || (int)size.height != h)
        g_layer.drawableSize = CGSizeMake(w, h);
    }
    app->pending_resize = false;
    g_cmd = [g_queue commandBuffer];
    g_drawable = [g_layer nextDrawable];
    w = h = 0;
    if (g_drawable) {
      w = (int)g_drawable.texture.width;
      h = (int)g_drawable.texture.height;
      mt_ensure_depth(w, h);
      g_main_x = g_main_y = 0;
      g_main_w = w;
      g_main_h = h;
      int mx = 0, my = 0, mw = 0, mh = 0;
      lub_host_main_rect(app, &mx, &my, &mw, &mh);
      if (mw > 0 && mh > 0 && mx + mw <= w && my + mh <= h) {
        g_main_x = mx;
        g_main_y = my;
        w = g_main_w = mw;
        h = g_main_h = mh;
      }
    }
    if (out_w)
      *out_w = w;
    if (out_h)
      *out_h = h;
  }
}

static void mt_end_frame(App *app) {
  (void)app;
  @autoreleasepool {
    if (g_cmd) {
      if (g_drawable)
        [g_cmd presentDrawable:g_drawable];
      [g_cmd commit];
    }
    g_cmd = nil;
    g_drawable = nil;
  }
}

static void mt_begin_pass(App *app, const PassBeginDesc *d) {
  (void)app;
  @autoreleasepool {
    g_pass = nil;
    g_pip = nil;
    g_ibuf = nil;
    g_scissor_empty = false;
    mt_ensure_command_buffer();
    MTLLoadAction load =
        (d->load == SGL_LOAD_LOAD) ? MTLLoadActionLoad : MTLLoadActionClear;
    MTLRenderPassDescriptor *rp =
        [MTLRenderPassDescriptor renderPassDescriptor];
    if (d->n_color_targets == 1 && d->targets[0] == 0 && !d->depth_target) {
      // swapchain target (single) with the default depth buffer
      if (!g_drawable || !g_depth)
        return;
      rp.colorAttachments[0].texture = g_drawable.texture;
      rp.colorAttachments[0].loadAction = load;
      rp.colorAttachments[0].storeAction = MTLStoreActionStore;
      rp.colorAttachments[0].clearColor = MTLClearColorMake(
          d->clear[0][0], d->clear[0][1], d->clear[0][2], d->clear[0][3]);
      rp.depthAttachment.texture = g_depth;
      rp.depthAttachment.loadAction = load;
      // Store so a later swapchain pass with load = LOAD sees valid depth.
      rp.depthAttachment.storeAction = MTLStoreActionStore;
      rp.depthAttachment.clearDepth = 1.0;
      g_pass_x = g_main_x;
      g_pass_y = g_main_y;
      g_pass_w = g_main_w;
      g_pass_h = g_main_h;
    } else {
      int nct = d->n_color_targets > 0 ? d->n_color_targets : 0;
      if (nct > SGL_MAX_COLOR_TARGETS)
        nct = SGL_MAX_COLOR_TARGETS;
      id<MTLTexture> first = nil;
      for (int i = 0; i < nct; ++i) {
        MtImage *im = MT_OBJ(MtImage, d->targets[i]);
        if (!im || !im->tex)
          return;
        rp.colorAttachments[i].texture = im->tex;
        rp.colorAttachments[i].loadAction = load;
        rp.colorAttachments[i].storeAction = MTLStoreActionStore;
        rp.colorAttachments[i].clearColor = MTLClearColorMake(
            d->clear[i][0], d->clear[i][1], d->clear[i][2], d->clear[i][3]);
        if (!first)
          first = im->tex;
      }
      if (d->depth_target) {
        MtImage *di = MT_OBJ(MtImage, d->depth_target);
        if (!di || !di->tex)
          return;
        rp.depthAttachment.texture = di->tex;
        rp.depthAttachment.loadAction = load;
        rp.depthAttachment.storeAction = MTLStoreActionStore;
        rp.depthAttachment.clearDepth = d->clear_depth;
        if (!first)
          first = di->tex;
      }
      if (!first)
        return;
      g_pass_x = g_pass_y = 0;
      g_pass_w = (int)first.width;
      g_pass_h = (int)first.height;
    }
    g_pass = [g_cmd renderCommandEncoderWithDescriptor:rp];
    [g_pass setViewport:(MTLViewport){g_pass_x, g_pass_y, g_pass_w, g_pass_h,
                                      0.0, 1.0}];
    [g_pass setScissorRect:(MTLScissorRect){
                               (NSUInteger)g_pass_x, (NSUInteger)g_pass_y,
                               (NSUInteger)g_pass_w, (NSUInteger)g_pass_h}];
  }
}

static void mt_end_pass(App *app) {
  (void)app;
  if (g_pass)
    [g_pass endEncoding];
  g_pass = nil;
  g_pip = nil;
  g_ibuf = nil;
}

// --- resources ------------------------------------------------------------

static id<MTLBuffer> mt_new_buffer(const void *data, size_t bytes,
                                   size_t capacity) {
  // Metal rejects zero-length buffers.
  size_t length = capacity > 0 ? capacity : 16;
  id<MTLBuffer> buf =
      [g_device newBufferWithLength:length
                            options:MTLResourceStorageModeShared];
  if (buf && data && bytes > 0)
    memcpy(buf.contents, data, bytes < length ? bytes : length);
  return buf;
}

static BackendBuffer mt_make_buffer(SglBufferType type, const void *data,
                                    size_t bytes) {
  if (!g_device) {
    SDL_Log("mt_make_buffer: no Metal device");
    return 0;
  }
  if (type != SGL_BUFFER_INDEX && type != SGL_BUFFER_STORAGE) {
    SDL_Log("mt_make_buffer: unsupported buffer type %d", (int)type);
    return 0;
  }
  @autoreleasepool {
    MtBuffer *b = [MtBuffer new];
    b->bytes = bytes;
    b->gpu = mt_new_buffer(data, bytes, bytes);
    if (!b->gpu) {
      SDL_Log("mt_make_buffer: newBufferWithLength failed (%zu bytes)", bytes);
      return 0;
    }
    gpu_stats_create(GPU_STAT_BUFFER, b->bytes);
    return MT_HANDLE(b);
  }
}

static void mt_destroy_buffer(BackendBuffer h) {
  if (!h)
    return;
  gpu_stats_destroy(GPU_STAT_BUFFER, MT_OBJ(MtBuffer, h)->bytes);
  MT_RELEASE(h);
}

// Draws already recorded this frame must keep reading the old contents, and
// they only run at commit. So an update never writes in place: it swaps in a
// fresh buffer, and the command buffer keeps the old one alive until it ran.
static void mt_update_buffer(BackendBuffer h, const void *data, size_t bytes) {
  if (!h || !data || bytes == 0)
    return;
  @autoreleasepool {
    MtBuffer *b = MT_OBJ(MtBuffer, h);
    id<MTLBuffer> fresh = mt_new_buffer(data, bytes, b->bytes);
    if (!fresh) {
      SDL_Log("mt_update_buffer: newBufferWithLength failed (%zu bytes)",
              b->bytes);
      return;
    }
    b->gpu = fresh;
  }
}

static id<MTLTexture> mt_new_texture(MtImage *im, const void *data,
                                     size_t bytes) {
  MTLTextureDescriptor *td = [MTLTextureDescriptor
      texture2DDescriptorWithPixelFormat:mt_pixel_format(im->fmt)
                                   width:(NSUInteger)im->w
                                  height:(NSUInteger)im->h
                               mipmapped:NO];
  td.usage = MTLTextureUsageShaderRead;
  if (im->render_target)
    td.usage |= MTLTextureUsageRenderTarget;
  if (im->storage)
    td.usage |= MTLTextureUsageShaderWrite;
  bool upload = data && bytes > 0;
  // Depth textures can't be CPU-visible on every Mac; targets are only ever
  // read back through a blit.
  if (!upload && (im->render_target || mt_is_depth_format(im->fmt)))
    td.storageMode = MTLStorageModePrivate;
  id<MTLTexture> tex = [g_device newTextureWithDescriptor:td];
  if (!tex)
    return nil;
  if (upload) {
    size_t row = (size_t)im->w * (size_t)mt_bytes_per_pixel(im->fmt);
    if (bytes < row * (size_t)im->h) {
      SDL_Log("metal: texture data too small (%zu < %zu bytes)", bytes,
              row * (size_t)im->h);
      return nil;
    }
    [tex replaceRegion:MTLRegionMake2D(0, 0, (NSUInteger)im->w,
                                       (NSUInteger)im->h)
           mipmapLevel:0
             withBytes:data
           bytesPerRow:row];
  }
  return tex;
}

static id<MTLSamplerState> mt_sampler(SglFilter filter, SglWrap wrap) {
  static id<MTLSamplerState> cache[2][2];
  int f = filter == SGL_FILTER_NEAREST ? 1 : 0;
  int c = wrap == SGL_WRAP_CLAMP ? 1 : 0;
  if (!cache[f][c]) {
    MTLSamplerDescriptor *sd = [MTLSamplerDescriptor new];
    sd.minFilter = sd.magFilter =
        f ? MTLSamplerMinMagFilterNearest : MTLSamplerMinMagFilterLinear;
    sd.sAddressMode = sd.tAddressMode = sd.rAddressMode =
        c ? MTLSamplerAddressModeClampToEdge : MTLSamplerAddressModeRepeat;
    cache[f][c] = [g_device newSamplerStateWithDescriptor:sd];
  }
  return cache[f][c];
}

static BackendImage mt_make_image(const ImageDesc *d) {
  if (!g_device) {
    SDL_Log("mt_make_image: no Metal device");
    return 0;
  }
  @autoreleasepool {
    MtImage *im = [MtImage new];
    im->w = d->w;
    im->h = d->h;
    im->fmt = d->fmt;
    im->render_target = d->render_target;
    im->storage = d->storage;
    im->tex =
        mt_new_texture(im, d->render_target ? NULL : d->data, d->data_bytes);
    im->smp = mt_sampler(d->filter, d->wrap);
    if (!im->tex || !im->smp) {
      SDL_Log("mt_make_image: texture create failed (%dx%d fmt %d)", d->w, d->h,
              (int)d->fmt);
      return 0;
    }
    gpu_stats_create(GPU_STAT_TEXTURE,
                     gpu_stats_image_bytes(d->fmt, d->w, d->h));
    return MT_HANDLE(im);
  }
}

static void mt_destroy_image(BackendImage h) {
  if (!h)
    return;
  MtImage *im = MT_OBJ(MtImage, h);
  gpu_stats_destroy(GPU_STAT_TEXTURE,
                    gpu_stats_image_bytes(im->fmt, im->w, im->h));
  MT_RELEASE(h);
}

// Same reasoning as mt_update_buffer: swap in a fresh texture.
static void mt_update_image(BackendImage h, const void *data, size_t bytes) {
  if (!h || !data || bytes == 0)
    return;
  @autoreleasepool {
    MtImage *im = MT_OBJ(MtImage, h);
    id<MTLTexture> fresh = mt_new_texture(im, data, bytes);
    if (!fresh) {
      SDL_Log("mt_update_image: texture create failed");
      return;
    }
    im->tex = fresh;
  }
}

static id<MTLFunction> mt_function(const uint32_t *source, size_t bytes,
                                   NSString *entry) {
  NSString *msl = [[NSString alloc] initWithBytes:source
                                           length:bytes
                                         encoding:NSUTF8StringEncoding];
  NSError *error = nil;
  id<MTLLibrary> lib = [g_device newLibraryWithSource:msl
                                              options:nil
                                                error:&error];
  if (!lib) {
    SDL_Log("mt_make_shader: %s: %s", entry.UTF8String,
            error.localizedDescription.UTF8String);
    return nil;
  }
  id<MTLFunction> fn = [lib newFunctionWithName:entry];
  if (!fn)
    SDL_Log("mt_make_shader: entry point %s not found", entry.UTF8String);
  return fn;
}

static BackendShader mt_make_shader(const ShaderDesc *d) {
  if (!g_device) {
    SDL_Log("mt_make_shader: no Metal device");
    return 0;
  }
  @autoreleasepool {
    MtShader *s = [MtShader new];
    if (d->refl)
      s->refl = *d->refl;
    if (d->cs_spirv) {
      id<MTLFunction> cs = mt_function(d->cs_spirv, d->cs_bytes, @"cs_main");
      if (!cs)
        return 0;
      NSError *error = nil;
      s->compute = [g_device newComputePipelineStateWithFunction:cs
                                                           error:&error];
      if (!s->compute) {
        SDL_Log("mt_make_shader: compute pipeline: %s",
                error.localizedDescription.UTF8String);
        return 0;
      }
      gpu_stats_create(GPU_STAT_PIPELINE, 0);
      return MT_HANDLE(s);
    }
    s->vs = mt_function(d->vs_spirv, d->vs_bytes, @"vs_main");
    s->fs = mt_function(d->fs_spirv, d->fs_bytes, @"fs_main");
    if (!s->vs || !s->fs)
      return 0;
    gpu_stats_create(GPU_STAT_SHADER, 0);
    gpu_stats_create(GPU_STAT_SHADER, 0);
    return MT_HANDLE(s);
  }
}

static void mt_destroy_shader(BackendShader h) {
  if (!h)
    return;
  MtShader *s = MT_OBJ(MtShader, h);
  if (s->compute) {
    gpu_stats_destroy(GPU_STAT_PIPELINE, 0);
  } else {
    gpu_stats_destroy(GPU_STAT_SHADER, 0);
    gpu_stats_destroy(GPU_STAT_SHADER, 0);
  }
  MT_RELEASE(h);
}

static void mt_apply_blend(MTLRenderPipelineColorAttachmentDescriptor *ca,
                           SglBlend blend) {
  switch (blend) {
  case SGL_BLEND_ALPHA:
    ca.blendingEnabled = YES;
    ca.sourceRGBBlendFactor = MTLBlendFactorSourceAlpha;
    ca.destinationRGBBlendFactor = MTLBlendFactorOneMinusSourceAlpha;
    ca.sourceAlphaBlendFactor = MTLBlendFactorOne;
    ca.destinationAlphaBlendFactor = MTLBlendFactorOneMinusSourceAlpha;
    break;
  case SGL_BLEND_ADDITIVE:
    ca.blendingEnabled = YES;
    ca.sourceRGBBlendFactor = MTLBlendFactorOne;
    ca.destinationRGBBlendFactor = MTLBlendFactorOne;
    ca.sourceAlphaBlendFactor = MTLBlendFactorOne;
    ca.destinationAlphaBlendFactor = MTLBlendFactorOne;
    break;
  case SGL_BLEND_MULTIPLY:
    ca.blendingEnabled = YES;
    ca.sourceRGBBlendFactor = MTLBlendFactorDestinationColor;
    ca.destinationRGBBlendFactor = MTLBlendFactorZero;
    ca.sourceAlphaBlendFactor = MTLBlendFactorOne;
    ca.destinationAlphaBlendFactor = MTLBlendFactorZero;
    break;
  default:
    break;
  }
}

static BackendPipeline mt_make_pipeline(const PipelineDesc *d) {
  if (!g_device) {
    SDL_Log("mt_make_pipeline: no Metal device");
    return 0;
  }
  MtShader *sh = MT_OBJ(MtShader, d->shader);
  if (!sh) {
    SDL_Log("mt_make_pipeline: null shader");
    return 0;
  }
  @autoreleasepool {
    MtPipeline *p = [MtPipeline new];
    if (d->refl)
      p->refl = *d->refl;
    if (d->is_compute) {
      if (!sh->compute) {
        SDL_Log("mt_make_pipeline: shader is not compute");
        return 0;
      }
      p->compute = sh->compute;
      return MT_HANDLE(p);
    }
    if (!sh->vs || !sh->fs) {
      SDL_Log("mt_make_pipeline: invalid shader");
      return 0;
    }

    MTLRenderPipelineDescriptor *pd = [MTLRenderPipelineDescriptor new];
    pd.vertexFunction = sh->vs;
    pd.fragmentFunction = sh->fs;
    int nct = d->n_color_targets > 0 ? d->n_color_targets : 0;
    if (nct > SGL_MAX_COLOR_TARGETS)
      nct = SGL_MAX_COLOR_TARGETS;
    for (int i = 0; i < nct; ++i) {
      pd.colorAttachments[i].pixelFormat = mt_pixel_format(d->color_fmts[i]);
      mt_apply_blend(pd.colorAttachments[i], d->blend);
    }
    if (d->has_depth)
      pd.depthAttachmentPixelFormat = mt_pixel_format(d->depth_fmt);
    NSError *error = nil;
    p->gpu = [g_device newRenderPipelineStateWithDescriptor:pd error:&error];
    if (!p->gpu) {
      SDL_Log("mt_make_pipeline: %s", error.localizedDescription.UTF8String);
      return 0;
    }

    MTLDepthStencilDescriptor *dd = [MTLDepthStencilDescriptor new];
    dd.depthCompareFunction = (d->has_depth && d->depth_test)
                                  ? MTLCompareFunctionLessEqual
                                  : MTLCompareFunctionAlways;
    dd.depthWriteEnabled = d->has_depth && d->depth_write;
    p->depth = [g_device newDepthStencilStateWithDescriptor:dd];

    p->cull = (d->cull == SGL_CULL_BACK)    ? MTLCullModeBack
              : (d->cull == SGL_CULL_FRONT) ? MTLCullModeFront
                                            : MTLCullModeNone;
    switch (d->primitive) {
    case SGL_PRIM_LINES:
      p->primitive = MTLPrimitiveTypeLine;
      break;
    case SGL_PRIM_LINE_STRIP:
      p->primitive = MTLPrimitiveTypeLineStrip;
      break;
    case SGL_PRIM_POINTS:
      p->primitive = MTLPrimitiveTypePoint;
      break;
    case SGL_PRIM_TRIANGLE_STRIP:
      p->primitive = MTLPrimitiveTypeTriangleStrip;
      break;
    case SGL_PRIM_TRIANGLES:
    default:
      p->primitive = MTLPrimitiveTypeTriangle;
      break;
    }
    gpu_stats_create(GPU_STAT_PIPELINE, 0);
    return MT_HANDLE(p);
  }
}

static void mt_destroy_pipeline(BackendPipeline h) {
  if (!h)
    return;
  // A compute MtPipeline only borrows the state its MtShader counted.
  if (MT_OBJ(MtPipeline, h)->gpu)
    gpu_stats_destroy(GPU_STAT_PIPELINE, 0);
  if (g_pip == MT_OBJ(MtPipeline, h))
    g_pip = nil;
  MT_RELEASE(h);
}

// --- draw -----------------------------------------------------------------

static void mt_apply_pipeline(BackendPipeline h) {
  g_pip = MT_OBJ(MtPipeline, h);
  if (!g_pass || !g_pip || !g_pip->gpu)
    return;
  [g_pass setRenderPipelineState:g_pip->gpu];
  [g_pass setDepthStencilState:g_pip->depth];
  [g_pass setCullMode:g_pip->cull];
  // Match the runtime's D3D-style LH examples.
  [g_pass setFrontFacingWinding:MTLWindingClockwise];
}

static void mt_apply_bindings(const BindingsDesc *b) {
  if (!g_pass)
    return;
  MtBuffer *ib = MT_OBJ(MtBuffer, b->ibuf);
  g_ibuf = ib ? ib->gpu : nil;
  if (!b->refl)
    return;
  // A name can be declared by both stages; each declaration has its own slot.
  for (int i = 0; i < b->texture_count; ++i) {
    MtImage *im = MT_OBJ(MtImage, b->textures[i].image);
    if (!im || !im->tex || !b->textures[i].name)
      continue;
    for (int j = 0; j < b->refl->tex_count; ++j) {
      const ShaderTexture *t = &b->refl->texs[j];
      if (strcmp(t->name, b->textures[i].name) != 0)
        continue;
      if (t->stage == SGL_STAGE_VERTEX) {
        [g_pass setVertexTexture:im->tex atIndex:(NSUInteger)t->img_slot];
        if (t->smp_slot >= 0)
          [g_pass setVertexSamplerState:im->smp
                                atIndex:(NSUInteger)t->smp_slot];
      } else if (t->stage == SGL_STAGE_FRAGMENT) {
        [g_pass setFragmentTexture:im->tex atIndex:(NSUInteger)t->img_slot];
        if (t->smp_slot >= 0)
          [g_pass setFragmentSamplerState:im->smp
                                  atIndex:(NSUInteger)t->smp_slot];
      }
    }
  }
  for (int i = 0; i < b->storage_buf_count; ++i) {
    MtBuffer *sb = MT_OBJ(MtBuffer, b->storage_bufs[i].buf);
    if (!sb || !sb->gpu || !b->storage_bufs[i].name)
      continue;
    for (int j = 0; j < b->refl->storage_buf_count; ++j) {
      const ShaderStorageBuf *r = &b->refl->storage_bufs[j];
      if (strcmp(r->name, b->storage_bufs[i].name) != 0)
        continue;
      if (r->stage == SGL_STAGE_VERTEX)
        [g_pass setVertexBuffer:sb->gpu offset:0 atIndex:(NSUInteger)r->slot];
      else if (r->stage == SGL_STAGE_FRAGMENT)
        [g_pass setFragmentBuffer:sb->gpu offset:0 atIndex:(NSUInteger)r->slot];
    }
  }
}

// MSL sizes a uniform struct to a multiple of its alignment, which can be
// more than the bytes lub packed, so the data is padded to 16 bytes.
static size_t mt_pad_uniform(const void *data, size_t bytes, uint8_t *out) {
  size_t padded = (bytes + 15) & ~(size_t)15;
  memcpy(out, data, bytes);
  memset(out + bytes, 0, padded - bytes);
  return padded;
}

static void mt_apply_uniforms(SglShaderStage stage, int slot, const void *data,
                              size_t bytes) {
  if (!g_pass || !data || bytes == 0 || slot < 0)
    return;
  if (bytes + 16 <= MT_INLINE_UNIFORM_MAX) {
    uint8_t padded[MT_INLINE_UNIFORM_MAX];
    size_t length = mt_pad_uniform(data, bytes, padded);
    if (stage == SGL_STAGE_FRAGMENT)
      [g_pass setFragmentBytes:padded length:length atIndex:(NSUInteger)slot];
    else
      [g_pass setVertexBytes:padded length:length atIndex:(NSUInteger)slot];
    return;
  }
  @autoreleasepool {
    id<MTLBuffer> buf = mt_new_buffer(data, bytes, (bytes + 15) & ~(size_t)15);
    if (stage == SGL_STAGE_FRAGMENT)
      [g_pass setFragmentBuffer:buf offset:0 atIndex:(NSUInteger)slot];
    else
      [g_pass setVertexBuffer:buf offset:0 atIndex:(NSUInteger)slot];
  }
}

static void mt_draw(int base, int count, int instance_count) {
  if (!g_pass || !g_pip || !g_pip->gpu || g_scissor_empty || count <= 0)
    return;
  NSUInteger instances = (NSUInteger)(instance_count > 0 ? instance_count : 1);
  if (g_ibuf) {
    [g_pass drawIndexedPrimitives:g_pip->primitive
                       indexCount:(NSUInteger)count
                        indexType:MTLIndexTypeUInt32
                      indexBuffer:g_ibuf
                indexBufferOffset:(NSUInteger)base * sizeof(uint32_t)
                    instanceCount:instances];
  } else {
    [g_pass drawPrimitives:g_pip->primitive
               vertexStart:(NSUInteger)base
               vertexCount:(NSUInteger)count
             instanceCount:instances];
  }
}

// Metal rejects a scissor rect that leaves the render target or is empty.
// x / y are relative to the pass viewport.
static void mt_set_scissor(int x, int y, int w, int h) {
  if (!g_pass)
    return;
  int x0 = x < 0 ? 0 : x;
  int y0 = y < 0 ? 0 : y;
  int x1 = x + w > g_pass_w ? g_pass_w : x + w;
  int y1 = y + h > g_pass_h ? g_pass_h : y + h;
  g_scissor_empty = x1 <= x0 || y1 <= y0;
  if (g_scissor_empty)
    return;
  [g_pass setScissorRect:(MTLScissorRect){(NSUInteger)(g_pass_x + x0),
                                          (NSUInteger)(g_pass_y + y0),
                                          (NSUInteger)(x1 - x0),
                                          (NSUInteger)(y1 - y0)}];
}

static void mt_dispatch(App *app, const ComputeDispatchDesc *d) {
  (void)app;
  if (!d || !d->pipeline || !d->refl)
    return;
  MtPipeline *p = MT_OBJ(MtPipeline, d->pipeline);
  if (!p->compute) {
    SDL_Log("mt_dispatch: not a compute pipeline");
    return;
  }
  @autoreleasepool {
    mt_ensure_command_buffer();
    id<MTLComputeCommandEncoder> ce = [g_cmd computeCommandEncoder];
    [ce setComputePipelineState:p->compute];
    for (int i = 0; i < d->n_storage_bufs; ++i) {
      MtBuffer *buf = MT_OBJ(MtBuffer, d->storage_bufs[i].buf);
      if (!buf || !buf->gpu || !d->storage_bufs[i].name)
        continue;
      for (int k = 0; k < d->refl->storage_buf_count; ++k) {
        const ShaderStorageBuf *r = &d->refl->storage_bufs[k];
        if (strcmp(r->name, d->storage_bufs[i].name) == 0) {
          [ce setBuffer:buf->gpu offset:0 atIndex:(NSUInteger)r->slot];
          break;
        }
      }
    }
    for (int i = 0; i < d->texture_count; ++i) {
      MtImage *im = MT_OBJ(MtImage, d->textures[i].image);
      if (!im || !im->tex || !d->textures[i].name)
        continue;
      for (int k = 0; k < d->refl->tex_count; ++k) {
        const ShaderTexture *t = &d->refl->texs[k];
        if (strcmp(t->name, d->textures[i].name) == 0) {
          [ce setTexture:im->tex atIndex:(NSUInteger)t->img_slot];
          if (t->smp_slot >= 0)
            [ce setSamplerState:im->smp atIndex:(NSUInteger)t->smp_slot];
          break;
        }
      }
    }
    for (int i = 0; i < d->n_storage_textures; ++i) {
      MtImage *im = MT_OBJ(MtImage, d->storage_textures[i].image);
      if (!im || !im->tex || !d->storage_textures[i].name)
        continue;
      for (int k = 0; k < d->refl->storage_tex_count; ++k) {
        const ShaderStorageTexture *t = &d->refl->storage_texs[k];
        if (strcmp(t->name, d->storage_textures[i].name) == 0) {
          [ce setTexture:im->tex atIndex:(NSUInteger)t->slot];
          break;
        }
      }
    }
    for (int i = 0; i < d->uniform_count; ++i) {
      size_t bytes = d->uniforms[i].bytes;
      if (d->uniforms[i].slot < 0 || !d->uniforms[i].data || bytes == 0)
        continue;
      NSUInteger slot = (NSUInteger)d->uniforms[i].slot;
      if (bytes + 16 <= MT_INLINE_UNIFORM_MAX) {
        uint8_t padded[MT_INLINE_UNIFORM_MAX];
        size_t length = mt_pad_uniform(d->uniforms[i].data, bytes, padded);
        [ce setBytes:padded length:length atIndex:slot];
      } else {
        [ce setBuffer:mt_new_buffer(d->uniforms[i].data, bytes,
                                    (bytes + 15) & ~(size_t)15)
               offset:0
              atIndex:slot];
      }
    }
    [ce dispatchThreadgroups:MTLSizeMake((NSUInteger)d->groups_x,
                                         (NSUInteger)d->groups_y,
                                         (NSUInteger)d->groups_z)
        threadsPerThreadgroup:MTLSizeMake((NSUInteger)p->refl.workgroup[0],
                                          (NSUInteger)p->refl.workgroup[1],
                                          (NSUInteger)p->refl.workgroup[2])];
    [ce endEncoding];
  }
}

// --- readback / capture ---------------------------------------------------

static int mt_readback_src_bpp(SglPixelFormat fmt) {
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

// Synchronous, like the other native backends: blit the texture into a
// CPU-visible buffer at the end of what the frame recorded so far, run it,
// then open a new command buffer for the rest of the frame.
static bool mt_read_texture(id<MTLTexture> tex, int w, int h,
                            SglPixelFormat src_fmt, ReadbackResult *out) {
  int bpp = mt_readback_src_bpp(src_fmt);
  if (bpp == 0) {
    SDL_Log("metal: unsupported readback format %d", (int)src_fmt);
    return false;
  }
  if (!tex || w <= 0 || h <= 0 || w > (int)tex.width || h > (int)tex.height)
    return false;
  size_t src_stride = (size_t)w * (size_t)bpp;
  size_t src_bytes = src_stride * (size_t)h;
  id<MTLBuffer> buf =
      [g_device newBufferWithLength:src_bytes
                            options:MTLResourceStorageModeShared];
  if (!buf)
    return false;

  mt_ensure_command_buffer();
  id<MTLBlitCommandEncoder> blit = [g_cmd blitCommandEncoder];
  [blit copyFromTexture:tex
                   sourceSlice:0
                   sourceLevel:0
                  sourceOrigin:MTLOriginMake(0, 0, 0)
                    sourceSize:MTLSizeMake((NSUInteger)w, (NSUInteger)h, 1)
                      toBuffer:buf
             destinationOffset:0
        destinationBytesPerRow:src_stride
      destinationBytesPerImage:src_bytes];
  [blit endEncoding];
  [g_cmd commit];
  [g_cmd waitUntilCompleted];
  bool ok = g_cmd.status == MTLCommandBufferStatusCompleted;
  if (!ok)
    SDL_Log("metal: readback command buffer failed: %s",
            g_cmd.error.localizedDescription.UTF8String);
  g_cmd = [g_queue commandBuffer];
  if (!ok)
    return false;

  size_t pixels = (size_t)w * (size_t)h;
  uint8_t *rgba = (uint8_t *)malloc(pixels * 4);
  if (!rgba)
    return false;
  const uint8_t *src = (const uint8_t *)buf.contents;
  if (src_fmt == SGL_PF_RGBA8) {
    memcpy(rgba, src, pixels * 4);
  } else if (src_fmt == SGL_PF_BGRA8) {
    for (size_t i = 0; i < pixels; ++i) {
      rgba[i * 4 + 0] = src[i * 4 + 2];
      rgba[i * 4 + 1] = src[i * 4 + 1];
      rgba[i * 4 + 2] = src[i * 4 + 0];
      rgba[i * 4 + 3] = src[i * 4 + 3];
    }
  } else {
    for (size_t i = 0; i < pixels; ++i) {
      rgba[i * 4 + 0] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = src[i];
      rgba[i * 4 + 3] = 255;
    }
  }
  out->w = w;
  out->h = h;
  out->stride = w * 4;
  out->fmt = SGL_PF_RGBA8;
  out->data = rgba;
  out->data_bytes = pixels * 4;
  return true;
}

static bool mt_request_readback_image(App *app, BackendImage image, int w,
                                      int h, SglPixelFormat src_fmt,
                                      BackendReadback *out) {
  (void)app;
  if (!out)
    return false;
  *out = 0;
  if (!g_device || !image)
    return false;
  @autoreleasepool {
    MtReadback *req = [MtReadback new];
    if (!mt_read_texture(MT_OBJ(MtImage, image)->tex, w, h, src_fmt, &req->rb))
      return false;
    *out = MT_HANDLE(req);
    return true;
  }
}

static ReadbackPollStatus mt_poll_readback(BackendReadback h,
                                           ReadbackResult *out) {
  if (!h || !out)
    return READBACK_POLL_ERROR;
  MtReadback *req = MT_OBJ(MtReadback, h);
  *out = req->rb;
  memset(&req->rb, 0, sizeof(req->rb));
  return READBACK_POLL_READY;
}

static void mt_destroy_readback(BackendReadback h) {
  if (h)
    MT_RELEASE(h);
}

static bool mt_capture(App *app, const char *path) {
  (void)app;
  if (!g_device || !g_drawable || !path)
    return false;
  @autoreleasepool {
    id<MTLTexture> tex = g_drawable.texture;
    ReadbackResult rb = {0};
    if (!mt_read_texture(tex, (int)tex.width, (int)tex.height, SGL_PF_BGRA8,
                         &rb))
      return false;
    int ok = stbi_write_png(path, rb.w, rb.h, 4, rb.data, rb.stride);
    free(rb.data);
    if (!ok)
      SDL_Log("mt_capture: stbi_write_png failed");
    return ok != 0;
  }
}

static SglPixelFormat mt_swapchain_color_format(App *app) {
  (void)app;
  return SGL_PF_BGRA8;
}

const RenderBackend g_backend_metal = {
    .name = "metal",
    .init = mt_init,
    .shutdown = mt_shutdown,
    .begin_frame = mt_begin_frame,
    .end_frame = mt_end_frame,
    .make_buffer = mt_make_buffer,
    .make_image = mt_make_image,
    .make_shader = mt_make_shader,
    .make_pipeline = mt_make_pipeline,
    .destroy_buffer = mt_destroy_buffer,
    .destroy_image = mt_destroy_image,
    .destroy_shader = mt_destroy_shader,
    .destroy_pipeline = mt_destroy_pipeline,
    .update_buffer = mt_update_buffer,
    .update_image = mt_update_image,
    .begin_pass = mt_begin_pass,
    .end_pass = mt_end_pass,
    .apply_pipeline = mt_apply_pipeline,
    .apply_bindings = mt_apply_bindings,
    .apply_uniforms = mt_apply_uniforms,
    .draw = mt_draw,
    .set_scissor = mt_set_scissor,
    .dispatch = mt_dispatch,
    .request_readback_image = mt_request_readback_image,
    .poll_readback = mt_poll_readback,
    .destroy_readback = mt_destroy_readback,
    .capture = mt_capture,
    .capture_before_end_frame = true,
    .swapchain_color_format = mt_swapchain_color_format,
};
