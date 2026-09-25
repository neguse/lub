// gfx の C API (include/lub/lub_api.h)。Lua binding にあった検証と resource
// 解決をここに置き、binding は desc への詰め替えだけにする。
#include "api_internal.h"
#include "backend.h"
#include "enums.h"
#include "pass.h"
#include "pipeline.h"
#include "resources.h"
#include "shader.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define LUB_KEY_MAX 256
// swapchain を指す特別な handle (lub_gfx_main_tex() が返す)。
#define LUB_GFX_MAIN_TEX ((LubHandle) - 1)
#define LUB_GFX_MAX_COLOR_TARGETS SGL_MAX_COLOR_TARGETS

static bool is_depth_format(SglPixelFormat fmt) {
  return fmt == SGL_PF_DEPTH16 || fmt == SGL_PF_DEPTH24_STENCIL8 ||
         fmt == SGL_PF_DEPTH32F;
}

// byte 列で初期化できる format の 1 pixel の byte 数。0 = 初期化不可。
static int bytes_per_pixel(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_RGBA8:
    return 4;
  case SGL_PF_R8:
    return 1;
  case SGL_PF_RG8:
    return 2;
  default:
    return 0;
  }
}

static bool is_known_format(int fmt) {
  return fmt >= SGL_PF_RGBA8 && fmt <= SGL_PF_DEPTH32F;
}

static ShaderTargetBackend shader_target_for_backend(void) {
#if defined(__EMSCRIPTEN__)
  // wasm: webgpu backend 一択。slang-wasm が WGSL を出す。
  return SHADER_TARGET_WGSL;
#elif defined(_WIN32)
  if (g_backend == &g_backend_d3d12)
    return SHADER_TARGET_D3D12;
  // vulkan / sdlgpu は SDLGPU target の SPIR-V を食う
  // (descriptor set 規約が SDL_GPU 準拠のため)。
  return SHADER_TARGET_SDLGPU;
#else
  return SHADER_TARGET_SDLGPU;
#endif
}

// use_* の version 引数。NULL は「内容が変わった」宣言で runtime が新しい
// 実効 version を発行する。非 NULL は identity claim。
static int64_t effective_version(App *app, const int32_t *version,
                                 bool *declared) {
  if (!version) {
    *declared = true;
    return res_table_next_revision(&app->res);
  }
  *declared = false;
  return (int64_t)*version;
}

// sweep で消えた resource の error の後半。handle が一度は発行されていれば
// (発行されていない値と違い) 使われずに破棄された resource。
#define SWEPT_HINT                                                             \
  "was swept (not used for %d frames); declare it again with use_*"

static bool handle_swept(App *app, LubHandle h) {
  return h > 0 && h <= app->res.next_handle &&
         !res_table_get_by_handle(&app->res, h);
}

LubStatus api_gfx_stale_ref(App *app, LubStr key) {
  return lub_api_fail(app, "'%.*s' " SWEPT_HINT, key.len,
                      key.ptr ? key.ptr : "", app->resource_sweep_after_frames);
}

// handle の entry を引き、今の frame で使ったことにする。
static ResEntry *entry_from_handle(App *app, LubHandle h, ResKind kind,
                                   const char *fn, const char *what) {
  ResEntry *e = res_table_get_by_handle(&app->res, h);
  if (!e) {
    if (handle_swept(app, h))
      lub_api_fail(app, "%s: %s handle %d " SWEPT_HINT, fn, what, (int)h,
                   app->resource_sweep_after_frames);
    else
      lub_api_fail(app, "%s: %s handle %d is invalid", fn, what, (int)h);
    return NULL;
  }
  if (e->kind != kind) {
    lub_api_fail(app, "%s: %s handle %d is not a %s", fn, what, (int)h,
                 kind == RES_TEXTURE  ? "texture"
                 : kind == RES_BUFFER ? "buffer"
                                      : "shader");
    return NULL;
  }
  res_table_touch(e, (int64_t)app->frame_index);
  return e;
}

static bool key_arg(App *app, LubStr key, char *buf, const char *fn) {
  if (key.len <= 0) {
    lub_api_fail(app, "%s: key must not be empty", fn);
    return false;
  }
  if (!lub_str_copy(key, buf, LUB_KEY_MAX)) {
    lub_api_fail(app, "%s: key too long (%d bytes, max %d)", fn, key.len,
                 LUB_KEY_MAX - 1);
    return false;
  }
  return true;
}

LubHandle lub_gfx_main_tex(LubContext *ctx) {
  (void)ctx;
  return LUB_GFX_MAIN_TEX;
}

void lub_gfx_size(LubContext *ctx, int32_t *out_w, int32_t *out_h) {
  App *app = lub_api_app(ctx);
  int w = 0, h = 0;
  if (app && app->window)
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
  if (w <= 0)
    w = 1280;
  if (h <= 0)
    h = 720;
  if (out_w)
    *out_w = w;
  if (out_h)
    *out_h = h;
}

static LubHandle lookup_kind(App *app, LubStr key, ResKind kind) {
  if (key.len <= 0)
    return 0;
  ResEntry *e = res_table_get_n(&app->res, key.ptr, (size_t)key.len);
  return e && e->kind == kind ? e->handle : 0;
}

LubHandle lub_gfx_lookup_texture(LubContext *ctx, LubStr key) {
  return lookup_kind(lub_api_app(ctx), key, RES_TEXTURE);
}

LubHandle lub_gfx_lookup_shader(LubContext *ctx, LubStr key) {
  return lookup_kind(lub_api_app(ctx), key, RES_SHADER);
}

LubHandle lub_gfx_lookup_buffer(LubContext *ctx, LubStr key) {
  return lookup_kind(lub_api_app(ctx), key, RES_BUFFER);
}

bool lub_gfx_resource_info(LubContext *ctx, int32_t handle, LubStr *key,
                           int32_t *version) {
  App *app = lub_api_app(ctx);
  ResEntry *e = res_table_get_by_handle(&app->res, handle);
  if (!e)
    return false;
  if (key)
    *key = lub_str_c(e->key);
  if (version)
    *version = (int32_t)e->version;
  return true;
}

// ------------------------------------------------------------ resources

// data を渡さない use_* (「変わっていない」の再主張と、Lua binding が data を
// 読む前に試す問い合わせ) の下調べ。version が与えられ、key がこの kind で
// その version を持っていれば entry を返す。無ければ (検査に通らない引数も
// 含めて) 何も変えずに NULL。data を渡す呼び出しが同じ引数なら hit するときに
// 限って entry を返す (種別ごとの形の一致は呼び出し側が見る)。
static ResEntry *cached_entry(App *app, LubStr key, ResKind kind,
                              const int32_t *version) {
  if (!version || key.len <= 0 || key.len >= LUB_KEY_MAX)
    return NULL;
  ResEntry *e = res_table_get_n(&app->res, key.ptr, (size_t)key.len);
  if (!e || e->kind != kind || e->version != (int64_t)*version)
    return NULL;
  return e;
}

// hit した entry を今の frame で使ったことにして返す。
static LubStatus reuse_entry(App *app, ResEntry *e, LubHandle *out) {
  res_table_touch(e, (int64_t)app->frame_index);
  *out = e->handle;
  return LUB_OK;
}

// key が同じ種別の buffer をこの version で持っている (data を読まずに返せる)。
static bool buffer_hit(const ResEntry *e, int32_t type, int64_t ver) {
  return e->u.buf.h != 0 && e->version == ver &&
         e->u.buf.type == (SglBufferType)type;
}

static ResEntry *buffer_cached(App *app, LubStr key, int32_t type,
                               const int32_t *version) {
  ResEntry *e = cached_entry(app, key, RES_BUFFER, version);
  return e && buffer_hit(e, type, e->version) ? e : NULL;
}

static void digest_use_buffer(App *app, const char *tag, LubStr key,
                              int32_t type, int32_t count,
                              const int32_t *version) {
  if (!app->digest.enabled)
    return;
  digest_tag(app, tag);
  digest_str(app, key);
  digest_i32(app, type);
  digest_i32(app, count);
  digest_i32(app, version ? *version : 0);
}

// use_buffer に渡された data の要素型。buffer は INDEX を u32、STORAGE を
// float で持つので、違う型の data は upload するときだけ写す。
typedef enum { BUF_DATA_NONE, BUF_DATA_FLOATS, BUF_DATA_INTS } BufData;

// data を buffer の要素型 (INDEX は u32、STORAGE は float) に写す。写す必要が
// あれば *conv に malloc した写しを置く (呼び出し側が free)。
static const void *buffer_data_convert(int32_t type, const void *data,
                                       int32_t count, BufData src,
                                       void **conv) {
  *conv = NULL;
  if (src == BUF_DATA_FLOATS && type == SGL_BUFFER_INDEX) {
    uint32_t *idx = (uint32_t *)malloc(sizeof(uint32_t) * (size_t)count);
    if (!idx)
      return NULL;
    for (int32_t i = 0; i < count; ++i)
      idx[i] = (uint32_t)((const float *)data)[i];
    *conv = idx;
  } else if (src == BUF_DATA_INTS && type == SGL_BUFFER_STORAGE) {
    float *f = (float *)malloc(sizeof(float) * (size_t)count);
    if (!f)
      return NULL;
    for (int32_t i = 0; i < count; ++i)
      f[i] = (float)((const int32_t *)data)[i];
    *conv = f;
  }
  return *conv ? *conv : data;
}

// 大きさが変わって作り直す buffer の確保量: 2 の冪 (最小 256 byte)。
static size_t buffer_capacity(size_t bytes) {
  size_t cap = 256;
  while (cap < bytes && cap <= SIZE_MAX / 2)
    cap *= 2;
  return cap < bytes ? bytes : cap;
}

// 今の buffer に bytes の data を書き込めるか (作り直さずに済むか)。同じ
// 大きさなら書ける。大きさが変わるときは、確保量に収まり、作り直しても
// 確保量が減らない (確保量の 1/4 以上か、buffer_capacity が今の確保量以上)
// なら書ける。ただしこの frame に既に使った buffer (draw / dispatch に
// 束縛した、upload した、version で再主張した) は、大きさが変われば
// 作り直す: WebGPU は書き込みがその frame のどの draw よりも先に届くので、
// 書き込むと先に記録した draw まで新しい内容を読む (作り直せば先の draw は
// 古い buffer を読む)。
static bool buffer_fits(const ResEntry *e, SglBufferType type, size_t bytes,
                        bool used_this_frame) {
  if (e->u.buf.h == 0 || e->u.buf.type != type)
    return false;
  if (bytes == e->u.buf.size_bytes)
    return true;
  size_t cap = e->u.buf.cap_bytes;
  return !used_this_frame && bytes <= cap &&
         (bytes >= cap / 4 || buffer_capacity(bytes) >= cap);
}

static LubStatus use_buffer_impl(App *app, LubStr key, int32_t type,
                                 const void *data, int32_t count, BufData src,
                                 const int32_t *version, LubHandle *out) {
  char kbuf[LUB_KEY_MAX];
  if (!key_arg(app, key, kbuf, "use_buffer"))
    return LUB_ERROR;
  if (type != SGL_BUFFER_INDEX && type != SGL_BUFFER_STORAGE)
    return lub_api_fail(app, "use_buffer: only INDEX/STORAGE are supported");
  if (count <= 0)
    return lub_api_fail(app, "use_buffer: empty data");
  if (!data && type != SGL_BUFFER_STORAGE)
    return lub_api_fail(app,
                        "use_buffer: VERTEX/INDEX buffers must be given data");

  bool declared = false;
  int64_t ver = effective_version(app, version, &declared);
  ResEntry *e = res_table_get_or_create(&app->res, kbuf, RES_BUFFER);
  if (!e)
    return lub_api_fail(
        app, "use_buffer: key '%s' already used as different kind", kbuf);
  int64_t frame = (int64_t)app->frame_index;
  bool used_this_frame = e->last_seen_frame == frame;
  res_table_touch(e, frame);

  if (!declared && buffer_hit(e, type, ver)) {
    *out = e->handle;
    return LUB_OK;
  }

  // 要素型の写しは upload するときだけ (INDEX は u32、STORAGE は float)
  void *conv = NULL;
  const void *bytes = data;
  if (data && !(bytes = buffer_data_convert(type, data, count, src, &conv)))
    return lub_api_fail(app, "use_buffer: out of memory");

  // 要素は float も u32 も 4 byte
  size_t new_bytes = (size_t)count * sizeof(float);
  if (bytes &&
      buffer_fits(e, (SglBufferType)type, new_bytes, used_this_frame)) {
    g_backend->update_buffer(e->u.buf.h, bytes, new_bytes);
  } else {
    // 初めての確保と data の無い確保 (use_buffer_empty) はちょうどの大きさ。
    // 大きさが変わって作り直すときは余裕を持たせ、次に少し変わったときは
    // 作り直さずに書き込む
    size_t cap =
        e->u.buf.h != 0 && bytes ? buffer_capacity(new_bytes) : new_bytes;
    if (e->u.buf.h != 0)
      g_backend->destroy_buffer(e->u.buf.h);
    e->u.buf.h = g_backend->make_buffer((SglBufferType)type, bytes,
                                        bytes ? new_bytes : 0, cap);
    e->u.buf.type = (SglBufferType)type;
    e->u.buf.cap_bytes = cap;
  }
  e->u.buf.size_bytes = new_bytes;
  free(conv);
  e->version = ver;
  *out = e->handle;
  return LUB_OK;
}

// data は float 列。INDEX は upload するときに uint32 に写す。data が NULL
// なら再主張だけ (data_count > 0 は Lua binding の問い合わせ): key が
// その version を持っていれば data を渡したときと同じ結果、無ければ何も
// せずに NOT_FOUND。
LubStatus lub_gfx_use_buffer(LubContext *ctx, LubStr key, int32_t type,
                             const float *data, int32_t data_count,
                             const int32_t *version, LubHandle *out) {
  App *app = lub_api_app(ctx);
  ResEntry *hit = NULL;
  if (!data && !(hit = buffer_cached(app, key, type, version)))
    return LUB_NOT_FOUND;
  digest_use_buffer(app, "use_buffer", key, type, data_count, version);
  if (hit)
    return reuse_entry(app, hit, out);
  if (data_count <= 0)
    return lub_api_fail(app, "use_buffer: empty data");
  return use_buffer_impl(app, key, type, data, data_count, BUF_DATA_FLOATS,
                         version, out);
}

// data は整数列。INDEX は int32 の bit 列をそのまま u32 として使い、STORAGE は
// upload するときに float に写す。data が NULL の意味は use_buffer と同じ。
LubStatus lub_gfx_use_buffer_ints(LubContext *ctx, LubStr key, int32_t type,
                                  const int32_t *data, int32_t data_count,
                                  const int32_t *version, LubHandle *out) {
  App *app = lub_api_app(ctx);
  ResEntry *hit = NULL;
  if (!data && !(hit = buffer_cached(app, key, type, version)))
    return LUB_NOT_FOUND;
  digest_use_buffer(app, "use_buffer_ints", key, type, data_count, version);
  if (hit)
    return reuse_entry(app, hit, out);
  if (data_count <= 0)
    return lub_api_fail(app, "use_buffer_ints: empty data");
  return use_buffer_impl(app, key, type, data, data_count, BUF_DATA_INTS,
                         version, out);
}

LubStatus lub_gfx_use_buffer_empty(LubContext *ctx, LubStr key, int32_t type,
                                   int32_t count, const int32_t *version,
                                   LubHandle *out) {
  App *app = lub_api_app(ctx);
  digest_use_buffer(app, "use_buffer_empty", key, type, count, version);
  if (count <= 0)
    return lub_api_fail(app, "use_buffer: count must be > 0");
  return use_buffer_impl(app, key, type, NULL, count, BUF_DATA_NONE, version,
                         out);
}

// ------------------------------------------------------------ transient

// この frame の transient buffer (TransientBuffer と ImGui の頂点)。data は
// 作るときに写し、以後は変わらないので、どの draw も作ったときの内容を読む。
// frame の終わりに全部を手放す。
//
// TransientBuffer の handle は resource table の handle (1 始まり) とも
// main_tex (-1) とも重ならない -2 以下の値で、frame 番号の下位 10 bit と
// frame 内の通し番号を持つ: -2 - (frame << 20 | index)。値は呼び出しの順だけで
// 決まる (Lua と .NET で同じになる)。前の frame の handle は frame の bit が
// 合わないので error にできる (1024 frame 前のものとは見分けない)。
#define TRANSIENT_INDEX_BITS 20
#define TRANSIENT_MAX (1 << TRANSIENT_INDEX_BITS)
#define TRANSIENT_FRAME_MASK 0x3ff

typedef struct TransientBuf {
  BufferSlice slice;
  SglBufferType type;
  bool owned; // runtime の fallback が作った buffer (frame の終わりに destroy)
} TransientBuf;

struct GfxTransients {
  TransientBuf *items;
  int32_t count, cap;
};

static bool transient_push(App *app, SglBufferType type, const void *data,
                           size_t bytes, int32_t *index) {
  if (!app->in_frame) {
    lub_api_fail(app, "transient_buffer: must be called inside a frame "
                      "(not in on_init / on_event)");
    return false;
  }
  if (!app->transients) {
    app->transients =
        (struct GfxTransients *)calloc(1, sizeof(*app->transients));
    if (!app->transients) {
      lub_api_fail(app, "transient_buffer: out of memory");
      return false;
    }
  }
  struct GfxTransients *ts = app->transients;
  if (ts->count >= TRANSIENT_MAX) {
    lub_api_fail(app, "transient_buffer: too many in one frame (max %d)",
                 TRANSIENT_MAX);
    return false;
  }
  if (ts->count == ts->cap) {
    int32_t cap = ts->cap ? ts->cap * 2 : 64;
    TransientBuf *grown =
        (TransientBuf *)realloc(ts->items, (size_t)cap * sizeof(TransientBuf));
    if (!grown) {
      lub_api_fail(app, "transient_buffer: out of memory");
      return false;
    }
    ts->items = grown;
    ts->cap = cap;
  }
  TransientBuf *t = &ts->items[ts->count];
  memset(t, 0, sizeof(*t));
  t->type = type;
  if (g_backend->transient_buffer) {
    if (!g_backend->transient_buffer(type, data, bytes, &t->slice)) {
      lub_api_fail(app,
                   "transient_buffer: backend allocation failed (%zu "
                   "bytes)",
                   bytes);
      return false;
    }
  } else {
    // どの backend でも動く fallback: 呼び出しごとに buffer を作り、frame の
    // 終わり (end_frame の後) に destroy する。GPU での破棄はどの backend も
    // 使い終わるまで待つ。D3D12 / Vulkan は次の fence の signal まで zombie
    // list に置き、sdlgpu の SDL_ReleaseGPUBuffer は参照中の command buffer
    // の完了を待ち、WebGPU は release しても encode 済みの command が参照を
    // 持ち続ける。呼び出しごとに GPU の確保が 1 つ要るので、1 frame に数千を
    // 作る使い方は backend の transient_buffer が要る (backend.h)。
    t->slice.buf = g_backend->make_buffer(type, data, bytes, bytes);
    if (!t->slice.buf) {
      lub_api_fail(app, "transient_buffer: make_buffer failed (%zu bytes)",
                   bytes);
      return false;
    }
    t->slice.offset = 0;
    t->slice.size = bytes;
    t->owned = true;
  }
  *index = ts->count++;
  return true;
}

bool api_gfx_transient(App *app, SglBufferType type, const void *data,
                       size_t bytes, BufferSlice *out) {
  int32_t index = 0;
  if (!transient_push(app, type, data, bytes, &index))
    return false;
  *out = app->transients->items[index].slice;
  return true;
}

void api_gfx_transients_frame_end(App *app) {
  struct GfxTransients *ts = app->transients;
  if (!ts)
    return;
  for (int32_t i = 0; i < ts->count; ++i)
    if (ts->items[i].owned)
      g_backend->destroy_buffer(ts->items[i].slice.buf);
  ts->count = 0;
}

static void transients_shutdown(App *app) {
  if (!app->transients)
    return;
  api_gfx_transients_frame_end(app);
  free(app->transients->items);
  free(app->transients);
  app->transients = NULL;
}

static LubHandle transient_handle(App *app, int32_t index) {
  int32_t frame = (int32_t)(app->frame_index & TRANSIENT_FRAME_MASK);
  return (LubHandle)(-2 - ((frame << TRANSIENT_INDEX_BITS) | index));
}

// handle (-2 以下) の transient。前の frame のものなら *stale を立てて NULL。
static const TransientBuf *transient_get(App *app, LubHandle h, bool *stale) {
  *stale = false;
  if (h > -2)
    return NULL;
  int64_t v = -2 - (int64_t)h;
  int64_t frame = v >> TRANSIENT_INDEX_BITS;
  int64_t index = v & (TRANSIENT_MAX - 1);
  if (frame > TRANSIENT_FRAME_MASK)
    return NULL;
  struct GfxTransients *ts = app->transients;
  if (frame != (int64_t)(app->frame_index & TRANSIENT_FRAME_MASK) || !ts ||
      index >= ts->count) {
    *stale = true;
    return NULL;
  }
  return &ts->items[index];
}

static LubStatus transient_impl(App *app, const char *fn, int32_t type,
                                const void *data, int32_t count, BufData src,
                                LubHandle *out) {
  if (!app->in_frame)
    return lub_api_fail(app,
                        "%s: must be called inside a frame (not in on_init / "
                        "on_event)",
                        fn);
  if (type != SGL_BUFFER_INDEX && type != SGL_BUFFER_STORAGE)
    return lub_api_fail(app, "%s: only INDEX/STORAGE are supported", fn);
  if (count <= 0 || !data)
    return lub_api_fail(app, "%s: empty data", fn);
  void *conv = NULL;
  const void *bytes = buffer_data_convert(type, data, count, src, &conv);
  if (!bytes)
    return lub_api_fail(app, "%s: out of memory", fn);
  int32_t index = 0;
  bool ok = transient_push(app, (SglBufferType)type, bytes,
                           (size_t)count * sizeof(float), &index);
  free(conv);
  if (!ok)
    return LUB_ERROR;
  *out = transient_handle(app, index);
  return LUB_OK;
}

static void digest_transient(App *app, const char *tag, int32_t type,
                             int32_t count) {
  if (!app->digest.enabled)
    return;
  digest_tag(app, tag);
  digest_i32(app, type);
  digest_i32(app, count);
}

// data は float 列 (INDEX は u32 に写す)。呼んだ frame の間だけ有効。
LubStatus lub_gfx_transient_buffer(LubContext *ctx, int32_t type,
                                   const float *data, int32_t data_count,
                                   LubHandle *out) {
  App *app = lub_api_app(ctx);
  digest_transient(app, "transient_buffer", type, data_count);
  return transient_impl(app, "transient_buffer", type, data, data_count,
                        BUF_DATA_FLOATS, out);
}

// data は整数列 (INDEX はそのまま u32、STORAGE は float に写す)。
LubStatus lub_gfx_transient_buffer_ints(LubContext *ctx, int32_t type,
                                        const int32_t *data, int32_t data_count,
                                        LubHandle *out) {
  App *app = lub_api_app(ctx);
  digest_transient(app, "transient_buffer_ints", type, data_count);
  return transient_impl(app, "transient_buffer_ints", type, data, data_count,
                        BUF_DATA_INTS, out);
}

// use_texture の本体。pixels (byte 列) か ints (byte 値の整数列、upload する
// ときに 0..255 に丸めて写す) は呼び出しの間だけ借用。
typedef struct TextureDesc {
  int32_t w, h;
  int32_t format;
  const uint8_t *pixels;
  const int32_t *ints;
  int32_t pixels_len;
  int32_t filter, wrap;
  bool target, storage;
} TextureDesc;

// 引数から決まる texture の形と sampler。entry が同じ形を持っていれば hit。
typedef struct TextureShape {
  SglPixelFormat fmt;
  int32_t w, h;
  SglFilter filter;
  SglWrap wrap;
  bool target, storage;
} TextureShape;

// use_texture の引数の検査と形の決定。has_data は画素を渡す呼び出しか
// (問い合わせでは、まだ読んでいない画素も含む)。通らなければ理由を err に
// 書いて false。
static bool texture_shape(const TextureDesc *d, bool has_data, TextureShape *s,
                          char *err, size_t errn) {
  if (!is_known_format(d->format)) {
    snprintf(err, errn,
             "format not supported "
             "(RGBA8/R8/RG8/R16F/RG16F/R32F/RGBA16F/RGBA32F/"
             "depth target formats only)");
    return false;
  }
  s->fmt = (SglPixelFormat)d->format;
  s->filter = SGL_FILTER_LINEAR;
  s->wrap = SGL_WRAP_REPEAT;
  s->target = d->target;
  s->storage = d->storage;
  s->w = d->w;
  s->h = d->h;
  bool filter_explicit = false;
  if (d->filter != 0) {
    if (d->filter != SGL_FILTER_LINEAR && d->filter != SGL_FILTER_NEAREST) {
      snprintf(err, errn, "opts.filter must be LINEAR or NEAREST");
      return false;
    }
    s->filter = (SglFilter)d->filter;
    filter_explicit = true;
  }
  if (d->wrap != 0) {
    if (d->wrap != SGL_WRAP_REPEAT && d->wrap != SGL_WRAP_CLAMP) {
      snprintf(err, errn, "opts.wrap must be REPEAT or CLAMP");
      return false;
    }
    s->wrap = (SglWrap)d->wrap;
  }
  if (d->target && has_data) {
    snprintf(err, errn, "render target cannot be initialized with data");
    return false;
  }
  if (d->storage && has_data) {
    snprintf(err, errn, "storage texture cannot be initialized with data");
    return false;
  }
  bool depth = is_depth_format(s->fmt);
  if (depth && !d->target) {
    snprintf(err, errn, "depth formats are only supported with {target=true}");
    return false;
  }
  if (depth && d->storage) {
    snprintf(err, errn, "depth formats cannot use storage=true");
    return false;
  }
  if (depth) {
    // WebGPU can only sample depth as unfilterable-float; a filtering sampler
    // is a validation error there (and LINEAR on D32 is optional in Vulkan).
    if (filter_explicit && s->filter == SGL_FILTER_LINEAR) {
      snprintf(err, errn, "depth textures must use NEAREST filter");
      return false;
    }
    s->filter = SGL_FILTER_NEAREST;
  }
  if (d->w <= 0 || d->h <= 0) {
    snprintf(err, errn, "invalid size %dx%d", d->w, d->h);
    return false;
  }
  return true;
}

// key が同じ形の texture をこの version で持っている (画素を読まずに返せる)。
static bool texture_hit(const ResEntry *e, const TextureShape *s, int64_t ver) {
  return e->u.tex.h != 0 && e->version == ver && e->u.tex.w == s->w &&
         e->u.tex.h_ == s->h && e->u.tex.fmt == s->fmt &&
         e->u.tex.filter == s->filter && e->u.tex.wrap == s->wrap &&
         e->u.tex.is_target == s->target && e->u.tex.storage == s->storage;
}

// 画素を渡す呼び出しの問い合わせ (まだ画素を読んでいない)。
static ResEntry *texture_cached(App *app, LubStr key, const TextureDesc *d,
                                const int32_t *version) {
  ResEntry *e = cached_entry(app, key, RES_TEXTURE, version);
  TextureShape s;
  char err[160];
  if (!e || !texture_shape(d, true, &s, err, sizeof(err)))
    return NULL;
  return texture_hit(e, &s, e->version) ? e : NULL;
}

static LubStatus use_texture_impl(App *app, LubStr key, const TextureDesc *d,
                                  const int32_t *version, LubHandle *out) {
  char kbuf[LUB_KEY_MAX];
  if (!key_arg(app, key, kbuf, "use_texture"))
    return LUB_ERROR;
  bool has_data = d->pixels != NULL || d->ints != NULL;
  TextureShape s;
  char err[160];
  if (!texture_shape(d, has_data, &s, err, sizeof(err)))
    return lub_api_fail(app, "use_texture: %s", err);

  bool declared = false;
  int64_t ver = effective_version(app, version, &declared);
  ResEntry *e = res_table_get_or_create(&app->res, kbuf, RES_TEXTURE);
  if (!e)
    return lub_api_fail(
        app, "use_texture: key '%s' already used as different kind", kbuf);
  res_table_touch(e, (int64_t)app->frame_index);

  if (!declared && texture_hit(e, &s, ver)) {
    *out = e->handle;
    return LUB_OK;
  }

  size_t new_bytes = 0;
  if (has_data) {
    int bpp = bytes_per_pixel(s.fmt);
    if (bpp == 0)
      return lub_api_fail(app, "use_texture: this texture format cannot be "
                               "initialized with byte data");
    size_t expected = (size_t)d->w * (size_t)d->h * (size_t)bpp;
    if ((size_t)d->pixels_len != expected)
      return lub_api_fail(
          app, "use_texture: byte size mismatch: got %d, expected %zu",
          d->pixels_len, expected);
    new_bytes = expected;
  }
  // 整数列は upload するときだけ byte に丸めて写す
  const uint8_t *pixels = d->pixels;
  uint8_t *clamped = NULL;
  if (d->ints && new_bytes > 0) {
    clamped = (uint8_t *)malloc(new_bytes);
    if (!clamped)
      return lub_api_fail(app, "use_texture: out of memory");
    for (size_t i = 0; i < new_bytes; ++i) {
      int32_t v = d->ints[i];
      clamped[i] = (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
    pixels = clamped;
  }

  bool sampler_changed = (e->u.tex.h != 0) && (e->u.tex.filter != s.filter ||
                                               e->u.tex.wrap != s.wrap);
  bool target_changed = (e->u.tex.h != 0) && (e->u.tex.is_target != d->target);
  bool storage_changed = (e->u.tex.h != 0) && (e->u.tex.storage != d->storage);
  bool same_shape = (e->u.tex.h != 0) && (e->u.tex.w == d->w) &&
                    (e->u.tex.h_ == d->h) && (e->u.tex.fmt == s.fmt);
  if (same_shape && !sampler_changed && !target_changed && !storage_changed &&
      has_data && new_bytes > 0) {
    g_backend->update_image(e->u.tex.h, pixels, new_bytes);
  } else {
    if (e->u.tex.h != 0)
      g_backend->destroy_image(e->u.tex.h);
    ImageDesc id = {
        .fmt = s.fmt,
        .w = d->w,
        .h = d->h,
        .data = has_data ? pixels : NULL,
        .data_bytes = new_bytes,
        .filter = s.filter,
        .wrap = s.wrap,
        .render_target = d->target,
        .storage = d->storage,
    };
    e->u.tex.h = g_backend->make_image(&id);
    e->u.tex.w = d->w;
    e->u.tex.h_ = d->h;
    e->u.tex.fmt = s.fmt;
  }
  free(clamped);
  e->u.tex.filter = s.filter;
  e->u.tex.wrap = s.wrap;
  e->u.tex.is_target = d->target;
  e->u.tex.storage = d->storage;
  e->version = ver;
  *out = e->handle;
  return LUB_OK;
}

static void texture_desc_init(TextureDesc *d, int32_t w, int32_t h, int32_t fmt,
                              const LubTextureOpts *opts) {
  memset(d, 0, sizeof(*d));
  d->w = w;
  d->h = h;
  d->format = fmt;
  if (opts) {
    d->filter = opts->has_filter ? opts->filter : 0;
    d->wrap = opts->has_wrap ? opts->wrap : 0;
    d->target = opts->has_target && opts->target;
    d->storage = opts->has_storage && opts->storage;
  }
}

static void digest_use_texture(App *app, LubStr key, int32_t w, int32_t h,
                               int32_t fmt, int32_t count,
                               const int32_t *version) {
  if (!app->digest.enabled)
    return;
  digest_tag(app, "use_texture");
  digest_str(app, key);
  digest_i32(app, w);
  digest_i32(app, h);
  digest_i32(app, fmt);
  digest_i32(app, count);
  digest_i32(app, version ? *version : 0);
}

LubStatus lub_gfx_use_texture_bytes(LubContext *ctx, LubStr key, int32_t w,
                                    int32_t h, int32_t fmt, const uint8_t *px,
                                    int32_t px_len, const int32_t *version,
                                    const LubTextureOpts *opts,
                                    LubHandle *out) {
  App *app = lub_api_app(ctx);
  digest_use_texture(app, key, w, h, fmt, px_len, version);
  TextureDesc d;
  texture_desc_init(&d, w, h, fmt, opts);
  d.pixels = px;
  d.pixels_len = px ? px_len : 0;
  return use_texture_impl(app, key, &d, version, out);
}

// px は byte 値 (0..255) の列。px が NULL で px_count > 0 は Lua binding が
// px を読む前に試す問い合わせ: key がその version を同じ形で持っていれば px を
// 渡したときと同じ結果、無ければ何もせず NOT_FOUND。px が NULL で px_count が
// 0 は画素を持たない texture (target / storage 用)。
LubStatus lub_gfx_use_texture(LubContext *ctx, LubStr key, int32_t w, int32_t h,
                              int32_t fmt, const int32_t *px, int32_t px_count,
                              const int32_t *version,
                              const LubTextureOpts *opts, LubHandle *out) {
  App *app = lub_api_app(ctx);
  TextureDesc d;
  texture_desc_init(&d, w, h, fmt, opts);
  d.ints = px;
  d.pixels_len = px ? px_count : 0;
  ResEntry *hit = NULL;
  if (!px && px_count > 0 && !(hit = texture_cached(app, key, &d, version)))
    return LUB_NOT_FOUND;
  // 整数列の経路は bytes の経路を通していた頃と同じく 2 回記録する (digest の
  // 形を変えない)
  digest_use_texture(app, key, w, h, fmt, px_count, version);
  digest_use_texture(app, key, w, h, fmt, px_count, version);
  if (hit)
    return reuse_entry(app, hit, out);
  return use_texture_impl(app, key, &d, version, out);
}

// vs/fs (graphics) か cs (compute) の compile と入れ替え。失敗時、既存の
// shader があればそれを保って log だけ、無ければ LUB_ERROR。
static LubStatus use_shader_impl(App *app, const char *fn, LubStr key,
                                 LubStr vs, LubStr fs, LubStr cs,
                                 const int32_t *version, LubHandle *out) {
  char kbuf[LUB_KEY_MAX];
  if (!key_arg(app, key, kbuf, fn))
    return LUB_ERROR;
  bool declared = false;
  int64_t ver = effective_version(app, version, &declared);
  ResEntry *e = res_table_get_or_create(&app->res, kbuf, RES_SHADER);
  if (!e)
    return lub_api_fail(app, "%s: key '%s' already used as different kind", fn,
                        kbuf);
  res_table_touch(e, (int64_t)app->frame_index);
  if (!declared && e->version == ver && e->u.sh.h != 0) {
    *out = e->handle;
    return LUB_OK;
  }

  // 呼び出しの間だけ借用する source を NUL 終端に写す。
  char *vs_s = NULL, *fs_s = NULL, *cs_s = NULL;
  bool compute = cs.ptr != NULL;
  if (compute) {
    cs_s = (char *)malloc((size_t)cs.len + 1);
    if (!cs_s)
      return lub_api_fail(app, "%s: out of memory", fn);
    memcpy(cs_s, cs.ptr, (size_t)cs.len);
    cs_s[cs.len] = '\0';
  } else {
    vs_s = (char *)malloc((size_t)vs.len + 1);
    fs_s = (char *)malloc((size_t)fs.len + 1);
    if (!vs_s || !fs_s) {
      free(vs_s);
      free(fs_s);
      return lub_api_fail(app, "%s: out of memory", fn);
    }
    memcpy(vs_s, vs.ptr, (size_t)vs.len);
    vs_s[vs.len] = '\0';
    memcpy(fs_s, fs.ptr, (size_t)fs.len);
    fs_s[fs.len] = '\0';
  }

  char err[1024];
  ShaderBlob vsb = {0}, fsb = {0}, csb = {0};
  ShaderReflection new_refl;
  ShaderTargetBackend tgt = shader_target_for_backend();
  bool ok = compute ? shader_compile_compute(cs_s, tgt, &csb, &new_refl, err,
                                             sizeof(err))
                    : shader_compile(vs_s, fs_s, tgt, &vsb, &fsb, &new_refl,
                                     err, sizeof(err));
  free(vs_s);
  free(fs_s);
  free(cs_s);
  if (!ok) {
    shader_blob_free(&vsb);
    shader_blob_free(&fsb);
    shader_blob_free(&csb);
    if (e->u.sh.h == 0)
      return lub_api_fail(app, "%s compile error: %s",
                          compute ? "compute shader" : "shader", err);
    SDL_Log("%s: recompile failed for key '%s': %s (keeping old)", fn, kbuf,
            err);
    *out = e->handle;
    return LUB_OK;
  }
  ShaderDesc sd = {
      .vs_spirv = vsb.spirv,
      .vs_bytes = vsb.bytes,
      .fs_spirv = fsb.spirv,
      .fs_bytes = fsb.bytes,
      .cs_spirv = csb.spirv,
      .cs_bytes = csb.bytes,
      .refl = &new_refl,
  };
  BackendShader new_h = g_backend->make_shader(&sd);
  shader_blob_free(&vsb);
  shader_blob_free(&fsb);
  shader_blob_free(&csb);
  if (!new_h) {
    if (e->u.sh.h == 0)
      return lub_api_fail(app, "%s: make_shader failed for key '%s'", fn, kbuf);
    SDL_Log("%s: make_shader failed for key '%s' (keeping old)", fn, kbuf);
    *out = e->handle;
    return LUB_OK;
  }
  BackendShader old_h = e->u.sh.h;
  if (old_h) {
    pipeline_cache_invalidate_shader(&app->pip_cache, (uintptr_t)old_h);
    g_backend->destroy_shader(old_h);
  }
  e->u.sh.h = new_h;
  e->u.sh.refl = new_refl;
  e->version = ver;
  *out = e->handle;
  return LUB_OK;
}

LubStatus lub_gfx_use_shader(LubContext *ctx, LubStr key, LubStr vs, LubStr fs,
                             const int32_t *version, LubHandle *out) {
  LubStr none = {NULL, 0};
  if (lub_api_app(ctx)->digest.enabled) {
    digest_tag(lub_api_app(ctx), "use_shader");
    digest_str(lub_api_app(ctx), key);
    digest_i32(lub_api_app(ctx), version ? *version : 0);
  }
  return use_shader_impl(lub_api_app(ctx), "use_shader", key, vs, fs, none,
                         version, out);
}

LubStatus lub_gfx_use_shader_compute(LubContext *ctx, LubStr key, LubStr cs,
                                     const int32_t *version, LubHandle *out) {
  LubStr none = {NULL, 0};
  if (lub_api_app(ctx)->digest.enabled) {
    digest_tag(lub_api_app(ctx), "use_shader_compute");
    digest_str(lub_api_app(ctx), key);
    digest_i32(lub_api_app(ctx), version ? *version : 0);
  }
  if (!cs.ptr)
    return lub_api_fail(lub_api_app(ctx),
                        "use_shader_compute: source required");
  return use_shader_impl(lub_api_app(ctx), "use_shader_compute", key, none,
                         none, cs, version, out);
}

// ----------------------------------------------------------------- pass

static ResEntry *color_target_entry(App *app, LubHandle h, int i) {
  ResEntry *te = entry_from_handle(app, h, RES_TEXTURE, "begin_pass", "target");
  if (!te)
    return NULL;
  if (!te->u.tex.is_target) {
    lub_api_fail(app,
                 "begin_pass: target texture '%s' was not declared with "
                 "{target=true}",
                 te->key);
    return NULL;
  }
  if (is_depth_format(te->u.tex.fmt)) {
    lub_api_fail(app,
                 "begin_pass: targets[%d] must be a color texture; use "
                 "depth_target for depth textures",
                 i + 1);
    return NULL;
  }
  return te;
}

// pass の宣言を平らにしたもの。target / targets / clear_color / clear_colors
// の組み合わせは opts の読み替えで吸収する。
typedef struct PassDesc {
  int32_t n_targets;
  LubHandle targets[LUB_GFX_MAX_COLOR_TARGETS];
  float clear_color[LUB_GFX_MAX_COLOR_TARGETS][4];
  LubHandle depth_target;
  float clear_depth;
  int32_t load;
} PassDesc;

static LubStatus pass_desc_from_opts(App *app, const LubPassOpts *o,
                                     PassDesc *d) {
  memset(d, 0, sizeof(*d));
  d->clear_depth = 1.0f;
  if (o->targets) {
    if (o->target != 0)
      return lub_api_fail(app, "begin_pass: target and targets are exclusive");
    if (o->targets_count > LUB_GFX_MAX_COLOR_TARGETS)
      return lub_api_fail(app, "begin_pass: too many targets (%d > %d)",
                          o->targets_count, LUB_GFX_MAX_COLOR_TARGETS);
    d->n_targets = o->targets_count;
    for (int32_t i = 0; i < o->targets_count; ++i)
      d->targets[i] = o->targets[i];
  } else if (o->target != 0) {
    d->n_targets = 1;
    d->targets[0] = o->target;
  }
  d->depth_target = o->depth_target;
  if (o->has_clear_depth)
    d->clear_depth = o->clear_depth;
  if (o->has_load)
    d->load = o->load;
  // 既定の clear は黒不透明
  for (int i = 0; i < LUB_GFX_MAX_COLOR_TARGETS; ++i) {
    d->clear_color[i][0] = 0;
    d->clear_color[i][1] = 0;
    d->clear_color[i][2] = 0;
    d->clear_color[i][3] = 1;
  }
  if (o->has_clear_color)
    for (int i = 0; i < LUB_GFX_MAX_COLOR_TARGETS; ++i)
      memcpy(d->clear_color[i], o->clear_color, sizeof(o->clear_color));
  if (o->clear_colors)
    for (int32_t i = 0;
         i < o->clear_colors_count && i < LUB_GFX_MAX_COLOR_TARGETS; ++i)
      memcpy(d->clear_color[i], o->clear_colors[i], sizeof(d->clear_color[i]));
  return LUB_OK;
}

LubStatus lub_gfx_begin_pass(LubContext *ctx, const LubPassOpts *opts) {
  App *app = lub_api_app(ctx);
  if (app->digest.enabled) {
    digest_tag(app, "begin_pass");
    digest_i32(app, opts ? opts->target : 0);
    digest_i32(app, opts ? opts->targets_count : 0);
    digest_i32(app, opts ? opts->depth_target : 0);
  }
  if (!opts)
    return lub_api_fail(app, "begin_pass: opts required");
  if (pass_state_in_pass(&app->pass))
    return lub_api_fail(app, "begin_pass: already inside a pass");
  PassDesc desc;
  if (pass_desc_from_opts(app, opts, &desc) != LUB_OK)
    return LUB_ERROR;
  const PassDesc *d = &desc;
  SglLoadAction load = SGL_LOAD_CLEAR;
  if (d->load != 0) {
    if (d->load != SGL_LOAD_CLEAR && d->load != SGL_LOAD_LOAD)
      return lub_api_fail(app, "begin_pass: load must be CLEAR or LOAD");
    load = (SglLoadAction)d->load;
  }
  float clear_depth = d->clear_depth;

  uintptr_t depth_image = 0;
  SglPixelFormat depth_fmt = SGL_PF_DEPTH24_STENCIL8;
  int depth_w = 0, depth_h = 0;
  if (d->depth_target != 0) {
    ResEntry *de = entry_from_handle(app, d->depth_target, RES_TEXTURE,
                                     "begin_pass", "depth_target");
    if (!de)
      return LUB_ERROR;
    if (!de->u.tex.is_target || !is_depth_format(de->u.tex.fmt))
      return lub_api_fail(app,
                          "begin_pass: depth_target '%s' must be a depth "
                          "texture declared with {target=true}",
                          de->key);
    depth_image = de->u.tex.h;
    depth_fmt = de->u.tex.fmt;
    depth_w = de->u.tex.w;
    depth_h = de->u.tex.h_;
  }

  if (d->n_targets < 0 || d->n_targets > SGL_MAX_COLOR_TARGETS)
    return lub_api_fail(app, "begin_pass: too many targets (%d > %d)",
                        d->n_targets, SGL_MAX_COLOR_TARGETS);

  // swapchain pass
  if (d->n_targets == 1 && d->targets[0] == LUB_GFX_MAIN_TEX) {
    if (depth_image)
      return lub_api_fail(app, "begin_pass: main_tex uses the swapchain depth "
                               "buffer; depth_target is only for offscreen "
                               "passes");
    const float *c = d->clear_color[0];
    pass_state_begin(&app->pass, 0, SGL_PF_RGBA8, 0, 0, c[0], c[1], c[2], c[3],
                     load);
    return LUB_OK;
  }

  if (d->n_targets == 0 && !depth_image)
    return lub_api_fail(app, "begin_pass: target must be main_tex, a color "
                             "TextureRef, or omitted for a depth-only pass");

  uintptr_t targets[SGL_MAX_COLOR_TARGETS] = {0};
  SglPixelFormat fmts[SGL_MAX_COLOR_TARGETS] = {0};
  float clears[SGL_MAX_COLOR_TARGETS][4];
  int tw = depth_w, th = depth_h;
  for (int i = 0; i < d->n_targets; ++i) {
    if (d->targets[i] == LUB_GFX_MAIN_TEX)
      return lub_api_fail(
          app, "begin_pass: main_tex cannot be combined with other targets");
    ResEntry *te = color_target_entry(app, d->targets[i], i);
    if (!te)
      return LUB_ERROR;
    if (i == 0) {
      tw = te->u.tex.w;
      th = te->u.tex.h_;
    } else if (te->u.tex.w != tw || te->u.tex.h_ != th) {
      return lub_api_fail(app,
                          "begin_pass: targets must share the same size (got "
                          "%dx%d at [%d], expected %dx%d)",
                          te->u.tex.w, te->u.tex.h_, i + 1, tw, th);
    }
    targets[i] = te->u.tex.h;
    fmts[i] = te->u.tex.fmt;
    memcpy(clears[i], d->clear_color[i], sizeof(clears[i]));
  }
  if (d->n_targets > 0 && depth_image && (depth_w != tw || depth_h != th))
    return lub_api_fail(
        app,
        "begin_pass: depth_target size %dx%d must match color targets %dx%d",
        depth_w, depth_h, tw, th);
  if (d->n_targets == 0) {
    clears[0][0] = 0;
    clears[0][1] = 0;
    clears[0][2] = 0;
    clears[0][3] = 1;
  }
  pass_state_begin_ex(&app->pass, d->n_targets, targets, fmts, tw, th,
                      (const float (*)[4])clears, depth_image, depth_fmt,
                      clear_depth, load);
  return LUB_OK;
}

LubStatus lub_gfx_end_pass(LubContext *ctx) {
  if (lub_api_app(ctx)->digest.enabled)
    digest_tag(lub_api_app(ctx), "end_pass");
  App *app = lub_api_app(ctx);
  if (!pass_state_in_pass(&app->pass))
    return lub_api_fail(app, "end_pass: no pass is active");
  pass_state_end(&app->pass);
  return LUB_OK;
}

// ----------------------------------------------------------------- draw

enum { UB_MAX_FLOATS = 512 };

// reflection の uniform block を、名前つきの値の列から詰める。無い member は
// 0 のまま。
// bindings を種類で分ける (handle の種類は resource table で判定)。
// buffer は key で宣言したもの (範囲は先頭から論理的な大きさまで) と
// transient を同じ形 (範囲と種別) にする。
typedef struct BoundBuffer {
  const LubBinding *b;
  BufferSlice slice;
  SglBufferType type;
  bool transient;
} BoundBuffer;

typedef struct Bindings {
  BoundBuffer buffers[16];
  int32_t n_buffers;
  const LubBinding *textures[16];
  ResEntry *texture_entries[16];
  int32_t n_textures;
  const LubBinding *uniforms[64];
  int32_t n_uniforms;
} Bindings;

static LubStatus split_bindings(App *app, const char *fn,
                                const LubBinding *bindings, int32_t n,
                                Bindings *out) {
  memset(out, 0, sizeof(*out));
  for (int32_t i = 0; i < n; ++i) {
    const LubBinding *b = &bindings[i];
    if (b->handle == 0) {
      if (out->n_uniforms >= 64)
        return lub_api_fail(app, "%s: too many uniforms (max 64)", fn);
      out->uniforms[out->n_uniforms++] = b;
      continue;
    }
    if (b->handle < -1) {
      // TransientBuffer (resource table の外。使用の記録は要らない)
      bool stale = false;
      const TransientBuf *t = transient_get(app, b->handle, &stale);
      if (!t)
        return lub_api_fail(
            app,
            stale ? "%s: binding '%.*s' is a transient buffer from an earlier "
                    "frame (TransientBuffer is valid until the end of the "
                    "frame that created it)"
                  : "%s: binding '%.*s' is invalid",
            fn, b->name.len, b->name.ptr ? b->name.ptr : "");
      if (out->n_buffers >= 16)
        return lub_api_fail(app, "%s: too many buffers (max 16)", fn);
      BoundBuffer *bb = &out->buffers[out->n_buffers++];
      bb->b = b;
      bb->slice = t->slice;
      bb->type = t->type;
      bb->transient = true;
      continue;
    }
    ResEntry *e = res_table_get_by_handle(&app->res, b->handle);
    if (!e) {
      if (handle_swept(app, b->handle))
        return lub_api_fail(app, "%s: binding '%.*s' " SWEPT_HINT, fn,
                            b->name.len, b->name.ptr ? b->name.ptr : "",
                            app->resource_sweep_after_frames);
      return lub_api_fail(app, "%s: binding '%.*s' is invalid", fn, b->name.len,
                          b->name.ptr ? b->name.ptr : "");
    }
    // draw / dispatch に束縛した resource は使われている (sweep しない)
    res_table_touch(e, (int64_t)app->frame_index);
    if (e->kind == RES_BUFFER) {
      if (out->n_buffers >= 16)
        return lub_api_fail(app, "%s: too many buffers (max 16)", fn);
      BoundBuffer *bb = &out->buffers[out->n_buffers++];
      bb->b = b;
      bb->slice.buf = e->u.buf.h;
      bb->slice.offset = 0;
      bb->slice.size = e->u.buf.size_bytes;
      bb->type = e->u.buf.type;
      bb->transient = false;
    } else if (e->kind == RES_TEXTURE) {
      if (out->n_textures >= 16)
        return lub_api_fail(app, "%s: too many textures (max 16)", fn);
      out->texture_entries[out->n_textures] = e;
      out->textures[out->n_textures++] = b;
    } else {
      return lub_api_fail(app, "%s: binding '%.*s' must be a buffer or texture",
                          fn, b->name.len, b->name.ptr ? b->name.ptr : "");
    }
  }
  return LUB_OK;
}

static void pack_uniform_block(const ShaderUniformBlock *ub,
                               const LubBinding *const *uniforms, int32_t n,
                               float *dst) {
  memset(dst, 0, (size_t)UB_MAX_FLOATS * sizeof(float));
  for (int m = 0; m < ub->member_count; ++m) {
    const ShaderUniformMember *mem = &ub->members[m];
    for (int32_t i = 0; i < n; ++i) {
      if (!lub_str_eq(uniforms[i]->name, mem->name))
        continue;
      int copy = uniforms[i]->count < mem->comp_count ? uniforms[i]->count
                                                      : mem->comp_count;
      if (mem->offset_floats + copy > UB_MAX_FLOATS)
        copy = UB_MAX_FLOATS - mem->offset_floats;
      for (int j = 0; j < copy; ++j)
        dst[mem->offset_floats + j] = uniforms[i]->values[j];
      break;
    }
  }
}

static bool refl_texture_index(const ShaderReflection *refl, LubStr name,
                               int *out_index) {
  for (int i = 0; i < refl->tex_count; ++i) {
    if (lub_str_eq(name, refl->texs[i].name)) {
      *out_index = i;
      return true;
    }
  }
  return false;
}

static bool refl_storage_buf_index(const ShaderReflection *refl, LubStr name,
                                   int *out_index) {
  for (int i = 0; i < refl->storage_buf_count; ++i) {
    if (lub_str_eq(name, refl->storage_bufs[i].name)) {
      *out_index = i;
      return true;
    }
  }
  return false;
}

static bool refl_has_storage_texture(const ShaderReflection *refl,
                                     LubStr name) {
  for (int i = 0; i < refl->storage_tex_count; ++i)
    if (lub_str_eq(name, refl->storage_texs[i].name))
      return true;
  return false;
}

// bindings の並びは実行形で違う (Lua の table は順不同) ので、項目ごとの
// hash の和で順序に依らない値にする。
static void digest_bindings(App *app, const LubBinding *bindings,
                            int32_t bindings_count) {
  digest_i32(app, bindings_count);
  uint64_t acc = 0;
  for (int32_t i = 0; i < bindings_count; ++i) {
    uint64_t h = 1469598103934665603ULL;
    for (int32_t j = 0; j < bindings[i].name.len; ++j) {
      h ^= (uint8_t)bindings[i].name.ptr[j];
      h *= 1099511628211ULL;
    }
    h ^= (uint64_t)(uint32_t)bindings[i].handle;
    h *= 1099511628211ULL;
    h ^= (uint64_t)(uint32_t)bindings[i].count;
    h *= 1099511628211ULL;
    acc += h;
  }
  digest_i32(app, (int32_t)(acc & 0xffffffffu));
  digest_i32(app, (int32_t)(acc >> 32));
}

LubStatus lub_gfx_draw(LubContext *ctx, int32_t count,
                       const LubBinding *bindings, int32_t bindings_count,
                       const LubDrawOpts *d) {
  App *app = lub_api_app(ctx);
  if (app->digest.enabled) {
    digest_tag(app, "draw");
    digest_i32(app, count);
    digest_i32(app, d ? d->shader : 0);
    digest_bindings(app, bindings, bindings_count);
  }
  if (!d)
    return lub_api_fail(app, "draw: opts required");
  if (!pass_state_in_pass(&app->pass))
    return lub_api_fail(app, "draw: must be called inside begin_pass/end_pass");
  ResEntry *sh =
      entry_from_handle(app, d->shader, RES_SHADER, "draw", "shader");
  if (!sh)
    return LUB_ERROR;
  if (sh->u.sh.refl.is_compute)
    return lub_api_fail(app, "draw: shader '%s' is a compute shader", sh->key);
  Bindings bs;
  if (split_bindings(app, "draw", bindings, bindings_count, &bs) != LUB_OK)
    return LUB_ERROR;
  int instance_count = d->has_instance_count ? d->instance_count : 1;
  int blend = d->has_blend ? d->blend : SGL_BLEND_NONE;
  int cull = d->has_cull ? d->cull : SGL_CULL_BACK;
  int prim = d->has_primitive ? d->primitive : SGL_PRIM_TRIANGLES;
  bool depth_test = d->has_depth ? d->depth : true;
  bool depth_write = d->has_depth_write ? d->depth_write : depth_test;
  if (blend < SGL_BLEND_NONE || blend > SGL_BLEND_MULTIPLY)
    return lub_api_fail(app, "draw: invalid blend %d", blend);
  if (cull < SGL_CULL_NONE || cull > SGL_CULL_FRONT)
    return lub_api_fail(app, "draw: invalid cull %d", cull);
  if (prim < SGL_PRIM_TRIANGLES || prim > SGL_PRIM_POINTS)
    return lub_api_fail(app, "draw: invalid primitive %d", prim);

  BindingsDesc bind = {0};
  bind.refl = &sh->u.sh.refl;
  uint8_t depth_tex_mask = 0;
  // buffers: "indices" は index buffer、それ以外は shader が同じ名前で宣言した
  // StructuredBuffer に束縛する (vertex pulling)。宣言の無い名前は無視。
  int sbi = 0;
  for (int32_t i = 0; i < bs.n_buffers; ++i) {
    const BoundBuffer *bb = &bs.buffers[i];
    if (lub_str_eq(bb->b->name, "indices")) {
      if (bb->type != SGL_BUFFER_INDEX)
        return lub_api_fail(
            app, "draw: 'indices' must be an INDEX buffer (got type %d)",
            (int)bb->type);
      bind.ibuf = bb->slice.buf;
      bind.ibuf_offset = bb->slice.offset;
      bind.ibuf_size = bb->slice.size;
    } else if (bb->type == SGL_BUFFER_STORAGE &&
               refl_storage_buf_index(&sh->u.sh.refl, bb->b->name, &sbi)) {
      if (bind.storage_buf_count < SGL_MAX_STORAGE_BUFS) {
        int n = bind.storage_buf_count++;
        bind.storage_bufs[n].name = sh->u.sh.refl.storage_bufs[sbi].name;
        bind.storage_bufs[n].slot = sbi;
        bind.storage_bufs[n].buf = bb->slice.buf;
        bind.storage_bufs[n].offset = bb->slice.offset;
        bind.storage_bufs[n].size = bb->slice.size;
      }
    }
  }
  const int max_tex = (int)(sizeof(bind.textures) / sizeof(bind.textures[0]));
  for (int32_t i = 0; i < bs.n_textures; ++i) {
    const LubBinding *t = bs.textures[i];
    ResEntry *te = bs.texture_entries[i];
    if (bind.texture_count >= max_tex)
      return lub_api_fail(app, "draw: too many textures (max %d)", max_tex);
    // name は shader の reflection 名 (NUL 終端の保証が要る)
    int ti = 0;
    if (!refl_texture_index(&sh->u.sh.refl, t->name, &ti))
      continue; // shader が使わない texture は無視 (従来どおり)
    bind.textures[bind.texture_count].name = sh->u.sh.refl.texs[ti].name;
    bind.textures[bind.texture_count].slot = ti;
    bind.textures[bind.texture_count].image = te->u.tex.h;
    bind.texture_count++;
    if (is_depth_format(te->u.tex.fmt))
      depth_tex_mask |= (uint8_t)(1u << ti);
  }

  // instance_count を 0 以下で渡した draw は描かない (検査と digest、使った
  // resource の記録は draw と同じ)
  if (instance_count <= 0)
    return LUB_OK;

  BackendPipeline pip = pipeline_cache_get(
      &app->pip_cache, sh->u.sh.h, &sh->u.sh.refl, (SglBlend)blend, depth_test,
      depth_write, (SglCull)cull, (SglPrimitive)prim,
      app->pass.current_n_color_targets, app->pass.current_color_fmts,
      app->pass.current_has_depth, app->pass.current_depth_fmt, depth_tex_mask,
      (int64_t)app->frame_index);
  g_backend->apply_pipeline(pip);
  g_backend->apply_bindings(&bind);

  if (bs.n_uniforms > 0 && sh->u.sh.refl.ub_count > 0) {
    float buf[UB_MAX_FLOATS];
    for (int i = 0; i < sh->u.sh.refl.ub_count; ++i) {
      const ShaderUniformBlock *ub = &sh->u.sh.refl.ubs[i];
      if (ub->stage == SGL_STAGE_COMPUTE)
        continue;
      int size = ub->size_floats < 0 ? 0 : ub->size_floats;
      if (size > UB_MAX_FLOATS)
        return lub_api_fail(app,
                            "draw: uniform block too large (%d floats > %d)",
                            size, UB_MAX_FLOATS);
      pack_uniform_block(ub, bs.uniforms, bs.n_uniforms, buf);
      g_backend->apply_uniforms(ub->stage, ub->slot, buf,
                                (size_t)size * sizeof(float));
    }
  }
  g_backend->draw(0, count, instance_count);
  return LUB_OK;
}

LubStatus lub_gfx_dispatch(LubContext *ctx, int32_t x, int32_t y, int32_t z,
                           const LubBinding *bindings, int32_t bindings_count,
                           const LubDispatchOpts *d) {
  App *app = lub_api_app(ctx);
  if (app->digest.enabled) {
    digest_tag(app, "dispatch");
    digest_i32(app, x);
    digest_i32(app, y);
    digest_i32(app, z);
    digest_i32(app, d ? d->shader : 0);
    digest_bindings(app, bindings, bindings_count);
  }
  if (!d)
    return lub_api_fail(app, "dispatch: opts required");
  if (pass_state_in_pass(&app->pass))
    return lub_api_fail(app,
                        "dispatch: must be called outside begin_pass/end_pass");
  ResEntry *sh =
      entry_from_handle(app, d->shader, RES_SHADER, "dispatch", "shader");
  if (!sh)
    return LUB_ERROR;
  if (!sh->u.sh.refl.is_compute)
    return lub_api_fail(app, "dispatch: shader '%s' is not a compute shader",
                        sh->key);
  Bindings bs;
  if (split_bindings(app, "dispatch", bindings, bindings_count, &bs) != LUB_OK)
    return LUB_ERROR;
  const ShaderReflection *refl = &sh->u.sh.refl;
  BackendPipeline pip = pipeline_cache_get_compute(
      &app->pip_cache, sh->u.sh.h, refl, (int64_t)app->frame_index);
  ComputeDispatchDesc dd = {0};
  dd.pipeline = pip;
  dd.refl = refl;
  dd.groups_x = x;
  dd.groups_y = y;
  dd.groups_z = z;

  for (int32_t i = 0; i < bs.n_buffers; ++i) {
    const BoundBuffer *bb = &bs.buffers[i];
    if (bb->type != SGL_BUFFER_STORAGE)
      continue;
    for (int k = 0; k < refl->storage_buf_count; ++k) {
      if (!lub_str_eq(bb->b->name, refl->storage_bufs[k].name))
        continue;
      // transient は読むだけ (RWStructuredBuffer には束縛しない)
      if (bb->transient && !refl->storage_bufs[k].readonly)
        return lub_api_fail(app,
                            "dispatch: binding '%s' is a transient buffer, "
                            "which is read-only (the shader declares it "
                            "RWStructuredBuffer)",
                            refl->storage_bufs[k].name);
      if (dd.n_storage_bufs < SGL_MAX_STORAGE_BUFS) {
        int n = dd.n_storage_bufs++;
        dd.storage_bufs[n].name = refl->storage_bufs[k].name;
        dd.storage_bufs[n].slot = k;
        dd.storage_bufs[n].buf = bb->slice.buf;
        dd.storage_bufs[n].offset = bb->slice.offset;
        dd.storage_bufs[n].size = bb->slice.size;
      }
      break;
    }
  }
  for (int32_t i = 0; i < bs.n_textures; ++i) {
    const LubBinding *t = bs.textures[i];
    ResEntry *te = bs.texture_entries[i];
    if (refl_has_storage_texture(refl, t->name)) {
      if (!te->u.tex.storage)
        return lub_api_fail(
            app, "dispatch: texture '%s' must be created with storage=true",
            te->key);
      for (int k = 0; k < refl->storage_tex_count; ++k) {
        if (!lub_str_eq(t->name, refl->storage_texs[k].name))
          continue;
        if (dd.n_storage_textures < SGL_MAX_STORAGE_TEXTURES) {
          dd.storage_textures[dd.n_storage_textures].name =
              refl->storage_texs[k].name;
          dd.storage_textures[dd.n_storage_textures].slot = k;
          dd.storage_textures[dd.n_storage_textures].image = te->u.tex.h;
          dd.n_storage_textures++;
        }
        break;
      }
    } else {
      int ti = 0;
      if (refl_texture_index(refl, t->name, &ti) &&
          dd.texture_count < SGL_MAX_TEXTURES) {
        dd.textures[dd.texture_count].name = refl->texs[ti].name;
        dd.textures[dd.texture_count].slot = ti;
        dd.textures[dd.texture_count].image = te->u.tex.h;
        dd.texture_count++;
      }
    }
  }
  for (int i = 0; i < dd.texture_count; ++i)
    for (int j = 0; j < dd.n_storage_textures; ++j)
      if (dd.textures[i].image &&
          dd.textures[i].image == dd.storage_textures[j].image)
        return lub_api_fail(app, "dispatch: same texture cannot be read and "
                                 "written in one dispatch");

  float ubufs[SGL_MAX_UNIFORM_BLOCKS][UB_MAX_FLOATS];
  if (bs.n_uniforms > 0 && refl->ub_count > 0) {
    for (int i = 0;
         i < refl->ub_count && dd.uniform_count < SGL_MAX_UNIFORM_BLOCKS; ++i) {
      const ShaderUniformBlock *ub = &refl->ubs[i];
      int size = ub->size_floats < 0 ? 0 : ub->size_floats;
      if (size > UB_MAX_FLOATS)
        return lub_api_fail(
            app, "dispatch: uniform block too large (%d floats > %d)", size,
            UB_MAX_FLOATS);
      pack_uniform_block(ub, bs.uniforms, bs.n_uniforms,
                         ubufs[dd.uniform_count]);
      dd.uniforms[dd.uniform_count].stage = ub->stage;
      dd.uniforms[dd.uniform_count].slot = ub->slot;
      dd.uniforms[dd.uniform_count].data = ubufs[dd.uniform_count];
      dd.uniforms[dd.uniform_count].bytes = (size_t)size * sizeof(float);
      dd.uniform_count++;
    }
  }
  g_backend->dispatch(app, &dd);
  return LUB_OK;
}

// ------------------------------------------------------------- readback

#define RB_MAX_DEPTH 32
#define RB_MAX_QUEUES 16

typedef struct RbItem {
  BackendReadback req;
  ReadbackResult rb;
  char *error;
  int32_t token;
  enum { RB_EMPTY = 0, RB_PENDING, RB_READY, RB_ERROR } state;
} RbItem;

typedef struct RbQueue {
  char key[64];
  int depth, head, count;
  int64_t last_seen_frame; // 最後に poll された frame。途切れたら sweep
  RbItem items[RB_MAX_DEPTH];
} RbQueue;

struct GfxReadbackQueues {
  int n;
  RbQueue q[RB_MAX_QUEUES];
};

static void rb_item_clear(RbItem *it) {
  if (it->req && g_backend && g_backend->destroy_readback)
    g_backend->destroy_readback(it->req);
  it->req = 0;
  free(it->rb.data);
  memset(&it->rb, 0, sizeof(it->rb));
  free(it->error);
  it->error = NULL;
  it->state = RB_EMPTY;
}

static void rb_queue_clear(RbQueue *q) {
  for (int k = 0; k < RB_MAX_DEPTH; ++k)
    rb_item_clear(&q->items[k]);
  q->head = 0;
  q->count = 0;
}

void api_gfx_shutdown(App *app) {
  transients_shutdown(app);
  struct GfxReadbackQueues *qs = app->readbacks;
  if (!qs)
    return;
  for (int i = 0; i < qs->n; ++i)
    rb_queue_clear(&qs->q[i]);
  free(qs);
  app->readbacks = NULL;
}

// poll が resource_sweep_after_frames の間途切れた queue を捨てる。
void api_gfx_frame_end(App *app) {
  struct GfxReadbackQueues *qs = app->readbacks;
  if (!qs || app->resource_sweep_after_frames <= 0)
    return;
  int64_t cf = (int64_t)app->frame_index;
  int64_t thr = (int64_t)app->resource_sweep_after_frames;
  for (int i = 0; i < qs->n;) {
    if (cf - qs->q[i].last_seen_frame > thr) {
      rb_queue_clear(&qs->q[i]);
      qs->q[i] = qs->q[qs->n - 1];
      memset(&qs->q[qs->n - 1], 0, sizeof(RbQueue));
      qs->n--;
    } else {
      ++i;
    }
  }
}

static RbQueue *rb_queue_get(App *app, LubStr key) {
  if (!app->readbacks) {
    app->readbacks =
        (struct GfxReadbackQueues *)calloc(1, sizeof(*app->readbacks));
    if (!app->readbacks) {
      lub_api_fail(app, "readback: out of memory");
      return NULL;
    }
  }
  struct GfxReadbackQueues *qs = app->readbacks;
  for (int i = 0; i < qs->n; ++i)
    if (lub_str_eq(key, qs->q[i].key)) {
      qs->q[i].last_seen_frame = (int64_t)app->frame_index;
      return &qs->q[i];
    }
  if (qs->n >= RB_MAX_QUEUES) {
    lub_api_fail(app, "readback: too many readback queues (max %d)",
                 RB_MAX_QUEUES);
    return NULL;
  }
  RbQueue *q = &qs->q[qs->n];
  if (!lub_str_copy(key, q->key, sizeof(q->key))) {
    lub_api_fail(app, "readback: key too long");
    return NULL;
  }
  int depth = app->readback_depth;
  if (depth < 1)
    depth = 1;
  if (depth > RB_MAX_DEPTH)
    depth = RB_MAX_DEPTH;
  q->depth = depth;
  q->head = 0;
  q->count = 0;
  q->last_seen_frame = (int64_t)app->frame_index;
  qs->n++;
  return q;
}

// 先頭の要求を進める。完了 (READY / ERROR) なら true。
static bool rb_poll_item(RbItem *it) {
  if (it->state == RB_READY || it->state == RB_ERROR)
    return true;
  if (it->state != RB_PENDING || !it->req) {
    it->error = SDL_strdup("read_texture: invalid readback request");
    it->state = RB_ERROR;
    return true;
  }
  if (!g_backend || !g_backend->poll_readback || !g_backend->destroy_readback) {
    it->error = SDL_strdup("read_texture: backend does not support readback");
    it->state = RB_ERROR;
    return true;
  }
  ReadbackResult out = {0};
  ReadbackPollStatus st = g_backend->poll_readback(it->req, &out);
  if (st == READBACK_POLL_PENDING)
    return false;
  g_backend->destroy_readback(it->req);
  it->req = 0;
  if (st == READBACK_POLL_READY) {
    it->rb = out;
    it->state = RB_READY;
  } else {
    it->error = SDL_strdup("read_texture: backend readback failed");
    it->state = RB_ERROR;
  }
  return true;
}

static void rb_enqueue(App *app, RbQueue *q, LubHandle tex, int32_t token) {
  int tail = (q->head + q->count) % RB_MAX_DEPTH;
  RbItem *it = &q->items[tail];
  rb_item_clear(it);
  it->token = token;
  q->count++;
  ResEntry *e = res_table_get_by_handle(&app->res, tex);
  if (!e || e->kind != RES_TEXTURE || e->u.tex.h == 0) {
    if (handle_swept(app, tex))
      SDL_asprintf(&it->error, "read_texture: texture handle %d " SWEPT_HINT,
                   (int)tex, app->resource_sweep_after_frames);
    else
      it->error = SDL_strdup("read_texture: texture handle is invalid");
    it->state = RB_ERROR;
    return;
  }
  if (is_depth_format(e->u.tex.fmt)) {
    it->error = SDL_strdup("read_texture: depth textures are not supported");
    it->state = RB_ERROR;
    return;
  }
  if (!g_backend || !g_backend->request_readback_image) {
    it->error = SDL_strdup("read_texture: backend does not support readback");
    it->state = RB_ERROR;
    return;
  }
  BackendReadback req = 0;
  if (!g_backend->request_readback_image(app, e->u.tex.h, e->u.tex.w,
                                         e->u.tex.h_, e->u.tex.fmt, &req) ||
      !req) {
    it->error = SDL_strdup("read_texture: backend readback request failed");
    it->state = RB_ERROR;
    return;
  }
  it->req = req;
  it->state = RB_PENDING;
}

// 完了した item を out に写し、view の実体を frame の終わりまで預けて item を
// 空にする。
typedef struct ReadbackTaken {
  int32_t status;
  LubView pixels;
  int32_t w, h, format, stride;
  int32_t token;
  LubStr error;
} ReadbackTaken;

static void rb_take(App *app, RbItem *it, ReadbackTaken *out) {
  memset(out, 0, sizeof(*out));
  out->token = it->token;
  if (it->state == RB_ERROR || it->error) {
    out->status = LUB_GFX_READBACK_STATUS_ERROR;
    char *err = it->error ? it->error : SDL_strdup("read_texture: error");
    it->error = NULL;
    app_frame_garbage_push(app, err);
    out->error = lub_str_c(err);
  } else {
    out->status = LUB_GFX_READBACK_STATUS_READY;
    uint8_t *data = it->rb.data;
    it->rb.data = NULL;
    app_frame_garbage_push(app, data);
    out->pixels.ptr = data;
    out->pixels.len = (int32_t)it->rb.data_bytes;
    out->pixels.frame = (int32_t)app->frame_index;
    out->w = it->rb.w;
    out->h = it->rb.h;
    out->format = (int32_t)it->rb.fmt;
    out->stride = it->rb.stride;
  }
  rb_item_clear(it);
}

LubStatus lub_gfx_read_texture(LubContext *ctx, LubStr rb, LubHandle tex,
                               const int32_t *id, int32_t *status,
                               LubView *bytes, int32_t *width, int32_t *height,
                               int32_t *format, int32_t *stride,
                               int32_t *result_id, int32_t *dropped,
                               LubStr *error) {
  App *app = lub_api_app(ctx);
  if (pass_state_in_pass(&app->pass))
    return lub_api_fail(app,
                        "read_texture: cannot read while a pass is active");
  RbQueue *q = rb_queue_get(app, rb);
  if (!q)
    return LUB_ERROR;
  bool has_request = id != NULL;
  int32_t token = id ? *id : 0;
  // 読み戻しを求めた texture は使われている (queue が一杯で落としても)
  ResEntry *te = has_request ? res_table_get_by_handle(&app->res, tex) : NULL;
  if (te)
    res_table_touch(te, (int64_t)app->frame_index);
  *status = LUB_GFX_READBACK_STATUS_PROCESSING;
  memset(bytes, 0, sizeof(*bytes));
  *width = *height = *format = *stride = 0;
  *result_id = *dropped = 0;
  error->ptr = NULL;
  error->len = 0;
  if (q->count > 0 && rb_poll_item(&q->items[q->head])) {
    int idx = q->head;
    q->head = (q->head + 1) % RB_MAX_DEPTH;
    q->count--;
    if (has_request && q->count < q->depth)
      rb_enqueue(app, q, tex, token);
    ReadbackTaken r;
    rb_take(app, &q->items[idx], &r);
    *status = r.status;
    *result_id = r.token;
    if (r.status == LUB_GFX_READBACK_STATUS_READY) {
      *bytes = r.pixels;
      *width = r.w;
      *height = r.h;
      *format = r.format;
      *stride = r.stride;
    } else {
      *error = r.error;
    }
    return LUB_OK;
  }
  if (has_request) {
    if (q->count >= q->depth) {
      *status = LUB_GFX_READBACK_STATUS_DROPPED;
      *dropped = token;
      return LUB_OK;
    }
    rb_enqueue(app, q, tex, token);
  }
  return LUB_OK;
}
