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

static uint64_t wg_align64(uint64_t v, uint64_t a) {
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

// id: bind group cache の key に使う通し番号 (作るたびに増やし、使い回さない。
// pointer は解放後に同じ番地が再び使われうるので key にしない)。buffer は
// GPU object を差し替えたときにも新しい id にする。
typedef struct WgBuffer {
  WGPUBuffer buf;
  uint64_t bytes;
  SglBufferType type;
  uint64_t id;
  // この値が g_submit_serial と等しい = まだ submit していない command が
  // この buffer を参照している (wg_update_buffer の記録順)
  uint64_t used_serial;
  bool transient; // transient arena の chunk
} WgBuffer;

typedef struct WgImage {
  WGPUTexture tex;
  WGPUTextureView view;
  WGPUTextureView color_att;
  WGPUTextureView depth_att;
  WGPUTextureView storage_view;
  WGPUSampler sampler;
  uint64_t stat_bytes;
  uint64_t id;
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

// group 1 (textures / samplers / storage) の bind group cache。key は layout
// の entry ごとの resource: reflection の並びで texs (image id)、
// storage_bufs (buffer id, offset, size)、storage_texs (image id)。未束縛は 0。
#define WG_BG_KEY_WORDS                                                        \
  (SGL_MAX_TEXTURES + 3 * SGL_MAX_STORAGE_BUFS + SGL_MAX_STORAGE_TEXTURES)
// 使われない entry を捨てるまでの frame 数 (transient の slice を参照する
// entry は、使われなかった frame の次の begin_frame で捨てる)
#define WG_BG_KEEP_FRAMES 120

typedef struct WgBgEntry {
  WGPUBindGroup bg; // NULL = 空き
  uint64_t hash;
  uint64_t last_frame;
  bool transient;
  uint64_t key[WG_BG_KEY_WORDS];
} WgBgEntry;

// open addressing (線形探索)。消すのは begin_frame の掃除だけで、そのとき
// 残る entry を入れ直す。
typedef struct WgBgCache {
  WgBgEntry *slots;
  uint32_t cap; // 2 の冪 (0 = 未確保)
  uint32_t count;
} WgBgCache;

typedef struct WgPipeline {
  WGPURenderPipeline render;
  WGPUComputePipeline compute;
  WGPUPipelineLayout layout;
  WGPUBindGroupLayout bgl0; // group 0: UBs
  WGPUBindGroupLayout bgl1; // group 1: textures/samplers/storage
  bool is_compute;
  ShaderReflection refl; // copy for bind group creation
  // group 0 は uniform ring を dynamic offset で指すだけなので pipeline ごとに
  // 1 つ。ring を作り直したら (g_ub.gen が変わったら) 作り直す。
  WGPUBindGroup bg0;
  uint64_t bg0_gen;
  int ub_n;                                // group 0 の entry 数
  int ub_bindings[SGL_MAX_UNIFORM_BLOCKS]; // binding 番号の昇順
  WgBgCache bgc;
  // reflection は stage ごとの宣言を名前で束ねずに並べるので、同じ名前が
  // 複数ある (VS と FS の両方が読む)。next = 後ろにある同じ名前の index
  // (無ければ -1)。束縛の slot はその名前の最初の index。
  int8_t tex_next[SGL_MAX_TEXTURES];
  int8_t sb_next[SGL_MAX_STORAGE_BUFS];
  int8_t st_next[SGL_MAX_STORAGE_TEXTURES];
  struct WgPipeline *prev, *next; // g_pipelines (cache の掃除)
} WgPipeline;

// ---- per-frame state -------------------------------------------------------

static WGPUDevice g_dev;
static WGPUQueue g_queue;
static WGPUCommandEncoder g_enc;
static WGPURenderPassEncoder g_rpass;

static uint64_t g_next_id = 1;
// frame の encoder を submit するたびに増やす (WgBuffer.used_serial)
static uint64_t g_submit_serial = 1;
// begin_frame の回数 (cache の entry の古さ、transient chunk の保持)
static uint64_t g_frame;
static WgPipeline *g_pipelines;
// 破棄した / 差し替えた resource の id。次の begin_frame で、これを参照する
// cache の entry を捨てる (cache の bind group が GPU object を生かし続け
// ないように)。id は使い回さないので、それまでに誤って当たることはない。
static uint64_t *g_dead_ids;
static int g_dead_count, g_dead_cap;
// 手放した GPU buffer のうち、まだ submit していない command が参照している
// もの。その submit の後で destroy する (wg_buffer_free)。
static WGPUBuffer *g_retired;
static int g_retired_count, g_retired_cap;
// device の maxBufferSize。これより大きい buffer は作れない (browser は
// NULL を返さずに無効な buffer を返し、それを使う submit がすべて拒まれる)
static uint64_t g_max_buffer_size;

// CPU 側の写しを持つ GPU buffer。書いた分 [flushed, used) を submit の直前に
// まとめて 1 回の writeBuffer で送る (wg_flush_uploads)。writeBuffer は
// queue の上でその submit より前に着くので、この frame の command はどれも
// 書いた内容を読む。frame の途中で先頭に戻って書き直すことはしない (まだ
// submit していない command が前の内容を読むため)。frame をまたいだ
// 使い回しは queue の順で安全 (次の frame の writeBuffer は前の frame の
// submit の後に着く)。
typedef struct WgStaged {
  WgBuffer wb; // transient chunk ではこれが BufferSlice.buf になる
  uint8_t *cpu;
  uint64_t used;
  uint64_t flushed;
} WgStaged;

// uniform: WebGPU には push constant が無いので、UB slot ごとの ring に書き、
// dynamic offset で束縛する。uniform を書き換えた draw ごとに ring の次の
// WG_UB_ALIGN 境界から取る。frame の途中で足りなくなったら折り返さずに
// 大きい ring に替える (替えられなければその draw を捨てる)。
#define WG_MAX_UB_SLOTS 2
#define WG_UB_ALIGN 256 // minUniformBufferOffsetAlignment
#define WG_UB_SIZE 2048 // max bytes per uniform block (bound size)
#define WG_UB_RING_SIZE (WG_UB_SIZE * 128) // initial ring size per slot

typedef struct WgUniformState {
  WgStaged ring[WG_MAX_UB_SLOTS];
  uint64_t gen; // ring の GPU buffer を替えるたびに増やす (group 0)
  bool dirty[WG_MAX_UB_SLOTS];
  uint8_t data[WG_MAX_UB_SLOTS][WG_UB_SIZE];
  size_t sizes[WG_MAX_UB_SLOTS];
  // 最後に書いた位置。uniform を渡さない draw はここを読む
  uint32_t last_off[WG_MAX_UB_SLOTS];
} WgUniformState;

static WgUniformState g_ub;
// ring を大きくできなかった frame (draw を捨てる error の log を frame に
// 1 回にする)
static uint64_t g_ub_fail_frame;
static WgPipeline *g_cur_pipeline;
static bool g_ibuf_bound;

// pass の中で最後に設定した状態。同じものの設定を省く。begin_pass で消し、
// bind group を release するとき (同じ番地が別の bind group に使われうる)
// にも消す。index buffer は buffer の id で比べる。
static WGPUBindGroup g_set_bg[2];
static uint32_t g_set_dyn[SGL_MAX_UNIFORM_BLOCKS]; // group 0 の dynamic offset
static uint64_t g_set_ib_id, g_set_ib_off, g_set_ib_size;

// transient buffer (Gfx.TransientBuffer、ImGui の頂点): Storage|Index の
// chunk を並べた frame ごとの arena。frame の中では前にだけ進む。chunk は
// frame をまたいで持ち続け、WG_TRANSIENT_KEEP_FRAMES frame 使われなかった
// ものだけ手放す。
#define WG_TRANSIENT_CHUNK (1u << 20)
#define WG_TRANSIENT_KEEP_FRAMES 120
#define WG_STORAGE_ALIGN 256 // minStorageBufferOffsetAlignment (default)

typedef struct WgChunk {
  WgStaged s;
  uint64_t last_frame;
} WgChunk;

static WgChunk **g_chunks;
static int g_chunk_count, g_chunk_cap;
static int g_chunk_cur; // この frame で割り当てている chunk

// ---- staging / submit helpers ----------------------------------------------

static WGPUBufferUsage wg_buffer_usage(SglBufferType type) {
  if (type == SGL_BUFFER_INDEX)
    return WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst;
  if (type == SGL_BUFFER_STORAGE)
    return WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
  return WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
}

static void wg_dead_id(uint64_t id) {
  if (g_dead_count == g_dead_cap) {
    int cap = g_dead_cap ? g_dead_cap * 2 : 64;
    uint64_t *grown =
        (uint64_t *)realloc(g_dead_ids, (size_t)cap * sizeof(uint64_t));
    if (!grown)
      return; // 捨て損ねた entry は WG_BG_KEEP_FRAMES 後に古さで消える
    g_dead_ids = grown;
    g_dead_cap = cap;
  }
  g_dead_ids[g_dead_count++] = id;
}

static void wg_release_bind_group(WGPUBindGroup bg) {
  if (!bg)
    return;
  for (int i = 0; i < 2; ++i)
    if (g_set_bg[i] == bg)
      g_set_bg[i] = NULL;
  wgpuBindGroupRelease(bg);
}

static bool wg_staged_init(WgStaged *s, WGPUBufferUsage usage, uint64_t bytes) {
  memset(s, 0, sizeof(*s));
  s->cpu = (uint8_t *)malloc((size_t)bytes);
  if (!s->cpu)
    return false;
  WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
  bd.usage = usage;
  bd.size = bytes;
  s->wb.buf = wgpuDeviceCreateBuffer(g_dev, &bd);
  if (!s->wb.buf) {
    free(s->cpu);
    s->cpu = NULL;
    return false;
  }
  s->wb.bytes = bytes;
  s->wb.id = g_next_id++;
  gpu_stats_create(GPU_STAT_BUFFER, bytes);
  return true;
}

// GPU buffer を手放す。emdawnwebgpu の release は JS object の対応を外す
// だけで destroy() を呼ばないので、release だけでは GPU の memory が JS の
// GC まで残る。submit した後の destroy は、GPU がその仕事を終えてから
// memory を返す。submit は destroy した buffer を使う command buffer を
// 拒むので、まだ submit していない command が参照している (pending) buffer
// は次の submit の後まで待たせる (wg_free_retired)。
static void wg_buffer_free(WGPUBuffer buf, bool pending) {
  if (!buf)
    return;
  if (pending) {
    if (g_retired_count == g_retired_cap) {
      int cap = g_retired_cap ? g_retired_cap * 2 : 16;
      WGPUBuffer *grown =
          (WGPUBuffer *)realloc(g_retired, (size_t)cap * sizeof(WGPUBuffer));
      if (!grown) {
        wgpuBufferRelease(buf); // 置けなければ GC に任せる
        return;
      }
      g_retired = grown;
      g_retired_cap = cap;
    }
    g_retired[g_retired_count++] = buf;
    return;
  }
  wgpuBufferDestroy(buf);
  wgpuBufferRelease(buf);
}

static void wg_free_retired(void) {
  for (int i = 0; i < g_retired_count; ++i) {
    wgpuBufferDestroy(g_retired[i]);
    wgpuBufferRelease(g_retired[i]);
  }
  g_retired_count = 0;
}

// pending: まだ submit していない command がこの buffer を参照しうる
static void wg_staged_free(WgStaged *s, bool pending) {
  if (s->wb.buf) {
    wg_dead_id(s->wb.id);
    wg_buffer_free(s->wb.buf, pending);
    gpu_stats_destroy(GPU_STAT_BUFFER, s->wb.bytes);
  }
  free(s->cpu);
  memset(s, 0, sizeof(*s));
}

static void wg_staged_flush(WgStaged *s) {
  if (s->used > s->flushed) {
    wgpuQueueWriteBuffer(g_queue, s->wb.buf, s->flushed, s->cpu + s->flushed,
                         (size_t)(s->used - s->flushed));
    s->flushed = s->used;
  }
}

static void wg_flush_uploads(void) {
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i)
    wg_staged_flush(&g_ub.ring[i]);
  for (int i = 0; i < g_chunk_count; ++i)
    wg_staged_flush(&g_chunks[i]->s);
}

// frame の encoder を submit する。uniform と transient の書いた分を先に
// 送り (queue の上でこの command buffer より前に着く)、この command buffer
// が参照していた手放した buffer は submit の後で destroy する。
static void wg_submit_encoder(void) {
  wg_flush_uploads();
  WGPUCommandBufferDescriptor cmd_desc = {0};
  WGPUCommandBuffer cmd = wgpuCommandEncoderFinish(g_enc, &cmd_desc);
  wgpuQueueSubmit(g_queue, 1, &cmd);
  wgpuCommandBufferRelease(cmd);
  wgpuCommandEncoderRelease(g_enc);
  g_enc = NULL;
  g_submit_serial++;
  wg_free_retired();
}

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
  WGPULimits limits = WGPU_LIMITS_INIT;
  g_max_buffer_size = 256u << 20; // WebGPU の既定値
  if (wgpuDeviceGetLimits(g_dev, &limits) == WGPUStatus_Success &&
      limits.maxBufferSize != WGPU_LIMIT_U64_UNDEFINED &&
      limits.maxBufferSize > 0)
    g_max_buffer_size = limits.maxBufferSize;
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
    if (!wg_staged_init(&g_ub.ring[i],
                        WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst,
                        WG_UB_RING_SIZE)) {
      SDL_Log("[webgpu] uniform ring create failed");
      return false;
    }
  }

  SDL_Log("[webgpu] backend init OK: %dx%d", cw, ch);
  return true;
}

static void wg_shutdown(App *app) {
  wg_free_retired();
  free(g_retired);
  g_retired = NULL;
  g_retired_cap = 0;
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i)
    wg_staged_free(&g_ub.ring[i], false);
  for (int i = 0; i < g_chunk_count; ++i) {
    wg_staged_free(&g_chunks[i]->s, false);
    free(g_chunks[i]);
  }
  free(g_chunks);
  g_chunks = NULL;
  g_chunk_count = g_chunk_cap = g_chunk_cur = 0;
  free(g_dead_ids);
  g_dead_ids = NULL;
  g_dead_count = g_dead_cap = 0;
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

// ---- bind group cache ------------------------------------------------------

// group 1 に入れる resource (reflection の並び、未束縛は NULL)
typedef struct WgGroup1 {
  WgImage *tex[SGL_MAX_TEXTURES];
  WgBuffer *sb[SGL_MAX_STORAGE_BUFS];
  uint64_t sb_off[SGL_MAX_STORAGE_BUFS];
  uint64_t sb_size[SGL_MAX_STORAGE_BUFS];
  WgImage *st[SGL_MAX_STORAGE_TEXTURES];
} WgGroup1;

static uint64_t wg_hash_words(const uint64_t *w, int n) {
  uint64_t h = 0x9E3779B97F4A7C15ull ^ (uint64_t)n;
  for (int i = 0; i < n; ++i) {
    h ^= w[i];
    h *= 0xFF51AFD7ED558CCDull;
    h ^= h >> 32;
  }
  return h;
}

static WgBgEntry *wg_bg_cache_find(WgBgCache *c, const uint64_t *key, int n,
                                   uint64_t hash) {
  if (!c->cap)
    return NULL;
  uint32_t mask = c->cap - 1;
  for (uint32_t i = (uint32_t)hash & mask;; i = (i + 1) & mask) {
    WgBgEntry *e = &c->slots[i];
    if (!e->bg)
      return NULL;
    if (e->hash == hash &&
        memcmp(e->key, key, (size_t)n * sizeof(uint64_t)) == 0)
      return e;
  }
}

static void wg_bg_cache_place(WgBgCache *c, const WgBgEntry *src) {
  uint32_t mask = c->cap - 1;
  uint32_t i = (uint32_t)src->hash & mask;
  while (c->slots[i].bg)
    i = (i + 1) & mask;
  c->slots[i] = *src;
  c->count++;
}

// cap (2 の冪、使用率 1/2 以下) の表に入れ直す。bg が NULL の entry は落とす
static bool wg_bg_cache_rehash(WgBgCache *c, uint32_t cap) {
  WgBgEntry *slots = (WgBgEntry *)calloc(cap, sizeof(WgBgEntry));
  if (!slots)
    return false;
  WgBgEntry *old = c->slots;
  uint32_t old_cap = c->cap;
  c->slots = slots;
  c->cap = cap;
  c->count = 0;
  for (uint32_t i = 0; i < old_cap; ++i)
    if (old[i].bg)
      wg_bg_cache_place(c, &old[i]);
  free(old);
  return true;
}

static void wg_bg_cache_clear(WgBgCache *c) {
  for (uint32_t i = 0; i < c->cap; ++i)
    wg_release_bind_group(c->slots[i].bg);
  free(c->slots);
  memset(c, 0, sizeof(*c));
}

static int wg_cmp_u64(const void *a, const void *b) {
  uint64_t x = *(const uint64_t *)a, y = *(const uint64_t *)b;
  return x < y ? -1 : x > y ? 1 : 0;
}

static bool wg_id_dead(uint64_t id) {
  return id && bsearch(&id, g_dead_ids, (size_t)g_dead_count, sizeof(uint64_t),
                       wg_cmp_u64) != NULL;
}

static bool wg_bg_entry_dead(const WgPipeline *p, const WgBgEntry *e) {
  int w = 0;
  for (int i = 0; i < p->refl.tex_count; ++i)
    if (wg_id_dead(e->key[w++]))
      return true;
  for (int i = 0; i < p->refl.storage_buf_count; ++i, w += 3)
    if (wg_id_dead(e->key[w]))
      return true;
  for (int i = 0; i < p->refl.storage_tex_count; ++i)
    if (wg_id_dead(e->key[w++]))
      return true;
  return false;
}

// begin_frame で呼ぶ。しばらく使われない entry と、破棄した resource を
// 参照する entry を捨てる。
static void wg_bg_cache_sweep(void) {
  if (g_dead_count > 1)
    qsort(g_dead_ids, (size_t)g_dead_count, sizeof(uint64_t), wg_cmp_u64);
  for (WgPipeline *p = g_pipelines; p; p = p->next) {
    WgBgCache *c = &p->bgc;
    if (!c->count)
      continue;
    uint32_t removed = 0;
    for (uint32_t i = 0; i < c->cap; ++i) {
      WgBgEntry *e = &c->slots[i];
      if (!e->bg)
        continue;
      bool stale = e->transient ? e->last_frame + 1 < g_frame
                                : g_frame - e->last_frame > WG_BG_KEEP_FRAMES;
      if (stale || (g_dead_count > 0 && wg_bg_entry_dead(p, e))) {
        wg_release_bind_group(e->bg);
        e->bg = NULL;
        removed++;
      }
    }
    if (!removed)
      continue;
    uint32_t live = c->count - removed;
    uint32_t cap = 16;
    while (cap < live * 2)
      cap *= 2;
    if (!wg_bg_cache_rehash(c, cap))
      wg_bg_cache_clear(c);
  }
  g_dead_count = 0;
}

// group 1 の bind group を cache から引く (無ければ作って入れる)。
// *uncached = true なら cache に入れられなかったので、使ったら release する。
static WGPUBindGroup wg_group1(WgPipeline *p, const WgGroup1 *g,
                               bool *uncached) {
  const ShaderReflection *r = &p->refl;
  uint64_t key[WG_BG_KEY_WORDS];
  bool transient = false;
  int w = 0;
  for (int i = 0; i < r->tex_count; ++i)
    key[w++] = g->tex[i] ? g->tex[i]->id : 0;
  for (int i = 0; i < r->storage_buf_count; ++i) {
    const WgBuffer *b = g->sb[i];
    key[w++] = b ? b->id : 0;
    key[w++] = b ? g->sb_off[i] : 0;
    key[w++] = b ? g->sb_size[i] : 0;
    if (b && b->transient)
      transient = true;
  }
  for (int i = 0; i < r->storage_tex_count; ++i)
    key[w++] = g->st[i] ? g->st[i]->id : 0;
  uint64_t hash = wg_hash_words(key, w);
  *uncached = false;
  WgBgEntry *hit = wg_bg_cache_find(&p->bgc, key, w, hash);
  if (hit) {
    hit->last_frame = g_frame;
    return hit->bg;
  }

  WGPUBindGroupEntry entries[2 * SGL_MAX_TEXTURES + SGL_MAX_STORAGE_BUFS +
                             SGL_MAX_STORAGE_TEXTURES] = {0};
  int count = 0;
  for (int i = 0; i < r->tex_count; ++i) {
    if (!g->tex[i])
      continue;
    if (r->texs[i].img_slot >= 0) {
      WGPUBindGroupEntry *e = &entries[count++];
      e->binding = (uint32_t)r->texs[i].img_slot;
      e->textureView = g->tex[i]->view;
    }
    if (r->texs[i].smp_slot >= 0) {
      WGPUBindGroupEntry *e = &entries[count++];
      e->binding = (uint32_t)r->texs[i].smp_slot;
      e->sampler = g->tex[i]->sampler;
    }
  }
  for (int i = 0; i < r->storage_buf_count; ++i) {
    if (!g->sb[i])
      continue;
    WGPUBindGroupEntry *e = &entries[count++];
    e->binding = (uint32_t)r->storage_bufs[i].slot;
    e->buffer = g->sb[i]->buf;
    e->offset = g->sb_off[i];
    e->size = g->sb_size[i];
  }
  for (int i = 0; i < r->storage_tex_count; ++i) {
    if (!g->st[i])
      continue;
    WGPUBindGroupEntry *e = &entries[count++];
    e->binding = (uint32_t)r->storage_texs[i].slot;
    e->textureView = g->st[i]->storage_view;
  }
  WGPUBindGroupDescriptor bgd = {
      .layout = p->bgl1,
      .entryCount = (size_t)count,
      .entries = entries,
  };
  WGPUBindGroup bg = wgpuDeviceCreateBindGroup(g_dev, &bgd);
  if (!bg)
    return NULL;

  WgBgCache *c = &p->bgc;
  if ((c->count + 1) * 2 > c->cap &&
      !wg_bg_cache_rehash(c, c->cap ? c->cap * 2 : 16)) {
    *uncached = true;
    return bg;
  }
  WgBgEntry ne;
  memset(&ne, 0, sizeof(ne));
  ne.bg = bg;
  ne.hash = hash;
  ne.last_frame = g_frame;
  ne.transient = transient;
  memcpy(ne.key, key, (size_t)w * sizeof(uint64_t));
  wg_bg_cache_place(c, &ne);
  return bg;
}

// ---- uniform ring ----------------------------------------------------------

// ring の空きが足りない: 書いた分を今の buffer へ送ってから倍の大きさの
// buffer に替える。先に記録した command は前の buffer を読み続ける (前の
// buffer は次の submit の後で destroy する)。maxBufferSize と、dynamic
// offset が 32 bit に収まる大きさまでしか大きくしない。
static bool wg_ub_grow(int slot) {
  WgStaged *r = &g_ub.ring[slot];
  uint64_t bytes = r->wb.bytes * 2;
  if (bytes > g_max_buffer_size || bytes > ((uint64_t)1 << 31))
    return false;
  wg_staged_flush(r);
  WgStaged grown;
  if (!wg_staged_init(&grown, WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst,
                      bytes))
    return false;
  wg_staged_free(r, true);
  *r = grown;
  g_ub.gen++;
  return true;
}

// uniform を 1 つ slot の ring に書き、g_ub.last_off をそこに向ける。ring を
// 大きくできなければ false (呼び手はその draw / dispatch を捨てる)。
static bool wg_ub_push(int slot, const void *data, size_t bytes) {
  WgStaged *r = &g_ub.ring[slot];
  if (r->used + WG_UB_SIZE > r->wb.bytes && !wg_ub_grow(slot)) {
    if (g_ub_fail_frame != g_frame) {
      g_ub_fail_frame = g_frame;
      SDL_Log("[webgpu] ERROR: uniform ring grow failed (slot %d, %llu "
              "bytes); skipping draws that write uniforms this frame",
              slot, (unsigned long long)r->wb.bytes * 2);
    }
    return false;
  }
  uint64_t off = r->used;
  memcpy(r->cpu + off, data, bytes);
  r->used = off + wg_align64(bytes > 0 ? bytes : 1, WG_UB_ALIGN);
  g_ub.last_off[slot] = (uint32_t)off;
  return true;
}

// pipeline の group 0: 各 UB slot の ring 全体 (dynamic offset で位置を選ぶ)
static WGPUBindGroup wg_pipeline_bg0(WgPipeline *p) {
  if (p->bg0 && p->bg0_gen == g_ub.gen)
    return p->bg0;
  wg_release_bind_group(p->bg0);
  WGPUBindGroupEntry entries[SGL_MAX_UNIFORM_BLOCKS] = {0};
  for (int i = 0; i < p->ub_n; ++i) {
    int slot = p->ub_bindings[i];
    entries[i].binding = (uint32_t)slot;
    entries[i].buffer = g_ub.ring[slot].wb.buf;
    entries[i].offset = 0;
    entries[i].size = WG_UB_SIZE;
  }
  WGPUBindGroupDescriptor bgd = {
      .layout = p->bgl0,
      .entryCount = (size_t)p->ub_n,
      .entries = entries,
  };
  p->bg0 = wgpuDeviceCreateBindGroup(g_dev, &bgd);
  p->bg0_gen = g_ub.gen;
  return p->bg0;
}

// ---- transient buffers -----------------------------------------------------

static void wg_transient_frame_begin(void) {
  int kept = 0;
  for (int i = 0; i < g_chunk_count; ++i) {
    WgChunk *c = g_chunks[i];
    if (g_frame - c->last_frame > WG_TRANSIENT_KEEP_FRAMES) {
      wg_staged_free(&c->s, c->s.wb.used_serial == g_submit_serial);
      free(c);
      continue;
    }
    c->s.used = c->s.flushed = 0;
    g_chunks[kept++] = c;
  }
  g_chunk_count = kept;
  g_chunk_cur = 0;
}

static bool wg_transient_buffer(SglBufferType type, const void *data,
                                size_t bytes, BufferSlice *out) {
  if (!out || (type != SGL_BUFFER_INDEX && type != SGL_BUFFER_STORAGE))
    return false;
  // 束縛する大きさは 4 の倍数に切り上げる (WebGPU の決まり) のでその分まで取る
  uint64_t need = wg_align64(bytes > 0 ? (uint64_t)bytes : 4, 4);
  uint64_t align = type == SGL_BUFFER_STORAGE ? WG_STORAGE_ALIGN : 4;
  for (;;) {
    if (g_chunk_cur < g_chunk_count) {
      WgChunk *c = g_chunks[g_chunk_cur];
      uint64_t off = wg_align64(c->s.used, align);
      if (off + need <= c->s.wb.bytes) {
        if (data && bytes > 0)
          memcpy(c->s.cpu + off, data, bytes);
        if (need > bytes)
          memset(c->s.cpu + off + bytes, 0, (size_t)(need - bytes));
        c->s.used = off + need;
        c->last_frame = g_frame;
        out->buf = (BackendBuffer)&c->s.wb;
        out->offset = (size_t)off;
        out->size = bytes;
        return true;
      }
      // 次の chunk へ。この frame のうちは戻らない
      g_chunk_cur++;
      continue;
    }
    if (g_chunk_count == g_chunk_cap) {
      int cap = g_chunk_cap ? g_chunk_cap * 2 : 8;
      WgChunk **grown =
          (WgChunk **)realloc(g_chunks, (size_t)cap * sizeof(WgChunk *));
      if (!grown)
        return false;
      g_chunks = grown;
      g_chunk_cap = cap;
    }
    uint64_t cap = WG_TRANSIENT_CHUNK;
    while (cap < need)
      cap *= 2;
    if (cap > g_max_buffer_size)
      cap = g_max_buffer_size;
    if (cap < need)
      return false; // maxBufferSize を超える (呼び手が error にする)
    WgChunk *c = (WgChunk *)calloc(1, sizeof(WgChunk));
    if (!c)
      return false;
    if (!wg_staged_init(&c->s,
                        WGPUBufferUsage_Storage | WGPUBufferUsage_Index |
                            WGPUBufferUsage_CopyDst,
                        cap)) {
      free(c);
      return false;
    }
    c->s.wb.type = SGL_BUFFER_STORAGE;
    c->s.wb.transient = true;
    c->last_frame = g_frame;
    g_chunks[g_chunk_count++] = c; // g_chunk_cur はこの chunk を指す
  }
}

// ---- frame begin / end -----------------------------------------------------

static void wg_begin_frame(App *app, int *out_w, int *out_h) {
  g_frame++;
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i) {
    g_ub.ring[i].used = g_ub.ring[i].flushed = 0;
    g_ub.last_off[i] = 0;
  }
  wg_transient_frame_begin();
  wg_bg_cache_sweep();

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
  if (g_enc)
    wg_submit_encoder();

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
                                    size_t data_bytes, size_t cap_bytes) {
  WgBuffer *wb = (WgBuffer *)calloc(1, sizeof(WgBuffer));
  if (!wb)
    return 0;
  wb->type = type;
  wb->bytes = (uint64_t)cap_bytes;
  wb->id = g_next_id++;

  WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
  bd.usage = wg_buffer_usage(type);
  bd.size = cap_bytes > 0 ? cap_bytes : 4;
  wb->buf = wgpuDeviceCreateBuffer(g_dev, &bd);
  if (!wb->buf) {
    free(wb);
    return 0;
  }
  gpu_stats_create(GPU_STAT_BUFFER, wb->bytes);
  if (data && data_bytes > 0) {
    wgpuQueueWriteBuffer(g_queue, wb->buf, 0, data, data_bytes);
  }
  return (uintptr_t)wb;
}

static void wg_destroy_buffer(BackendBuffer h) {
  WgBuffer *wb = (WgBuffer *)h;
  if (!wb)
    return;
  if (wb->buf) {
    wg_dead_id(wb->id);
    wg_buffer_free(wb->buf, wb->used_serial == g_submit_serial);
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
  wi->id = g_next_id++;

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
  wg_dead_id(wi->id);
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
      e->visibility = is_compute ? WGPUShaderStage_Compute
                      : (refl->storage_bufs[i].stage == SGL_STAGE_VERTEX)
                          ? WGPUShaderStage_Vertex
                          : WGPUShaderStage_Fragment;
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

// 束縛のための表を reflection から作り、g_pipelines に繋ぐ
static void wg_pipeline_register(WgPipeline *wp) {
  const ShaderReflection *r = &wp->refl;
  for (int i = 0; i < r->ub_count && i < SGL_MAX_UNIFORM_BLOCKS; ++i) {
    int slot = r->ubs[i].slot;
    if (slot < 0 || slot >= WG_MAX_UB_SLOTS)
      continue;
    int k = wp->ub_n++;
    while (k > 0 && wp->ub_bindings[k - 1] > slot) {
      wp->ub_bindings[k] = wp->ub_bindings[k - 1];
      --k;
    }
    wp->ub_bindings[k] = slot;
  }
  for (int i = 0; i < r->tex_count; ++i) {
    wp->tex_next[i] = -1;
    for (int j = i + 1; j < r->tex_count && wp->tex_next[i] < 0; ++j)
      if (strcmp(r->texs[i].name, r->texs[j].name) == 0)
        wp->tex_next[i] = (int8_t)j;
  }
  for (int i = 0; i < r->storage_buf_count; ++i) {
    wp->sb_next[i] = -1;
    for (int j = i + 1; j < r->storage_buf_count && wp->sb_next[i] < 0; ++j)
      if (strcmp(r->storage_bufs[i].name, r->storage_bufs[j].name) == 0)
        wp->sb_next[i] = (int8_t)j;
  }
  for (int i = 0; i < r->storage_tex_count; ++i) {
    wp->st_next[i] = -1;
    for (int j = i + 1; j < r->storage_tex_count && wp->st_next[i] < 0; ++j)
      if (strcmp(r->storage_texs[i].name, r->storage_texs[j].name) == 0)
        wp->st_next[i] = (int8_t)j;
  }
  wp->next = g_pipelines;
  if (g_pipelines)
    g_pipelines->prev = wp;
  g_pipelines = wp;
}

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
    wg_pipeline_register(wp);
    return (uintptr_t)wp;
  }

  // Graphics pipeline
  // Vertex pulling: no vertex buffer layouts, shaders read storage buffers.
  WGPUVertexState vs = {
      .module = ws->vs_mod,
      .entryPoint = wg_sv("vs_main"),
      .bufferCount = 0,
      .buffers = NULL,
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
  //
  // Strip topologies must name the index format up front for indexed draws,
  // and list topologies must leave it undefined. lub indices are always u32,
  // so the format follows the topology and indexed vs non-indexed draws share
  // one pipeline.
  const bool strip = d->primitive == SGL_PRIM_TRIANGLE_STRIP ||
                     d->primitive == SGL_PRIM_LINE_STRIP;
  WGPURenderPipelineDescriptor rpd = {
      .layout = wp->layout,
      .vertex = vs,
      .primitive =
          {
              .topology = sgl_to_wgpu_prim(d->primitive),
              .stripIndexFormat =
                  strip ? WGPUIndexFormat_Uint32 : WGPUIndexFormat_Undefined,
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
  wg_pipeline_register(wp);
  return (uintptr_t)wp;
}

static void wg_destroy_pipeline(BackendPipeline h) {
  WgPipeline *wp = (WgPipeline *)h;
  if (!wp)
    return;
  // 同じ番地に次の pipeline が作られうる (apply_pipeline の省略)
  if (g_cur_pipeline == wp)
    g_cur_pipeline = NULL;
  wg_bg_cache_clear(&wp->bgc);
  wg_release_bind_group(wp->bg0);
  if (wp->prev)
    wp->prev->next = wp->next;
  else
    g_pipelines = wp->next;
  if (wp->next)
    wp->next->prev = wp->prev;
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
  if (bytes > wb->bytes)
    bytes = (size_t)wb->bytes;
  // writeBuffer は queue の上で frame の command buffer より前に着くので、
  // まだ submit していない command がこの buffer を参照していると、先に記録
  // した draw まで新しい内容を読んでしまう。同じ容量の buffer に替えてそこへ
  // 書き、記録した順に読ませる (前の buffer は次の submit の後で destroy
  // する。それを参照する cache の bind group は前の id で引くので、もう
  // 当たらない)。
  if (wb->used_serial == g_submit_serial) {
    WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
    bd.usage = wg_buffer_usage(wb->type);
    bd.size = wb->bytes > 0 ? wb->bytes : 4;
    WGPUBuffer nb = wgpuDeviceCreateBuffer(g_dev, &bd);
    if (nb) {
      wg_dead_id(wb->id);
      wg_buffer_free(wb->buf, true);
      gpu_stats_destroy(GPU_STAT_BUFFER, wb->bytes);
      gpu_stats_create(GPU_STAT_BUFFER, wb->bytes);
      wb->buf = nb;
      wb->id = g_next_id++;
      wb->used_serial = 0;
    } else {
      SDL_Log("[webgpu] ERROR: update_buffer: replacement buffer create "
              "failed; draws recorded earlier in this frame read the new "
              "data");
    }
  }
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
  g_set_bg[0] = g_set_bg[1] = NULL;
  g_set_ib_id = 0;
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
  if (wp == g_cur_pipeline)
    return; // この pass で設定済み
  g_cur_pipeline = wp;
  wgpuRenderPassEncoderSetPipeline(g_rpass, wp->render);
}

static void wg_apply_bindings(const BindingsDesc *b) {
  WgPipeline *wp = g_cur_pipeline;
  if (!g_rpass || !wp)
    return;

  // Index buffer
  g_ibuf_bound = false;
  if (b->ibuf) {
    WgBuffer *ib = (WgBuffer *)b->ibuf;
    ib->used_serial = g_submit_serial;
    if (ib->id != g_set_ib_id || b->ibuf_offset != g_set_ib_off ||
        b->ibuf_size != g_set_ib_size) {
      wgpuRenderPassEncoderSetIndexBuffer(g_rpass, ib->buf,
                                          WGPUIndexFormat_Uint32,
                                          b->ibuf_offset, b->ibuf_size);
      g_set_ib_id = ib->id;
      g_set_ib_off = b->ibuf_offset;
      g_set_ib_size = b->ibuf_size;
    }
    g_ibuf_bound = true;
  }

  // Bind group 1: textures + samplers + storage.
  // Always set group 1 — WebKit requires all pipeline bind groups to be bound.
  if (wp->bgl1) {
    const ShaderReflection *r = &wp->refl;
    WgGroup1 g;
    memset(&g, 0, sizeof(g));
    if (b->refl) {
      // slot はその名前の最初の entry。同じ名前の後ろの entry にも束縛する
      for (int i = 0; i < b->texture_count; ++i) {
        WgImage *wi = (WgImage *)b->textures[i].image;
        int k = b->textures[i].slot;
        if (!wi || k < 0 || k >= r->tex_count)
          continue;
        for (; k >= 0; k = wp->tex_next[k])
          g.tex[k] = wi;
      }
      for (int i = 0; i < b->storage_buf_count; ++i) {
        WgBuffer *wb = (WgBuffer *)b->storage_bufs[i].buf;
        int k = b->storage_bufs[i].slot;
        if (!wb || k < 0 || k >= r->storage_buf_count)
          continue;
        wb->used_serial = g_submit_serial;
        for (; k >= 0; k = wp->sb_next[k]) {
          g.sb[k] = wb;
          // the logical size (a keyed buffer can have spare capacity),
          // rounded up to the 4-byte multiple WebGPU requires
          g.sb_off[k] = b->storage_bufs[i].offset;
          g.sb_size[k] = (b->storage_bufs[i].size + 3) & ~(uint64_t)3;
        }
      }
    }
    bool uncached = false;
    WGPUBindGroup bg = wg_group1(wp, &g, &uncached);
    if (bg) {
      if (bg != g_set_bg[1]) {
        wgpuRenderPassEncoderSetBindGroup(g_rpass, 1, bg, 0, NULL);
        g_set_bg[1] = bg;
      }
      if (uncached)
        wg_release_bind_group(bg);
    } else {
      SDL_Log("[webgpu] WARN: createBindGroup(group1) failed, "
              "tex_count=%d bgl1=%p",
              b->texture_count, (void *)wp->bgl1);
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

// uniform を書き換えた slot を ring に書き、group 0 を dynamic offset で
// 束縛する。書き換えていない slot は最後に書いた位置を読む。ring に書けな
// かったら false (その slot は書き換えたままにし、次の draw でまた書く)。
static bool wg_flush_uniforms(void) {
  WgPipeline *wp = g_cur_pipeline;
  if (!wp)
    return true;

  bool ok = true;
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i) {
    if (!g_ub.dirty[i])
      continue;
    size_t aligned = wg_align((uint32_t)g_ub.sizes[i], 16);
    if (aligned > WG_UB_SIZE)
      aligned = WG_UB_SIZE;
    if (wg_ub_push(i, g_ub.data[i], aligned))
      g_ub.dirty[i] = false;
    else
      ok = false;
  }
  if (!ok)
    return false;
  if (wp->ub_n == 0)
    return true;

  WGPUBindGroup bg = wg_pipeline_bg0(wp);
  if (!bg)
    return false;
  uint32_t dyn[SGL_MAX_UNIFORM_BLOCKS];
  for (int i = 0; i < wp->ub_n; ++i)
    dyn[i] = g_ub.last_off[wp->ub_bindings[i]];
  if (bg == g_set_bg[0] &&
      memcmp(dyn, g_set_dyn, (size_t)wp->ub_n * sizeof(uint32_t)) == 0)
    return true;
  wgpuRenderPassEncoderSetBindGroup(g_rpass, 0, bg, (size_t)wp->ub_n, dyn);
  g_set_bg[0] = bg;
  memcpy(g_set_dyn, dyn, (size_t)wp->ub_n * sizeof(uint32_t));
  return true;
}

// ---- draw / dispatch -------------------------------------------------------

static void wg_draw(int base, int count, int instance_count) {
  if (!g_rpass)
    return;
  // uniform を渡せない draw は、前の draw の uniform で描かずに捨てる
  // (wg_ub_push が log を出す)
  if (!wg_flush_uniforms())
    return;
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
  const ShaderReflection *r = &wp->refl;

  // Group 0: uniforms (ring-buffered with dynamic offsets). draw と同じ
  // ring に同じ幅で書く。
  for (int i = 0; i < d->uniform_count; ++i) {
    int slot = d->uniforms[i].slot;
    if (slot < 0 || slot >= WG_MAX_UB_SLOTS || !d->uniforms[i].data)
      continue;
    size_t aligned = wg_align((uint32_t)d->uniforms[i].bytes, 16);
    if (aligned > WG_UB_SIZE)
      aligned = WG_UB_SIZE;
    if (!wg_ub_push(slot, d->uniforms[i].data, aligned))
      return; // draw と同じく捨てる
  }
  WGPUBindGroup bg0 = wg_pipeline_bg0(wp);
  uint32_t ub_dyn_offsets[SGL_MAX_UNIFORM_BLOCKS] = {0};
  for (int i = 0; i < wp->ub_n; ++i)
    ub_dyn_offsets[i] = g_ub.last_off[wp->ub_bindings[i]];

  // Group 1: textures + samplers + storage buffers + storage textures
  WgGroup1 g;
  memset(&g, 0, sizeof(g));
  for (int i = 0; i < d->texture_count; ++i) {
    WgImage *wi = (WgImage *)d->textures[i].image;
    int k = d->textures[i].slot;
    if (!wi || k < 0 || k >= r->tex_count)
      continue;
    for (; k >= 0; k = wp->tex_next[k])
      g.tex[k] = wi;
  }
  for (int i = 0; i < d->n_storage_bufs; ++i) {
    WgBuffer *wb = (WgBuffer *)d->storage_bufs[i].buf;
    int k = d->storage_bufs[i].slot;
    if (!wb || k < 0 || k >= r->storage_buf_count)
      continue;
    wb->used_serial = g_submit_serial;
    for (; k >= 0; k = wp->sb_next[k]) {
      g.sb[k] = wb;
      g.sb_off[k] = d->storage_bufs[i].offset;
      g.sb_size[k] = (d->storage_bufs[i].size + 3) & ~(uint64_t)3;
    }
  }
  for (int i = 0; i < d->n_storage_textures; ++i) {
    WgImage *wi = (WgImage *)d->storage_textures[i].image;
    int k = d->storage_textures[i].slot;
    if (!wi || k < 0 || k >= r->storage_tex_count)
      continue;
    for (; k >= 0; k = wp->st_next[k])
      g.st[k] = wi;
  }
  bool uncached = false;
  WGPUBindGroup bg1 = wg_group1(wp, &g, &uncached);

  WGPUComputePassDescriptor cpd = {0};
  WGPUComputePassEncoder cpass =
      wgpuCommandEncoderBeginComputePass(g_enc, &cpd);
  if (cpass) {
    wgpuComputePassEncoderSetPipeline(cpass, wp->compute);
    if (bg0)
      wgpuComputePassEncoderSetBindGroup(cpass, 0, bg0, (size_t)wp->ub_n,
                                         ub_dyn_offsets);
    if (bg1)
      wgpuComputePassEncoderSetBindGroup(cpass, 1, bg1, 0, NULL);
    wgpuComputePassEncoderDispatchWorkgroups(cpass, (uint32_t)d->groups_x,
                                             (uint32_t)d->groups_y,
                                             (uint32_t)d->groups_z);
    wgpuComputePassEncoderEnd(cpass);
    wgpuComputePassEncoderRelease(cpass);
  }
  if (uncached)
    wg_release_bind_group(bg1);
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
    wg_submit_encoder();
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
    wg_submit_encoder();
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
    .transient_buffer = wg_transient_buffer,
};

#else
typedef int _wg_empty_tu;
#endif // __EMSCRIPTEN__
