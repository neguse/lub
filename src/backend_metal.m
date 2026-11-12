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
#include "gpu_time.h"
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
  uint64_t gen; // gpu を作るたびに変わる
  size_t bytes;
}
@end
@implementation MtBuffer
@end

@interface MtImage : NSObject {
@public
  id<MTLTexture> tex;
  uint64_t gen; // tex を作るたびに変わる
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
  uint64_t gen;
  id<MTLDepthStencilState> depth;
  MTLCullMode cull;
  MTLPrimitiveType primitive;
  id<MTLComputePipelineState> compute;
  ShaderReflection refl;
}
@end
@implementation MtPipeline
@end

// A readback blits into a shared buffer at the end of what the frame recorded
// so far and completes with the frame's command buffer, which it keeps to
// check (and, when asked to wait, wait for) completion.
@interface MtReadback : NSObject {
@public
  id<MTLBuffer> buf;
  id<MTLCommandBuffer> cmd;
  int w, h;
  SglPixelFormat src_fmt;
}
@end
@implementation MtReadback
@end

#define MT_HANDLE(obj) ((uintptr_t)(__bridge_retained void *)(obj))
#define MT_OBJ(type, h) ((__bridge type *)(void *)(h))
#define MT_RELEASE(h) ((void)(__bridge_transfer id)(void *)(h))

// frame_buffer の範囲を切り出す buffer。
// 使ったフレームの command buffer が終わると g_free_chunks に戻る (完了の handler は別の thread で動く)。
@interface MtChunk : NSObject {
@public
  MtBuffer *whole;
  size_t off;
}
@end
@implementation MtChunk
@end

#define MT_CHUNK_BYTES (4 * 1024 * 1024)
#define MT_CHUNK_KEEP 8
#define MT_FRAME_RANGE_ALIGN 256
static NSMutableArray<MtChunk *> *g_frame_chunks;
static NSMutableArray<MtChunk *> *g_free_chunks;

// --- backend state --------------------------------------------------------

static id<MTLDevice> g_device;
static id<MTLCommandQueue> g_queue;
static SDL_MetalView g_view;
static CAMetalLayer *g_layer;
static id<MTLCommandBuffer> g_cmd;     // current frame
static id<CAMetalDrawable> g_drawable; // current frame; nil while occluded
static id<MTLTexture> g_depth;         // drawable-sized default depth

// The part of the drawable the game draws to (lub_host_main_rect): the safe
// area on a notched device, everything otherwise. Swapchain passes clear (or,
// with load = LOAD, keep) the whole drawable and set their viewport to this
// rect.
static int g_main_x, g_main_y, g_main_w, g_main_h;

// Render pass state is per begin/end-pass pair. g_pass_x/y/w/h is the
// viewport, which scissor rects are relative to. The index buffer of the
// most recent apply_bindings decides between indexed and non-indexed draws.
static id<MTLRenderCommandEncoder> g_pass;
static int g_pass_x, g_pass_y, g_pass_w, g_pass_h;
static MtPipeline *g_pip;
static id<MTLBuffer> g_ibuf;
static size_t g_ibuf_offset;
static bool g_scissor_empty;

// render encoder に設定済みの状態の写しで、同じものを設定し直さないために持つ。
// buffer と texture は実体の世代で比べる (update_* は handle を保ったまま実体を差し替える)。
// encoder ごとの状態なので begin_pass と end_pass で空にする。
// stage の添字は 0 が vertex、1 が fragment。
#define MT_BOUND_SLOTS 32
static uint64_t g_last_gen; // 実体を作るたびに進めるので、0 はどの実体でもない
static struct {
  uint64_t pip;
  int cull; // -1 は未設定
  uint64_t buf[2][MT_BOUND_SLOTS];
  NSUInteger buf_off[2][MT_BOUND_SLOTS];
  uint64_t tex[2][MT_BOUND_SLOTS];
  // sampler は mt_sampler が持ち続けるので pointer で比べられる。
  const void *smp[2][MT_BOUND_SLOTS];
} g_bound;
// uniform を書く chunk で、g_frame_chunks に入っている間だけ使う。
static MtChunk *g_uniform_chunk;
// draw が束縛しなかった texture と storage buffer の slot に置く仮の resource。
// 値は sdlgpu と vulkan の backend と同じで、texture は 1x1 の白、storage buffer は 0 の 256 byte。
static BackendImage g_dummy_tex;
static BackendBuffer g_dummy_sbuf;

static void mt_bound_reset(void) {
  memset(&g_bound, 0, sizeof(g_bound));
  g_bound.cull = -1;
}

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

static BackendImage mt_make_image(const ImageDesc *d);
static BackendBuffer mt_make_buffer(SglBufferType type, const void *data,
                                    size_t bytes);
static void mt_destroy_image(BackendImage h);
static void mt_destroy_buffer(BackendBuffer h);

// --- GPU の時刻 (gpu_time.h) --------------------------------------------------
// Apple silicon は pass の境目でしか時刻を取れないので、pass を作るときに sampleBufferAttachments で頼む
// (頂点の始まりと fragment の終わり)。frame ごとに MT_TS_FRAMES 組の sample を回し、組の中の k 番目に頼んだ pass は 2k と 2k + 1。
// gpu_time の pass i がどの k かは of で引く。frame の最後の command buffer が終わった組を、後の begin_frame で読んで返す。

#define MT_TS_FRAMES 4

typedef struct MtTsFrame {
  uint64_t frame;
  uint32_t passes; // 時刻を頼んだ pass の数
  int16_t of
      [GPU_TIME_PASSES]; // gpu_time の pass i に頼んだ番号 (-1 は頼んでいない)
  uint32_t count;        // gpu_time の時刻の番号の数
  bool busy;             // command buffer の終わりを待っている
} MtTsFrame;

static id<MTLCounterSampleBuffer> g_ts_buf API_AVAILABLE(macos(11.0),
                                                         ios(14.0));
static MtTsFrame g_ts[MT_TS_FRAMES];
static id<MTLCommandBuffer>
    g_ts_cmd[MT_TS_FRAMES]; // 組の frame の最後の command buffer
static MtTsFrame *g_ts_cur; // この frame の組 (測らなければ NULL)
static int g_ts_last = -1;  // 直前に始めた pass に頼んだ番号
// GPU の時刻を ns にする比 (init と読むときの CPU と GPU の時刻の組から求める)
static MTLTimestamp g_ts_cpu0, g_ts_gpu0;

static bool mt_ts_on(void) {
  if (@available(macOS 11.0, iOS 14.0, *))
    return g_ts_buf != nil;
  return false;
}

static void mt_ts_init(void) {
  if (@available(macOS 11.0, iOS 14.0, *)) {
    if (![g_device
            supportsCounterSampling:MTLCounterSamplingPointAtStageBoundary])
      return;
    id<MTLCounterSet> set = nil;
    for (id<MTLCounterSet> s in g_device.counterSets)
      if ([s.name isEqualToString:MTLCommonCounterSetTimestamp])
        set = s;
    if (!set)
      return;
    MTLCounterSampleBufferDescriptor *sd =
        [[MTLCounterSampleBufferDescriptor alloc] init];
    sd.counterSet = set;
    sd.sampleCount = MT_TS_FRAMES * GPU_TIME_STAMPS;
    sd.storageMode = MTLStorageModeShared;
    NSError *err = nil;
    g_ts_buf = [g_device newCounterSampleBufferWithDescriptor:sd error:&err];
    if (!g_ts_buf) {
      SDL_Log("metal: counter sample buffer create failed; no GPU pass times");
      return;
    }
    [g_device sampleTimestamps:&g_ts_cpu0 gpuTimestamp:&g_ts_gpu0];
  }
}

static void mt_ts_shutdown(void) {
  if (@available(macOS 11.0, iOS 14.0, *))
    g_ts_buf = nil;
  for (int r = 0; r < MT_TS_FRAMES; r++) {
    g_ts_cmd[r] = nil;
    g_ts[r].busy = false;
  }
  g_ts_cur = NULL;
}

// command buffer の終わった組を返し、この frame の組を決める
static void mt_ts_begin_frame(App *app) {
  if (!mt_ts_on())
    return;
  if (@available(macOS 11.0, iOS 14.0, *)) {
    MTLTimestamp cpu = 0, gpu = 0;
    [g_device sampleTimestamps:&cpu gpuTimestamp:&gpu];
    double ns_per_tick =
        gpu > g_ts_gpu0 ? (double)(cpu - g_ts_cpu0) / (double)(gpu - g_ts_gpu0)
                        : 1.0;
    for (int r = 0; r < MT_TS_FRAMES; r++) {
      MtTsFrame *t = &g_ts[r];
      if (!t->busy)
        continue;
      MTLCommandBufferStatus st = g_ts_cmd[r].status;
      if (st != MTLCommandBufferStatusCompleted &&
          st != MTLCommandBufferStatusError)
        continue;
      NSData *data =
          st == MTLCommandBufferStatusCompleted
              ? [g_ts_buf resolveCounterRange:NSMakeRange((NSUInteger)r *
                                                              GPU_TIME_STAMPS,
                                                          2 * t->passes)]
              : nil;
      const MTLCounterResultTimestamp *v =
          data && data.length >=
                      2 * t->passes * sizeof(MTLCounterResultTimestamp)
              ? (const MTLCounterResultTimestamp *)data.bytes
              : NULL;
      uint64_t ns[GPU_TIME_STAMPS];
      for (uint32_t i = 0; i < t->count; i++) {
        int k = t->of[i / 2];
        uint64_t tick = v && k >= 0 ? v[2 * k + (i & 1)].timestamp : 0;
        ns[i] = tick && tick != MTLCounterErrorValue
                    ? (uint64_t)((double)tick * ns_per_tick)
                    : GPU_TIME_NONE;
      }
      gpu_time_resolved(t->frame, ns, t->count);
      t->busy = false;
      g_ts_cmd[r] = nil;
    }
  }
  g_ts_cur = NULL;
  g_ts_last = -1;
  MtTsFrame *t = &g_ts[app->frame_index % MT_TS_FRAMES];
  if (!gpu_time_enabled() || t->busy)
    return;
  t->frame = app->frame_index;
  t->passes = 0;
  t->count = 0;
  for (int i = 0; i < GPU_TIME_PASSES; i++)
    t->of[i] = -1;
  g_ts_cur = t;
}

// pass の時刻を頼む (pass を作る前に、控える pass だけ)
static void mt_ts_attach(App *app, MTLRenderPassDescriptor *rp) {
  if (!g_ts_cur || g_ts_cur->passes >= GPU_TIME_PASSES ||
      !gpu_time_wants_pass(app))
    return;
  if (@available(macOS 11.0, iOS 14.0, *)) {
    NSUInteger base = (NSUInteger)(g_ts_cur - g_ts) * GPU_TIME_STAMPS;
    uint32_t k = g_ts_cur->passes++;
    MTLRenderPassSampleBufferAttachmentDescriptor *a =
        rp.sampleBufferAttachments[0];
    a.sampleBuffer = g_ts_buf;
    a.startOfVertexSampleIndex = base + 2 * k;
    a.endOfVertexSampleIndex = MTLCounterDontSample;
    a.startOfFragmentSampleIndex = MTLCounterDontSample;
    a.endOfFragmentSampleIndex = base + 2 * k + 1;
    g_ts_last = (int)k;
  }
}

// frame の最後の command buffer を組に覚える (commit の前)
static void mt_ts_end_frame(void) {
  MtTsFrame *t = g_ts_cur;
  g_ts_cur = NULL;
  if (!t || !t->passes || !g_cmd)
    return;
  g_ts_cmd[t - g_ts] = g_cmd;
  t->busy = true;
}

static bool mt_timestamp(App *app, uint32_t index) {
  (void)app;
  MtTsFrame *t = g_ts_cur;
  if (!t || index >= GPU_TIME_STAMPS)
    return false;
  uint32_t i = index / 2;
  if (!(index & 1)) {
    if (g_ts_last < 0)
      return false;
    t->of[i] = (int16_t)g_ts_last;
    g_ts_last = -1;
  } else if (t->of[i] < 0) {
    return false;
  }
  if (index + 1 > t->count)
    t->count = index + 1;
  return true;
}

static bool mt_init(App *app) {
  @autoreleasepool {
    g_device = MTLCreateSystemDefaultDevice();
    if (!g_device) {
      SDL_Log("metal: no Metal device");
      return false;
    }
    g_queue = [g_device newCommandQueue];
    g_view = SDL_Metal_CreateView(app->proc->window);
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
      app->proc->fixed_w =
          app->proc->cfg_w > 0 ? app->proc->cfg_w : LUB_DEFAULT_WINDOW_W;
      app->proc->fixed_h =
          app->proc->cfg_h > 0 ? app->proc->cfg_h : LUB_DEFAULT_WINDOW_H;
    }
    mt_ts_init();
    SDL_Log("metal: device: %s%s", g_device.name.UTF8String,
            mt_ts_on() ? " (GPU pass times)" : "");
    static const uint8_t white[4] = {255, 255, 255, 255};
    static const uint8_t zeros[256] = {0};
    g_dummy_tex = mt_make_image(&(ImageDesc){
        .fmt = SGL_PF_RGBA8,
        .w = 1,
        .h = 1,
        .data = white,
        .data_bytes = sizeof(white),
    });
    g_dummy_sbuf = mt_make_buffer(SGL_BUFFER_STORAGE, zeros, sizeof(zeros));
    if (!g_dummy_tex || !g_dummy_sbuf) {
      SDL_Log("metal: dummy resource creation failed");
      mt_destroy_image(g_dummy_tex);
      mt_destroy_buffer(g_dummy_sbuf);
      g_dummy_tex = 0;
      g_dummy_sbuf = 0;
      g_layer = nil;
      SDL_Metal_DestroyView(g_view);
      g_view = NULL;
      g_queue = nil;
      g_device = nil;
      return false;
    }
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
    mt_ts_shutdown();
    if (g_depth)
      gpu_stats_destroy(GPU_STAT_TEXTURE,
                        gpu_stats_image_bytes(SGL_PF_DEPTH24_STENCIL8,
                                              (int)g_depth.width,
                                              (int)g_depth.height));
    mt_destroy_image(g_dummy_tex);
    mt_destroy_buffer(g_dummy_sbuf);
    g_dummy_tex = 0;
    g_dummy_sbuf = 0;
    g_pass = nil;
    g_pip = nil;
    g_ibuf = nil;
    g_cmd = nil;
    g_drawable = nil;
    g_depth = nil;
    g_layer = nil;
    g_frame_chunks = nil;
    g_uniform_chunk = nil;
    @synchronized([MtChunk class]) {
      g_free_chunks = nil;
    }
    if (g_view)
      SDL_Metal_DestroyView(g_view);
    g_view = NULL;
    g_queue = nil;
    g_device = nil;
  }
}

static void mt_begin_frame(App *app, int *out_w, int *out_h) {
  @autoreleasepool {
    mt_ts_begin_frame(app);
    int w = 0, h = 0;
    SDL_GetWindowSizeInPixels(app->proc->window, &w, &h);
    if (app->proc->fixed_w > 0 && app->proc->fixed_h > 0) {
      w = app->proc->fixed_w;
      h = app->proc->fixed_h;
    }
    if (w > 0 && h > 0) {
      CGSize size = g_layer.drawableSize;
      if ((int)size.width != w || (int)size.height != h)
        g_layer.drawableSize = CGSizeMake(w, h);
    }
    app->proc->pending_resize = false;
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
      mt_ts_end_frame();
      if (g_frame_chunks.count > 0) {
        NSArray<MtChunk *> *used = g_frame_chunks;
        g_frame_chunks = nil;
        g_uniform_chunk = nil;
        [g_cmd addCompletedHandler:^(id<MTLCommandBuffer> cb) {
          (void)cb;
          @synchronized([MtChunk class]) {
            for (MtChunk *c in used)
              if (g_free_chunks && g_free_chunks.count < MT_CHUNK_KEEP)
                [g_free_chunks addObject:c];
          }
        }];
      }
      [g_cmd commit];
    }
    g_cmd = nil;
    g_drawable = nil;
  }
}

static void mt_begin_pass(App *app, const PassBeginDesc *d) {
  @autoreleasepool {
    g_ts_last = -1;
    g_pass = nil;
    g_pip = nil;
    g_ibuf = nil;
    g_scissor_empty = false;
    mt_bound_reset();
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
    mt_ts_attach(app, rp);
    g_pass = [g_cmd renderCommandEncoderWithDescriptor:rp];
    [g_pass setViewport:(MTLViewport){g_pass_x, g_pass_y, g_pass_w, g_pass_h,
                                      0.0, 1.0}];
    [g_pass setScissorRect:(MTLScissorRect){
                               (NSUInteger)g_pass_x, (NSUInteger)g_pass_y,
                               (NSUInteger)g_pass_w, (NSUInteger)g_pass_h}];
    // Clockwise front faces in every backend: the D3D convention, used with
    // the left-handed Mat4 (docs/manual/02-coordinates.md).
    [g_pass setFrontFacingWinding:MTLWindingClockwise];
  }
}

static void mt_end_pass(App *app) {
  (void)app;
  if (g_pass)
    [g_pass endEncoding];
  g_pass = nil;
  g_pip = nil;
  g_ibuf = nil;
  mt_bound_reset();
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
    b->gen = ++g_last_gen;
    if (!b->gpu) {
      SDL_Log("mt_make_buffer: newBufferWithLength failed (%zu bytes)", bytes);
      return 0;
    }
    gpu_stats_create(GPU_STAT_BUFFER, b->bytes);
    return MT_HANDLE(b);
  }
}

static MtChunk *mt_chunk_for(size_t bytes) {
  for (MtChunk *c in g_frame_chunks) {
    size_t off = (c->off + MT_FRAME_RANGE_ALIGN - 1) &
                 ~(size_t)(MT_FRAME_RANGE_ALIGN - 1);
    if (off + bytes <= c->whole->bytes) {
      c->off = off;
      return c;
    }
  }
  MtChunk *found = nil;
  @synchronized([MtChunk class]) {
    if (!g_free_chunks)
      g_free_chunks = [NSMutableArray new];
    for (MtChunk *c in g_free_chunks) {
      if (bytes <= c->whole->bytes) {
        found = c;
        break;
      }
    }
    if (found)
      [g_free_chunks removeObject:found];
  }
  if (!found) {
    found = [MtChunk new];
    found->whole = [MtBuffer new];
    found->whole->bytes = bytes > MT_CHUNK_BYTES ? bytes : MT_CHUNK_BYTES;
    found->whole->gpu = mt_new_buffer(NULL, 0, found->whole->bytes);
    found->whole->gen = ++g_last_gen;
    if (!found->whole->gpu) {
      SDL_Log("mt_frame_buffer: newBufferWithLength failed (%zu bytes)",
              found->whole->bytes);
      return nil;
    }
  }
  found->off = 0;
  if (!g_frame_chunks)
    g_frame_chunks = [NSMutableArray new];
  [g_frame_chunks addObject:found];
  return found;
}

static bool mt_frame_buffer(SglBufferType type, const void *data, size_t bytes,
                            BackendFrameRange *out) {
  (void)type;
  @autoreleasepool {
    MtChunk *c = mt_chunk_for(bytes);
    if (!c)
      return false;
    memcpy((uint8_t *)c->whole->gpu.contents + c->off, data, bytes);
    out->buf = (uintptr_t)(__bridge void *)c->whole;
    out->offset = c->off;
    out->bytes = bytes;
    c->off += bytes;
    return true;
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
    b->gen = ++g_last_gen;
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
    im->gen = ++g_last_gen;
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
    im->gen = ++g_last_gen;
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

// SglBlendFactor / SglBlendOp の値で引く。
static const MTLBlendFactor kMtBlendFactor[] = {
    MTLBlendFactorZero,
    MTLBlendFactorZero,
    MTLBlendFactorOne,
    MTLBlendFactorSourceColor,
    MTLBlendFactorOneMinusSourceColor,
    MTLBlendFactorSourceAlpha,
    MTLBlendFactorOneMinusSourceAlpha,
    MTLBlendFactorDestinationColor,
    MTLBlendFactorOneMinusDestinationColor,
    MTLBlendFactorDestinationAlpha,
    MTLBlendFactorOneMinusDestinationAlpha,
};
static const MTLBlendOperation kMtBlendOp[] = {
    MTLBlendOperationAdd,      MTLBlendOperationAdd,
    MTLBlendOperationSubtract, MTLBlendOperationReverseSubtract,
    MTLBlendOperationMin,      MTLBlendOperationMax,
};

static void mt_apply_blend(MTLRenderPipelineColorAttachmentDescriptor *ca,
                           SglBlend blend) {
  if (!blend.enabled)
    return;
  ca.blendingEnabled = YES;
  ca.sourceRGBBlendFactor = kMtBlendFactor[blend.src];
  ca.destinationRGBBlendFactor = kMtBlendFactor[blend.dst];
  ca.rgbBlendOperation = kMtBlendOp[blend.op];
  ca.sourceAlphaBlendFactor = kMtBlendFactor[blend.src_alpha];
  ca.destinationAlphaBlendFactor = kMtBlendFactor[blend.dst_alpha];
  ca.alphaBlendOperation = kMtBlendOp[blend.op_alpha];
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
    p->gen = ++g_last_gen;
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
  if (g_bound.pip != g_pip->gen) {
    [g_pass setRenderPipelineState:g_pip->gpu];
    [g_pass setDepthStencilState:g_pip->depth];
    g_bound.pip = g_pip->gen;
  }
  if (g_bound.cull != (int)g_pip->cull) {
    [g_pass setCullMode:g_pip->cull];
    g_bound.cull = (int)g_pip->cull;
  }
}

// 同じ buffer が設定済みなら offset だけを切り替える。
static void mt_bind_buffer(int si, int slot, MtBuffer *b, NSUInteger offset) {
  bool tracked = slot >= 0 && slot < MT_BOUND_SLOTS;
  if (tracked && g_bound.buf[si][slot] == b->gen) {
    if (g_bound.buf_off[si][slot] == offset)
      return;
    if (si)
      [g_pass setFragmentBufferOffset:offset atIndex:(NSUInteger)slot];
    else
      [g_pass setVertexBufferOffset:offset atIndex:(NSUInteger)slot];
  } else if (si) {
    [g_pass setFragmentBuffer:b->gpu offset:offset atIndex:(NSUInteger)slot];
  } else {
    [g_pass setVertexBuffer:b->gpu offset:offset atIndex:(NSUInteger)slot];
  }
  if (tracked) {
    g_bound.buf[si][slot] = b->gen;
    g_bound.buf_off[si][slot] = offset;
  }
}

static void mt_bind_texture(int si, int slot, MtImage *im) {
  bool tracked = slot >= 0 && slot < MT_BOUND_SLOTS;
  if (tracked && g_bound.tex[si][slot] == im->gen)
    return;
  if (si)
    [g_pass setFragmentTexture:im->tex atIndex:(NSUInteger)slot];
  else
    [g_pass setVertexTexture:im->tex atIndex:(NSUInteger)slot];
  if (tracked)
    g_bound.tex[si][slot] = im->gen;
}

static void mt_bind_sampler(int si, int slot, id<MTLSamplerState> smp) {
  bool tracked = slot >= 0 && slot < MT_BOUND_SLOTS;
  const void *key = (__bridge const void *)smp;
  if (tracked && g_bound.smp[si][slot] == key)
    return;
  if (si)
    [g_pass setFragmentSamplerState:smp atIndex:(NSUInteger)slot];
  else
    [g_pass setVertexSamplerState:smp atIndex:(NSUInteger)slot];
  if (tracked)
    g_bound.smp[si][slot] = key;
}

static void mt_apply_bindings(const BindingsDesc *b) {
  if (!g_pass)
    return;
  MtBuffer *ib = MT_OBJ(MtBuffer, b->ibuf);
  g_ibuf = ib ? ib->gpu : nil;
  g_ibuf_offset = b->ibuf_offset;
  if (!b->refl)
    return;
  // shader が宣言した slot をすべて埋め、bindings に無い slot は仮の resource にする。
  // 埋めないと前の draw が設定した resource が残り、draw の結果が前の draw に依存する。
  // 同じ名前を両方の stage が宣言することがあり、宣言ごとに slot が別にある。
  for (int j = 0; j < b->refl->tex_count; ++j) {
    const ShaderTexture *t = &b->refl->texs[j];
    if (t->stage != SGL_STAGE_VERTEX && t->stage != SGL_STAGE_FRAGMENT)
      continue;
    MtImage *im = MT_OBJ(MtImage, g_dummy_tex);
    for (int i = 0; i < b->texture_count; ++i) {
      MtImage *bound = MT_OBJ(MtImage, b->textures[i].image);
      if (bound && bound->tex && b->textures[i].name &&
          strcmp(t->name, b->textures[i].name) == 0) {
        im = bound;
        break;
      }
    }
    int si = t->stage == SGL_STAGE_FRAGMENT;
    mt_bind_texture(si, t->img_slot, im);
    if (t->smp_slot >= 0)
      mt_bind_sampler(si, t->smp_slot, im->smp);
  }
  for (int j = 0; j < b->refl->storage_buf_count; ++j) {
    const ShaderStorageBuf *r = &b->refl->storage_bufs[j];
    if (r->stage != SGL_STAGE_VERTEX && r->stage != SGL_STAGE_FRAGMENT)
      continue;
    MtBuffer *sb = MT_OBJ(MtBuffer, g_dummy_sbuf);
    size_t offset = 0;
    for (int i = 0; i < b->storage_buf_count; ++i) {
      MtBuffer *bound = MT_OBJ(MtBuffer, b->storage_bufs[i].buf);
      if (bound && bound->gpu && b->storage_bufs[i].name &&
          strcmp(r->name, b->storage_bufs[i].name) == 0) {
        sb = bound;
        offset = b->storage_bufs[i].offset;
        break;
      }
    }
    mt_bind_buffer(r->stage == SGL_STAGE_FRAGMENT, r->slot, sb,
                   (NSUInteger)offset);
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

// uniform は frame の chunk に順に置き、同じ chunk の間は buffer の offset だけを切り替える。
// 置いた範囲はフレームの間書き換えないので、uniform を省いた draw は直前の範囲を読む。
static void mt_apply_uniforms(SglShaderStage stage, int slot, const void *data,
                              size_t bytes) {
  if (!g_pass || !data || bytes == 0 || slot < 0)
    return;
  int si = stage == SGL_STAGE_FRAGMENT;
  size_t padded = (bytes + 15) & ~(size_t)15;
  MtChunk *c = g_uniform_chunk;
  size_t off = 0;
  if (c) {
    off = (c->off + MT_FRAME_RANGE_ALIGN - 1) &
          ~(size_t)(MT_FRAME_RANGE_ALIGN - 1);
    if (off + padded > c->whole->bytes)
      c = nil;
  }
  if (!c) {
    @autoreleasepool {
      c = mt_chunk_for(padded);
    }
    g_uniform_chunk = c;
    off = c ? c->off : 0;
  }
  if (c) {
    mt_pad_uniform(data, bytes, (uint8_t *)c->whole->gpu.contents + off);
    c->off = off + padded;
    mt_bind_buffer(si, slot, c->whole, (NSUInteger)off);
    return;
  }
  // chunk を確保できないときは、小さい uniform だけ encoder に写して描く。
  if (bytes + 16 > MT_INLINE_UNIFORM_MAX) {
    SDL_Log("mt_apply_uniforms: no frame chunk for %zu bytes", bytes);
    return;
  }
  uint8_t inline_bytes[MT_INLINE_UNIFORM_MAX];
  size_t length = mt_pad_uniform(data, bytes, inline_bytes);
  if (si)
    [g_pass setFragmentBytes:inline_bytes
                      length:length
                     atIndex:(NSUInteger)slot];
  else
    [g_pass setVertexBytes:inline_bytes length:length atIndex:(NSUInteger)slot];
  if (slot < MT_BOUND_SLOTS)
    g_bound.buf[si][slot] = 0;
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
                indexBufferOffset:g_ibuf_offset +
                                  (NSUInteger)base * sizeof(uint32_t)
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
          [ce setBuffer:buf->gpu
                 offset:d->storage_bufs[i].offset
                atIndex:(NSUInteger)r->slot];
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

// Records a blit of the texture into a new shared buffer on the current
// command buffer.
static id<MTLBuffer> mt_encode_read(id<MTLTexture> tex, int w, int h,
                                    SglPixelFormat src_fmt) {
  int bpp = mt_readback_src_bpp(src_fmt);
  if (bpp == 0) {
    SDL_Log("metal: unsupported readback format %d", (int)src_fmt);
    return nil;
  }
  if (!tex || w <= 0 || h <= 0 || w > (int)tex.width || h > (int)tex.height)
    return nil;
  size_t src_stride = (size_t)w * (size_t)bpp;
  size_t src_bytes = src_stride * (size_t)h;
  id<MTLBuffer> buf =
      [g_device newBufferWithLength:src_bytes
                            options:MTLResourceStorageModeShared];
  if (!buf)
    return nil;
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
  return buf;
}

// Commits what the current command buffer recorded, waits for it, and opens
// a new one for the rest of the frame.
static bool mt_flush_and_wait(void) {
  [g_cmd commit];
  [g_cmd waitUntilCompleted];
  bool ok = g_cmd.status == MTLCommandBufferStatusCompleted;
  if (!ok)
    SDL_Log("metal: readback command buffer failed: %s",
            g_cmd.error.localizedDescription.UTF8String);
  g_cmd = [g_queue commandBuffer];
  return ok;
}

static bool mt_convert(id<MTLBuffer> buf, int w, int h, SglPixelFormat src_fmt,
                       ReadbackResult *out) {
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
  if (!out)
    return false;
  *out = 0;
  if (!g_device || !image)
    return false;
  @autoreleasepool {
    id<MTLBuffer> buf =
        mt_encode_read(MT_OBJ(MtImage, image)->tex, w, h, src_fmt);
    if (!buf)
      return false;
    MtReadback *req = [MtReadback new];
    req->buf = buf;
    req->cmd = g_cmd;
    req->w = w;
    req->h = h;
    req->src_fmt = src_fmt;
    // Outside a frame no frame commit follows: finish it here.
    if (!app->in_frame && !mt_flush_and_wait())
      return false;
    *out = MT_HANDLE(req);
    return true;
  }
}

static ReadbackPollStatus mt_poll_readback(BackendReadback h, bool wait,
                                           ReadbackResult *out) {
  if (!h || !out)
    return READBACK_POLL_ERROR;
  MtReadback *req = MT_OBJ(MtReadback, h);
  if (!req->buf)
    return READBACK_POLL_ERROR;
  MTLCommandBufferStatus st = req->cmd.status;
  if (st < MTLCommandBufferStatusCompleted) {
    if (!wait || st < MTLCommandBufferStatusCommitted)
      return READBACK_POLL_PENDING;
    [req->cmd waitUntilCompleted];
    st = req->cmd.status;
  }
  bool ok = st == MTLCommandBufferStatusCompleted &&
            mt_convert(req->buf, req->w, req->h, req->src_fmt, out);
  if (st == MTLCommandBufferStatusError)
    SDL_Log("metal: readback command buffer failed: %s",
            req->cmd.error.localizedDescription.UTF8String);
  req->buf = nil;
  req->cmd = nil;
  return ok ? READBACK_POLL_READY : READBACK_POLL_ERROR;
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
    int w = (int)tex.width, h = (int)tex.height;
    ReadbackResult rb = {0};
    id<MTLBuffer> buf = mt_encode_read(tex, w, h, SGL_PF_BGRA8);
    if (!buf || !mt_flush_and_wait() ||
        !mt_convert(buf, w, h, SGL_PF_BGRA8, &rb))
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
    .frame_buffer = mt_frame_buffer,
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
    .timestamp = mt_timestamp,
};
