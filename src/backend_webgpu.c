// backend_webgpu.c — RenderBackend impl using webgpu.h directly.
//
// Emscripten-only for now. Uses the browser's native WebGPU API via
// emdawnwebgpu. Shaders arrive as WGSL source from shader.cpp.
//
// Bind group layout (matches shader.cpp's remap_wgsl_groups):
//   @group(0): uniform buffers (b0, b1)
//   @group(1): textures, samplers, storage buffers, storage textures
#ifdef __EMSCRIPTEN__

#include "app.h"
#include "backend.h"
#include "gpu_stats.h"
#include "gpu_time.h"
#include "shader.h"
#include "webxr.h"

#include <SDL3/SDL.h>
#include <emscripten/emscripten.h>
#include <webgpu/webgpu.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- EM_JS canvas helpers -------------------------------------------------
// サイトの page (入口と player の page) は描く解像度を window._canvasWidth / _canvasHeight で決める。
// それが無い page (export、serve) は、ゲームが Gfx.Size で見る SDL の window の画素数 (App.Config の Width / Height) で描く。
// clang-format off
EM_JS(int, wg_canvas_width, (void),
      { return (window._canvasWidth || 0) | 0; })
EM_JS(int, wg_canvas_height, (void),
      { return (window._canvasHeight || 0) | 0; })
// clang-format on

static void wg_target_size(Process *proc, int *w, int *h) {
  *w = wg_canvas_width();
  *h = wg_canvas_height();
  if (*w <= 0 || *h <= 0)
    SDL_GetWindowSizeInPixels(proc->window, w, h);
}

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
  case SGL_PF_RGBA8_SRGB:
    return WGPUTextureFormat_RGBA8UnormSrgb;
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

typedef struct WgBuffer {
  WGPUBuffer buf;
  uint64_t bytes;
  SglBufferType type;
  // g_enc_serial of the last encoder that bound this buffer (0 = never).
  uint64_t used_serial;
  uint64_t gen; // buf を作り直すたびに変わる (wg_next_gen)
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
  // g_enc_serial of the last encoder that bound this texture or rendered to
  // it (0 = never).
  uint64_t used_serial;
  uint64_t gen; // tex と view を作り直すたびに変わる (wg_next_gen)
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
  uint64_t gen;          // bind group の cache の key で layout を指す
} WgPipeline;

// ---- per-frame state -------------------------------------------------------

// bind group の cache の key は handle でなく実体の世代にする。
// use_buffer は大きさが変わると handle を保ったまま実体を作り直し、解放した実体と同じ address に別の実体ができることもあるため。
static uint64_t g_last_gen;
static uint64_t wg_next_gen(void) { return ++g_last_gen; }

// The device, the canvas surface and its depth target. The backend is one per
// process, like the canvas it presents to. The surface lives across resizes;
// the depth target and the current swapchain view get rebuilt when the canvas
// extents change.
static WGPUDevice g_dev;
static WGPUInstance g_instance;
static WGPUSurface g_surface;
static WGPUTextureFormat g_surface_format; // WGPUTextureFormat_BGRA8Unorm
static WGPUTexture g_depth_tex;
static WGPUTextureView g_depth_view;
// Per-frame: acquired from wgpuSurfaceGetCurrentTexture in begin_frame,
// released in end_frame. begin_pass reads it to populate the swapchain
// attachment.
static WGPUTexture g_swapchain_tex;
static WGPUTextureView g_swapchain_view;
static WGPUQueue g_queue;
static WGPUCommandEncoder g_enc;
static WGPURenderPassEncoder g_rpass;
// Identifies the open g_enc; bumped each time it is submitted. Queue writes
// land before the open encoder's commands, so a texture or buffer it already
// binds must not be written in place (wg_update_image / wg_update_buffer).
static uint64_t g_enc_serial = 1;

// Textures and buffers that were replaced (wg_update_*) or destroyed
// (wg_destroy_*) while the open encoder still uses them. Destroying them
// before the submit would invalidate it, and a release alone leaves the GPU
// memory to the JS GC, so wg_submit_enc destroys them right after the submit.
typedef struct WgRetired {
  WGPUTexture tex;
  WGPUTextureView view;
  WGPUBuffer buf;
} WgRetired;
static WgRetired *g_retired;
static size_t g_retired_count, g_retired_cap;

static void wg_retire(WgRetired r) {
  if (g_retired_count == g_retired_cap) {
    size_t cap = g_retired_cap ? g_retired_cap * 2 : 16;
    WgRetired *grown = (WgRetired *)realloc(g_retired, cap * sizeof(*grown));
    if (!grown) {
      // Without room to wait for the submit, fall back to dropping the
      // reference; the encoder keeps the object alive.
      if (r.view)
        wgpuTextureViewRelease(r.view);
      if (r.tex)
        wgpuTextureRelease(r.tex);
      if (r.buf)
        wgpuBufferRelease(r.buf);
      return;
    }
    g_retired = grown;
    g_retired_cap = cap;
  }
  g_retired[g_retired_count++] = r;
}

static void wg_destroy_retired(void) {
  for (size_t i = 0; i < g_retired_count; ++i) {
    WgRetired *r = &g_retired[i];
    if (r->view)
      wgpuTextureViewRelease(r->view);
    if (r->tex) {
      wgpuTextureDestroy(r->tex);
      wgpuTextureRelease(r->tex);
    }
    if (r->buf) {
      wgpuBufferDestroy(r->buf);
      wgpuBufferRelease(r->buf);
    }
  }
  g_retired_count = 0;
}

// frame_buffer ranges are written with queue writes into chunks that are reused
// every frame. A write lands before the open encoder's commands but ranges never
// overlap, so no command reads another range's data; commands of earlier frames
// were submitted before the write.
#define WG_CHUNK_BYTES (4 * 1024 * 1024)
#define WG_CHUNK_CAP 32
#define WG_FRAME_RANGE_ALIGN 256 // minStorageBufferOffsetAlignment

typedef struct WgChunk {
  WgBuffer whole;
  uint64_t off;
} WgChunk;
static WgChunk g_chunks[WG_CHUNK_CAP];
static int g_chunk_count;

static void wg_chunk_destroy(WgChunk *c) {
  wgpuBufferDestroy(c->whole.buf);
  wgpuBufferRelease(c->whole.buf);
}

// Chunks the previous frame did not use are released, so a one-off spike
// doesn't pin memory forever.
static void wg_chunks_reset(void) {
  int kept = 0;
  for (int i = 0; i < g_chunk_count; ++i) {
    if (i > 0 && g_chunks[i].off == 0) {
      wg_chunk_destroy(&g_chunks[i]);
      continue;
    }
    g_chunks[i].off = 0;
    g_chunks[kept++] = g_chunks[i];
  }
  g_chunk_count = kept;
}

static bool wg_frame_buffer(SglBufferType type, const void *data, size_t bytes,
                            BackendFrameRange *out) {
  (void)type;
  WgChunk *c = NULL;
  uint64_t off = 0;
  for (int i = 0; i < g_chunk_count && !c; ++i) {
    off = (g_chunks[i].off + WG_FRAME_RANGE_ALIGN - 1) &
          ~(uint64_t)(WG_FRAME_RANGE_ALIGN - 1);
    if (off + bytes <= g_chunks[i].whole.bytes)
      c = &g_chunks[i];
  }
  if (!c) {
    if (g_chunk_count == WG_CHUNK_CAP)
      return false;
    // 数に上限があるので、後の chunk ほど大きくする。
    size_t base = WG_CHUNK_BYTES * ((size_t)g_chunk_count + 1);
    size_t cap = bytes > base ? bytes : base;
    WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
    bd.usage = WGPUBufferUsage_Storage | WGPUBufferUsage_Index |
               WGPUBufferUsage_CopyDst;
    bd.size = cap;
    WGPUBuffer buf = wgpuDeviceCreateBuffer(g_dev, &bd);
    if (!buf)
      return false;
    c = &g_chunks[g_chunk_count++];
    memset(c, 0, sizeof(*c));
    c->whole.buf = buf;
    c->whole.bytes = cap;
    c->whole.gen = wg_next_gen();
    off = 0;
  }
  wgpuQueueWriteBuffer(g_queue, c->whole.buf, off, data, bytes);
  c->off = off + bytes;
  out->buf = (BackendBuffer)&c->whole;
  out->offset = off;
  out->bytes = bytes;
  return true;
}

// WebGPU には push constant が無いので、uniform block は slot ごとの ring に置く。
// フレームの中で書いた block はそれぞれ 256 byte 境界の別の領域に置き、draw は group 0 の dynamic offset で選ぶ。
// group 0 が変わるのは ring を作り直したときだけになる。
// ring は CPU で埋め、submit の直前に slot ごとに 1 回の queue write で送る (wg_flush_uniform_rings)。
#define WG_MAX_UB_SLOTS 2
#define WG_UB_ALIGN 256 // minUniformBufferOffsetAlignment
#define WG_UB_SIZE 2048 // uniform block の上限で、binding の大きさ
#define WG_UB_RING_SIZE (256 * 1024)

typedef struct WgUniformRing {
  WGPUBuffer buf;
  uint8_t *cpu; // ring の中身
  uint32_t cap;
  uint32_t head;    // このフレームで次に書く位置
  uint32_t flushed; // このフレームで [0, flushed) を buf に送った
  // draw が読む領域。
  // このフレームで最後に書いた block で、まだ書いていなければ前のフレームの値が残る ring の先頭。
  uint32_t cur_off;
  uint32_t cur_bytes; // 0 = このフレームではまだ書いていない
  uint64_t gen;       // buf を作り直すたびに変わる
} WgUniformRing;

static WgUniformRing g_ub[WG_MAX_UB_SLOTS];
static WgPipeline *g_cur_pipeline;
static bool g_ibuf_bound;

// draw と dispatch が渡さなかった slot を埋める仮の resource。
// group に欠けた entry があると group が invalid になり、command buffer ごと捨てられる。
static WgImage *g_dummy_tex;   // 1x1 の白
static WgBuffer *g_dummy_sbuf; // 0 埋め
#define WG_DUMMY_SBUF_BYTES 256
// storage texture の layout は format を決めるので、format ごとに初めて要るときに作る。
static WgImage *g_dummy_storage[SGL_PF_RGBA8_SRGB + 1];

static BackendImage wg_make_image(const ImageDesc *d);
static BackendBuffer wg_make_buffer(SglBufferType type, const void *data,
                                    size_t bytes);
static void wg_destroy_image(BackendImage h);
static void wg_destroy_buffer(BackendBuffer h);

// 開いている render pass に設定済みの状態で、同じ状態は設定し直さない。
// pass が変わると encoder の状態は消えるので、begin_pass で消す。
static struct {
  uint64_t pipeline_gen;
  WGPUBindGroup groups[2];
  uint32_t offsets[WG_MAX_UB_SLOTS];
  int offset_count;
  uint64_t ibuf_gen, ibuf_offset, ibuf_size;
} g_bound;

// ---- bind group cache -----------------------------------------------------
// key は group の layout を持つ pipeline の世代、group の番号、entry ごとの binding と実体の世代と buffer の範囲。
// XR は両目で束ねる buffer が交互に変わるので、最後の 1 件でなく map で持つ。
// 前のフレームと今のフレームのどちらでも使わなかった group は、フレームの先頭 (wg_bg_cache_sweep) で release する。
// 記録済みの command が使う group は encoder が保持するので、submit 前に release してもよい。
#define WG_BG_MAX_ENTRIES 32
#define WG_BG_KEY_MAX (2 + WG_BG_MAX_ENTRIES * 4)
#define WG_BG_CACHE_MIN 64

typedef struct WgBgCacheEntry {
  WGPUBindGroup group; // NULL = empty slot
  uint64_t hash;
  uint64_t used_frame;
  uint64_t *key;
  int key_len;
} WgBgCacheEntry;

static WgBgCacheEntry *g_bg_cache;
static size_t g_bg_cap, g_bg_count;
static uint64_t g_frame_no;

// 1 つの bind group の entry と cache の key。
// draw ごとに作るので 0 埋めしない。
typedef struct WgBgBuild {
  WGPUBindGroupEntry entries[WG_BG_MAX_ENTRIES];
  uint64_t key[WG_BG_KEY_MAX];
  int count;
  int key_len;
} WgBgBuild;

static void wg_bg_begin(WgBgBuild *b, uint64_t pipeline_gen, int group) {
  b->count = 0;
  b->key[0] = pipeline_gen;
  b->key[1] = (uint64_t)group;
  b->key_len = 2;
}

static void wg_bg_add(WgBgBuild *b, uint32_t binding, uint64_t gen,
                      WGPUBuffer buf, uint64_t offset, uint64_t size,
                      WGPUSampler sampler, WGPUTextureView view) {
  if (b->count >= WG_BG_MAX_ENTRIES)
    return;
  b->entries[b->count++] = (WGPUBindGroupEntry){
      .binding = binding,
      .buffer = buf,
      .offset = offset,
      .size = size,
      .sampler = sampler,
      .textureView = view,
  };
  uint64_t *k = &b->key[b->key_len];
  k[0] = binding;
  k[1] = gen;
  k[2] = offset;
  k[3] = size;
  b->key_len += 4;
}

static uint64_t wg_bg_hash(const uint64_t *key, int n) {
  uint64_t h = 0;
  for (int i = 0; i < n; ++i)
    h = (h ^ key[i]) * 0x9E3779B97F4A7C15ull;
  return h ^ (h >> 32);
}

static size_t wg_bg_slot(const WgBgCacheEntry *table, size_t cap, uint64_t h,
                         const uint64_t *key, int n) {
  size_t mask = cap - 1;
  for (size_t i = (size_t)h & mask;; i = (i + 1) & mask) {
    const WgBgCacheEntry *e = &table[i];
    if (!e->group || (e->hash == h && e->key_len == n &&
                      memcmp(e->key, key, (size_t)n * sizeof(*key)) == 0))
      return i;
  }
}

static void wg_bg_release(WgBgCacheEntry *e) {
  wgpuBindGroupRelease(e->group);
  free(e->key);
}

// 残す entry を cap 個の slot の table に移し、残さない entry は release する。
static bool wg_bg_rehash(size_t cap, bool (*keep)(const WgBgCacheEntry *)) {
  WgBgCacheEntry *table = (WgBgCacheEntry *)calloc(cap, sizeof(*table));
  if (!table)
    return false;
  size_t count = 0;
  for (size_t i = 0; i < g_bg_cap; ++i) {
    WgBgCacheEntry *e = &g_bg_cache[i];
    if (!e->group)
      continue;
    if (!keep(e)) {
      wg_bg_release(e);
      continue;
    }
    table[wg_bg_slot(table, cap, e->hash, e->key, e->key_len)] = *e;
    count++;
  }
  free(g_bg_cache);
  g_bg_cache = table;
  g_bg_cap = cap;
  g_bg_count = count;
  return true;
}

static bool wg_bg_keep_all(const WgBgCacheEntry *e) {
  (void)e;
  return true;
}

static bool wg_bg_keep_recent(const WgBgCacheEntry *e) {
  return e->used_frame + 1 >= g_frame_no;
}

static void wg_bg_cache_sweep(void) {
  g_frame_no++;
  size_t kept = 0;
  for (size_t i = 0; i < g_bg_cap; ++i)
    if (g_bg_cache[i].group && wg_bg_keep_recent(&g_bg_cache[i]))
      kept++;
  if (kept == g_bg_count)
    return;
  size_t cap = WG_BG_CACHE_MIN;
  while (cap < kept * 4)
    cap *= 2;
  (void)wg_bg_rehash(cap, wg_bg_keep_recent);
}

static void wg_bg_cache_clear(void) {
  for (size_t i = 0; i < g_bg_cap; ++i)
    if (g_bg_cache[i].group)
      wg_bg_release(&g_bg_cache[i]);
  free(g_bg_cache);
  g_bg_cache = NULL;
  g_bg_cap = g_bg_count = 0;
}

// 返す group は cache が持つので、呼び出し側は release しない。
static WGPUBindGroup wg_bg_get(WGPUBindGroupLayout layout, const WgBgBuild *b) {
  if ((g_bg_count + 1) * 2 > g_bg_cap &&
      !wg_bg_rehash(g_bg_cap ? g_bg_cap * 2 : WG_BG_CACHE_MIN, wg_bg_keep_all))
    return NULL;
  uint64_t h = wg_bg_hash(b->key, b->key_len);
  WgBgCacheEntry *e =
      &g_bg_cache[wg_bg_slot(g_bg_cache, g_bg_cap, h, b->key, b->key_len)];
  if (e->group) {
    e->used_frame = g_frame_no;
    return e->group;
  }
  size_t key_bytes = (size_t)b->key_len * sizeof(*b->key);
  uint64_t *key = (uint64_t *)malloc(key_bytes);
  if (!key)
    return NULL;
  WGPUBindGroupDescriptor bgd = {
      .layout = layout,
      .entryCount = (size_t)b->count,
      .entries = b->entries,
  };
  WGPUBindGroup group = wgpuDeviceCreateBindGroup(g_dev, &bgd);
  if (!group) {
    free(key);
    return NULL;
  }
  memcpy(key, b->key, key_bytes);
  *e = (WgBgCacheEntry){
      .group = group,
      .hash = h,
      .used_frame = g_frame_no,
      .key = key,
      .key_len = b->key_len,
  };
  g_bg_count++;
  return group;
}

// ---- uniform rings ---------------------------------------------------------

static WGPUBuffer wg_make_uniform_buffer(uint32_t bytes) {
  WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
  bd.usage = WGPUBufferUsage_Uniform | WGPUBufferUsage_CopyDst;
  bd.size = bytes;
  return wgpuDeviceCreateBuffer(g_dev, &bd);
}

static void wg_flush_uniform_ring(WgUniformRing *r) {
  if (r->head <= r->flushed)
    return;
  wgpuQueueWriteBuffer(g_queue, r->buf, r->flushed, r->cpu + r->flushed,
                       r->head - r->flushed);
  r->flushed = r->head;
}

// queue write は次の submit の command より先に届くので、g_enc を submit する直前に毎回呼ぶ。
static void wg_flush_uniform_rings(void) {
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i)
    wg_flush_uniform_ring(&g_ub[i]);
}

// 記録済みの command は古い buffer を読むので、古い buffer は submit の後で破棄する (wg_retire)。
static bool wg_grow_uniform_ring(WgUniformRing *r) {
  uint32_t cap = r->cap * 2;
  uint8_t *cpu = (uint8_t *)realloc(r->cpu, cap);
  if (!cpu)
    return false;
  r->cpu = cpu;
  WGPUBuffer buf = wg_make_uniform_buffer(cap);
  if (!buf)
    return false;
  wg_flush_uniform_ring(r);
  wg_retire((WgRetired){.buf = r->buf});
  r->buf = buf;
  r->cap = cap;
  r->head = r->flushed = r->cur_off = 0;
  r->gen = wg_next_gen();
  return true;
}

static WGPUTexture g_xr_color[2], g_xr_depth[2];
static WGPUTextureView g_xr_color_view[2], g_xr_depth_view[2];
static bool g_xr_drawn[2];
static bool g_xr_copied;
static int g_xr_width, g_xr_height;

static void wg_release_xr(void) {
  for (int eye = 0; eye < 2; eye++) {
    if (g_xr_color_view[eye]) {
      wgpuTextureViewRelease(g_xr_color_view[eye]);
      gpu_stats_destroy(GPU_STAT_VIEW, 0);
    }
    if (g_xr_depth_view[eye]) {
      wgpuTextureViewRelease(g_xr_depth_view[eye]);
      gpu_stats_destroy(GPU_STAT_VIEW, 0);
    }
    // Only submitted frames use the eye targets: wg_prepare_xr runs before
    // anything is recorded, and the rest after the submit.
    if (g_xr_color[eye]) {
      wgpuTextureDestroy(g_xr_color[eye]);
      wgpuTextureRelease(g_xr_color[eye]);
      gpu_stats_destroy(GPU_STAT_TEXTURE, 0);
    }
    if (g_xr_depth[eye]) {
      wgpuTextureDestroy(g_xr_depth[eye]);
      wgpuTextureRelease(g_xr_depth[eye]);
      gpu_stats_destroy(GPU_STAT_TEXTURE, 0);
    }
    g_xr_color_view[eye] = g_xr_depth_view[eye] = NULL;
    g_xr_color[eye] = g_xr_depth[eye] = NULL;
  }
  g_xr_width = g_xr_height = 0;
}

static bool wg_prepare_xr(Process *proc, int width, int height) {
  if (g_xr_width == width && g_xr_height == height)
    return true;
  wg_release_xr();
  for (int eye = 0; eye < 2; eye++) {
    WGPUTextureDescriptor desc = {
        .usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc,
        .dimension = WGPUTextureDimension_2D,
        .size = {(uint32_t)width, (uint32_t)height, 1},
        .format = WGPUTextureFormat_RGBA8UnormSrgb,
        .mipLevelCount = 1,
        .sampleCount = 1,
    };
    g_xr_color[eye] = wgpuDeviceCreateTexture(g_dev, &desc);
    if (!g_xr_color[eye])
      goto fail;
    gpu_stats_create(GPU_STAT_TEXTURE, 0);
    g_xr_color_view[eye] = wgpuTextureCreateView(g_xr_color[eye], NULL);
    if (!g_xr_color_view[eye])
      goto fail;
    gpu_stats_create(GPU_STAT_VIEW, 0);
    desc.usage = WGPUTextureUsage_RenderAttachment;
    desc.format = WGPUTextureFormat_Depth32FloatStencil8;
    g_xr_depth[eye] = wgpuDeviceCreateTexture(g_dev, &desc);
    if (!g_xr_depth[eye])
      goto fail;
    gpu_stats_create(GPU_STAT_TEXTURE, 0);
    g_xr_depth_view[eye] = wgpuTextureCreateView(g_xr_depth[eye], NULL);
    if (!g_xr_depth_view[eye])
      goto fail;
    gpu_stats_create(GPU_STAT_VIEW, 0);
  }
  g_xr_width = width;
  g_xr_height = height;
  return true;
fail:
  wg_release_xr();
  return false;
}

// ---- bring-up (surface / depth / swapchain) --------------------------------

// Runs from wg_begin_frame before the frame's encoder records anything, so
// only submitted work has used the old depth texture.
static bool wg_recreate_depth(Process *proc, uint32_t w, uint32_t h) {
  if (g_depth_view) {
    wgpuTextureViewRelease(g_depth_view);
    gpu_stats_destroy(GPU_STAT_VIEW, 0);
    g_depth_view = NULL;
  }
  if (g_depth_tex) {
    wgpuTextureDestroy(g_depth_tex);
    wgpuTextureRelease(g_depth_tex);
    gpu_stats_destroy(GPU_STAT_TEXTURE, 0);
    g_depth_tex = NULL;
  }
  WGPUTextureDescriptor ds = {
      .usage = WGPUTextureUsage_RenderAttachment,
      .dimension = WGPUTextureDimension_2D,
      .size = {.width = w, .height = h, .depthOrArrayLayers = 1},
      .format = WGPUTextureFormat_Depth32FloatStencil8,
      .mipLevelCount = 1,
      .sampleCount = 1,
  };
  g_depth_tex = wgpuDeviceCreateTexture(g_dev, &ds);
  if (!g_depth_tex) {
    SDL_Log("[webgpu] depth texture create failed");
    return false;
  }
  gpu_stats_create(GPU_STAT_TEXTURE, 0);
  g_depth_view = wgpuTextureCreateView(g_depth_tex, NULL);
  if (!g_depth_view) {
    SDL_Log("[webgpu] depth view create failed");
    wgpuTextureRelease(g_depth_tex);
    gpu_stats_destroy(GPU_STAT_TEXTURE, 0);
    g_depth_tex = NULL;
    return false;
  }
  gpu_stats_create(GPU_STAT_VIEW, 0);
  return true;
}

static void wg_configure_surface(Process *proc, uint32_t w, uint32_t h) {
  // CopySrc: wg_capture (golden test) が swapchain texture を readback する。
  WGPUSurfaceConfiguration cfg = {
      .device = g_dev,
      .format = g_surface_format,
      .usage = WGPUTextureUsage_RenderAttachment | WGPUTextureUsage_CopySrc |
               WGPUTextureUsage_CopyDst,
      .width = w,
      .height = h,
      .alphaMode = WGPUCompositeAlphaMode_Opaque,
      .presentMode = WGPUPresentMode_Fifo,
  };
  wgpuSurfaceConfigure(g_surface, &cfg);
}

// ---- init / shutdown -------------------------------------------------------

// ---- GPU の時刻 (gpu_time.h) ------------------------------------------------
// pass の時刻は pass を作るときに timestampWrites で頼む。frame ごとに WG_TS_FRAMES 組の query を回し、
// 組の中の k 番目に頼んだ pass は 2k と 2k + 1 に書く。gpu_time の pass i がどの k かは of で引く。
// 読み出しの map は非同期なので、終わった組を後の begin_frame で読んで返す。

#define WG_TS_FRAMES 4
#define WG_TS_BYTES ((uint64_t)GPU_TIME_STAMPS * sizeof(uint64_t))

typedef struct WgTsFrame {
  uint64_t frame;
  uint32_t passes; // 時刻を頼んだ pass の数
  int16_t of
      [GPU_TIME_PASSES]; // gpu_time の pass i に頼んだ番号 (-1 は頼んでいない)
  uint32_t count;        // gpu_time の時刻の番号の数
  WGPUBuffer read;       // MapRead
  bool busy;             // 写して map を待っている (読むまで使わない)
  bool mapped, failed;
} WgTsFrame;

static WGPUQuerySet g_ts_set;
static WGPUBuffer g_ts_resolve;
static WgTsFrame g_ts[WG_TS_FRAMES];
static WgTsFrame *g_ts_cur; // この frame の組 (測らなければ NULL)
static int g_ts_last = -1;  // 直前に始めた pass に頼んだ番号

static void wg_ts_shutdown(void) {
  for (int r = 0; r < WG_TS_FRAMES; r++) {
    if (g_ts[r].read)
      wgpuBufferRelease(g_ts[r].read);
    memset(&g_ts[r], 0, sizeof(g_ts[r]));
  }
  if (g_ts_resolve)
    wgpuBufferRelease(g_ts_resolve);
  if (g_ts_set)
    wgpuQuerySetRelease(g_ts_set);
  g_ts_resolve = NULL;
  g_ts_set = NULL;
  g_ts_cur = NULL;
}

// device が timestamp-query を持てば (JS が device を作るときに頼む) query と読み出し先を作る
static void wg_ts_init(void) {
  if (!wgpuDeviceHasFeature(g_dev, WGPUFeatureName_TimestampQuery))
    return;
  WGPUQuerySetDescriptor qd = {
      .type = WGPUQueryType_Timestamp,
      .count = WG_TS_FRAMES * GPU_TIME_STAMPS,
  };
  g_ts_set = wgpuDeviceCreateQuerySet(g_dev, &qd);
  WGPUBufferDescriptor rd = {
      .usage = WGPUBufferUsage_QueryResolve | WGPUBufferUsage_CopySrc,
      .size = WG_TS_FRAMES * WG_TS_BYTES,
  };
  g_ts_resolve = g_ts_set ? wgpuDeviceCreateBuffer(g_dev, &rd) : NULL;
  bool ok = g_ts_set && g_ts_resolve;
  for (int r = 0; ok && r < WG_TS_FRAMES; r++) {
    WGPUBufferDescriptor bd = {
        .usage = WGPUBufferUsage_MapRead | WGPUBufferUsage_CopyDst,
        .size = WG_TS_BYTES,
    };
    g_ts[r].read = wgpuDeviceCreateBuffer(g_dev, &bd);
    ok = g_ts[r].read != NULL;
  }
  if (!ok) {
    SDL_Log("[webgpu] timestamp query create failed; no GPU pass times");
    wg_ts_shutdown();
  }
}

static void wg_ts_mapped(WGPUMapAsyncStatus status, WGPUStringView message,
                         void *userdata1, void *userdata2) {
  (void)message;
  (void)userdata2;
  WgTsFrame *t = (WgTsFrame *)userdata1;
  if (status == WGPUMapAsyncStatus_Success)
    t->mapped = true;
  else
    t->failed = true;
}

// map の終わった組を返し、この frame の組を決める
static void wg_ts_begin_frame(Process *proc) {
  for (int r = 0; r < WG_TS_FRAMES; r++) {
    WgTsFrame *t = &g_ts[r];
    if (!t->busy || !(t->mapped || t->failed))
      continue;
    const uint64_t *v = t->mapped
                            ? (const uint64_t *)wgpuBufferGetConstMappedRange(
                                  t->read, 0, 2 * t->passes * sizeof(uint64_t))
                            : NULL;
    uint64_t ns[GPU_TIME_STAMPS];
    for (uint32_t i = 0; i < t->count; i++) {
      int k = t->of[i / 2];
      ns[i] = v && k >= 0 ? v[2 * k + (i & 1)] : GPU_TIME_NONE;
    }
    if (t->mapped)
      wgpuBufferUnmap(t->read);
    gpu_time_resolved(t->frame, ns, t->count);
    t->busy = t->mapped = t->failed = false;
  }
  g_ts_cur = NULL;
  g_ts_last = -1;
  WgTsFrame *t = &g_ts[proc->frame_index % WG_TS_FRAMES];
  if (!g_ts_set || !gpu_time_enabled() || t->busy)
    return;
  t->frame = proc->frame_index;
  t->passes = 0;
  t->count = 0;
  for (int i = 0; i < GPU_TIME_PASSES; i++)
    t->of[i] = -1;
  g_ts_cur = t;
}

// 書いた時刻を組の読み出し先へ写す (submit の前)
static void wg_ts_resolve(void) {
  WgTsFrame *t = g_ts_cur;
  if (!t || !t->passes || !g_enc)
    return;
  uint32_t r = (uint32_t)(t - g_ts);
  wgpuCommandEncoderResolveQuerySet(g_enc, g_ts_set, r * GPU_TIME_STAMPS,
                                    2 * t->passes, g_ts_resolve,
                                    r * WG_TS_BYTES);
  wgpuCommandEncoderCopyBufferToBuffer(g_enc, g_ts_resolve, r * WG_TS_BYTES,
                                       t->read, 0,
                                       2 * t->passes * sizeof(uint64_t));
}

// submit の後に読み出し先の map を始める
static void wg_ts_map(void) {
  WgTsFrame *t = g_ts_cur;
  g_ts_cur = NULL;
  if (!t || !t->passes)
    return;
  t->busy = true;
  WGPUBufferMapCallbackInfo cb = WGPU_BUFFER_MAP_CALLBACK_INFO_INIT;
  cb.mode = WGPUCallbackMode_AllowSpontaneous;
  cb.callback = wg_ts_mapped;
  cb.userdata1 = t;
  wgpuBufferMapAsync(t->read, WGPUMapMode_Read, 0,
                     2 * t->passes * sizeof(uint64_t), cb);
}

static bool wg_timestamp(Process *proc, uint32_t index) {
  (void)proc;
  WgTsFrame *t = g_ts_cur;
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

static bool wg_init(Process *proc) {
  g_dev = emscripten_webgpu_get_device();
  if (!g_dev) {
    SDL_Log("[webgpu] emscripten_webgpu_get_device returned NULL");
    return false;
  }

  g_instance = wgpuCreateInstance(NULL);
  if (!g_instance) {
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
  g_surface = wgpuInstanceCreateSurface(g_instance, &surf_desc);
  if (!g_surface) {
    SDL_Log("[webgpu] surface create failed");
    return false;
  }

  g_surface_format = WGPUTextureFormat_BGRA8Unorm;
  g_queue = wgpuDeviceGetQueue(g_dev);
  int cw, ch;
  wg_target_size(proc, &cw, &ch);
  wg_configure_surface(proc, (uint32_t)cw, (uint32_t)ch);
  if (!wg_recreate_depth(proc, (uint32_t)cw, (uint32_t)ch))
    return false;

  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i) {
    WgUniformRing *r = &g_ub[i];
    r->cpu = (uint8_t *)calloc(1, WG_UB_RING_SIZE);
    r->buf = wg_make_uniform_buffer(WG_UB_RING_SIZE);
    if (!r->cpu || !r->buf) {
      SDL_Log("[webgpu] uniform ring create failed");
      return false;
    }
    r->cap = WG_UB_RING_SIZE;
    r->gen = wg_next_gen();
  }

  static const uint8_t white[4] = {255, 255, 255, 255};
  static const uint8_t zeros[WG_DUMMY_SBUF_BYTES] = {0};
  g_dummy_tex = (WgImage *)wg_make_image(&(ImageDesc){
      .fmt = SGL_PF_RGBA8,
      .w = 1,
      .h = 1,
      .data = white,
      .data_bytes = sizeof(white),
  });
  g_dummy_sbuf =
      (WgBuffer *)wg_make_buffer(SGL_BUFFER_STORAGE, zeros, sizeof(zeros));
  if (!g_dummy_tex || !g_dummy_sbuf) {
    SDL_Log("[webgpu] placeholder resource create failed");
    return false;
  }

  wg_ts_init();
  SDL_Log("[webgpu] backend init OK: %dx%d%s", cw, ch,
          g_ts_set ? " (GPU pass times)" : "");
  return true;
}

static void wg_shutdown(Process *proc) {
  wg_ts_shutdown();
  wg_destroy_image((BackendImage)g_dummy_tex);
  wg_destroy_buffer((BackendBuffer)g_dummy_sbuf);
  g_dummy_tex = NULL;
  g_dummy_sbuf = NULL;
  for (size_t i = 0; i < sizeof(g_dummy_storage) / sizeof(g_dummy_storage[0]);
       ++i) {
    wg_destroy_image((BackendImage)g_dummy_storage[i]);
    g_dummy_storage[i] = NULL;
  }
  wg_release_xr();
  wg_destroy_retired();
  free(g_retired);
  g_retired = NULL;
  g_retired_cap = 0;
  for (int i = 0; i < g_chunk_count; ++i)
    wg_chunk_destroy(&g_chunks[i]);
  g_chunk_count = 0;
  wg_bg_cache_clear();
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i) {
    if (g_ub[i].buf)
      wgpuBufferRelease(g_ub[i].buf);
    free(g_ub[i].cpu);
    g_ub[i] = (WgUniformRing){0};
  }
  if (g_queue) {
    wgpuQueueRelease(g_queue);
    g_queue = NULL;
  }
  if (g_swapchain_view) {
    wgpuTextureViewRelease(g_swapchain_view);
    gpu_stats_destroy(GPU_STAT_SURFACE_VIEW, 0);
    g_swapchain_view = NULL;
  }
  if (g_swapchain_tex) {
    wgpuTextureRelease(g_swapchain_tex);
    gpu_stats_destroy(GPU_STAT_SURFACE_TEXTURE, 0);
    g_swapchain_tex = NULL;
  }
  if (g_depth_view) {
    wgpuTextureViewRelease(g_depth_view);
    gpu_stats_destroy(GPU_STAT_VIEW, 0);
    g_depth_view = NULL;
  }
  if (g_depth_tex) {
    wgpuTextureRelease(g_depth_tex);
    gpu_stats_destroy(GPU_STAT_TEXTURE, 0);
    g_depth_tex = NULL;
  }
  if (g_surface) {
    wgpuSurfaceRelease(g_surface);
    g_surface = NULL;
  }
  if (g_instance) {
    wgpuInstanceRelease(g_instance);
    g_instance = NULL;
  }
  g_dev = NULL;
}

// ---- frame begin / end -----------------------------------------------------

static void wg_submit_enc(void) {
  wg_flush_uniform_rings();
  WGPUCommandBufferDescriptor cmd_desc = {0};
  WGPUCommandBuffer cmd = wgpuCommandEncoderFinish(g_enc, &cmd_desc);
  wgpuQueueSubmit(g_queue, 1, &cmd);
  wgpuCommandBufferRelease(cmd);
  wgpuCommandEncoderRelease(g_enc);
  g_enc = NULL;
  g_enc_serial++;
  wg_destroy_retired();
}

static void wg_begin_frame(Process *proc, int *out_w, int *out_h) {
  wg_ts_begin_frame(proc);
  lubwebxr_begin();
  g_xr_drawn[0] = g_xr_drawn[1] = false;
  g_xr_copied = false;
  for (int i = 0; i < WG_MAX_UB_SLOTS; ++i) {
    WgUniformRing *r = &g_ub[i];
    r->head = r->flushed = r->cur_off = r->cur_bytes = 0;
  }
  wg_chunks_reset();
  wg_bg_cache_sweep();

  int cw, ch;
  wg_target_size(proc, &cw, &ch);

  bool xr = lubwebxr_active();
  WGPUTextureFormat format =
      xr ? WGPUTextureFormat_RGBA8Unorm : WGPUTextureFormat_BGRA8Unorm;
  bool needs_resize = proc->pending_resize || g_surface_format != format ||
                      (proc->last_w != 0 && cw != proc->last_w) ||
                      (proc->last_h != 0 && ch != proc->last_h);
  if (needs_resize) {
    g_surface_format = format;
    proc->pending_resize = false;
    if (g_swapchain_view) {
      wgpuTextureViewRelease(g_swapchain_view);
      gpu_stats_destroy(GPU_STAT_SURFACE_VIEW, 0);
      g_swapchain_view = NULL;
    }
    if (g_swapchain_tex) {
      wgpuTextureRelease(g_swapchain_tex);
      gpu_stats_destroy(GPU_STAT_SURFACE_TEXTURE, 0);
      g_swapchain_tex = NULL;
    }
    wg_configure_surface(proc, (uint32_t)cw, (uint32_t)ch);
    if (!wg_recreate_depth(proc, (uint32_t)cw, (uint32_t)ch)) {
      if (out_w)
        *out_w = cw;
      if (out_h)
        *out_h = ch;
      return;
    }
  }

  WGPUSurfaceTexture surf_tex = {0};
  wgpuSurfaceGetCurrentTexture(g_surface, &surf_tex);
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
      wg_configure_surface(proc, (uint32_t)cw, (uint32_t)ch);
      if (!wg_recreate_depth(proc, (uint32_t)cw, (uint32_t)ch)) {
        if (out_w)
          *out_w = cw;
        if (out_h)
          *out_h = ch;
        return;
      }
      WGPUSurfaceTexture retry = {0};
      wgpuSurfaceGetCurrentTexture(g_surface, &retry);
      ok = (retry.status == WGPUSurfaceGetCurrentTextureStatus_SuccessOptimal ||
            retry.status ==
                WGPUSurfaceGetCurrentTextureStatus_SuccessSuboptimal);
      surf_tex = retry;
    }
  }
  if (ok && surf_tex.texture) {
    g_swapchain_tex = surf_tex.texture;
    gpu_stats_create(GPU_STAT_SURFACE_TEXTURE, 0);
    g_swapchain_view = wgpuTextureCreateView(surf_tex.texture, NULL);
    if (g_swapchain_view)
      gpu_stats_create(GPU_STAT_SURFACE_VIEW, 0);
    uint32_t sw = wgpuTextureGetWidth(surf_tex.texture);
    uint32_t sh = wgpuTextureGetHeight(surf_tex.texture);
    if ((int)sw != cw || (int)sh != ch) {
      if (!wg_recreate_depth(proc, sw, sh))
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
  g_enc = wgpuDeviceCreateCommandEncoder(g_dev, &enc_desc);
  if (xr) {
    if (!wg_prepare_xr(proc, cw / 2, ch))
      SDL_Log("[webgpu] XR targets unavailable");
  } else if (g_xr_width)
    wg_release_xr();

  if (out_w)
    *out_w = cw;
  if (out_h)
    *out_h = ch;
}

// Puts the drawn eyes side by side on the surface.
// The capture of an XR frame needs this before end_frame does.
static void wg_copy_xr_eyes(Process *proc) {
  if (g_xr_copied || !g_xr_width || !g_swapchain_tex)
    return;
  g_xr_copied = true;
  for (int eye = 0; eye < 2; eye++) {
    if (!g_xr_drawn[eye])
      continue;
    WGPUTexelCopyTextureInfo src = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    WGPUTexelCopyTextureInfo dst = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
    src.texture = g_xr_color[eye];
    dst.texture = g_swapchain_tex;
    dst.origin.x = (uint32_t)(eye * g_xr_width);
    WGPUExtent3D extent = {(uint32_t)g_xr_width, (uint32_t)g_xr_height, 1};
    wgpuCommandEncoderCopyTextureToTexture(g_enc, &src, &dst, &extent);
  }
}

static void wg_end_frame(Process *proc) {
  // Submit the command buffer.
  if (g_enc) {
    wg_copy_xr_eyes(proc);
    wg_ts_resolve();
    wg_submit_enc();
    wg_ts_map();
    lubwebxr_present(g_xr_drawn[0] && g_xr_drawn[1]);
  }

  if (g_swapchain_view) {
    wgpuTextureViewRelease(g_swapchain_view);
    gpu_stats_destroy(GPU_STAT_SURFACE_VIEW, 0);
    g_swapchain_view = NULL;
  }
  if (g_swapchain_tex) {
    wgpuTextureRelease(g_swapchain_tex);
    gpu_stats_destroy(GPU_STAT_SURFACE_TEXTURE, 0);
    g_swapchain_tex = NULL;
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
  wb->gen = wg_next_gen();

  WGPUBufferUsage usage = 0;
  if (type == SGL_BUFFER_INDEX)
    usage = WGPUBufferUsage_Index | WGPUBufferUsage_CopyDst;
  else if (type == SGL_BUFFER_STORAGE)
    usage = WGPUBufferUsage_Storage | WGPUBufferUsage_CopyDst;
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
    if (wb->used_serial == g_enc_serial) {
      wg_retire((WgRetired){.buf = wb->buf});
    } else {
      wgpuBufferDestroy(wb->buf);
      wgpuBufferRelease(wb->buf);
    }
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
  wi->gen = wg_next_gen();

  WGPUTextureFormat wfmt = sgl_to_wgpu_fmt(d->fmt);

  bool is_depth = (d->fmt == SGL_PF_DEPTH16 || d->fmt == SGL_PF_DEPTH32F ||
                   d->fmt == SGL_PF_DEPTH24_STENCIL8);
  WGPUTextureUsage usage = WGPUTextureUsage_TextureBinding;
  // 深度でない texture は render target でなくても (storage だけでも) Gfx.ReadTexture が読み戻す。
  // CopySrc が無いと読み戻しの CopyTextureToBuffer がその frame の command buffer ごと invalid にする
  if (!is_depth)
    usage |= WGPUTextureUsage_CopySrc;
  if (d->render_target)
    usage |= WGPUTextureUsage_RenderAttachment;
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
    if (wi->used_serial == g_enc_serial) {
      wg_retire((WgRetired){.tex = wi->tex});
    } else {
      wgpuTextureDestroy(wi->tex);
      wgpuTextureRelease(wi->tex);
    }
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

// SglBlendFactor / SglBlendOp の値で引く。
static const WGPUBlendFactor kWgBlendFactor[] = {
    WGPUBlendFactor_Zero,
    WGPUBlendFactor_Zero,
    WGPUBlendFactor_One,
    WGPUBlendFactor_Src,
    WGPUBlendFactor_OneMinusSrc,
    WGPUBlendFactor_SrcAlpha,
    WGPUBlendFactor_OneMinusSrcAlpha,
    WGPUBlendFactor_Dst,
    WGPUBlendFactor_OneMinusDst,
    WGPUBlendFactor_DstAlpha,
    WGPUBlendFactor_OneMinusDstAlpha,
};
static const WGPUBlendOperation kWgBlendOp[] = {
    WGPUBlendOperation_Add,      WGPUBlendOperation_Add,
    WGPUBlendOperation_Subtract, WGPUBlendOperation_ReverseSubtract,
    WGPUBlendOperation_Min,      WGPUBlendOperation_Max,
};

static BackendPipeline wg_make_pipeline(const PipelineDesc *d) {
  WgShader *ws = (WgShader *)d->shader;
  if (!ws)
    return 0;

  WgPipeline *wp = (WgPipeline *)calloc(1, sizeof(WgPipeline));
  if (!wp)
    return 0;
  wp->is_compute = d->is_compute;
  wp->gen = wg_next_gen();
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
  // Vertex pulling: no vertex buffer layouts, shaders read storage buffers.
  WGPUVertexState vs = {
      .module = ws->vs_mod,
      .entryPoint = wg_sv("vs_main"),
      .bufferCount = 0,
      .buffers = NULL,
  };

  // Fragment state. n_color_targets == 0 = depth-only pass.
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

    if (d->blend.enabled) {
      WGPUBlendState *bs = &blend_states[i];
      bs->color.srcFactor = kWgBlendFactor[d->blend.src];
      bs->color.dstFactor = kWgBlendFactor[d->blend.dst];
      bs->color.operation = kWgBlendOp[d->blend.op];
      bs->alpha.srcFactor = kWgBlendFactor[d->blend.src_alpha];
      bs->alpha.dstFactor = kWgBlendFactor[d->blend.dst_alpha];
      bs->alpha.operation = kWgBlendOp[d->blend.op_alpha];
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
  // Bound by the open encoder: the write would reach its earlier draws too.
  // Write a fresh buffer instead.
  if (wb->used_serial == g_enc_serial) {
    WGPUBufferDescriptor bd = WGPU_BUFFER_DESCRIPTOR_INIT;
    bd.usage = wgpuBufferGetUsage(wb->buf);
    bd.size = wgpuBufferGetSize(wb->buf);
    WGPUBuffer buf = wgpuDeviceCreateBuffer(g_dev, &bd);
    if (!buf) {
      SDL_Log("[webgpu] update_buffer: buffer recreate failed");
      return;
    }
    wg_retire((WgRetired){.buf = wb->buf});
    gpu_stats_destroy(GPU_STAT_BUFFER, wb->bytes);
    gpu_stats_create(GPU_STAT_BUFFER, wb->bytes);
    wb->buf = buf;
    wb->gen = wg_next_gen();
    wb->used_serial = 0;
  }
  wgpuQueueWriteBuffer(g_queue, wb->buf, 0, data, bytes);
}

static void wg_update_image(BackendImage h, const void *data, size_t bytes) {
  if (!h || !data || bytes == 0)
    return;
  WgImage *wi = (WgImage *)h;
  uint32_t w = wgpuTextureGetWidth(wi->tex);
  uint32_t hh = wgpuTextureGetHeight(wi->tex);

  // Bound by the open encoder: the write would reach its earlier draws too.
  // Write a fresh texture instead. Only plain textures (CopyDst) can be
  // written, so there is no attachment or storage view to recreate.
  if (wi->used_serial == g_enc_serial && !wi->render_target && !wi->storage) {
    WGPUTextureDescriptor td = {
        .usage = wgpuTextureGetUsage(wi->tex),
        .dimension = WGPUTextureDimension_2D,
        .size = {.width = w, .height = hh, .depthOrArrayLayers = 1},
        .format = wgpuTextureGetFormat(wi->tex),
        .mipLevelCount = 1,
        .sampleCount = 1,
    };
    WGPUTexture tex = wgpuDeviceCreateTexture(g_dev, &td);
    WGPUTextureView view = tex ? wgpuTextureCreateView(tex, NULL) : NULL;
    if (!view) {
      SDL_Log("[webgpu] update_image: texture recreate failed");
      if (tex)
        wgpuTextureRelease(tex);
      return;
    }
    wg_retire((WgRetired){.tex = wi->tex, .view = wi->view});
    gpu_stats_destroy(GPU_STAT_VIEW, 0);
    gpu_stats_destroy(GPU_STAT_TEXTURE, wi->stat_bytes);
    gpu_stats_create(GPU_STAT_TEXTURE, wi->stat_bytes);
    gpu_stats_create(GPU_STAT_VIEW, 0);
    wi->tex = tex;
    wi->view = view;
    wi->gen = wg_next_gen();
    wi->used_serial = 0;
  }

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

static void wg_begin_pass(Process *proc, const PassBeginDesc *d) {
  g_ts_last = -1;
  if (!g_enc)
    return;

  // n_color_targets == 0 with a depth target is a depth-only pass.
  int nct = d->n_color_targets;
  if (nct <= 0 && !d->depth_target)
    nct = 1;
  if (nct > SGL_MAX_COLOR_TARGETS)
    nct = SGL_MAX_COLOR_TARGETS;
  WGPULoadOp load_op =
      (d->load == SGL_LOAD_LOAD) ? WGPULoadOp_Load : WGPULoadOp_Clear;

  WGPURenderPassColorAttachment colors[SGL_MAX_COLOR_TARGETS] = {0};

  bool is_offscreen = (d->targets[0] != 0 || d->depth_target != 0);
  int xr_eye = is_offscreen ? -1 : d->xr_eye;
  if (!g_xr_width)
    xr_eye = -1;

  for (int i = 0; i < nct; ++i) {
    colors[i].depthSlice = WGPU_DEPTH_SLICE_UNDEFINED;
    colors[i].loadOp = load_op;
    colors[i].storeOp = WGPUStoreOp_Store;
    colors[i].clearValue = (WGPUColor){d->clear[i][0], d->clear[i][1],
                                       d->clear[i][2], d->clear[i][3]};

    if (is_offscreen && d->targets[i]) {
      WgImage *wi = (WgImage *)d->targets[i];
      colors[i].view = wi->color_att ? wi->color_att : wi->view;
      wi->used_serial = g_enc_serial;
    } else {
      colors[i].view = xr_eye >= 0 ? g_xr_color_view[xr_eye] : g_swapchain_view;
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
      di->used_serial = g_enc_serial;
    } else {
      depth_att.view = xr_eye >= 0 ? g_xr_depth_view[xr_eye] : g_depth_view;
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

  WGPUPassTimestampWrites tsw;
  if (g_ts_cur && g_ts_cur->passes < GPU_TIME_PASSES && d->timed) {
    uint32_t base = (uint32_t)(g_ts_cur - g_ts) * GPU_TIME_STAMPS;
    uint32_t k = g_ts_cur->passes++;
    tsw = (WGPUPassTimestampWrites){
        .querySet = g_ts_set,
        .beginningOfPassWriteIndex = base + 2 * k,
        .endOfPassWriteIndex = base + 2 * k + 1,
    };
    rpd.timestampWrites = &tsw;
    g_ts_last = (int)k;
  }
  g_rpass = wgpuCommandEncoderBeginRenderPass(g_enc, &rpd);

  // Set viewport to match target.
  if (g_rpass) {
    if (xr_eye >= 0)
      g_xr_drawn[xr_eye] = true;
    int w, h;
    if (xr_eye >= 0) {
      w = g_xr_width;
      h = g_xr_height;
    } else if (is_offscreen && d->target_w > 0 && d->target_h > 0) {
      w = d->target_w;
      h = d->target_h;
    } else {
      w = proc->last_w;
      h = proc->last_h;
    }
    wgpuRenderPassEncoderSetViewport(g_rpass, 0, 0, (float)w, (float)h, 0.f,
                                     1.f);
    wgpuRenderPassEncoderSetScissorRect(g_rpass, 0, 0, (uint32_t)w,
                                        (uint32_t)h);
  }

  g_cur_pipeline = NULL;
  g_ibuf_bound = false;
  memset(&g_bound, 0, sizeof(g_bound));
}

static void wg_end_pass(Process *proc) {
  (void)proc;
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
  if (g_bound.pipeline_gen == wp->gen)
    return;
  wgpuRenderPassEncoderSetPipeline(g_rpass, wp->render);
  g_bound.pipeline_gen = wp->gen;
}

// Group-1 entry filling shared by draw bindings and compute dispatch.
//
// Reflection keeps one entry per stage: on WGSL the VS and FS are reflected
// separately and merged with distinct slots, so a resource both stages
// declare under the same name (`StructuredBuffer<float4> item;` in both,
// or one texture sampled in both) is two layout entries. Every entry that
// shares the name gets the same resource; filling only the first would
// leave a layout entry empty and the whole bind group invalid.
static void wg_add_texture_entries(WgBgBuild *b, const ShaderReflection *refl,
                                   const char *name, const WgImage *wi) {
  for (int k = 0; k < refl->tex_count; ++k) {
    if (strcmp(refl->texs[k].name, name) != 0)
      continue;
    int img_slot = refl->texs[k].img_slot;
    int smp_slot = refl->texs[k].smp_slot;
    if (img_slot >= 0)
      wg_bg_add(b, (uint32_t)img_slot, wi->gen, NULL, 0, 0, NULL, wi->view);
    if (smp_slot >= 0)
      wg_bg_add(b, (uint32_t)smp_slot, wi->gen, NULL, 0, 0, wi->sampler, NULL);
  }
}

static void wg_add_storage_buf_entries(WgBgBuild *b,
                                       const ShaderReflection *refl,
                                       const char *name, const WgBuffer *wb,
                                       uint64_t offset, uint64_t bytes) {
  for (int k = 0; k < refl->storage_buf_count; ++k) {
    if (strcmp(refl->storage_bufs[k].name, name) != 0)
      continue;
    wg_bg_add(b, (uint32_t)refl->storage_bufs[k].slot, wb->gen, wb->buf, offset,
              bytes, NULL, NULL);
  }
}

static bool wg_bg_has_binding(const WgBgBuild *b, uint32_t binding) {
  for (int i = 0; i < b->count; ++i)
    if (b->entries[i].binding == binding)
      return true;
  return false;
}

static WgImage *wg_dummy_storage_image(SglPixelFormat fmt) {
  if ((int)fmt < 0 ||
      (size_t)fmt >= sizeof(g_dummy_storage) / sizeof(g_dummy_storage[0]))
    return NULL;
  if (!g_dummy_storage[fmt])
    g_dummy_storage[fmt] = (WgImage *)wg_make_image(&(ImageDesc){
        .fmt = fmt,
        .w = 1,
        .h = 1,
        .storage = true,
    });
  return g_dummy_storage[fmt];
}

static void wg_add_placeholder_entries(WgBgBuild *b,
                                       const ShaderReflection *refl) {
  for (int k = 0; k < refl->tex_count; ++k) {
    int img_slot = refl->texs[k].img_slot;
    int smp_slot = refl->texs[k].smp_slot;
    if (img_slot >= 0 && !wg_bg_has_binding(b, (uint32_t)img_slot))
      wg_bg_add(b, (uint32_t)img_slot, g_dummy_tex->gen, NULL, 0, 0, NULL,
                g_dummy_tex->view);
    if (smp_slot >= 0 && !wg_bg_has_binding(b, (uint32_t)smp_slot))
      wg_bg_add(b, (uint32_t)smp_slot, g_dummy_tex->gen, NULL, 0, 0,
                g_dummy_tex->sampler, NULL);
  }
  for (int k = 0; k < refl->storage_buf_count; ++k) {
    uint32_t slot = (uint32_t)refl->storage_bufs[k].slot;
    if (!wg_bg_has_binding(b, slot))
      wg_bg_add(b, slot, g_dummy_sbuf->gen, g_dummy_sbuf->buf, 0,
                WG_DUMMY_SBUF_BYTES, NULL, NULL);
  }
  for (int k = 0; k < refl->storage_tex_count; ++k) {
    uint32_t slot = (uint32_t)refl->storage_texs[k].slot;
    if (wg_bg_has_binding(b, slot))
      continue;
    WgImage *d = wg_dummy_storage_image(refl->storage_texs[k].access_format);
    if (d)
      wg_bg_add(b, slot, d->gen, NULL, 0, 0, NULL, d->storage_view);
  }
}

static void wg_apply_bindings(const BindingsDesc *b) {
  if (!g_rpass || !g_cur_pipeline)
    return;

  // Index buffer
  g_ibuf_bound = false;
  if (b->ibuf) {
    WgBuffer *ib = (WgBuffer *)b->ibuf;
    uint64_t size = b->ibuf_bytes ? b->ibuf_bytes : ib->bytes;
    if (g_bound.ibuf_gen != ib->gen || g_bound.ibuf_offset != b->ibuf_offset ||
        g_bound.ibuf_size != size) {
      wgpuRenderPassEncoderSetIndexBuffer(
          g_rpass, ib->buf, WGPUIndexFormat_Uint32, b->ibuf_offset, size);
      g_bound.ibuf_gen = ib->gen;
      g_bound.ibuf_offset = b->ibuf_offset;
      g_bound.ibuf_size = size;
    }
    ib->used_serial = g_enc_serial;
    g_ibuf_bound = true;
  }

  // WebKit は pipeline の group を全部束ねることを求めるので、group 1 は entry が無くても束ねる。
  if (!g_cur_pipeline->bgl1)
    return;
  WgBgBuild build;
  wg_bg_begin(&build, g_cur_pipeline->gen, 1);
  if (b->refl) {
    for (int i = 0; i < b->texture_count; ++i) {
      const char *name = b->textures[i].name;
      WgImage *wi = (WgImage *)b->textures[i].image;
      if (!name || !wi)
        continue;
      wg_add_texture_entries(&build, b->refl, name, wi);
      wi->used_serial = g_enc_serial;
    }
    for (int i = 0; i < b->storage_buf_count; ++i) {
      const char *name = b->storage_bufs[i].name;
      WgBuffer *wb = (WgBuffer *)b->storage_bufs[i].buf;
      if (!name || !wb)
        continue;
      uint64_t bytes = b->storage_bufs[i].bytes;
      wg_add_storage_buf_entries(&build, b->refl, name, wb,
                                 b->storage_bufs[i].offset,
                                 bytes ? bytes : wb->bytes);
      wb->used_serial = g_enc_serial;
    }
    wg_add_placeholder_entries(&build, b->refl);
  }
  WGPUBindGroup bg = wg_bg_get(g_cur_pipeline->bgl1, &build);
  if (!bg) {
    SDL_Log("[webgpu] WARN: createBindGroup(group1) failed, count=%d "
            "tex_count=%d bgl1=%p",
            build.count, b->texture_count, (void *)g_cur_pipeline->bgl1);
    return;
  }
  if (bg == g_bound.groups[1])
    return;
  wgpuRenderPassEncoderSetBindGroup(g_rpass, 1, bg, 0, NULL);
  g_bound.groups[1] = bg;
}

// 今 draw が読む block と同じ中身なら、その領域をそのまま使う。
static void wg_write_uniform(int slot, const void *data, size_t bytes) {
  if (slot < 0 || slot >= WG_MAX_UB_SLOTS || !data || bytes == 0)
    return;
  uint32_t n = bytes < WG_UB_SIZE ? (uint32_t)bytes : WG_UB_SIZE;
  WgUniformRing *r = &g_ub[slot];
  if (r->cur_bytes == n && memcmp(r->cpu + r->cur_off, data, n) == 0)
    return;
  if (r->head + WG_UB_SIZE > r->cap && !wg_grow_uniform_ring(r)) {
    SDL_Log("[webgpu] uniform ring grow failed");
    return;
  }
  memcpy(r->cpu + r->head, data, n);
  r->cur_off = r->head;
  r->cur_bytes = n;
  r->head += wg_align(n, WG_UB_ALIGN);
}

static void wg_apply_uniforms(SglShaderStage stage, int ub_slot,
                              const void *data, size_t bytes) {
  (void)stage;
  // pass が無いと続く draw は飛ばされるので、block も書かない。
  // uniform を渡さない後の draw は、実際に描いた最後の draw の block を読む。
  if (!g_rpass)
    return;
  wg_write_uniform(ub_slot, data, bytes);
}

// pipeline の group 0 で、dynamic offset は slot の今の領域 (WgUniformRing.cur_off) を指す。
// entry が欠けると group が invalid になり command buffer ごと捨てられるので、uniform を渡さない draw でも pipeline が宣言する uniform block を全部入れる。
// uniform block の無い pipeline には NULL を返す。
static WGPUBindGroup wg_uniform_group(const WgPipeline *wp,
                                      uint32_t *dyn_offsets, int *out_count) {
  WgBgBuild build;
  wg_bg_begin(&build, wp->gen, 0);
  for (int i = 0; i < wp->refl.ub_count && i < WG_MAX_UB_SLOTS; ++i) {
    int slot = wp->refl.ubs[i].slot;
    if (slot < 0 || slot >= WG_MAX_UB_SLOTS)
      continue;
    const WgUniformRing *r = &g_ub[slot];
    dyn_offsets[build.count] = r->cur_off;
    wg_bg_add(&build, (uint32_t)slot, r->gen, r->buf, 0, WG_UB_SIZE, NULL,
              NULL);
  }
  *out_count = build.count;
  if (build.count == 0)
    return NULL;
  return wg_bg_get(wp->bgl0, &build);
}

static void wg_bind_uniforms(void) {
  uint32_t offsets[WG_MAX_UB_SLOTS];
  int count = 0;
  WGPUBindGroup bg = wg_uniform_group(g_cur_pipeline, offsets, &count);
  if (!bg)
    return;
  size_t offset_bytes = (size_t)count * sizeof(offsets[0]);
  if (bg == g_bound.groups[0] && count == g_bound.offset_count &&
      memcmp(offsets, g_bound.offsets, offset_bytes) == 0)
    return;
  wgpuRenderPassEncoderSetBindGroup(g_rpass, 0, bg, (size_t)count, offsets);
  g_bound.groups[0] = bg;
  g_bound.offset_count = count;
  memcpy(g_bound.offsets, offsets, offset_bytes);
}

// ---- draw / dispatch -------------------------------------------------------

static void wg_draw(int base, int count, int instance_count) {
  if (!g_rpass)
    return;
  if (g_cur_pipeline)
    wg_bind_uniforms();
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

static void wg_dispatch(Process *proc, const ComputeDispatchDesc *d) {
  (void)proc;
  if (!d || !d->pipeline || !d->refl || !g_enc)
    return;

  WgPipeline *wp = (WgPipeline *)d->pipeline;
  if (!wp->compute)
    return;

  // group 0 は draw と同じ ring と group を使う。
  for (int i = 0; i < d->uniform_count; ++i)
    wg_write_uniform(d->uniforms[i].slot, d->uniforms[i].data,
                     d->uniforms[i].bytes);
  uint32_t ub_dyn_offsets[WG_MAX_UB_SLOTS];
  int ub_count = 0;
  WGPUBindGroup bg0 = wg_uniform_group(wp, ub_dyn_offsets, &ub_count);

  // group 1 は dispatch ごとに作る。
  WgBgBuild build;
  wg_bg_begin(&build, wp->gen, 1);
  for (int i = 0; i < d->texture_count; ++i) {
    WgImage *wi = (WgImage *)d->textures[i].image;
    if (!wi || !d->textures[i].name)
      continue;
    wg_add_texture_entries(&build, d->refl, d->textures[i].name, wi);
    wi->used_serial = g_enc_serial;
  }
  for (int i = 0; i < d->n_storage_bufs; ++i) {
    WgBuffer *wb = (WgBuffer *)d->storage_bufs[i].buf;
    if (!wb || !d->storage_bufs[i].name)
      continue;
    uint64_t bytes = d->storage_bufs[i].bytes;
    wg_add_storage_buf_entries(&build, d->refl, d->storage_bufs[i].name, wb,
                               d->storage_bufs[i].offset,
                               bytes ? bytes : wb->bytes);
    wb->used_serial = g_enc_serial;
  }
  for (int i = 0; i < d->n_storage_textures; ++i) {
    WgImage *wi = (WgImage *)d->storage_textures[i].image;
    if (!wi || !d->storage_textures[i].name)
      continue;
    wi->used_serial = g_enc_serial;
    for (int k = 0; k < d->refl->storage_tex_count; ++k) {
      if (strcmp(d->refl->storage_texs[k].name, d->storage_textures[i].name) !=
          0)
        continue;
      wg_bg_add(&build, (uint32_t)d->refl->storage_texs[k].slot, wi->gen, NULL,
                0, 0, NULL, wi->storage_view);
    }
  }
  wg_add_placeholder_entries(&build, d->refl);
  WGPUBindGroupDescriptor bgd = {
      .layout = wp->bgl1,
      .entryCount = (size_t)build.count,
      .entries = build.entries,
  };
  WGPUBindGroup bg1 = wgpuDeviceCreateBindGroup(g_dev, &bgd);

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
  // Reached before the copy is submitted or after the map completed, so no
  // pending work uses the buffer.
  wgpuBufferDestroy(req->buf);
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

// Copies tex into a mappable buffer after the work recorded so far and starts
// the map; poll_readback returns the result once the map has completed.
static bool wg_readback_texture(Process *proc, WGPUTexture tex, int w, int h,
                                SglPixelFormat src_fmt, BackendReadback *out) {
  int bpp = wg_readback_bpp(src_fmt);
  if (bpp == 0)
    return false;

  // Submit pending work so render-target writes are visible.
  if (g_enc) {
    wg_submit_enc();
    // Re-create encoder for rest of frame.
    WGPUCommandEncoderDescriptor enc_desc = {0};
    g_enc = wgpuDeviceCreateCommandEncoder(g_dev, &enc_desc);
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
  req->buf = wgpuDeviceCreateBuffer(g_dev, &bd);
  if (!req->buf) {
    free(req);
    return false;
  }

  WGPUCommandEncoderDescriptor enc_desc = {0};
  WGPUCommandEncoder enc = wgpuDeviceCreateCommandEncoder(g_dev, &enc_desc);
  if (!enc) {
    wg_readback_release_buf(req);
    free(req);
    return false;
  }

  WGPUTexelCopyTextureInfo src = WGPU_TEXEL_COPY_TEXTURE_INFO_INIT;
  src.texture = tex;
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

static bool wg_request_readback_image(Process *proc, BackendImage image, int w,
                                      int h, SglPixelFormat src_fmt,
                                      BackendReadback *out) {
  if (!out)
    return false;
  *out = 0;
  if (!proc || !image || w <= 0 || h <= 0)
    return false;
  return wg_readback_texture(proc, ((WgImage *)image)->tex, w, h, src_fmt, out);
}

static ReadbackPollStatus wg_poll_readback(BackendReadback h, bool wait,
                                           ReadbackResult *out) {
  (void)wait;
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

// Runs before end_frame (capture_before_end_frame) because end_frame releases
// the surface texture.
static bool wg_capture(Process *proc, BackendReadback *out) {
  *out = 0;
  if (!proc || !g_dev || !g_swapchain_tex)
    return false;
  int w = (int)wgpuTextureGetWidth(g_swapchain_tex);
  int h = (int)wgpuTextureGetHeight(g_swapchain_tex);
  if (w <= 0 || h <= 0) {
    SDL_Log("[webgpu] capture: zero extent");
    return false;
  }
  // A frame missing an eye never reaches the headset, so the capture slips past it.
  bool xr = lubwebxr_active();
  if (xr) {
    if (!g_xr_drawn[0] || !g_xr_drawn[1])
      return false;
    wg_copy_xr_eyes(proc);
  }
  // wg_begin_frame picks the surface format from the same condition.
  return wg_readback_texture(proc, g_swapchain_tex, w, h,
                             xr ? SGL_PF_RGBA8 : SGL_PF_BGRA8, out);
}

static SglPixelFormat wg_swapchain_color_format(Process *proc) {
  if (lubwebxr_active())
    return SGL_PF_RGBA8_SRGB;
  return g_surface_format == WGPUTextureFormat_RGBA8Unorm ? SGL_PF_RGBA8
                                                          : SGL_PF_BGRA8;
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
    .frame_buffer = wg_frame_buffer,
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
    .capture_before_end_frame = true,
    .swapchain_color_format = wg_swapchain_color_format,
    .cannot_block_on_gpu = true,
    .request_capture = wg_capture,
    .timestamp = wg_timestamp,
};

#else
typedef int _wg_empty_tu;
#endif // __EMSCRIPTEN__
