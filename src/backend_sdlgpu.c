// SDL3 GPU backend.
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

// Graphics pipeline bound in the current render pass. Binding the same one
// again only makes SDL_GPU build every descriptor set anew at the next draw,
// so sg_apply_pipeline skips it (reset per pass and when the pipeline is
// destroyed, since a new pipeline may reuse the address).
static struct SgPipeline *g_bound_pip = NULL;

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

// --- transient buffers ----------------------------------------------------
//
// Per-frame, write-once buffer data (backend.h transient_buffer: the
// runtime's Gfx.TransientBuffer and the ImGui vertices / indices).
//
// SDL_GPU binds a storage buffer without an offset, so every STORAGE slice
// gets an SDL_GPUBuffer of its own, taken from a pool of buffers of exactly
// the slice's size: the shader sees the whole buffer, and its length must be
// the slice's length as on the other backends. INDEX slices share larger
// chunks (the index binding takes an offset).
//
// The data is staged on the CPU. sg_flush_transients uploads all of it with
// one transfer buffer and one copy pass in a command buffer of its own,
// submitted just before every submit of the frame command buffer (end_frame,
// the readback and capture submits). The queue runs it before the frame's
// commands, so every command reads the data whether it was recorded before
// or after the call. The upload writes with cycle=false: draws recorded
// earlier in the frame already refer to the buffer's current internal
// buffer. (SDL's defragmenter, which moves a buffer to a new internal
// buffer, only runs when a command buffer holding a swapchain texture is
// submitted, i.e. a frame command buffer, and the flush precedes each one.)
//
// Pools belong to one of SG_TRANSIENT_SLOTS frame slots. A slot is reused
// SG_TRANSIENT_SLOTS frames later, after the fences of the submits that used
// it have signaled. A fence is only released once signaled: SDL_GPU puts a
// released fence back in its pool and resets it for the next submit, even
// while the submit it belonged to is still running. Buffers stay from frame
// to frame. Every frame ages its slot, whether or not it makes transients
// (sg_transients_age): a size the slot did not use during its last
// SG_TRANSIENT_IDLE frames loses its pool (a size that changes every frame
// must not pile up buffers that no later frame asks for), and every
// SG_TRANSIENT_TRIM frames of the slot the pools and the INDEX chunks give
// back what they did not need since the previous trim.
#define SG_TRANSIENT_SLOTS 3 // one more than SDL_GPU's frames in flight
#define SG_TRANSIENT_IDLE 2
#define SG_TRANSIENT_TRIM 64
#define SG_INDEX_CHUNK_BYTES (256u * 1024u)
#define SG_STAGE_MIN_BYTES (64u * 1024u)

// STORAGE buffers of one size. bufs[0..used) are handed out this frame.
typedef struct SgSizePool {
  Uint32 size;
  SgBuffer **bufs; // stable pointers: a slice's BackendBuffer
  int count, cap;
  int used;
  int peak;        // max `used` since the last trim
  Uint32 last_use; // the slot's `frames` when it last handed out a buffer
} SgSizePool;

typedef struct SgTransientSlot {
  SgSizePool *pools;
  int n_pools, cap_pools;
  int *lookup; // open addressing, size -> pools index + 1 (0 = empty)
  int lookup_cap;
  // INDEX chunks. chunks[0..chunks_used) are in use this frame; the last of
  // them is filled from chunk_off.
  SgBuffer **chunks;
  int n_chunks, cap_chunks;
  int chunks_used;
  Uint32 chunk_off;
  int chunk_peak;
  Uint32 frames;  // frames that took this slot (wraps; only differences count)
  Uint32 trimmed; // `frames` at the last trim
  // Submits of the frame that used the slot: the uploads and the frame
  // command buffers (end_frame and the readback submits).
  SDL_GPUFence **fences;
  int n_fences, cap_fences;
} SgTransientSlot;

typedef struct SgUpload {
  SDL_GPUBuffer *dst;
  Uint32 dst_offset;
  Uint32 src_offset; // in the staged bytes
  Uint32 size;
} SgUpload;

static SgTransientSlot g_tslots[SG_TRANSIENT_SLOTS];
static int g_tslot = 0;
// g_tslots[g_tslot] was made ready for the current frame (its fences waited
// for, its pools rewound) by the frame's first transient buffer.
static bool g_tslot_open = false;

// Slice data waiting for the next flush.
static struct {
  uint8_t *data;
  size_t bytes, cap;
  SgUpload *ups;
  int n_ups, cap_ups;
  SDL_GPUTransferBuffer *tbuf;
  Uint32 tbuf_bytes;
  size_t peak; // largest flush since the last trim
  int frames;  // since the last trim
} g_stage;

static Uint32 sg_pow2(Uint32 v) {
  if (v > 0x80000000u)
    return v;
  Uint32 p = 1;
  while (p < v)
    p <<= 1;
  return p;
}

// Makes room for `need` items (the capacity doubles). Returns the array,
// moved or not, or NULL when out of memory (the old one is then kept).
static void *sg_grow(void *items, int *cap, int need, size_t item_bytes) {
  if (need <= *cap)
    return items;
  int n = *cap ? *cap * 2 : 16;
  while (n < need)
    n *= 2;
  void *grown = realloc(items, (size_t)n * item_bytes);
  if (grown)
    *cap = n;
  return grown;
}

static SgBuffer *sg_create_transient_buffer(SglBufferType type, Uint32 bytes) {
  SgBuffer *b = (SgBuffer *)calloc(1, sizeof(SgBuffer));
  if (!b)
    return NULL;
  b->bytes = bytes;
  b->type = type;
  // Read-only: a transient is never bound as a compute RW buffer.
  SDL_GPUBufferUsageFlags usage =
      type == SGL_BUFFER_INDEX ? SDL_GPU_BUFFERUSAGE_INDEX
                               : SDL_GPU_BUFFERUSAGE_GRAPHICS_STORAGE_READ |
                                     SDL_GPU_BUFFERUSAGE_COMPUTE_STORAGE_READ;
  b->gpu = SDL_CreateGPUBuffer(g_app->gpu_device, &(SDL_GPUBufferCreateInfo){
                                                      .usage = usage,
                                                      .size = bytes,
                                                  });
  if (!b->gpu) {
    SDL_Log("sg_transient_buffer: SDL_CreateGPUBuffer failed: %s",
            SDL_GetError());
    free(b);
    return NULL;
  }
  gpu_stats_create(GPU_STAT_BUFFER, bytes);
  return b;
}

static void sg_release_transient_buffer(SgBuffer *b) {
  SDL_ReleaseGPUBuffer(g_app->gpu_device, b->gpu);
  gpu_stats_destroy(GPU_STAT_BUFFER, b->bytes);
  free(b);
}

// The table is indexed with the low bits, so they must depend on every bit
// of the size (murmur3's finalizer): sizes that are multiples of a large
// power of two would otherwise share a few buckets.
static Uint32 sg_size_hash(Uint32 size) {
  size ^= size >> 16;
  size *= 0x85ebca6bu;
  size ^= size >> 13;
  size *= 0xc2b2ae35u;
  size ^= size >> 16;
  return size;
}

// Rebuilds the lookup table with `cap` entries (a power of two); only a new
// size can fail to allocate.
static bool sg_pool_rehash(SgTransientSlot *s, int cap) {
  int *lookup = s->lookup;
  if (cap != s->lookup_cap) {
    lookup = (int *)calloc((size_t)cap, sizeof(int));
    if (!lookup)
      return false;
    free(s->lookup);
    s->lookup = lookup;
    s->lookup_cap = cap;
  } else {
    memset(lookup, 0, (size_t)cap * sizeof(int));
  }
  Uint32 mask = (Uint32)cap - 1;
  for (int i = 0; i < s->n_pools; ++i) {
    Uint32 k = sg_size_hash(s->pools[i].size) & mask;
    while (lookup[k])
      k = (k + 1) & mask;
    lookup[k] = i + 1;
  }
  return true;
}

static SgSizePool *sg_pool_get(SgTransientSlot *s, Uint32 size) {
  if (s->lookup_cap > 0) {
    Uint32 mask = (Uint32)s->lookup_cap - 1;
    for (Uint32 k = sg_size_hash(size) & mask; s->lookup[k];
         k = (k + 1) & mask) {
      SgSizePool *p = &s->pools[s->lookup[k] - 1];
      if (p->size == size)
        return p;
    }
  }
  SgSizePool *pools =
      sg_grow(s->pools, &s->cap_pools, s->n_pools + 1, sizeof(SgSizePool));
  if (!pools)
    return NULL;
  s->pools = pools;
  if ((s->n_pools + 1) * 2 > s->lookup_cap &&
      !sg_pool_rehash(s, s->lookup_cap ? s->lookup_cap * 2 : 64))
    return NULL;
  SgSizePool *p = &s->pools[s->n_pools++];
  memset(p, 0, sizeof(*p));
  p->size = size;
  Uint32 mask = (Uint32)s->lookup_cap - 1;
  Uint32 k = sg_size_hash(size) & mask;
  while (s->lookup[k])
    k = (k + 1) & mask;
  s->lookup[k] = s->n_pools;
  return p;
}

static SgBuffer *sg_transient_storage(SgTransientSlot *s, Uint32 size) {
  SgSizePool *p = sg_pool_get(s, size);
  if (!p)
    return NULL;
  if (p->used == p->count) {
    SgBuffer **bufs =
        sg_grow(p->bufs, &p->cap, p->count + 1, sizeof(SgBuffer *));
    if (!bufs)
      return NULL;
    p->bufs = bufs;
    SgBuffer *b = sg_create_transient_buffer(SGL_BUFFER_STORAGE, size);
    if (!b)
      return NULL;
    p->bufs[p->count++] = b;
  }
  SgBuffer *b = p->bufs[p->used++];
  if (p->used > p->peak)
    p->peak = p->used;
  p->last_use = s->frames;
  return b;
}

// Index slices are placed one after another (u32 indices keep every offset
// 4-byte aligned); a slice that does not fit moves on to the next chunk,
// never back to the start of one in use.
static SgBuffer *sg_transient_index(SgTransientSlot *s, Uint32 size,
                                    Uint32 *out_offset) {
  Uint32 off = s->chunk_off;
  if (s->chunks_used == 0 ||
      size > s->chunks[s->chunks_used - 1]->bytes - off) {
    // Next chunk: a kept one that is big enough, else a new one.
    int pick = -1;
    for (int i = s->chunks_used; i < s->n_chunks; ++i) {
      if (s->chunks[i]->bytes >= size) {
        pick = i;
        break;
      }
    }
    if (pick < 0) {
      SgBuffer **chunks = sg_grow(s->chunks, &s->cap_chunks, s->n_chunks + 1,
                                  sizeof(SgBuffer *));
      if (!chunks)
        return NULL;
      s->chunks = chunks;
      SgBuffer *c = sg_create_transient_buffer(
          SGL_BUFFER_INDEX,
          size > SG_INDEX_CHUNK_BYTES ? sg_pow2(size) : SG_INDEX_CHUNK_BYTES);
      if (!c)
        return NULL;
      pick = s->n_chunks++;
      s->chunks[pick] = c;
    }
    SgBuffer *c = s->chunks[pick];
    s->chunks[pick] = s->chunks[s->chunks_used];
    s->chunks[s->chunks_used++] = c;
    if (s->chunks_used > s->chunk_peak)
      s->chunk_peak = s->chunks_used;
    off = 0;
  }
  s->chunk_off = off + size;
  *out_offset = off;
  return s->chunks[s->chunks_used - 1];
}

static bool sg_stage_upload(SDL_GPUBuffer *dst, Uint32 dst_offset,
                            const void *data, Uint32 size) {
  size_t src = g_stage.bytes;
  size_t end = (src + size + 3u) & ~(size_t)3u;
  if (end > 0xffffffffu) // one transfer buffer (Uint32 size) per flush
    return false;
  if (end > g_stage.cap) {
    size_t cap = g_stage.cap ? g_stage.cap : SG_STAGE_MIN_BYTES;
    while (cap < end)
      cap *= 2;
    uint8_t *grown = (uint8_t *)realloc(g_stage.data, cap);
    if (!grown)
      return false;
    g_stage.data = grown;
    g_stage.cap = cap;
  }
  memcpy(g_stage.data + src, data, size);
  SgUpload *last = g_stage.n_ups > 0 ? &g_stage.ups[g_stage.n_ups - 1] : NULL;
  if (last && last->dst == dst && last->dst_offset + last->size == dst_offset &&
      last->src_offset + last->size == src) {
    last->size += size; // the next slice of the same INDEX chunk
  } else {
    SgUpload *ups = sg_grow(g_stage.ups, &g_stage.cap_ups, g_stage.n_ups + 1,
                            sizeof(SgUpload));
    if (!ups)
      return false;
    g_stage.ups = ups;
    g_stage.ups[g_stage.n_ups++] = (SgUpload){
        .dst = dst,
        .dst_offset = dst_offset,
        .src_offset = (Uint32)src,
        .size = size,
    };
  }
  g_stage.bytes = end;
  return true;
}

static void sg_release_fences(App *app, SDL_GPUFence **fences, int n) {
  for (int i = 0; i < n; ++i) {
    SDL_ReleaseGPUFence(app->gpu_device, fences[i]);
    gpu_stats_destroy(GPU_STAT_FENCE, 0);
  }
}

static void sg_tslot_add_fence(App *app, SDL_GPUFence *fence) {
  SgTransientSlot *s = &g_tslots[g_tslot];
  gpu_stats_create(GPU_STAT_FENCE, 0);
  SDL_GPUFence **fences = sg_grow(s->fences, &s->cap_fences, s->n_fences + 1,
                                  sizeof(SDL_GPUFence *));
  if (!fences) {
    // No room to keep it: wait now instead.
    SDL_WaitForGPUFences(app->gpu_device, true, &fence, 1);
    sg_release_fences(app, &fence, 1);
    return;
  }
  s->fences = fences;
  s->fences[s->n_fences++] = fence;
}

// Called by every frame as it takes its slot (sg_begin_frame), whether or
// not the frame makes transients. Releases the slot's fences that have
// signaled, without waiting (sg_tslot_open waits for the rest), and what
// the slot no longer needs: the pools of sizes it did not use during its
// last SG_TRANSIENT_IDLE frames, and every SG_TRANSIENT_TRIM frames the
// buffers of a pool past its peak count and the INDEX chunks past the peak
// number in use since the previous trim. Only reusing a buffer needs the
// fences: SDL_ReleaseGPUBuffer destroys a buffer once the command buffers
// that use it have finished. Staging memory far larger than the largest
// flush of the last SG_TRANSIENT_TRIM frames is released too.
static void sg_transients_age(App *app) {
  SgTransientSlot *s = &g_tslots[g_tslot];
  int kept = 0;
  for (int i = 0; i < s->n_fences; ++i) {
    if (SDL_QueryGPUFence(app->gpu_device, s->fences[i]))
      sg_release_fences(app, &s->fences[i], 1);
    else
      s->fences[kept++] = s->fences[i];
  }
  s->n_fences = kept;

  ++s->frames;
  bool trim = s->frames - s->trimmed >= SG_TRANSIENT_TRIM;
  int n = 0;
  for (int i = 0; i < s->n_pools; ++i) {
    SgSizePool *p = &s->pools[i];
    int keep = s->frames - p->last_use > SG_TRANSIENT_IDLE ? 0
               : trim                                      ? p->peak
                                                           : p->count;
    while (p->count > keep)
      sg_release_transient_buffer(p->bufs[--p->count]);
    if (trim)
      p->peak = 0;
    if (p->count == 0) {
      free(p->bufs);
      continue;
    }
    s->pools[n++] = *p;
  }
  if (n != s->n_pools) {
    s->n_pools = n;
    // A burst of sizes leaves a large table: halve it while at most 1/8 full.
    int cap = s->lookup_cap;
    while (cap > 64 && n * 8 <= cap)
      cap /= 2;
    if (!sg_pool_rehash(s, cap))
      (void)sg_pool_rehash(s, s->lookup_cap); // same size: rebuilt in place
  }
  if (trim) {
    while (s->n_chunks > s->chunk_peak)
      sg_release_transient_buffer(s->chunks[--s->n_chunks]);
    s->chunk_peak = 0;
    s->trimmed = s->frames;
  }

  if (++g_stage.frames < SG_TRANSIENT_TRIM)
    return;
  g_stage.frames = 0;
  size_t keep =
      g_stage.peak > SG_STAGE_MIN_BYTES ? g_stage.peak : SG_STAGE_MIN_BYTES;
  if (g_stage.tbuf && g_stage.tbuf_bytes / 4 > keep) {
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, g_stage.tbuf);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, g_stage.tbuf_bytes);
    g_stage.tbuf = NULL;
    g_stage.tbuf_bytes = 0;
  }
  if (g_stage.bytes == 0 && g_stage.cap / 4 > keep) {
    free(g_stage.data);
    g_stage.data = NULL;
    g_stage.cap = 0;
  }
  g_stage.peak = 0;
}

// The current frame's slot, made ready by the frame's first transient
// buffer: waits until the GPU is done with the frame that used the slot
// last, then hands its buffers out again from the start.
static SgTransientSlot *sg_tslot_open(App *app) {
  SgTransientSlot *s = &g_tslots[g_tslot];
  if (g_tslot_open)
    return s;
  if (s->n_fences > 0) {
    if (!SDL_WaitForGPUFences(app->gpu_device, true, s->fences,
                              (Uint32)s->n_fences)) {
      SDL_Log("sg_transient_buffer: SDL_WaitForGPUFences failed: %s",
              SDL_GetError());
      return NULL;
    }
    sg_release_fences(app, s->fences, s->n_fences);
    s->n_fences = 0;
  }
  for (int i = 0; i < s->n_pools; ++i)
    s->pools[i].used = 0;
  s->chunks_used = 0;
  s->chunk_off = 0;
  g_tslot_open = true;
  return s;
}

static bool sg_upload_staged(App *app) {
  SDL_GPUDevice *dev = app->gpu_device;
  Uint32 bytes = (Uint32)g_stage.bytes;
  if (g_stage.peak < bytes)
    g_stage.peak = bytes;
  if (g_stage.tbuf && g_stage.tbuf_bytes < bytes) {
    SDL_ReleaseGPUTransferBuffer(dev, g_stage.tbuf);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, g_stage.tbuf_bytes);
    g_stage.tbuf = NULL;
    g_stage.tbuf_bytes = 0;
  }
  if (!g_stage.tbuf) {
    Uint32 cap = sg_pow2(bytes);
    if (cap < SG_STAGE_MIN_BYTES)
      cap = SG_STAGE_MIN_BYTES;
    g_stage.tbuf = SDL_CreateGPUTransferBuffer(
        dev, &(SDL_GPUTransferBufferCreateInfo){
                 .usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
                 .size = cap,
             });
    if (!g_stage.tbuf) {
      SDL_Log("sg_flush_transients: tbuf: %s", SDL_GetError());
      return false;
    }
    g_stage.tbuf_bytes = cap;
    gpu_stats_create(GPU_STAT_TRANSFER_BUFFER, cap);
  }
  // cycle: an earlier flush of this or a previous frame may still read it
  void *map = SDL_MapGPUTransferBuffer(dev, g_stage.tbuf, true);
  if (!map) {
    SDL_Log("sg_flush_transients: map: %s", SDL_GetError());
    return false;
  }
  memcpy(map, g_stage.data, bytes);
  SDL_UnmapGPUTransferBuffer(dev, g_stage.tbuf);
  SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(dev);
  if (!cmd) {
    SDL_Log("sg_flush_transients: cmd: %s", SDL_GetError());
    return false;
  }
  SDL_GPUCopyPass *cp = SDL_BeginGPUCopyPass(cmd);
  for (int i = 0; i < g_stage.n_ups; ++i) {
    const SgUpload *u = &g_stage.ups[i];
    SDL_UploadToGPUBuffer(cp,
                          &(SDL_GPUTransferBufferLocation){
                              .transfer_buffer = g_stage.tbuf,
                              .offset = u->src_offset,
                          },
                          &(SDL_GPUBufferRegion){
                              .buffer = u->dst,
                              .offset = u->dst_offset,
                              .size = u->size,
                          },
                          false);
  }
  SDL_EndGPUCopyPass(cp);
  // Fenced too, so the slot is never reused while this is in flight even if
  // the frame's own submit fails.
  SDL_GPUFence *fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
  if (!fence) {
    SDL_Log("sg_flush_transients: submit: %s", SDL_GetError());
    return false;
  }
  sg_tslot_add_fence(app, fence);
  return true;
}

// Uploads the staged slice data (see the section comment). Called right
// before each submit of the frame command buffer.
static bool sg_flush_transients(App *app) {
  if (g_stage.n_ups == 0)
    return true;
  bool ok = sg_upload_staged(app);
  if (!ok)
    SDL_Log("sg_flush_transients: %d uploads (%zu bytes) failed; draws "
            "reading this frame's transient buffers see stale data",
            g_stage.n_ups, g_stage.bytes);
  g_stage.n_ups = 0;
  g_stage.bytes = 0;
  return ok;
}

// Submits the frame command buffer after the staged transient data. When
// the frame used transient buffers the submit's fence is kept for the slot
// (the command buffer may bind any of the frame's transient buffers).
static bool sg_submit_frame_cmd(App *app) {
  sg_flush_transients(app);
  bool ok;
  if (g_tslot_open) {
    SDL_GPUFence *fence =
        SDL_SubmitGPUCommandBufferAndAcquireFence(app->gpu_cmd);
    if (fence)
      sg_tslot_add_fence(app, fence);
    ok = fence != NULL;
  } else {
    ok = SDL_SubmitGPUCommandBuffer(app->gpu_cmd);
  }
  app->gpu_cmd = NULL;
  app->gpu_swapchain_tex = NULL;
  return ok;
}

static bool sg_transient_buffer(SglBufferType type, const void *data,
                                size_t bytes, BufferSlice *out) {
  if (!g_app || !g_app->gpu_device || !data || bytes == 0 ||
      bytes > 0xffffffffu)
    return false;
  if (type != SGL_BUFFER_INDEX && type != SGL_BUFFER_STORAGE)
    return false;
  SgTransientSlot *s = sg_tslot_open(g_app);
  if (!s)
    return false;
  Uint32 size = (Uint32)bytes;
  Uint32 offset = 0;
  SgBuffer *b = type == SGL_BUFFER_INDEX ? sg_transient_index(s, size, &offset)
                                         : sg_transient_storage(s, size);
  if (!b || !sg_stage_upload(b->gpu, offset, data, size))
    return false;
  out->buf = (BackendBuffer)b;
  out->offset = offset;
  out->size = bytes;
  return true;
}

static void sg_transients_shutdown(App *app) {
  for (int k = 0; k < SG_TRANSIENT_SLOTS; ++k) {
    SgTransientSlot *s = &g_tslots[k];
    if (s->n_fences > 0) {
      SDL_WaitForGPUFences(app->gpu_device, true, s->fences,
                           (Uint32)s->n_fences);
      sg_release_fences(app, s->fences, s->n_fences);
    }
    free(s->fences);
    for (int i = 0; i < s->n_pools; ++i) {
      for (int j = 0; j < s->pools[i].count; ++j)
        sg_release_transient_buffer(s->pools[i].bufs[j]);
      free(s->pools[i].bufs);
    }
    for (int i = 0; i < s->n_chunks; ++i)
      sg_release_transient_buffer(s->chunks[i]);
    free(s->pools);
    free(s->lookup);
    free(s->chunks);
    memset(s, 0, sizeof(*s));
  }
  if (g_stage.tbuf) {
    SDL_ReleaseGPUTransferBuffer(app->gpu_device, g_stage.tbuf);
    gpu_stats_destroy(GPU_STAT_TRANSFER_BUFFER, g_stage.tbuf_bytes);
  }
  free(g_stage.data);
  free(g_stage.ups);
  memset(&g_stage, 0, sizeof(g_stage));
  g_tslot = 0;
  g_tslot_open = false;
}

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
    sg_transients_shutdown(app);
    sg_release_depth_texture(app);
    SDL_ReleaseWindowFromGPUDevice(app->gpu_device, app->window);
    SDL_DestroyGPUDevice(app->gpu_device);
    app->gpu_device = NULL;
  }
  g_app = NULL;
}

static void sg_begin_frame(App *app, int *out_w, int *out_h) {
  g_app = app;
  g_tslot = (g_tslot + 1) % SG_TRANSIENT_SLOTS;
  g_tslot_open = false;
  sg_transients_age(app);
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
  if (app->gpu_cmd && !sg_submit_frame_cmd(app)) {
    SDL_Log("SDL_SubmitGPUCommandBuffer failed: %s", SDL_GetError());
  }
  // Without a command buffer left to submit, nothing reads what was staged.
  g_stage.n_ups = 0;
  g_stage.bytes = 0;
  app->gpu_cmd = NULL;
  app->gpu_swapchain_tex = NULL;
}

static void sg_begin_pass(App *app, const PassBeginDesc *d) {
  g_bound_pip = NULL;
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
  g_bound_pip = NULL;
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
                                    size_t data_bytes, size_t cap_bytes) {
  if (!g_app || !g_app->gpu_device) {
    SDL_Log("sg_make_buffer: no GPU device");
    return 0;
  }
  SgBuffer *b = (SgBuffer *)calloc(1, sizeof(SgBuffer));
  if (!b)
    return 0;
  b->bytes = (Uint32)cap_bytes;
  b->type = type;
  SDL_GPUBufferUsageFlags usage;
  switch (type) {
  case SGL_BUFFER_INDEX:
    usage = SDL_GPU_BUFFERUSAGE_INDEX;
    break;
  case SGL_BUFFER_STORAGE:
    // Storage buffer for compute and for graphics-stage reads (vertex
    // pulling): the compute pass writes via COMPUTE_STORAGE_*, the render
    // pass reads it as a graphics storage buffer.
    usage = SDL_GPU_BUFFERUSAGE_COMPUTE_STORAGE_READ |
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
                                                      .size = b->bytes,
                                                  });
  if (!b->gpu) {
    SDL_Log("SDL_CreateGPUBuffer failed: %s", SDL_GetError());
    free(b);
    return 0;
  }
  gpu_stats_create(GPU_STAT_BUFFER, b->bytes);
  if (data && data_bytes > 0) {
    if (!sg_upload_to_buffer(b->gpu, data, data_bytes, false)) {
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
  if (g_bound_pip == p)
    g_bound_pip = NULL;
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
  if (g_current_pip && g_current_pip->gpu && g_render_pass &&
      g_current_pip != g_bound_pip) {
    SDL_BindGPUGraphicsPipeline(g_render_pass, g_current_pip->gpu);
    g_bound_pip = g_current_pip;
  }
}

static void sg_apply_bindings(const BindingsDesc *b) {
  if (!g_render_pass)
    return;
  if (b->ibuf) {
    SgBuffer *ib = (SgBuffer *)b->ibuf;
    if (ib && ib->gpu) {
      SDL_BindGPUIndexBuffer(g_render_pass,
                             &(SDL_GPUBufferBinding){
                                 .buffer = ib->gpu,
                                 .offset = (Uint32)b->ibuf_offset,
                             },
                             SDL_GPU_INDEXELEMENTSIZE_32BIT);
      g_last_indexed = true;
    } else {
      g_last_indexed = false;
    }
  } else {
    g_last_indexed = false;
  }
  // Fragment-stage texture+sampler binding: the reflection entry (`slot`)
  // gives the sampler slot; a single SDL_BindGPUFragmentSamplers covers
  // [0..max_slot].
  if (b->texture_count > 0 && b->refl) {
    SDL_GPUTextureSamplerBinding tsb[8] = {0};
    int max_slot = -1;
    for (int i = 0; i < b->texture_count; ++i) {
      int j = b->textures[i].slot;
      if (j < 0 || j >= b->refl->tex_count)
        continue;
      SgImage *im = (SgImage *)b->textures[i].image;
      if (!im || !im->tex || !im->smp)
        continue;
      int slot = b->refl->texs[j].smp_slot;
      if (slot < 0 || slot >= 8)
        continue;
      tsb[slot] = (SDL_GPUTextureSamplerBinding){
          .texture = im->tex,
          .sampler = im->smp,
      };
      if (slot > max_slot)
        max_slot = slot;
    }
    if (max_slot >= 0) {
      SDL_BindGPUFragmentSamplers(g_render_pass, 0, tsb,
                                  (Uint32)(max_slot + 1));
    }
  }
  // Graphics-stage read-only storage buffers: SDL_GPU numbers them in their
  // own slot space per stage (the reflection entry's `slot`). The entry
  // `slot` and every later entry of the same name (the buffer read by
  // another stage) are bound. SDL_GPU binds a whole buffer (no offset /
  // range): a keyed buffer shows the shader its full capacity rather than
  // the logical size, and the offset is always 0 (transient slices get
  // buffers of their own).
  for (int i = 0; i < b->storage_buf_count && b->refl; ++i) {
    SgBuffer *sb = (SgBuffer *)b->storage_bufs[i].buf;
    int first = b->storage_bufs[i].slot;
    if (!sb || !sb->gpu || first < 0 || first >= b->refl->storage_buf_count)
      continue;
    const char *name = b->refl->storage_bufs[first].name;
    for (int j = first; j < b->refl->storage_buf_count; ++j) {
      const ShaderStorageBuf *r = &b->refl->storage_bufs[j];
      if (!r->readonly || (j != first && strcmp(r->name, name) != 0))
        continue;
      if (r->stage == SGL_STAGE_VERTEX)
        SDL_BindGPUVertexStorageBuffers(g_render_pass, (Uint32)r->slot,
                                        &sb->gpu, 1);
      else if (r->stage == SGL_STAGE_FRAGMENT)
        SDL_BindGPUFragmentStorageBuffers(g_render_pass, (Uint32)r->slot,
                                          &sb->gpu, 1);
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
  // layout (set 1 = RW, set 0 = RO). The reflection entry (`slot`) gives the
  // binding within its set; the current compute binding normally uses one
  // of each. Whole buffers, as in sg_apply_bindings (SDL_GPU has no
  // storage-buffer range; a transient slice has a buffer of its own).
  SDL_GPUStorageBufferReadWriteBinding rw[SGL_MAX_STORAGE_BUFS] = {0};
  SDL_GPUBuffer *ro[SGL_MAX_STORAGE_BUFS] = {0};
  int n_rw = 0, n_ro = 0;
  for (int i = 0; i < d->n_storage_bufs; ++i) {
    SgBuffer *buf = (SgBuffer *)d->storage_bufs[i].buf;
    int k = d->storage_bufs[i].slot;
    if (!buf || !buf->gpu || k < 0 || k >= d->refl->storage_buf_count)
      continue;
    int slot = d->refl->storage_bufs[k].slot;
    if (slot < 0 || slot >= SGL_MAX_STORAGE_BUFS)
      continue;
    if (d->refl->storage_bufs[k].readonly) {
      ro[slot] = buf->gpu;
      if (slot + 1 > n_ro)
        n_ro = slot + 1;
    } else {
      rw[slot].buffer = buf->gpu;
      rw[slot].cycle = true; // discard previous content
      if (slot + 1 > n_rw)
        n_rw = slot + 1;
    }
  }
  SDL_GPUStorageTextureReadWriteBinding rw_tex[SGL_MAX_STORAGE_TEXTURES] = {0};
  SDL_GPUTexture *ro_tex[SGL_MAX_STORAGE_TEXTURES] = {0};
  int n_rw_tex = 0, n_ro_tex = 0;
  for (int i = 0; i < d->n_storage_textures; ++i) {
    SgImage *img = (SgImage *)d->storage_textures[i].image;
    int k = d->storage_textures[i].slot;
    if (!img || !img->tex || k < 0 || k >= d->refl->storage_tex_count)
      continue;
    int slot = d->refl->storage_texs[k].slot;
    if (slot < 0 || slot >= SGL_MAX_STORAGE_TEXTURES)
      continue;
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
      int k = d->textures[i].slot;
      if (!img || !img->tex || !img->smp || k < 0 || k >= d->refl->tex_count)
        continue;
      int slot = d->refl->texs[k].smp_slot;
      if (slot >= 0 && slot < SGL_MAX_TEXTURES) {
        tsb[slot].texture = img->tex;
        tsb[slot].sampler = img->smp;
        if (slot > max_slot)
          max_slot = slot;
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
  if (!sg_submit_frame_cmd(app)) {
    SDL_Log("sg_readback_image: SDL_SubmitGPUCommandBuffer failed: %s",
            SDL_GetError());
    return false;
  }
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

  // The transient data goes first. The slot needs no fence of this submit:
  // the wait below finishes all of the frame's GPU work (the slot's fences
  // are still released only at its next use).
  sg_flush_transients(app);
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
    .transient_buffer = sg_transient_buffer,
};
