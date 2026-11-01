// Gfx の Bindings: 名前ごとの handle か float の列を 1 つの block に持ち、draw に渡す。
// block の確保は host (Lua の userdata、tcs2c の GC の配列) が持つので、ここは確保も解放もしない。
// 足りないときは何も変えずに LUB_BINDINGS_FULL を返し、host が大きい block に写してやり直す。
// host の GC は block を動かすことがあるので、中の pointer は self と比べて入口で直す。
// 名前の解決と handle の検査は draw のたびに lub_gfx_draw が行う。
// 解決の結果を覚えないので、shader や resource が作り直されても古い結果を使わない。
#include "api_internal.h"
#include <string.h>

enum { BASE_ITEMS = 6, BASE_FLOATS = 36, BASE_NAME_BYTES = 64 };

struct LubBindings {
  LubBindings *self;
  LubBinding *items;
  int32_t
      *value_caps; // items と同じ添字。その item が今の場所で持てる float の数
  float *values;
  char *names;
  int32_t scale;
  int32_t n;
  int32_t values_used;
  int32_t names_used;
};

#define ITEMS_CAP(b) (BASE_ITEMS * (b)->scale)
#define VALUES_CAP(b) (BASE_FLOATS * (b)->scale)
#define NAMES_CAP(b) (BASE_NAME_BYTES * (b)->scale)

size_t lub_bindings_size(int32_t scale) {
  return sizeof(LubBindings) +
         (size_t)scale * (BASE_ITEMS * (sizeof(LubBinding) + sizeof(int32_t)) +
                          BASE_FLOATS * sizeof(float) + BASE_NAME_BYTES);
}

static void set_layout(LubBindings *b) {
  b->self = b;
  b->items = (LubBinding *)(b + 1);
  b->value_caps = (int32_t *)(b->items + ITEMS_CAP(b));
  b->values = (float *)(b->value_caps + ITEMS_CAP(b));
  b->names = (char *)(b->values + VALUES_CAP(b));
}

void lub_bindings_init(LubBindings *b, int32_t scale) {
  b->scale = scale;
  b->n = 0;
  b->values_used = 0;
  b->names_used = 0;
  set_layout(b);
}

int32_t lub_bindings_scale(const LubBindings *b) { return b->scale; }

static void fix_pointers(LubBindings *b) {
  if (b->self == b)
    return;
  const char *old_values = (const char *)b->values;
  const char *old_names = b->names;
  set_layout(b);
  for (int32_t i = 0; i < b->n; ++i) {
    LubBinding *it = &b->items[i];
    it->name.ptr = b->names + (it->name.ptr - old_names);
    if (it->values)
      it->values =
          b->values + ((const float *)it->values - (const float *)old_values);
  }
}

void lub_bindings_grow_into(LubBindings *dst, LubBindings *src) {
  fix_pointers(src);
  dst->n = src->n;
  memcpy(dst->names, src->names, (size_t)src->names_used);
  dst->names_used = src->names_used;
  memcpy(dst->value_caps, src->value_caps, (size_t)src->n * sizeof(int32_t));
  // 使っている分だけを写す (handle に変わった item などの跡を捨てる)
  int32_t used = 0;
  for (int32_t i = 0; i < src->n; ++i) {
    const LubBinding *from = &src->items[i];
    LubBinding *to = &dst->items[i];
    *to = *from;
    to->name.ptr = dst->names + (from->name.ptr - src->names);
    if (from->values) {
      memcpy(dst->values + used, from->values,
             (size_t)from->count * sizeof(float));
      to->values = dst->values + used;
      used += src->value_caps[i];
    }
  }
  dst->values_used = used;
}

static LubBinding *find(LubBindings *b, LubStr name) {
  for (int32_t i = 0; i < b->n; ++i) {
    LubBinding *it = &b->items[i];
    if (it->name.len == name.len &&
        memcmp(it->name.ptr, name.ptr, (size_t)name.len) == 0)
      return it;
  }
  return NULL;
}

// 足りないなら何も変えずに NULL を返す。
static LubBinding *find_or_add(LubBindings *b, LubStr name) {
  LubBinding *it = find(b, name);
  if (it)
    return it;
  if (b->n == ITEMS_CAP(b) || b->names_used + name.len > NAMES_CAP(b))
    return NULL;
  it = &b->items[b->n];
  char *p = b->names + b->names_used;
  if (name.len > 0)
    memcpy(p, name.ptr, (size_t)name.len);
  b->names_used += name.len;
  it->name.ptr = p;
  it->name.len = name.len;
  it->handle = 0;
  it->values = NULL;
  it->count = 0;
  b->value_caps[b->n] = 0;
  b->n++;
  return it;
}

static float *uniform_slot(LubBindings *b, LubStr name, int32_t count) {
  // 足りなければ追加も書き込みもしない
  LubBinding *it = find(b, name);
  bool place = !it || !it->values || b->value_caps[it - b->items] < count;
  if (place && b->values_used + count > VALUES_CAP(b))
    return NULL;
  it = find_or_add(b, name);
  if (!it)
    return NULL;
  int32_t i = (int32_t)(it - b->items);
  if (place) {
    it->values = b->values + b->values_used;
    b->value_caps[i] = count;
    b->values_used += count;
  }
  it->handle = 0;
  it->count = count;
  return (float *)it->values;
}

static LubStatus set_handle(LubContext *ctx, LubBindings *b, LubStr name,
                            LubHandle h) {
  if (!b)
    return lub_api_fail(lub_api_app(ctx), "bindings: null");
  fix_pointers(b);
  LubBinding *it = find_or_add(b, name);
  if (!it)
    return LUB_BINDINGS_FULL;
  it->handle = h;
  it->values = NULL;
  it->count = 0;
  b->value_caps[it - b->items] = 0;
  return LUB_OK;
}

LubStatus lub_gfx_bindings_set_texture(LubContext *ctx, LubBindings *b,
                                       LubStr name, LubHandle texture) {
  return set_handle(ctx, b, name, texture);
}

LubStatus lub_gfx_bindings_set_buffer(LubContext *ctx, LubBindings *b,
                                      LubStr name, LubHandle buffer) {
  return set_handle(ctx, b, name, buffer);
}

LubStatus lub_gfx_bindings_set_floats(LubContext *ctx, LubBindings *b,
                                      LubStr name, const float *values,
                                      int32_t values_count) {
  if (!b)
    return lub_api_fail(lub_api_app(ctx), "bindings: null");
  fix_pointers(b);
  float *p = uniform_slot(b, name, values_count);
  if (!p)
    return LUB_BINDINGS_FULL;
  if (values_count > 0)
    memcpy(p, values, (size_t)values_count * sizeof(float));
  return LUB_OK;
}

LubStatus lub_gfx_bindings_set_float_array(LubContext *ctx, LubBindings *b,
                                           LubStr name, const float *values,
                                           int32_t values_count) {
  return lub_gfx_bindings_set_floats(ctx, b, name, values, values_count);
}

LubStatus lub_gfx_bindings_set4(LubContext *ctx, LubBindings *b, LubStr name,
                                int32_t count, float x, float y, float z,
                                float w) {
  if (count < 1 || count > 4)
    return lub_api_fail(lub_api_app(ctx),
                        "bindings: count must be 1 to 4 (got %d)", count);
  const float v[4] = {x, y, z, w};
  return lub_gfx_bindings_set_floats(ctx, b, name, v, count);
}

LubStatus lub_gfx_bindings_set16(LubContext *ctx, LubBindings *b, LubStr name,
                                 float m00, float m01, float m02, float m03,
                                 float m10, float m11, float m12, float m13,
                                 float m20, float m21, float m22, float m23,
                                 float m30, float m31, float m32, float m33) {
  const float v[16] = {m00, m01, m02, m03, m10, m11, m12, m13,
                       m20, m21, m22, m23, m30, m31, m32, m33};
  return lub_gfx_bindings_set_floats(ctx, b, name, v, 16);
}

LubStatus lub_gfx_bindings_clear(LubContext *ctx, LubBindings *b) {
  if (!b)
    return lub_api_fail(lub_api_app(ctx), "bindings: null");
  b->n = 0;
  b->values_used = 0;
  b->names_used = 0;
  return LUB_OK;
}

LubStatus lub_gfx_draw_bound(LubContext *ctx, int32_t count,
                             LubBindings *bindings, const LubDrawOpts *opts,
                             int32_t instances) {
  if (!bindings)
    return lub_api_fail(lub_api_app(ctx), "draw: bindings required");
  fix_pointers(bindings);
  return lub_gfx_draw(ctx, count, bindings->items, bindings->n, opts,
                      instances);
}

LubStatus lub_gfx_dispatch_bound(LubContext *ctx, int32_t x, int32_t y,
                                 int32_t z, LubBindings *bindings,
                                 const LubDispatchOpts *opts) {
  if (!bindings)
    return lub_api_fail(lub_api_app(ctx), "dispatch: bindings required");
  fix_pointers(bindings);
  return lub_gfx_dispatch(ctx, x, y, z, bindings->items, bindings->n, opts);
}
