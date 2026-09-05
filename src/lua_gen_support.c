// 生成した Lua binding の土台 (src/lua_gen_support.h)。
#include "lua_gen_support.h"
#include "api_internal.h"
#include "app.h"
#include "lua_api.h"
#include <SDL3/SDL.h>
#include <inttypes.h>
#include <stdlib.h>
#include <string.h>

static App *g_app;

LubContext *lgen_ctx(void) { return lub_api_ctx(g_app); }

int lgen_raise(lua_State *L) {
  return luaL_error(L, "%s", lub_last_error(lgen_ctx()));
}

// ---------------------------------------------------------------- arena
// 呼び出しの間だけ生きる bump allocator。chunk を連ねて伸び、release で
// mark まで戻す (chunk は残す)。

typedef struct Chunk {
  struct Chunk *next;
  size_t cap, used;
  unsigned char data[];
} Chunk;

static struct {
  Chunk *first;
  Chunk *cur;
  size_t total; // cur より前の chunk の used の和 (mark 用)
} g_arena;

LgenMark lgen_mark(void) {
  return g_arena.cur ? g_arena.total + g_arena.cur->used : 0;
}

void lgen_release(LgenMark mark) {
  // mark を含む chunk まで戻す
  size_t base = 0;
  for (Chunk *c = g_arena.first; c; c = c->next) {
    if (mark <= base + c->cap) {
      c->used = mark - base;
      for (Chunk *d = c->next; d; d = d->next)
        d->used = 0;
      g_arena.cur = c;
      g_arena.total = base;
      return;
    }
    base += c->cap;
  }
}

void *lgen_alloc(lua_State *L, size_t bytes) {
  bytes = (bytes + 15) & ~(size_t)15;
  Chunk *c = g_arena.cur;
  if (!c || c->used + bytes > c->cap) {
    // 次の chunk (既存があればそれ、足りなければ作る)
    Chunk *next = c ? c->next : g_arena.first;
    if (next && next->cap < bytes)
      next = NULL;
    if (!next) {
      size_t cap = bytes > (1u << 20) ? bytes : (1u << 20);
      next = (Chunk *)malloc(sizeof(Chunk) + cap);
      if (!next) {
        luaL_error(L, "lub: out of memory");
        return NULL;
      }
      next->cap = cap;
      next->used = 0;
      next->next = c ? c->next : g_arena.first;
      if (c)
        c->next = next;
      else
        g_arena.first = next;
    }
    if (c)
      g_arena.total += c->used;
    next->used = 0;
    g_arena.cur = c = next;
  }
  void *p = c->data + c->used;
  c->used += bytes;
  memset(p, 0, bytes);
  return p;
}

// ---------------------------------------------------------------- views
// runtime の memory への frame 有効の view userdata。数値の配列は 1 始まりの
// index と # で読める (table と同じ形)。古い frame の view は error。

#define VIEW_MT "lub.View"

typedef enum { VIEW_BYTES, VIEW_FLOAT, VIEW_INT } ViewKind;

typedef struct View {
  ViewKind kind;
  const void *data;
  int32_t count; // 要素数 (bytes は byte 数)
  int32_t frame;
} View;

static View *view_check(lua_State *L, int idx) {
  View *v = (View *)luaL_checkudata(L, idx, VIEW_MT);
  int32_t now = lub_frame_index(lgen_ctx());
  if (v->frame != now)
    luaL_error(L,
               "stale view: returned in frame %d, now frame %d (a view is "
               "valid until the end of its frame; copy what you keep)",
               (int)v->frame, (int)now);
  return v;
}

static View *view_test(lua_State *L, int idx) {
  View *v = (View *)luaL_testudata(L, idx, VIEW_MT);
  if (!v)
    return NULL;
  return view_check(L, idx);
}

static void view_push(lua_State *L, ViewKind kind, const void *data,
                      int32_t count) {
  View *v = (View *)lua_newuserdatauv(L, sizeof(View), 0);
  v->kind = kind;
  v->data = data;
  v->count = count;
  v->frame = lub_frame_index(lgen_ctx());
  luaL_getmetatable(L, VIEW_MT);
  lua_setmetatable(L, -2);
}

static int view_len(lua_State *L) {
  View *v = view_check(L, 1);
  lua_pushinteger(L, v->count);
  return 1;
}

static int view_index(lua_State *L) {
  View *v = view_check(L, 1);
  if (lua_isinteger(L, 2)) {
    lua_Integer i = lua_tointeger(L, 2);
    if (i < 1 || i > v->count) {
      lua_pushnil(L);
      return 1;
    }
    switch (v->kind) {
    case VIEW_FLOAT:
      lua_pushnumber(L, ((const float *)v->data)[i - 1]);
      break;
    case VIEW_INT:
      lua_pushinteger(L, ((const int32_t *)v->data)[i - 1]);
      break;
    default:
      lua_pushinteger(L, ((const uint8_t *)v->data)[i - 1]);
      break;
    }
    return 1;
  }
  const char *key = lua_tostring(L, 2);
  if (key && (strcmp(key, "length") == 0 || strcmp(key, "len") == 0 ||
              strcmp(key, "count") == 0)) {
    lua_pushinteger(L, v->count);
    return 1;
  }
  lua_pushnil(L);
  return 1;
}

static void view_register(lua_State *L) {
  if (luaL_newmetatable(L, VIEW_MT)) {
    lua_pushcfunction(L, view_len);
    lua_setfield(L, -2, "__len");
    lua_pushcfunction(L, view_index);
    lua_setfield(L, -2, "__index");
  }
  lua_pop(L, 1);
}

void lgen_push_float_view(lua_State *L, const float *data, int32_t count) {
  view_push(L, VIEW_FLOAT, data, count);
}

void lgen_push_int_view(lua_State *L, const int32_t *data, int32_t count) {
  view_push(L, VIEW_INT, data, count);
}

void lgen_push_bytes_view(lua_State *L, LubView v) {
  view_push(L, VIEW_BYTES, v.ptr, v.len);
}

// -------------------------------------------------------------- scalars

LubStr lgen_str_arg(lua_State *L, int idx) {
  View *v = view_test(L, idx);
  if (v) {
    LubStr r = {(const char *)v->data, v->kind == VIEW_BYTES
                                           ? v->count
                                           : v->count * (int32_t)sizeof(float)};
    return r;
  }
  size_t n = 0;
  const char *s = luaL_checklstring(L, idx, &n);
  LubStr r = {s, (int32_t)n};
  return r;
}

LubStr lgen_str_opt(lua_State *L, int idx) {
  if (lua_isnoneornil(L, idx)) {
    LubStr r = {NULL, 0};
    return r;
  }
  return lgen_str_arg(L, idx);
}

const uint8_t *lgen_bytes_arg(lua_State *L, int idx, int32_t *len,
                              bool required) {
  if (!required && lua_isnoneornil(L, idx)) {
    *len = 0;
    return NULL;
  }
  LubStr s = lgen_str_arg(L, idx);
  *len = s.len;
  return (const uint8_t *)s.ptr;
}

bool lgen_has(lua_State *L, int idx, const char *key) {
  lua_getfield(L, idx, key);
  bool has = !lua_isnil(L, -1);
  lua_pop(L, 1);
  return has;
}

static bool field_number(lua_State *L, int idx, const char *key,
                         lua_Number *out) {
  lua_getfield(L, idx, key);
  int t = lua_type(L, -1);
  bool ok = false;
  if (t == LUA_TNUMBER) {
    *out = lua_tonumber(L, -1);
    ok = true;
  } else if (t != LUA_TNIL) {
    lua_pop(L, 1);
    luaL_error(L, "field '%s' must be a number (got %s)", key,
               lua_typename(L, t));
    return false;
  }
  lua_pop(L, 1);
  return ok;
}

float lgen_num(lua_State *L, int idx, const char *key, float def) {
  lua_Number n;
  return field_number(L, idx, key, &n) ? (float)n : def;
}

bool lgen_num_opt(lua_State *L, int idx, const char *key, float *out) {
  lua_Number n;
  if (!field_number(L, idx, key, &n))
    return false;
  *out = (float)n;
  return true;
}

int32_t lgen_int(lua_State *L, int idx, const char *key, int32_t def) {
  lua_Number n;
  return field_number(L, idx, key, &n) ? (int32_t)n : def;
}

bool lgen_int_opt(lua_State *L, int idx, const char *key, int32_t *out) {
  lua_Number n;
  if (!field_number(L, idx, key, &n))
    return false;
  *out = (int32_t)n;
  return true;
}

bool lgen_bool(lua_State *L, int idx, const char *key, bool def) {
  lua_getfield(L, idx, key);
  bool r = lua_isnil(L, -1) ? def : lua_toboolean(L, -1);
  lua_pop(L, 1);
  return r;
}

bool lgen_bool_opt(lua_State *L, int idx, const char *key, bool *out) {
  lua_getfield(L, idx, key);
  bool has = !lua_isnil(L, -1);
  if (has)
    *out = lua_toboolean(L, -1);
  lua_pop(L, 1);
  return has;
}

LubStr lgen_str(lua_State *L, int idx, const char *key) {
  LubStr r = {NULL, 0};
  lua_getfield(L, idx, key);
  if (lua_type(L, -1) == LUA_TSTRING) {
    size_t n = 0;
    r.ptr = lua_tolstring(L, -1, &n); // 文字列は table が参照し続ける
    r.len = (int32_t)n;
  } else {
    View *v = view_test(L, -1);
    if (v) {
      r.ptr = (const char *)v->data;
      r.len = v->count;
    }
  }
  lua_pop(L, 1);
  return r;
}

static int32_t enum_lookup(lua_State *L, const char *s, size_t n,
                           const char *const *names, const int32_t *values,
                           const char *enum_name) {
  for (int i = 0; names[i]; ++i)
    if (strlen(names[i]) == n && memcmp(names[i], s, n) == 0)
      return values[i];
  luaL_error(L, "unknown %s '%s'", enum_name, s);
  return 0;
}

int32_t lgen_enum_str(lua_State *L, int idx, const char *key,
                      const char *const *names, const int32_t *values,
                      const char *enum_name, bool *has) {
  int32_t r = 0;
  lua_getfield(L, idx, key);
  int t = lua_type(L, -1);
  if (t == LUA_TSTRING) {
    size_t n = 0;
    const char *s = lua_tolstring(L, -1, &n);
    r = enum_lookup(L, s, n, names, values, enum_name);
    if (has)
      *has = true;
  } else if (t == LUA_TNUMBER) {
    r = (int32_t)lua_tointeger(L, -1); // 整数の enum 値も受ける
    if (has)
      *has = true;
  } else {
    if (has)
      *has = false;
    if (t != LUA_TNIL) {
      lua_pop(L, 1);
      luaL_error(L, "field '%s' must be a %s name", key, enum_name);
      return 0;
    }
  }
  lua_pop(L, 1);
  return r;
}

int32_t lgen_enum_str_arg(lua_State *L, int idx, const char *const *names,
                          const int32_t *values, const char *enum_name) {
  if (lua_type(L, idx) == LUA_TNUMBER)
    return (int32_t)lua_tointeger(L, idx);
  size_t n = 0;
  const char *s = luaL_checklstring(L, idx, &n);
  return enum_lookup(L, s, n, names, values, enum_name);
}

static bool parse_bits(lua_State *L, int idx, uint64_t *out) {
  int t = lua_type(L, idx);
  if (t == LUA_TNUMBER) {
    *out = (uint64_t)lua_tointeger(L, idx);
    return true;
  }
  if (t == LUA_TSTRING) {
    const char *s = lua_tostring(L, idx);
    if (s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
      s += 2;
    uint64_t v = 0;
    if (!*s)
      return luaL_error(L, "bit mask: empty hex string") != 0;
    for (; *s; ++s) {
      int d;
      if (*s >= '0' && *s <= '9')
        d = *s - '0';
      else if (*s >= 'a' && *s <= 'f')
        d = *s - 'a' + 10;
      else if (*s >= 'A' && *s <= 'F')
        d = *s - 'A' + 10;
      else
        return luaL_error(L, "bit mask: invalid hex '%s'",
                          lua_tostring(L, idx)) != 0;
      v = (v << 4) | (uint64_t)d;
    }
    *out = v;
    return true;
  }
  return false;
}

bool lgen_bits_opt(lua_State *L, int idx, const char *key, uint64_t *out) {
  lua_getfield(L, idx, key);
  bool has = parse_bits(L, -1, out);
  lua_pop(L, 1);
  return has;
}

void lgen_set_num(lua_State *L, const char *key, float v) {
  lua_pushnumber(L, v);
  lua_setfield(L, -2, key);
}

void lgen_set_int(lua_State *L, const char *key, int32_t v) {
  lua_pushinteger(L, v);
  lua_setfield(L, -2, key);
}

void lgen_set_bool(lua_State *L, const char *key, bool v) {
  lua_pushboolean(L, v);
  lua_setfield(L, -2, key);
}

void lgen_push_str(lua_State *L, LubStr v) {
  lua_pushlstring(L, v.ptr ? v.ptr : "", (size_t)v.len);
}

void lgen_set_str(lua_State *L, const char *key, LubStr v) {
  lgen_push_str(L, v);
  lua_setfield(L, -2, key);
}

// 16 桁の hex (旧 Lua binding と同じ形。0x 付きも読める)。
void lgen_push_bits(lua_State *L, uint64_t v) {
  char buf[32];
  snprintf(buf, sizeof(buf), "%016" PRIx64, v);
  lua_pushstring(L, buf);
}

void lgen_set_bits(lua_State *L, const char *key, uint64_t v) {
  lgen_push_bits(L, v);
  lua_setfield(L, -2, key);
}

// --------------------------------------------------------------- arrays

// 配列らしい値 (table か view) の長さ。table は 1 始まり。
static int32_t array_len(lua_State *L, int idx, bool *is_view) {
  View *v = view_test(L, idx);
  if (v) {
    *is_view = true;
    return v->count;
  }
  *is_view = false;
  return (int32_t)lua_rawlen(L, idx);
}

static const float *read_floats(lua_State *L, int idx, int32_t *count) {
  idx = lua_absindex(L, idx);
  bool is_view;
  int32_t n = array_len(L, idx, &is_view);
  *count = n;
  if (is_view) {
    View *v = (View *)lua_touserdata(L, idx);
    if (v->kind == VIEW_FLOAT)
      return (const float *)v->data;
    float *out = (float *)lgen_alloc(L, (size_t)(n ? n : 1) * sizeof(float));
    for (int32_t i = 0; i < n; ++i)
      out[i] = v->kind == VIEW_INT ? (float)((const int32_t *)v->data)[i]
                                   : (float)((const uint8_t *)v->data)[i];
    return out;
  }
  float *out = (float *)lgen_alloc(L, (size_t)(n ? n : 1) * sizeof(float));
  for (int32_t i = 0; i < n; ++i) {
    lua_rawgeti(L, idx, i + 1);
    out[i] = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
  }
  return out;
}

static const int32_t *read_ints(lua_State *L, int idx, int32_t *count) {
  idx = lua_absindex(L, idx);
  bool is_view;
  int32_t n = array_len(L, idx, &is_view);
  *count = n;
  if (is_view) {
    View *v = (View *)lua_touserdata(L, idx);
    if (v->kind == VIEW_INT)
      return (const int32_t *)v->data;
    int32_t *out =
        (int32_t *)lgen_alloc(L, (size_t)(n ? n : 1) * sizeof(int32_t));
    for (int32_t i = 0; i < n; ++i)
      out[i] = v->kind == VIEW_FLOAT ? (int32_t)((const float *)v->data)[i]
                                     : (int32_t)((const uint8_t *)v->data)[i];
    return out;
  }
  int32_t *out =
      (int32_t *)lgen_alloc(L, (size_t)(n ? n : 1) * sizeof(int32_t));
  for (int32_t i = 0; i < n; ++i) {
    lua_rawgeti(L, idx, i + 1);
    out[i] = (int32_t)lua_tointeger(L, -1);
    lua_pop(L, 1);
  }
  return out;
}

static bool array_arg_present(lua_State *L, int idx, bool required,
                              const char *what) {
  if (lua_isnoneornil(L, idx)) {
    if (required)
      luaL_error(L, "argument %d: %s required", idx, what);
    return false;
  }
  if (lua_type(L, idx) != LUA_TTABLE && !luaL_testudata(L, idx, VIEW_MT))
    luaL_error(L, "argument %d: %s must be a table", idx, what);
  return true;
}

const float *lgen_floats_arg(lua_State *L, int idx, int32_t *count,
                             bool required) {
  *count = 0;
  if (!array_arg_present(L, idx, required, "number array"))
    return NULL;
  return read_floats(L, idx, count);
}

const int32_t *lgen_ints_arg(lua_State *L, int idx, int32_t *count,
                             bool required) {
  *count = 0;
  if (!array_arg_present(L, idx, required, "integer array"))
    return NULL;
  return read_ints(L, idx, count);
}

static bool field_array(lua_State *L, int idx, const char *key) {
  lua_getfield(L, idx, key);
  if (lua_isnil(L, -1)) {
    lua_pop(L, 1);
    return false;
  }
  if (lua_type(L, -1) != LUA_TTABLE && !luaL_testudata(L, -1, VIEW_MT)) {
    lua_pop(L, 1);
    luaL_error(L, "field '%s' must be an array", key);
    return false;
  }
  return true; // 値を積んだまま返す
}

const float *lgen_floats(lua_State *L, int idx, const char *key,
                         int32_t *count) {
  *count = 0;
  if (!field_array(L, idx, key))
    return NULL;
  const float *r = read_floats(L, -1, count);
  lua_pop(L, 1);
  return r;
}

const int32_t *lgen_ints(lua_State *L, int idx, const char *key,
                         int32_t *count) {
  *count = 0;
  if (!field_array(L, idx, key))
    return NULL;
  const int32_t *r = read_ints(L, -1, count);
  lua_pop(L, 1);
  return r;
}

bool lgen_floats_fixed(lua_State *L, int idx, const char *key, float *out,
                       int32_t cap, int32_t *count) {
  memset(out, 0, sizeof(float) * (size_t)cap);
  if (count)
    *count = 0;
  if (!field_array(L, idx, key))
    return false;
  int32_t n = 0;
  const float *v = read_floats(L, -1, &n);
  lua_pop(L, 1);
  if (n > cap)
    n = cap;
  memcpy(out, v, sizeof(float) * (size_t)n);
  if (count)
    *count = n;
  return true;
}

bool lgen_ints_fixed(lua_State *L, int idx, const char *key, int32_t *out,
                     int32_t cap, int32_t *count) {
  memset(out, 0, sizeof(int32_t) * (size_t)cap);
  if (count)
    *count = 0;
  if (!field_array(L, idx, key))
    return false;
  int32_t n = 0;
  const int32_t *v = read_ints(L, -1, &n);
  lua_pop(L, 1);
  if (n > cap)
    n = cap;
  memcpy(out, v, sizeof(int32_t) * (size_t)n);
  if (count)
    *count = n;
  return true;
}

const float *lgen_float_rows(lua_State *L, int idx, const char *key,
                             int32_t width, int32_t *count) {
  *count = 0;
  if (!field_array(L, idx, key))
    return NULL;
  int t = lua_gettop(L);
  int32_t n = (int32_t)lua_rawlen(L, t);
  float *out = (float *)lgen_alloc(L, (size_t)(n ? n : 1) * (size_t)width *
                                          sizeof(float));
  for (int32_t i = 0; i < n; ++i) {
    lua_rawgeti(L, t, i + 1);
    int32_t m = 0;
    const float *row = read_floats(L, -1, &m);
    if (m > width)
      m = width;
    memcpy(out + (size_t)i * (size_t)width, row, sizeof(float) * (size_t)m);
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  *count = n;
  return out;
}

const LubStr *lgen_strs(lua_State *L, int idx, const char *key,
                        int32_t *count) {
  *count = 0;
  if (!field_array(L, idx, key))
    return NULL;
  int t = lua_gettop(L);
  int32_t n = (int32_t)lua_rawlen(L, t);
  LubStr *out = (LubStr *)lgen_alloc(L, (size_t)(n ? n : 1) * sizeof(LubStr));
  for (int32_t i = 0; i < n; ++i) {
    lua_rawgeti(L, t, i + 1);
    size_t len = 0;
    out[i].ptr = lua_tolstring(L, -1, &len);
    out[i].len = (int32_t)len;
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  *count = n;
  return out;
}

const LubHandle *lgen_handles(lua_State *L, int idx, const char *key,
                              const char *kind, int32_t *count) {
  *count = 0;
  if (!field_array(L, idx, key))
    return NULL;
  int t = lua_gettop(L);
  int32_t n = (int32_t)lua_rawlen(L, t);
  LubHandle *out =
      (LubHandle *)lgen_alloc(L, (size_t)(n ? n : 1) * sizeof(LubHandle));
  for (int32_t i = 0; i < n; ++i) {
    lua_rawgeti(L, t, i + 1);
    out[i] = lgen_ref_arg(L, lua_gettop(L), kind, true);
    lua_pop(L, 1);
  }
  lua_pop(L, 1);
  *count = n;
  return out;
}

static const void *read_records(lua_State *L, int t, size_t size,
                                LgenReader reader, int32_t *count) {
  t = lua_absindex(L, t);
  int32_t n = (int32_t)lua_rawlen(L, t);
  unsigned char *out =
      (unsigned char *)lgen_alloc(L, (size_t)(n ? n : 1) * size);
  for (int32_t i = 0; i < n; ++i) {
    lua_rawgeti(L, t, i + 1);
    if (lua_type(L, -1) != LUA_TTABLE) {
      lua_pop(L, 1);
      luaL_error(L, "array element %d must be a table", (int)i + 1);
      return NULL;
    }
    reader(L, lua_gettop(L), out + (size_t)i * size);
    lua_pop(L, 1);
  }
  *count = n;
  return out;
}

const void *lgen_records(lua_State *L, int idx, const char *key, size_t size,
                         LgenReader reader, int32_t *count) {
  *count = 0;
  if (!field_array(L, idx, key))
    return NULL;
  const void *r = read_records(L, -1, size, reader, count);
  lua_pop(L, 1);
  return r;
}

const void *lgen_records_arg(lua_State *L, int idx, size_t size,
                             LgenReader reader, int32_t *count, bool required) {
  *count = 0;
  if (!array_arg_present(L, idx, required, "array of tables"))
    return NULL;
  return read_records(L, idx, size, reader, count);
}

void lgen_push_float_table(lua_State *L, const float *data, int32_t count) {
  lua_createtable(L, count, 0);
  for (int32_t i = 0; i < count; ++i) {
    lua_pushnumber(L, data[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

void lgen_push_int_table(lua_State *L, const int32_t *data, int32_t count) {
  lua_createtable(L, count, 0);
  for (int32_t i = 0; i < count; ++i) {
    lua_pushinteger(L, data[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

void lgen_push_str_table(lua_State *L, const LubStr *data, int32_t count) {
  lua_createtable(L, count, 0);
  for (int32_t i = 0; i < count; ++i) {
    lgen_push_str(L, data[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

// -------------------------------------------------------------- handles
// sentinel table: { __lub_kind = kind, handle = h, key = ..., ... }。
// gfx の resource (texture / shader / buffer) は key と version も持ち、
// handle が stale なら key から引き直す。

static bool is_sentinel(lua_State *L, int idx, const char *kind) {
  if (lua_type(L, idx) != LUA_TTABLE)
    return false;
  lua_getfield(L, idx, "__lub_kind");
  const char *k = lua_tostring(L, -1);
  bool ok = k && strcmp(k, kind) == 0;
  lua_pop(L, 1);
  return ok;
}

static bool is_gfx_kind(const char *kind) {
  return strcmp(kind, "texture") == 0 || strcmp(kind, "shader") == 0 ||
         strcmp(kind, "buffer") == 0;
}

static LubHandle sentinel_handle(lua_State *L, int idx, const char *kind) {
  lua_getfield(L, idx, "handle");
  LubHandle h = lua_isinteger(L, -1) ? (LubHandle)lua_tointeger(L, -1) : 0;
  lua_pop(L, 1);
  if (is_gfx_kind(kind)) {
    // main_tex は特別な handle。stale なら key から引き直す
    LubStr key = {NULL, 0};
    int32_t ver = 0;
    if (h != 0 && h != -1 &&
        !lub_gfx_resource_info(lgen_ctx(), h, &key, &ver)) {
      lua_getfield(L, idx, "key");
      size_t n = 0;
      const char *s = lua_tolstring(L, -1, &n);
      LubStr k = {s, (int32_t)n};
      if (!s)
        h = 0;
      else if (strcmp(kind, "texture") == 0)
        h = lub_gfx_lookup_texture(lgen_ctx(), k);
      else if (strcmp(kind, "shader") == 0)
        h = lub_gfx_lookup_shader(lgen_ctx(), k);
      else
        h = lub_gfx_lookup_buffer(lgen_ctx(), k);
      lua_pop(L, 1);
    }
  }
  return h;
}

LubHandle lgen_ref_arg(lua_State *L, int idx, const char *kind, bool required) {
  idx = lua_absindex(L, idx);
  if (lua_isnoneornil(L, idx)) {
    if (required)
      luaL_error(L, "argument %d: %s expected", idx, kind);
    return 0;
  }
  if (!is_sentinel(L, idx, kind)) {
    // main_tex の sentinel は kind "main_tex"
    if (strcmp(kind, "texture") == 0 && is_sentinel(L, idx, "main_tex"))
      return lub_gfx_main_tex(lgen_ctx());
    luaL_error(L, "argument %d: %s expected", idx, kind);
    return 0;
  }
  return sentinel_handle(L, idx, kind);
}

LubHandle lgen_ref(lua_State *L, int idx, const char *key, const char *kind) {
  lua_getfield(L, idx, key);
  LubHandle h = 0;
  if (!lua_isnil(L, -1))
    h = lgen_ref_arg(L, lua_gettop(L), kind, true);
  lua_pop(L, 1);
  return h;
}

void lgen_push_ref(lua_State *L, const char *kind, LubHandle h) {
  lua_createtable(L, 0, 4);
  lua_pushstring(L, kind);
  lua_setfield(L, -2, "__lub_kind");
  lua_pushinteger(L, (lua_Integer)h);
  lua_setfield(L, -2, "handle");
  // handle の method table (world:step(...) の形)。生成物が登録する
  char mt[64];
  snprintf(mt, sizeof(mt), "lub.ref.%s", kind);
  if (luaL_getmetatable(L, mt) != LUA_TNIL)
    lua_setmetatable(L, -2);
  else
    lua_pop(L, 1);
  if (is_gfx_kind(kind)) {
    LubStr key = {NULL, 0};
    int32_t ver = 0;
    if (lub_gfx_resource_info(lgen_ctx(), h, &key, &ver)) {
      lgen_push_str(L, key);
      lua_setfield(L, -2, "key");
      lua_pushinteger(L, ver);
      lua_setfield(L, -2, "version");
    }
  }
}

void lgen_push_ref_keyed(lua_State *L, const char *kind, LubHandle h,
                         int parent_idx, LubStr key) {
  lgen_push_ref(L, kind, h);
  if (parent_idx != 0 && lua_type(L, parent_idx) == LUA_TTABLE) {
    // 親の key 列 (world / body の key) を引き継ぐ
    static const char *const inherit[] = {"world", "body", NULL};
    for (int i = 0; inherit[i]; ++i) {
      lua_getfield(L, parent_idx, inherit[i]);
      if (!lua_isnil(L, -1))
        lua_setfield(L, -2, inherit[i]);
      else
        lua_pop(L, 1);
    }
    lua_getfield(L, parent_idx, "__lub_kind");
    const char *pk = lua_tostring(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, parent_idx, "key");
    if (!lua_isnil(L, -1) && pk)
      lua_setfield(L, -2, strncmp(pk, "world", 5) == 0 ? "world" : "body");
    else
      lua_pop(L, 1);
  }
  lgen_push_str(L, key);
  lua_setfield(L, -2, "key");
}

void lgen_push_handle_table(lua_State *L, const char *kind,
                            const LubHandle *data, int32_t count) {
  lua_createtable(L, count, 0);
  for (int32_t i = 0; i < count; ++i) {
    lgen_push_ref(L, kind, data[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

LubStr lgen_keyed_arg(lua_State *L, int idx, const char *kind) {
  idx = lua_absindex(L, idx);
  LubStr r = {NULL, 0};
  if (lua_type(L, idx) == LUA_TSTRING) {
    size_t n = 0;
    r.ptr = lua_tolstring(L, idx, &n);
    r.len = (int32_t)n;
    return r;
  }
  if (!is_sentinel(L, idx, kind)) {
    luaL_error(L, "argument %d: %s expected", idx, kind);
    return r;
  }
  lua_getfield(L, idx, "key");
  size_t n = 0;
  r.ptr = lua_tolstring(L, -1, &n); // 文字列は sentinel が参照し続ける
  r.len = (int32_t)n;
  lua_pop(L, 1);
  return r;
}

LubStr lgen_keyed(lua_State *L, int idx, const char *key, const char *kind) {
  LubStr r = {NULL, 0};
  lua_getfield(L, idx, key);
  if (!lua_isnil(L, -1))
    r = lgen_keyed_arg(L, lua_gettop(L), kind);
  lua_pop(L, 1);
  return r;
}

void lgen_push_keyed(lua_State *L, const char *kind, LubStr key) {
  lua_createtable(L, 0, 2);
  lua_pushstring(L, kind);
  lua_setfield(L, -2, "__lub_kind");
  lgen_push_str(L, key);
  lua_setfield(L, -2, "key");
}

// ------------------------------------------------------------ callbacks

LgenCallbacks *lgen_callbacks_new(lua_State *L, int n) {
  LgenCallbacks *cb = (LgenCallbacks *)calloc(1, sizeof(LgenCallbacks));
  if (!cb) {
    luaL_error(L, "lub: out of memory");
    return NULL;
  }
  cb->L = L;
  cb->n = n;
  for (int i = 0; i < 8; ++i)
    cb->refs[i] = LUA_NOREF;
  return cb;
}

bool lgen_callbacks_field(lua_State *L, LgenCallbacks *cb, int i, int idx,
                          const char *key) {
  lua_getfield(L, idx, key);
  if (lua_type(L, -1) != LUA_TFUNCTION) {
    lua_pop(L, 1);
    return false;
  }
  cb->refs[i] = luaL_ref(L, LUA_REGISTRYINDEX);
  return true;
}

LgenCallbacks *lgen_callbacks_arg(lua_State *L, int idx) {
  if (lua_isnoneornil(L, idx))
    return NULL;
  luaL_checktype(L, idx, LUA_TFUNCTION);
  LgenCallbacks *cb = lgen_callbacks_new(L, 1);
  cb->transient = true;
  lua_pushvalue(L, idx);
  cb->refs[0] = luaL_ref(L, LUA_REGISTRYINDEX);
  return cb;
}

void lgen_callbacks_free(void *user) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  if (!cb)
    return;
  for (int i = 0; i < cb->n; ++i)
    if (cb->refs[i] != LUA_NOREF)
      luaL_unref(cb->L, LUA_REGISTRYINDEX, cb->refs[i]);
  free(cb->error);
  free(cb);
}

bool lgen_callbacks_push(LgenCallbacks *cb, int i) {
  if (cb->refs[i] == LUA_NOREF)
    return false;
  lua_rawgeti(cb->L, LUA_REGISTRYINDEX, cb->refs[i]);
  return true;
}

bool lgen_callbacks_call(LgenCallbacks *cb, int i, int nargs, int nresults) {
  lua_State *L = cb->L;
  if (lua_pcall(L, nargs, nresults, 0) != LUA_OK) {
    const char *msg = lua_tostring(L, -1);
    if (cb->transient) {
      // query の visitor: 呼び出し元が nil, error で返す
      if (!cb->error && msg)
        cb->error = strdup(msg);
    } else if (!cb->logged[i]) {
      SDL_Log("lub: callback error: %s", msg ? msg : "?");
      cb->logged[i] = true;
    }
    lua_pop(L, 1);
    return false;
  }
  return true;
}

const char *lgen_callbacks_error(LgenCallbacks *cb) {
  return cb ? cb->error : NULL;
}

// ------------------------------------------------------------ bindings

const LubBinding *lgen_bindings_arg(lua_State *L, int idx, int32_t *count) {
  idx = lua_absindex(L, idx);
  *count = 0;
  luaL_checktype(L, idx, LUA_TTABLE);
  // 個数を数えてから詰める (uniforms は入れ子)
  int32_t n = 0;
  lua_pushnil(L);
  while (lua_next(L, idx)) {
    if (lua_type(L, -2) == LUA_TSTRING) {
      const char *k = lua_tostring(L, -2);
      if (strcmp(k, "uniforms") == 0 && lua_type(L, -1) == LUA_TTABLE) {
        lua_pushnil(L);
        while (lua_next(L, -2)) {
          n++;
          lua_pop(L, 1);
        }
      } else if (k[0] != '_' || k[1] != '_') {
        n++;
      }
    }
    lua_pop(L, 1);
  }
  LubBinding *out =
      (LubBinding *)lgen_alloc(L, (size_t)(n ? n : 1) * sizeof(LubBinding));
  int32_t i = 0;
  lua_pushnil(L);
  while (lua_next(L, idx)) {
    if (lua_type(L, -2) != LUA_TSTRING) {
      lua_pop(L, 1);
      continue;
    }
    size_t klen = 0;
    const char *k = lua_tolstring(L, -2, &klen);
    // Haxe の匿名 object が持つ __fields__ 等の予約 key は読まない
    if (k[0] == '_' && k[1] == '_') {
      lua_pop(L, 1);
      continue;
    }
    if (strcmp(k, "uniforms") == 0 && lua_type(L, -1) == LUA_TTABLE) {
      int ut = lua_gettop(L);
      lua_pushnil(L);
      while (lua_next(L, ut)) {
        if (lua_type(L, -2) == LUA_TSTRING &&
            !(lua_tostring(L, -2)[0] == '_' && lua_tostring(L, -2)[1] == '_')) {
          size_t ulen = 0;
          out[i].name.ptr = lua_tolstring(L, -2, &ulen);
          out[i].name.len = (int32_t)ulen;
          out[i].handle = 0;
          if (lua_type(L, -1) == LUA_TNUMBER) {
            float *one = (float *)lgen_alloc(L, sizeof(float));
            one[0] = (float)lua_tonumber(L, -1);
            out[i].values = one;
            out[i].count = 1;
          } else {
            out[i].values = read_floats(L, -1, &out[i].count);
          }
          i++;
        }
        lua_pop(L, 1);
      }
    } else {
      out[i].name.ptr = k;
      out[i].name.len = (int32_t)klen;
      out[i].values = NULL;
      out[i].count = 0;
      const char *kind =
          is_sentinel(L, -1, "texture") || is_sentinel(L, -1, "main_tex")
              ? "texture"
          : is_sentinel(L, -1, "buffer") ? "buffer"
                                         : NULL;
      if (!kind) {
        lua_pop(L, 2);
        luaL_error(L, "bindings.%s: buffer or texture expected", k);
        return NULL;
      }
      out[i].handle = lgen_ref_arg(L, lua_gettop(L), kind, true);
      i++;
    }
    lua_pop(L, 1);
  }
  *count = i;
  return out;
}

// ------------------------------------------------------------ register

static int l_readback_new(lua_State *L) {
  LubStr key = lgen_str_arg(L, 1);
  lgen_push_keyed(L, "readback", key);
  luaL_getmetatable(L, "lub.Readback");
  lua_setmetatable(L, -2);
  return 1;
}

void lgen_support_register(lua_State *L, LubContext *ctx) {
  g_app = lub_api_app(ctx);
  view_register(L);
  // readback(key) の sentinel。rb:read_texture(...) は lub.gfx.read_texture。
  lua_getglobal(L, "lub");
  lua_getfield(L, -1, "gfx");
  if (luaL_newmetatable(L, "lub.Readback")) {
    lua_newtable(L);
    lua_getfield(L, -3, "read_texture");
    lua_setfield(L, -2, "read_texture");
    lua_setfield(L, -2, "__index");
  }
  lua_pop(L, 1);
  lua_pushcfunction(L, l_readback_new);
  lua_setfield(L, -2, "readback");
  lua_pop(L, 2);
}
