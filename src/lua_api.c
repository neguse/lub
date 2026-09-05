#include "lua_api.h"
#include "api_internal.h"
#include "app.h"
#include "backend.h"
#include "enums.h"
#include "enums_lua.h"
#include "lua_phys.h"
#include "pass.h"
#include "physics_box3d.h"
#include "pipeline.h"
#include "resources.h"
#include "shader.h"
#include "ui.h"
#include <SDL3/SDL.h>
#include <ctype.h>
#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

static App *g_app_for_lua = NULL;

// Bytes: runtime が所有する byte 列への view (所有権の規則 2)。返した frame の
// 終わりまで有効で、古い view を渡された API は stale として error にする。
// 跨いで持ちたい内容はゲームが自分の memory に写す。
typedef struct LubBytes {
  const uint8_t *data;
  size_t len;
  int32_t frame; // 返した frame。今の frame と違えば stale
} LubBytes;

#define LUB_BYTES_MT "lub.Bytes"
#define LUB_READBACK_MT "lub.Readback"

static int is_sentinel(lua_State *L, int idx, const char *kind);

static int32_t frame_now(void) { return (int32_t)g_app_for_lua->frame_index; }

static LubBytes *lub_bytes_test(lua_State *L, int idx) {
  return (LubBytes *)luaL_testudata(L, idx, LUB_BYTES_MT);
}

// Bytes なら stale 検査をして返す。Bytes でなければ NULL。
static LubBytes *lub_bytes_live(lua_State *L, int idx) {
  LubBytes *b = lub_bytes_test(L, idx);
  if (b && b->frame != frame_now())
    luaL_error(L,
               "stale Bytes view: returned in frame %d, now frame %d (a view "
               "is valid until the end of its frame; copy what you keep)",
               (int)b->frame, (int)frame_now());
  return b;
}

static LubBytes *lub_bytes_check(lua_State *L, int idx) {
  luaL_checkudata(L, idx, LUB_BYTES_MT);
  return lub_bytes_live(L, idx);
}

static void lub_bytes_push(lua_State *L, const uint8_t *data, size_t len) {
  LubBytes *b = (LubBytes *)lua_newuserdatauv(L, sizeof(LubBytes), 0);
  b->data = data;
  b->len = len;
  b->frame = frame_now();
  luaL_getmetatable(L, LUB_BYTES_MT);
  lua_setmetatable(L, -2);
}

const uint8_t *lub_bytes_arg(lua_State *L, int idx, size_t *len) {
  LubBytes *b = lub_bytes_live(L, idx);
  if (b) {
    *len = b->len;
    return b->data;
  }
  return (const uint8_t *)luaL_checklstring(L, idx, len);
}

static int l_bytes_len(lua_State *L) {
  LubBytes *b = lub_bytes_check(L, 1);
  lua_pushinteger(L, (lua_Integer)b->len);
  return 1;
}

static int l_bytes_index(lua_State *L) {
  LubBytes *b = lub_bytes_check(L, 1);
  const char *key = luaL_checkstring(L, 2);
  if (strcmp(key, "length") == 0 || strcmp(key, "len") == 0) {
    lua_pushinteger(L, (lua_Integer)b->len);
    return 1;
  }
  lua_pushnil(L);
  return 1;
}

static void lub_bytes_register(lua_State *L) {
  if (luaL_newmetatable(L, LUB_BYTES_MT)) {
    lua_pushcfunction(L, l_bytes_len);
    lua_setfield(L, -2, "__len");
    lua_pushcfunction(L, l_bytes_index);
    lua_setfield(L, -2, "__index");
  }
  lua_pop(L, 1);
}

// Helper: check if value at stack index is a sentinel table with __lub_kind ==
// kind
static int is_sentinel(lua_State *L, int idx, const char *kind) {
  if (!lua_istable(L, idx))
    return 0;
  lua_getfield(L, idx, "__lub_kind");
  int ok = lua_isstring(L, -1) && strcmp(lua_tostring(L, -1), kind) == 0;
  lua_pop(L, 1);
  return ok;
}

static int numeric_table_len(lua_State *L, int idx, bool *zero_based) {
  if (idx < 0)
    idx = lua_gettop(L) + idx + 1;
  if (zero_based)
    *zero_based = false;

  // Haxe's Lua backend represents Array as a 0-based table with a numeric
  // `length` field. Accept that shape directly so hot paths don't need to
  // copy into a temporary 1-based lua.Table.
  lua_getfield(L, idx, "length");
  if (lua_isinteger(L, -1)) {
    lua_Integer n = lua_tointeger(L, -1);
    lua_pop(L, 1);
    if (n >= 0) {
      if (zero_based)
        *zero_based = true;
      return (int)n;
    }
  } else if (lua_isnumber(L, -1)) {
    lua_Number n = lua_tonumber(L, -1);
    lua_pop(L, 1);
    if (n >= 0) {
      if (zero_based)
        *zero_based = true;
      return (int)n;
    }
  } else {
    lua_pop(L, 1);
  }

  return (int)lua_rawlen(L, idx);
}

// ----------------------------------------------------------------------
// gfx: C API (include/lub/lub_api.h) への詰め替え。検証は api_gfx.c 側。
// Lua 面の参照は sentinel table { __lub_kind, key, version, handle } のまま。

static LubContext *api_ctx(void) { return lub_api_ctx(g_app_for_lua); }

static int api_raise(lua_State *L) {
  return luaL_error(L, "%s", lub_last_error(api_ctx()));
}

static LubStr lstr_check(lua_State *L, int idx) {
  size_t n = 0;
  const char *s = luaL_checklstring(L, idx, &n);
  LubStr r = {s, (int32_t)n};
  return r;
}

// use_* の version 引数。nil/省略 = 「内容が変わった」宣言 (NULL)。
static const int32_t *version_arg(lua_State *L, int idx, int32_t *store) {
  if (lua_isnoneornil(L, idx))
    return NULL;
  *store = (int32_t)luaL_checkinteger(L, idx);
  return store;
}

// sentinel table を push する。handle は直前の use_* が返した有効なもの。
static void push_ref(lua_State *L, const char *kind, LubHandle h) {
  LubStr key = {NULL, 0};
  int32_t ver = 0;
  lub_gfx_resource_info(api_ctx(), h, &key, &ver);
  lua_newtable(L);
  lua_pushstring(L, kind);
  lua_setfield(L, -2, "__lub_kind");
  lua_pushlstring(L, key.ptr ? key.ptr : "", (size_t)key.len);
  lua_setfield(L, -2, "key");
  lua_pushinteger(L, (lua_Integer)ver);
  lua_setfield(L, -2, "version");
  lua_pushinteger(L, (lua_Integer)h);
  lua_setfield(L, -2, "handle");
}

// sentinel table を handle に解決する。kind 違いや sentinel でなければ 0。
// handle が stale (sweep 済み) なら key から引き直す。
static LubHandle ref_handle(lua_State *L, int idx, const char *kind) {
  if (idx < 0)
    idx = lua_gettop(L) + idx + 1;
  if (!is_sentinel(L, idx, kind))
    return 0;
  lua_getfield(L, idx, "handle");
  LubHandle h = lua_isinteger(L, -1) ? (LubHandle)lua_tointeger(L, -1) : 0;
  lua_pop(L, 1);
  if (h > 0 && lub_gfx_resource_info(api_ctx(), h, NULL, NULL) == LUB_OK)
    return h;
  lua_getfield(L, idx, "key");
  size_t n = 0;
  const char *k = lua_tolstring(L, -1, &n);
  LubStr key = {k, (int32_t)n};
  h = k ? lub_gfx_lookup(api_ctx(), key) : 0;
  lua_pop(L, 1);
  return h;
}

static int l_use_buffer(lua_State *L) {
  LubStr key = lstr_check(L, 1);
  int type = (int)luaL_checkinteger(L, 2);
  int32_t vstore = 0;
  const int32_t *ver = version_arg(L, 4, &vstore);
  if (type != SGL_BUFFER_VERTEX && type != SGL_BUFFER_INDEX &&
      type != SGL_BUFFER_STORAGE)
    return luaL_error(L, "use_buffer: only VERTEX/INDEX/STORAGE are supported");

  void *data = NULL;
  int32_t bytes = 0;
  if (type == SGL_BUFFER_STORAGE && lua_isinteger(L, 3)) {
    // STORAGE の空確保 (float 個数)
    lua_Integer n = lua_tointeger(L, 3);
    if (n <= 0)
      return luaL_error(L, "use_buffer: STORAGE float-count must be > 0");
    bytes = (int32_t)(n * (lua_Integer)sizeof(float));
  } else {
    luaL_checktype(L, 3, LUA_TTABLE);
    bool zero_based = false;
    int n = numeric_table_len(L, 3, &zero_based);
    if (n <= 0)
      return luaL_error(L, "use_buffer: empty data");
    if (type == SGL_BUFFER_INDEX) {
      uint32_t *idx = (uint32_t *)malloc((size_t)n * sizeof(uint32_t));
      if (!idx)
        return luaL_error(L, "use_buffer: out of memory");
      for (int i = 0; i < n; ++i) {
        lua_rawgeti(L, 3, zero_based ? i : i + 1);
        idx[i] = (uint32_t)lua_tonumber(L, -1);
        lua_pop(L, 1);
      }
      data = idx;
      bytes = (int32_t)((size_t)n * sizeof(uint32_t));
    } else {
      float *fdata = (float *)malloc((size_t)n * sizeof(float));
      if (!fdata)
        return luaL_error(L, "use_buffer: out of memory");
      for (int i = 0; i < n; ++i) {
        lua_rawgeti(L, 3, zero_based ? i : i + 1);
        fdata[i] = (float)lua_tonumber(L, -1);
        lua_pop(L, 1);
      }
      data = fdata;
      bytes = (int32_t)((size_t)n * sizeof(float));
    }
  }
  LubHandle h = 0;
  LubStatus st = lub_gfx_use_buffer(api_ctx(), key, type, data, bytes, ver, &h);
  free(data);
  if (st != LUB_OK)
    return api_raise(L);
  push_ref(L, "buffer", h);
  return 1;
}

static int l_use_texture(lua_State *L) {
  LubStr key = lstr_check(L, 1);
  LubGfxTextureDesc d = {0};
  d.w = (int32_t)luaL_checkinteger(L, 2);
  d.h = (int32_t)luaL_checkinteger(L, 3);
  d.format = (int32_t)luaL_checkinteger(L, 4);
  int32_t vstore = 0;
  const int32_t *ver = version_arg(L, 6, &vstore);
  if (!lua_isnoneornil(L, 7)) {
    luaL_checktype(L, 7, LUA_TTABLE);
    lua_getfield(L, 7, "filter");
    if (lua_isinteger(L, -1))
      d.filter = (int32_t)lua_tointeger(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 7, "wrap");
    if (lua_isinteger(L, -1))
      d.wrap = (int32_t)lua_tointeger(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 7, "target");
    if (!lua_isnoneornil(L, -1))
      d.target = lua_toboolean(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 7, "storage");
    if (!lua_isnoneornil(L, -1))
      d.storage = lua_toboolean(L, -1);
    lua_pop(L, 1);
  }
  uint8_t *owned = NULL;
  if (!lua_isnoneornil(L, 5)) {
    LubBytes *b = lub_bytes_live(L, 5);
    if (b) {
      d.pixels = b->data;
      d.pixels_len = (int32_t)b->len;
    } else {
      luaL_checktype(L, 5, LUA_TTABLE);
      int n = (int)lua_rawlen(L, 5);
      owned = (uint8_t *)malloc(n > 0 ? (size_t)n : 1);
      if (!owned)
        return luaL_error(L, "use_texture: out of memory");
      for (int i = 0; i < n; ++i) {
        lua_rawgeti(L, 5, i + 1);
        int v = (int)lua_tointeger(L, -1);
        owned[i] = (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v);
        lua_pop(L, 1);
      }
      d.pixels = owned;
      d.pixels_len = n;
    }
  }
  LubHandle h = 0;
  LubStatus st = lub_gfx_use_texture(api_ctx(), key, &d, ver, &h);
  free(owned);
  if (st != LUB_OK)
    return api_raise(L);
  push_ref(L, "texture", h);
  return 1;
}

static int l_use_shader(lua_State *L) {
  LubStr key = lstr_check(L, 1);
  LubStr vs = lstr_check(L, 2);
  LubStr fs = lstr_check(L, 3);
  int32_t vstore = 0;
  const int32_t *ver = version_arg(L, 4, &vstore);
  LubHandle h = 0;
  if (lub_gfx_use_shader(api_ctx(), key, vs, fs, ver, &h) != LUB_OK)
    return api_raise(L);
  push_ref(L, "shader", h);
  return 1;
}

static int l_use_shader_compute(lua_State *L) {
  LubStr key = lstr_check(L, 1);
  LubStr cs = lstr_check(L, 2);
  int32_t vstore = 0;
  const int32_t *ver = version_arg(L, 3, &vstore);
  LubHandle h = 0;
  if (lub_gfx_use_shader_compute(api_ctx(), key, cs, ver, &h) != LUB_OK)
    return api_raise(L);
  push_ref(L, "shader", h);
  return 1;
}

static void read_clear_color(lua_State *L, int idx, float out[4]) {
  out[0] = 0;
  out[1] = 0;
  out[2] = 0;
  out[3] = 1;
  if (!lua_istable(L, idx))
    return;
  for (int i = 0; i < 4; ++i) {
    lua_rawgeti(L, idx, i + 1);
    if (lua_isnumber(L, -1))
      out[i] = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
  }
}

static int l_begin_pass(lua_State *L) {
  luaL_checktype(L, 1, LUA_TTABLE);
  LubGfxPassDesc d = {0};
  d.clear_depth = 1.0f;

  lua_getfield(L, 1, "clear_depth");
  if (lua_isnumber(L, -1))
    d.clear_depth = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);

  lua_getfield(L, 1, "load");
  if (!lua_isnoneornil(L, -1))
    d.load = (int32_t)lua_tointeger(L, -1);
  lua_pop(L, 1);

  lua_getfield(L, 1, "depth_target");
  if (!lua_isnoneornil(L, -1)) {
    d.depth_target = ref_handle(L, -1, "texture");
    if (d.depth_target == 0) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: depth_target must be a TextureRef");
    }
  }
  lua_pop(L, 1);

  lua_getfield(L, 1, "targets");
  if (lua_istable(L, -1)) {
    // MRT: { targets = {t1, ...}, clear_colors = {{r,g,b,a}, ...} }
    int n = (int)lua_rawlen(L, -1);
    if (n < 1) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: targets table is empty");
    }
    if (n > LUB_GFX_MAX_COLOR_TARGETS) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: too many targets (%d > %d)", n,
                        LUB_GFX_MAX_COLOR_TARGETS);
    }
    d.n_targets = n;
    for (int i = 0; i < n; ++i) {
      lua_rawgeti(L, -1, i + 1);
      d.targets[i] = ref_handle(L, -1, "texture");
      lua_pop(L, 1);
      if (d.targets[i] == 0) {
        lua_pop(L, 1);
        return luaL_error(L, "begin_pass: targets[%d] must be a TextureRef",
                          i + 1);
      }
      read_clear_color(L, 0, d.clear_color[i]); // 既定 {0,0,0,1}
    }
    lua_pop(L, 1);
    lua_getfield(L, 1, "clear_colors");
    if (lua_istable(L, -1)) {
      int m = (int)lua_rawlen(L, -1);
      if (m > n)
        m = n;
      for (int i = 0; i < m; ++i) {
        lua_rawgeti(L, -1, i + 1);
        if (lua_istable(L, -1))
          read_clear_color(L, lua_gettop(L), d.clear_color[i]);
        lua_pop(L, 1);
      }
    }
    lua_pop(L, 1);
  } else {
    lua_pop(L, 1);
    lua_getfield(L, 1, "target");
    if (is_sentinel(L, -1, "main_tex")) {
      d.n_targets = 1;
      d.targets[0] = LUB_GFX_MAIN_TEX;
    } else if (is_sentinel(L, -1, "texture")) {
      d.n_targets = 1;
      d.targets[0] = ref_handle(L, -1, "texture");
      if (d.targets[0] == 0) {
        lua_pop(L, 1);
        return luaL_error(L, "begin_pass: target texture not found");
      }
    } else if (lua_isnoneornil(L, -1) && d.depth_target != 0) {
      d.n_targets = 0; // depth-only
    } else {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: target must be main_tex, a color "
                           "TextureRef, or omitted for a depth-only pass");
    }
    lua_pop(L, 1);
    lua_getfield(L, 1, "clear_color");
    read_clear_color(L, lua_gettop(L), d.clear_color[0]);
    lua_pop(L, 1);
  }
  if (lub_gfx_begin_pass(api_ctx(), &d) != LUB_OK)
    return api_raise(L);
  return 0;
}

static int l_end_pass(lua_State *L) {
  if (lub_gfx_end_pass(api_ctx()) != LUB_OK)
    return api_raise(L);
  return 0;
}

// draw / dispatch の resources table を名前つきの束縛に写す。
enum {
  LUB_BIND_MAX = 16,
  LUB_UNIFORM_MAX = 64,
  LUB_UNIFORM_POOL = 4096,
};

typedef struct LubBindScratch {
  LubGfxBinding buffers[LUB_BIND_MAX];
  int32_t n_buffers;
  LubGfxBinding textures[LUB_BIND_MAX];
  int32_t n_textures;
  LubGfxUniform uniforms[LUB_UNIFORM_MAX];
  int32_t n_uniforms;
  float pool[LUB_UNIFORM_POOL];
  int32_t pool_used;
} LubBindScratch;

static void collect_bindings(lua_State *L, int res_idx, const char *fn,
                             LubBindScratch *s) {
  s->n_buffers = 0;
  s->n_textures = 0;
  s->n_uniforms = 0;
  s->pool_used = 0;
  lua_pushnil(L);
  while (lua_next(L, res_idx) != 0) {
    // lua_isstring は number も真にするので lua_type で判定する
    // (lua_tostring の in-place 変換が lua_next を壊す)。
    if (lua_type(L, -2) == LUA_TSTRING && lua_istable(L, -1)) {
      size_t nlen = 0;
      const char *name = lua_tolstring(L, -2, &nlen);
      LubStr sname = {name, (int32_t)nlen};
      if (is_sentinel(L, -1, "buffer")) {
        if (s->n_buffers >= LUB_BIND_MAX)
          luaL_error(L, "%s: too many buffers (max %d)", fn, LUB_BIND_MAX);
        s->buffers[s->n_buffers].name = sname;
        s->buffers[s->n_buffers].handle = ref_handle(L, -1, "buffer");
        s->n_buffers++;
      } else if (is_sentinel(L, -1, "texture")) {
        if (s->n_textures >= LUB_BIND_MAX)
          luaL_error(L, "%s: too many textures (max %d)", fn, LUB_BIND_MAX);
        s->textures[s->n_textures].name = sname;
        s->textures[s->n_textures].handle = ref_handle(L, -1, "texture");
        s->n_textures++;
      } else if (nlen == 8 && memcmp(name, "uniforms", 8) == 0) {
        // uniforms = { member = {floats...}, ... }
        int utab = lua_gettop(L);
        lua_pushnil(L);
        while (lua_next(L, utab) != 0) {
          if (lua_type(L, -2) == LUA_TSTRING && lua_istable(L, -1)) {
            if (s->n_uniforms >= LUB_UNIFORM_MAX)
              luaL_error(L, "%s: too many uniforms (max %d)", fn,
                         LUB_UNIFORM_MAX);
            size_t ulen = 0;
            const char *uname = lua_tolstring(L, -2, &ulen);
            int cnt = (int)lua_rawlen(L, -1);
            if (s->pool_used + cnt > LUB_UNIFORM_POOL)
              luaL_error(L, "%s: uniform data too large", fn);
            float *dst = s->pool + s->pool_used;
            for (int j = 0; j < cnt; ++j) {
              lua_rawgeti(L, -1, j + 1);
              dst[j] = lua_isnumber(L, -1) ? (float)lua_tonumber(L, -1) : 0.0f;
              lua_pop(L, 1);
            }
            LubGfxUniform *u = &s->uniforms[s->n_uniforms++];
            u->name.ptr = uname;
            u->name.len = (int32_t)ulen;
            u->values = dst;
            u->count = cnt;
            s->pool_used += cnt;
          }
          lua_pop(L, 1);
        }
      }
    }
    lua_pop(L, 1);
  }
}

static int l_draw(lua_State *L) {
  int count = (int)luaL_checkinteger(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE); // resources
  luaL_checktype(L, 3, LUA_TTABLE); // options
  LubGfxDrawDesc d = {0};
  d.vertex_count = count;
  d.depth_test = true;
  d.depth_write = true;
  d.instance_count = 1;

  lua_getfield(L, 3, "shader");
  d.shader = ref_handle(L, -1, "shader");
  lua_pop(L, 1);
  if (d.shader == 0)
    return luaL_error(L, "draw: options.shader required (ShaderRef)");
  lua_getfield(L, 3, "blend");
  if (lua_isinteger(L, -1))
    d.blend = (int32_t)lua_tointeger(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, 3, "cull");
  if (lua_isinteger(L, -1))
    d.cull = (int32_t)lua_tointeger(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, 3, "primitive");
  if (lua_isinteger(L, -1))
    d.primitive = (int32_t)lua_tointeger(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, 3, "depth");
  if (!lua_isnoneornil(L, -1))
    d.depth_test = lua_toboolean(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, 3, "depth_write");
  if (!lua_isnoneornil(L, -1))
    d.depth_write = lua_toboolean(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, 3, "instance_count");
  if (lua_isinteger(L, -1))
    d.instance_count = (int32_t)lua_tointeger(L, -1);
  lua_pop(L, 1);
  if (d.instance_count <= 0)
    return 0;

  static LubBindScratch scratch;
  collect_bindings(L, 2, "draw", &scratch);
  d.buffers = scratch.buffers;
  d.n_buffers = scratch.n_buffers;
  d.textures = scratch.textures;
  d.n_textures = scratch.n_textures;
  d.uniforms = scratch.uniforms;
  d.n_uniforms = scratch.n_uniforms;
  if (lub_gfx_draw(api_ctx(), &d) != LUB_OK)
    return api_raise(L);
  return 0;
}

static int l_dispatch(lua_State *L) {
  LubGfxDispatchDesc d = {0};
  d.groups_x = (int32_t)luaL_checkinteger(L, 1);
  d.groups_y = (int32_t)luaL_checkinteger(L, 2);
  d.groups_z = (int32_t)luaL_checkinteger(L, 3);
  luaL_checktype(L, 4, LUA_TTABLE); // resources
  luaL_checktype(L, 5, LUA_TTABLE); // options
  lua_getfield(L, 5, "shader");
  d.shader = ref_handle(L, -1, "shader");
  lua_pop(L, 1);
  if (d.shader == 0)
    return luaL_error(L, "dispatch: options.shader required (ShaderRef)");
  static LubBindScratch scratch;
  collect_bindings(L, 4, "dispatch", &scratch);
  d.buffers = scratch.buffers;
  d.n_buffers = scratch.n_buffers;
  d.textures = scratch.textures;
  d.n_textures = scratch.n_textures;
  d.uniforms = scratch.uniforms;
  d.n_uniforms = scratch.n_uniforms;
  if (lub_gfx_dispatch(api_ctx(), &d) != LUB_OK)
    return api_raise(L);
  return 0;
}

// Readback: key で宣言する runtime の readback queue (所有権の規則 3)。Lua 面は
// sentinel table { __lub_kind = "readback", key } で、id は int32 の user
// token。結果の pixel は frame 有効の Bytes view。
static LubStr readback_key(lua_State *L, int idx) {
  if (!is_sentinel(L, idx, "readback"))
    luaL_argerror(L, idx, "readback expected (readback(key))");
  lua_getfield(L, idx, "key");
  size_t n = 0;
  const char *k = lua_tolstring(L, -1, &n);
  lua_pop(L, 1); // 文字列は sentinel table が参照し続ける
  if (!k)
    luaL_argerror(L, idx, "readback key missing");
  LubStr r = {k, (int32_t)n};
  return r;
}

// read_texture(rb, tex, id) -> status, bytes, w, h, format, stride, id,
// dropped, error (9 値)
static int l_readback_read_texture(lua_State *L) {
  LubStr key = readback_key(L, 1);
  bool has_id = !lua_isnoneornil(L, 3);
  LubHandle tex = 0;
  int32_t token = 0;
  if (has_id) {
    tex = ref_handle(L, 2, "texture");
    token = (int32_t)luaL_checkinteger(L, 3);
  }
  LubGfxReadbackResult r;
  if (lub_gfx_readback(api_ctx(), key, has_id, tex, token, &r) != LUB_OK)
    return api_raise(L);
  switch (r.status) {
  case LUB_GFX_READBACK_STATUS_READY:
    lua_pushstring(L, "ready");
    lub_bytes_push(L, r.pixels.ptr, (size_t)r.pixels.len);
    lua_pushinteger(L, r.w);
    lua_pushinteger(L, r.h);
    lua_pushinteger(L, r.format);
    lua_pushinteger(L, r.stride);
    lua_pushinteger(L, r.token);
    lua_pushnil(L);
    lua_pushnil(L);
    return 9;
  case LUB_GFX_READBACK_STATUS_ERROR:
    lua_pushstring(L, "error");
    for (int i = 0; i < 5; ++i)
      lua_pushnil(L);
    lua_pushinteger(L, r.token);
    lua_pushnil(L);
    lua_pushlstring(L, r.error.ptr ? r.error.ptr : "", (size_t)r.error.len);
    return 9;
  case LUB_GFX_READBACK_STATUS_DROPPED:
    lua_pushstring(L, "dropped");
    for (int i = 0; i < 6; ++i)
      lua_pushnil(L);
    lua_pushinteger(L, r.token);
    lua_pushnil(L);
    return 9;
  default:
    lua_pushstring(L, "processing");
    for (int i = 0; i < 8; ++i)
      lua_pushnil(L);
    return 9;
  }
}

// readback(key) -> sentinel。runtime には触らない (queue は最初の
// read_texture で作られ、poll が途切れると sweep される)。
static int l_readback_new(lua_State *L) {
  LubStr key = lstr_check(L, 1);
  lua_newtable(L);
  lua_pushstring(L, "readback");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushlstring(L, key.ptr, (size_t)key.len);
  lua_setfield(L, -2, "key");
  luaL_getmetatable(L, LUB_READBACK_MT);
  lua_setmetatable(L, -2);
  return 1;
}

static void lub_readback_register(lua_State *L) {
  if (luaL_newmetatable(L, LUB_READBACK_MT)) {
    lua_newtable(L);
    lua_pushcfunction(L, l_readback_read_texture);
    lua_setfield(L, -2, "read_texture");
    lua_setfield(L, -2, "__index");
  }
  lua_pop(L, 1);
}

// ----------------------------------------------------------------------
// input / sys / profiler / config: C API への詰め替え。

static int l_key_down(lua_State *L) {
  lua_pushboolean(L, lub_input_key_down(api_ctx(), lstr_check(L, 1)));
  return 1;
}

static int l_key_pressed(lua_State *L) {
  lua_pushboolean(L, lub_input_key_pressed(api_ctx(), lstr_check(L, 1)));
  return 1;
}

static int l_key_released(lua_State *L) {
  lua_pushboolean(L, lub_input_key_released(api_ctx(), lstr_check(L, 1)));
  return 1;
}

static int mouse_button_arg(lua_State *L, const char *fn) {
  int btn = (int)luaL_optinteger(L, 1, 1);
  if (btn < 1)
    luaL_error(L, "%s: button must be >= 1 (1=left, 2=middle, 3=right)", fn);
  return btn;
}

static int l_mouse_down(lua_State *L) {
  int btn = mouse_button_arg(L, "mouse_down");
  lua_pushboolean(L, lub_input_mouse_down(api_ctx(), btn));
  return 1;
}

static int l_mouse_pressed(lua_State *L) {
  int btn = mouse_button_arg(L, "mouse_pressed");
  lua_pushboolean(L, lub_input_mouse_pressed(api_ctx(), btn));
  return 1;
}

static int l_mouse_released(lua_State *L) {
  int btn = mouse_button_arg(L, "mouse_released");
  lua_pushboolean(L, lub_input_mouse_released(api_ctx(), btn));
  return 1;
}

static int l_mouse_pos(lua_State *L) {
  float x = 0, y = 0;
  lub_input_mouse_pos(api_ctx(), &x, &y);
  lua_pushnumber(L, (lua_Number)x);
  lua_pushnumber(L, (lua_Number)y);
  return 2;
}

static int l_mouse_delta(lua_State *L) {
  float x = 0, y = 0;
  lub_input_mouse_delta(api_ctx(), &x, &y);
  lua_pushnumber(L, (lua_Number)x);
  lua_pushnumber(L, (lua_Number)y);
  return 2;
}

static int l_gfx_size(lua_State *L) {
  int32_t w = 0, h = 0;
  lub_gfx_size(api_ctx(), &w, &h);
  lua_pushinteger(L, w);
  lua_pushinteger(L, h);
  return 2;
}

static int l_actual_fps(lua_State *L) {
  lua_pushnumber(L, (lua_Number)lub_sys_actual_fps(api_ctx()));
  return 1;
}

static int l_profile_enabled(lua_State *L) {
  lua_pushboolean(L, lub_profiler_enabled(api_ctx()));
  return 1;
}

static int l_profile_begin(lua_State *L) {
  lub_profiler_begin_scope(api_ctx(), lstr_check(L, 1));
  return 0;
}

static int l_profile_end(lua_State *L) {
  LubStr name = {NULL, 0};
  if (!lua_isnoneornil(L, 1))
    name = lstr_check(L, 1);
  lub_profiler_end_scope(api_ctx(), name);
  return 0;
}

static int l_profile_reset(lua_State *L) {
  (void)L;
  lub_profiler_reset(api_ctx());
  return 0;
}

static int l_profile_report(lua_State *L) {
  LubStr label = {NULL, 0};
  if (!lua_isnoneornil(L, 1))
    label = lstr_check(L, 1);
  lub_profiler_report(api_ctx(), label);
  return 0;
}

static int32_t opt_int_field(lua_State *L, int idx, const char *key,
                             const char *fn, int32_t missing) {
  lua_getfield(L, idx, key);
  if (lua_isnoneornil(L, -1)) {
    lua_pop(L, 1);
    return missing;
  }
  if (!lua_isinteger(L, -1)) {
    lua_pop(L, 1);
    luaL_error(L, "%s: %s must be integer", fn, key);
  }
  int32_t v = (int32_t)lua_tointeger(L, -1);
  lua_pop(L, 1);
  return v;
}

static int l_config(lua_State *L) {
  luaL_checktype(L, 1, LUA_TTABLE);
  LubConfigDesc d = {0};
  d.resource_sweep_after_frames = -1;
  d.readback_depth = -1;
  lua_getfield(L, 1, "backend");
  if (lua_type(L, -1) == LUA_TSTRING) {
    size_t n = 0;
    d.backend.ptr = lua_tolstring(L, -1, &n);
    d.backend.len = (int32_t)n;
  }
  // backend 文字列は table が生きている間有効 (引数 1 の table から参照)
  lua_pop(L, 1);
  d.resource_sweep_after_frames =
      opt_int_field(L, 1, "resource_sweep_after_frames", "config", -1);
  if (d.resource_sweep_after_frames < -1)
    return luaL_error(L, "config: resource_sweep_after_frames must be >= 0");
  d.readback_depth = opt_int_field(L, 1, "readback_depth", "config", -1);
  d.width = opt_int_field(L, 1, "width", "config", 0);
  d.height = opt_int_field(L, 1, "height", "config", 0);
  if (d.width < 0 || d.height < 0)
    return luaL_error(L, "config: width/height must be positive");
  if (lub_config(api_ctx(), &d) != LUB_OK)
    return api_raise(L);
  return 0;
}

static int l_quit(lua_State *L) {
  (void)L;
  lub_quit(api_ctx());
  return 0;
}

int64_t app_file_mtime_ns(const char *path) {
  if (!path)
    return 0;
#ifdef _WIN32
  // MSVC's struct _stat64 has only seconds resolution in st_mtime.
  // sub-second changes are still caught by the content-hash fallback
  // in samples/lub_io.lua, so seconds-precision mtime is fine here.
  struct _stat64 st;
  if (_stat64(path, &st) != 0)
    return 0;
  return (int64_t)st.st_mtime * 1000000000LL;
#else
  struct stat st;
  if (stat(path, &st) != 0)
    return 0;
  return (int64_t)st.st_mtim.tv_sec * 1000000000LL +
         (int64_t)st.st_mtim.tv_nsec;
#endif
}

static int l_file_mtime(lua_State *L) {
  const char *path = luaL_checkstring(L, 1);
  int64_t ns = app_file_mtime_ns(path);
  if (ns == 0) {
    // Preserve the original binding contract: nil for "not found / error"
    // so samples/lub_io.lua's `if not mtime then return nil end` keeps
    // working unchanged.
    lua_pushnil(L);
    return 1;
  }
  lua_pushinteger(L, (lua_Integer)ns);
  return 1;
}

static int l_request_file(lua_State *L) {
  const char *path = luaL_checkstring(L, 1);
  int status = lub_io_request_file(path);
  if (status == 1) {
    lua_pushstring(L, "ready");
    return 1;
  }
  if (status == 0) {
    lua_pushstring(L, "pending");
    return 1;
  }
  lua_pushstring(L, "error");
  lua_pushstring(L, "missing");
  return 2;
}

static int l_is_web(lua_State *L) {
  lua_pushboolean(L, lub_sys_is_web(api_ctx()));
  return 1;
}

static int l_fnv1a64(lua_State *L) {
  size_t n;
  const char *str = luaL_checklstring(L, 1, &n);
  lua_pushinteger(L, (lua_Integer)lub_io_fnv1a64(str, n));
  return 1;
}

// ---------------------------------------------------------------------------
// io / png: C API への詰め替え。Lua 面は lub_io.lua / lubx_png.lua と同じ
// multi-return (本体, version, status, error)。

static const char *io_status_name(int32_t st) {
  return st == LUB_IO_STATUS_READY     ? "ready"
         : st == LUB_IO_STATUS_PENDING ? "pending"
                                       : "error";
}

// (version, status, error) を push する (本体は呼び出し側が先に push)。
static int push_io_tail(lua_State *L, const LubIoResult *r) {
  lua_pushinteger(L, (lua_Integer)r->version);
  lua_pushstring(L, io_status_name(r->status));
  if (r->error.ptr && r->error.len > 0)
    lua_pushlstring(L, r->error.ptr, (size_t)r->error.len);
  else
    lua_pushnil(L);
  return 4;
}

static int l_io_load_text(lua_State *L) {
  LubView text;
  LubIoResult r;
  lub_io_load_text(api_ctx(), lstr_check(L, 1), &text, &r);
  if (text.ptr)
    lua_pushlstring(L, (const char *)text.ptr, (size_t)text.len);
  else
    lua_pushnil(L);
  return push_io_tail(L, &r);
}

static int l_io_load_floats(lua_State *L) {
  const float *data = NULL;
  int32_t n = 0;
  LubIoResult r;
  lub_io_load_floats(api_ctx(), lstr_check(L, 1), &data, &n, &r);
  if (data) {
    lua_createtable(L, n, 0);
    for (int32_t i = 0; i < n; ++i) {
      lua_pushnumber(L, (lua_Number)data[i]);
      lua_rawseti(L, -2, i + 1);
    }
  } else {
    lua_pushnil(L);
  }
  return push_io_tail(L, &r);
}

static void push_float_table(lua_State *L, const float *v, int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    lua_pushnumber(L, (lua_Number)v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void push_material_table(lua_State *L, const LubGltfMaterial *m) {
  static const LubGltfMaterial defaults = {
      .base_color_factor = {1, 1, 1, 1},
      .metallic_factor = 1.0f,
      .roughness_factor = 1.0f,
      .alpha_cutoff = 0.5f,
      .normal_scale = 1.0f,
  };
  if (!m)
    m = &defaults;
  lua_createtable(L, 0, 11);
  push_float_table(L, m->base_color_factor, 4);
  lua_setfield(L, -2, "base_color_factor");
  lua_pushnumber(L, m->metallic_factor);
  lua_setfield(L, -2, "metallic_factor");
  lua_pushnumber(L, m->roughness_factor);
  lua_setfield(L, -2, "roughness_factor");
  lua_pushinteger(L, m->alpha_mode);
  lua_setfield(L, -2, "alpha_mode");
  lua_pushnumber(L, m->alpha_cutoff);
  lua_setfield(L, -2, "alpha_cutoff");
  lua_pushboolean(L, m->double_sided);
  lua_setfield(L, -2, "double_sided");
  lua_pushnumber(L, m->normal_scale);
  lua_setfield(L, -2, "normal_scale");
  const struct {
    const char *field;
    LubStr value;
  } paths[] = {
      {"base_color_path", m->base_color_path},
      {"metallic_roughness_path", m->metallic_roughness_path},
      {"normal_path", m->normal_path},
      {"name", m->name},
  };
  for (size_t i = 0; i < sizeof(paths) / sizeof(paths[0]); ++i) {
    if (paths[i].value.ptr && paths[i].value.len > 0) {
      lua_pushlstring(L, paths[i].value.ptr, (size_t)paths[i].value.len);
      lua_setfield(L, -2, paths[i].field);
    }
  }
}

static void push_mesh_fields(lua_State *L, const LubMeshData *m) {
  push_float_table(L, m->positions, m->vert_count * 3);
  lua_setfield(L, -2, "positions");
  if (m->normals) {
    push_float_table(L, m->normals, m->vert_count * 3);
    lua_setfield(L, -2, "normals");
  }
  if (m->uvs) {
    push_float_table(L, m->uvs, m->vert_count * 2);
    lua_setfield(L, -2, "uvs");
  }
  if (m->tangents) {
    push_float_table(L, m->tangents, m->vert_count * 4);
    lua_setfield(L, -2, "tangents");
  }
  if (m->indices) {
    lua_createtable(L, m->index_count, 0);
    for (int32_t i = 0; i < m->index_count; ++i) {
      lua_pushinteger(L, (lua_Integer)m->indices[i]);
      lua_rawseti(L, -2, i + 1);
    }
    lua_setfield(L, -2, "indices");
  }
  lua_pushinteger(L, m->vert_count);
  lua_setfield(L, -2, "vert_count");
  lua_pushinteger(L, m->index_count);
  lua_setfield(L, -2, "index_count");
}

// load_gltf の Lua table: top-level は primitives[1] の写し + primitives /
// primitive_count (samples/lub_io.lua 時代と同じ形)。
static int l_io_load_gltf(lua_State *L) {
  LubGltfView v;
  LubIoResult r;
  lub_io_load_gltf(api_ctx(), lstr_check(L, 1), &v, &r);
  if (!v.primitives || v.primitive_count <= 0) {
    lua_pushnil(L);
    return push_io_tail(L, &r);
  }
  lua_createtable(L, 0, 10);
  int result = lua_gettop(L);
  lua_createtable(L, v.primitive_count, 0);
  for (int32_t i = 0; i < v.primitive_count; ++i) {
    const LubGltfPrimitive *p = &v.primitives[i];
    lua_createtable(L, 0, 10);
    push_mesh_fields(L, &p->mesh);
    lua_pushinteger(L, p->material_index);
    lua_setfield(L, -2, "material_index");
    push_material_table(L, p->material_index >= 0 &&
                                   p->material_index < v.material_count
                               ? &v.materials[p->material_index]
                               : NULL);
    lua_setfield(L, -2, "material");
    lua_rawseti(L, -2, i + 1);
  }
  lua_setfield(L, result, "primitives");
  lua_pushinteger(L, v.primitive_count);
  lua_setfield(L, result, "primitive_count");
  static const char *const mirrored[] = {
      "positions", "normals",    "uvs",         "tangents",
      "indices",   "vert_count", "index_count", "material",
  };
  lua_getfield(L, result, "primitives");
  lua_rawgeti(L, -1, 1);
  for (size_t i = 0; i < sizeof(mirrored) / sizeof(mirrored[0]); ++i) {
    lua_getfield(L, -1, mirrored[i]);
    lua_setfield(L, result, mirrored[i]);
  }
  lua_pop(L, 2);
  return push_io_tail(L, &r);
}

// mesh table (positions / normals / ...) の配列 field を float 配列に写す。
// n_expected 個未満なら NULL 扱い (欠損)。
static float *mesh_field_floats(lua_State *L, int idx, const char *field,
                                int32_t n_expected) {
  lua_getfield(L, idx, field);
  if (!lua_istable(L, -1) || (int32_t)lua_rawlen(L, -1) < n_expected ||
      n_expected <= 0) {
    lua_pop(L, 1);
    return NULL;
  }
  float *out = (float *)malloc((size_t)n_expected * sizeof(float));
  if (out) {
    for (int32_t i = 0; i < n_expected; ++i) {
      lua_rawgeti(L, -1, i + 1);
      out[i] = (float)lua_tonumber(L, -1);
      lua_pop(L, 1);
    }
  }
  lua_pop(L, 1);
  return out;
}

static int io_interleave(lua_State *L, int32_t layout) {
  luaL_checktype(L, 1, LUA_TTABLE);
  lua_getfield(L, 1, "vert_count");
  int32_t n = (int32_t)lua_tointeger(L, -1);
  lua_pop(L, 1);
  LubMeshData m = {0};
  m.vert_count = n;
  float *pos = mesh_field_floats(L, 1, "positions", n * 3);
  float *nrm = mesh_field_floats(L, 1, "normals", n * 3);
  float *uv = mesh_field_floats(L, 1, "uvs", n * 2);
  float *tan = mesh_field_floats(L, 1, "tangents", n * 4);
  float *col = mesh_field_floats(L, 1, "colors", n * 3);
  float *mr = mesh_field_floats(L, 1, "metal_rough", n * 2);
  float *jt = mesh_field_floats(L, 1, "joints", n * 2);
  float *wt = mesh_field_floats(L, 1, "weights", n * 2);
  m.positions = pos;
  m.normals = nrm;
  m.uvs = uv;
  m.tangents = tan;
  m.colors = col;
  m.metal_rough = mr;
  m.joints = jt;
  m.weights = wt;
  int32_t need = pos ? lub_mesh_interleave(api_ctx(), &m, layout, NULL, 0) : 0;
  float *out = need > 0 ? (float *)malloc((size_t)need * sizeof(float)) : NULL;
  if (out)
    lub_mesh_interleave(api_ctx(), &m, layout, out, need);
  lua_createtable(L, need, 0);
  for (int32_t i = 0; out && i < need; ++i) {
    lua_pushnumber(L, (lua_Number)out[i]);
    lua_rawseti(L, -2, i + 1);
  }
  free(out);
  free(pos);
  free(nrm);
  free(uv);
  free(tan);
  free(col);
  free(mr);
  free(jt);
  free(wt);
  return 1;
}

static int l_io_interleave_pn(lua_State *L) {
  return io_interleave(L, LUB_MESH_LAYOUT_PN);
}
static int l_io_interleave_pnu(lua_State *L) {
  return io_interleave(L, LUB_MESH_LAYOUT_PNU);
}
static int l_io_interleave_pnut(lua_State *L) {
  return io_interleave(L, LUB_MESH_LAYOUT_PNUT);
}
static int l_io_interleave_pncm(lua_State *L) {
  return io_interleave(L, LUB_MESH_LAYOUT_PNCM);
}
static int l_io_interleave_pncmw(lua_State *L) {
  return io_interleave(L, LUB_MESH_LAYOUT_PNCMW);
}

// png_load(path) -> bytes, w, h, fmt, stride, version, status, error
static int l_png_load(lua_State *L) {
  LubView px;
  int32_t w = 0, h = 0, fmt = 0, stride = 0;
  LubIoResult r;
  lub_png_load(api_ctx(), lstr_check(L, 1), &px, &w, &h, &fmt, &stride, &r);
  if (!px.ptr) {
    for (int i = 0; i < 5; ++i)
      lua_pushnil(L);
    push_io_tail(L, &r);
    return 8;
  }
  lub_bytes_push(L, px.ptr, (size_t)px.len);
  lua_pushinteger(L, w);
  lua_pushinteger(L, h);
  lua_pushinteger(L, fmt);
  lua_pushinteger(L, stride);
  push_io_tail(L, &r);
  return 8;
}

static int l_png_write(lua_State *L) {
  LubStr path = lstr_check(L, 1);
  LubBytes *bytes = lub_bytes_check(L, 2);
  int32_t w = (int32_t)luaL_checkinteger(L, 3);
  int32_t h = (int32_t)luaL_checkinteger(L, 4);
  int32_t stride = (int32_t)luaL_optinteger(L, 5, 0);
  if (lub_png_write(api_ctx(), path, bytes->data, (int32_t)bytes->len, w, h,
                    stride) != LUB_OK)
    return api_raise(L);
  lua_pushboolean(L, 1);
  return 1;
}

// ---------------------------------------------------------------------------
// audio / host: C API への詰め替え。

// audio_snd(key, data, channels, rate, version) -> snd。data は f32 の
// Bytes / string、またはサンプル値の table (コードで波形を作る経路)。
// version が前回と同じなら data は読まない (use_texture と同じ規約)。
static int l_audio_snd(lua_State *L) {
  LubStr key = lstr_check(L, 1);
  int32_t channels = (int32_t)luaL_checkinteger(L, 3);
  int32_t rate = (int32_t)luaL_checkinteger(L, 4);
  int32_t vstore = 0;
  const int32_t *ver = version_arg(L, 5, &vstore);
  int32_t snd = 0;
  if (ver) {
    LubStatus st =
        lub_audio_snd(api_ctx(), key, NULL, 0, channels, rate, ver, &snd);
    if (st == LUB_OK) {
      lua_pushinteger(L, snd);
      return 1;
    }
    if (st == LUB_ERROR)
      return api_raise(L);
  }
  if (lua_isnoneornil(L, 2))
    return luaL_error(L, "audio_snd: data required (no snd for this version)");
  const float *pcm = NULL;
  float *tmp = NULL;
  size_t samples = 0;
  if (lua_istable(L, 2)) {
    samples = lua_rawlen(L, 2);
    tmp = (float *)malloc((samples ? samples : 1) * sizeof(float));
    if (!tmp)
      return luaL_error(L, "audio_snd: out of memory");
    for (size_t i = 0; i < samples; i++) {
      lua_rawgeti(L, 2, (lua_Integer)i + 1);
      tmp[i] = (float)lua_tonumber(L, -1);
      lua_pop(L, 1);
    }
    pcm = tmp;
  } else {
    size_t len = 0;
    const uint8_t *data = lub_bytes_arg(L, 2, &len);
    if (len % sizeof(float) != 0)
      return luaL_error(L, "audio_snd: byte length %zu is not f32-aligned",
                        len);
    pcm = (const float *)data;
    samples = len / sizeof(float);
  }
  LubStatus st = lub_audio_snd(api_ctx(), key, pcm, (int32_t)samples, channels,
                               rate, ver, &snd);
  free(tmp);
  if (st != LUB_OK)
    return api_raise(L);
  lua_pushinteger(L, snd);
  return 1;
}

// (bytes|string) -> (bytes, channels, rate) | nil。bytes は frame 有効の view。
static int l_audio_decode(lua_State *L) {
  size_t len = 0;
  const uint8_t *data = lub_bytes_arg(L, 1, &len);
  LubView pcm = {0};
  int32_t ch = 0, rate = 0;
  if (lub_audio_decode(api_ctx(), data, (int32_t)len, &pcm, &ch, &rate) !=
      LUB_OK) {
    lua_pushnil(L);
    return 1;
  }
  lub_bytes_push(L, pcm.ptr, (size_t)pcm.len);
  lua_pushinteger(L, ch);
  lua_pushinteger(L, rate);
  return 3;
}

static void audio_read_opts(lua_State *L, int idx, LubAudioPlayDesc *d) {
  d->volume = 1.0f;
  d->pitch = 1.0f;
  d->pan = 0.0f;
  d->loop = false;
  if (lua_isnoneornil(L, idx))
    return;
  luaL_checktype(L, idx, LUA_TTABLE);
  lua_getfield(L, idx, "volume");
  if (!lua_isnil(L, -1))
    d->volume = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, idx, "pitch");
  if (!lua_isnil(L, -1))
    d->pitch = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, idx, "pan");
  if (!lua_isnil(L, -1))
    d->pan = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, idx, "loop");
  d->loop = lua_toboolean(L, -1);
  lua_pop(L, 1);
}

static int l_audio_play(lua_State *L) {
  int32_t snd = (int32_t)luaL_checkinteger(L, 1);
  LubAudioPlayDesc d;
  audio_read_opts(L, 2, &d);
  lua_pushboolean(L, lub_audio_play(api_ctx(), snd, &d));
  return 1;
}

static int l_audio_voice(lua_State *L) {
  LubStr key = lstr_check(L, 1);
  int32_t snd = (int32_t)luaL_checkinteger(L, 2);
  LubAudioPlayDesc d;
  audio_read_opts(L, 3, &d);
  lua_pushboolean(L, lub_audio_voice(api_ctx(), key, snd, &d));
  return 1;
}

static int l_audio_master_volume(lua_State *L) {
  lub_audio_master_volume(api_ctx(), (float)luaL_checknumber(L, 1));
  return 0;
}

static int l_audio_info(lua_State *L) {
  LubAudioInfo info;
  lub_audio_info(api_ctx(), &info);
  lua_newtable(L);
  lua_pushboolean(L, info.device);
  lua_setfield(L, -2, "device");
  lua_pushinteger(L, info.rate);
  lua_setfield(L, -2, "rate");
  lua_pushinteger(L, info.voices);
  lua_setfield(L, -2, "voices");
  lua_pushinteger(L, info.snds);
  lua_setfield(L, -2, "snds");
  return 1;
}

static int l_host_available(lua_State *L) {
  lua_pushboolean(L, lub_host_available(api_ctx()));
  return 1;
}

static int l_host_send(lua_State *L) {
  LubStr topic = lstr_check(L, 1);
  LubStr payload = lstr_check(L, 2);
  lub_host_send(api_ctx(), topic, payload);
  return 0;
}

// host_poll() -> topic, payload。queue が空なら nil。
static int l_host_poll(lua_State *L) {
  LubView topic = {0}, payload = {0};
  if (!lub_host_poll(api_ctx(), &topic, &payload)) {
    lua_pushnil(L);
    return 1;
  }
  lua_pushlstring(L, (const char *)topic.ptr, (size_t)topic.len);
  lua_pushlstring(L, (const char *)payload.ptr, (size_t)payload.len);
  return 2;
}

// ---------------------------------------------------------------------------
// font / ui: C API への詰め替え。

static LubStr bytes_arg(lua_State *L, int idx) {
  size_t len = 0;
  const uint8_t *data = lub_bytes_arg(L, idx, &len);
  LubStr r = {(const char *)data, (int32_t)len};
  return r;
}

static int l_font_metrics(lua_State *L) {
  LubFontMetrics m;
  if (lub_font_metrics(api_ctx(), bytes_arg(L, 1), &m) != LUB_OK)
    return api_raise(L);
  lua_createtable(L, 0, 3);
  lua_pushnumber(L, m.ascent);
  lua_setfield(L, -2, "ascent");
  lua_pushnumber(L, m.descent);
  lua_setfield(L, -2, "descent");
  lua_pushnumber(L, m.line_gap);
  lua_setfield(L, -2, "line_gap");
  return 1;
}

// font_glyph(ttf, codepoint, px) -> nil | { w, h, xoff, yoff, advance, bytes }
static int l_font_glyph(lua_State *L) {
  LubStr ttf = bytes_arg(L, 1);
  int32_t cp = (int32_t)luaL_checkinteger(L, 2);
  float px = (float)luaL_checknumber(L, 3);
  LubFontGlyph g;
  if (lub_font_glyph(api_ctx(), ttf, cp, px, &g) != LUB_OK)
    return api_raise(L);
  if (!g.found) {
    lua_pushnil(L);
    return 1;
  }
  lua_createtable(L, 0, 6);
  lua_pushinteger(L, g.w);
  lua_setfield(L, -2, "w");
  lua_pushinteger(L, g.h);
  lua_setfield(L, -2, "h");
  lua_pushinteger(L, g.xoff);
  lua_setfield(L, -2, "xoff");
  lua_pushinteger(L, g.yoff);
  lua_setfield(L, -2, "yoff");
  lua_pushnumber(L, g.advance);
  lua_setfield(L, -2, "advance");
  if (g.bytes.ptr && g.bytes.len > 0) {
    // Lua string: readable from script (string.byte) so the atlas blit can
    // happen outside the core.
    lua_pushlstring(L, (const char *)g.bytes.ptr, (size_t)g.bytes.len);
    lua_setfield(L, -2, "bytes");
  }
  return 1;
}

// font_glyph_mesh(ttf, codepoint [, tolerance]) -> nil | mesh table + advance
static int l_font_glyph_mesh(lua_State *L) {
  LubStr ttf = bytes_arg(L, 1);
  int32_t cp = (int32_t)luaL_checkinteger(L, 2);
  float tol = (float)luaL_optnumber(L, 3, 0.002);
  LubFontGlyphMesh gm;
  if (lub_font_glyph_mesh(api_ctx(), ttf, cp, tol, &gm) != LUB_OK)
    return api_raise(L);
  if (!gm.found) {
    lua_pushnil(L);
    return 1;
  }
  lua_createtable(L, 0, 6);
  push_mesh_fields(L, &gm.mesh);
  lua_pushnumber(L, gm.advance);
  lua_setfield(L, -2, "advance");
  return 1;
}

static int l_font_kern(lua_State *L) {
  LubStr ttf = bytes_arg(L, 1);
  int32_t cp1 = (int32_t)luaL_checkinteger(L, 2);
  int32_t cp2 = (int32_t)luaL_checkinteger(L, 3);
  float k = 0;
  if (lub_font_kern(api_ctx(), ttf, cp1, cp2, &k) != LUB_OK)
    return api_raise(L);
  lua_pushnumber(L, k);
  return 1;
}

static int l_ui_render(lua_State *L) {
  if (lub_ui_render(api_ctx()) != LUB_OK)
    return api_raise(L);
  return 0;
}

static int l_ui_begin(lua_State *L) {
  lua_pushboolean(L, lub_ui_begin_window(api_ctx(), lstr_check(L, 1)));
  return 1;
}

static int l_ui_end(lua_State *L) {
  (void)L;
  lub_ui_end_window(api_ctx());
  return 0;
}

static int l_ui_text(lua_State *L) {
  lub_ui_text(api_ctx(), lstr_check(L, 1));
  return 0;
}

static int l_ui_button(lua_State *L) {
  lua_pushboolean(L, lub_ui_button(api_ctx(), lstr_check(L, 1)));
  return 1;
}

static int l_ui_checkbox(lua_State *L) {
  LubStr label = lstr_check(L, 1);
  bool v = lua_toboolean(L, 2) != 0;
  lua_pushboolean(L, lub_ui_checkbox(api_ctx(), label, v));
  return 1;
}

static int l_ui_slider_float(lua_State *L) {
  LubStr label = lstr_check(L, 1);
  float v = (float)luaL_checknumber(L, 2);
  float lo = (float)luaL_checknumber(L, 3);
  float hi = (float)luaL_checknumber(L, 4);
  lua_pushnumber(L, lub_ui_slider_float(api_ctx(), label, v, lo, hi));
  return 1;
}

static int l_ui_slider_int(lua_State *L) {
  LubStr label = lstr_check(L, 1);
  int32_t v = (int32_t)luaL_checkinteger(L, 2);
  int32_t lo = (int32_t)luaL_checkinteger(L, 3);
  int32_t hi = (int32_t)luaL_checkinteger(L, 4);
  lua_pushinteger(L, lub_ui_slider_int(api_ctx(), label, v, lo, hi));
  return 1;
}

static int l_ui_drag_float(lua_State *L) {
  LubStr label = lstr_check(L, 1);
  float v = (float)luaL_checknumber(L, 2);
  float speed = (float)luaL_optnumber(L, 3, 1.0);
  float lo = (float)luaL_optnumber(L, 4, 0.0);
  float hi = (float)luaL_optnumber(L, 5, 0.0);
  lua_pushnumber(L, lub_ui_drag_float(api_ctx(), label, v, speed, lo, hi));
  return 1;
}

static int l_ui_color_edit3(lua_State *L) {
  LubStr label = lstr_check(L, 1);
  float c[3] = {(float)luaL_checknumber(L, 2), (float)luaL_checknumber(L, 3),
                (float)luaL_checknumber(L, 4)};
  lub_ui_color_edit3(api_ctx(), label, c);
  lua_pushnumber(L, c[0]);
  lua_pushnumber(L, c[1]);
  lua_pushnumber(L, c[2]);
  return 3;
}

static int l_ui_separator(lua_State *L) {
  (void)L;
  lub_ui_separator(api_ctx());
  return 0;
}

static int l_ui_same_line(lua_State *L) {
  (void)L;
  lub_ui_same_line(api_ctx());
  return 0;
}

static int l_ui_tree_node(lua_State *L) {
  LubStr label = lstr_check(L, 1);
  bool def_open = lua_toboolean(L, 2) != 0;
  lua_pushboolean(L, lub_ui_tree_node(api_ctx(), label, def_open));
  return 1;
}

static int l_ui_tree_pop(lua_State *L) {
  (void)L;
  lub_ui_tree_pop(api_ctx());
  return 0;
}

static int l_ui_set_next_window(lua_State *L) {
  float x = (float)luaL_checknumber(L, 1);
  float y = (float)luaL_checknumber(L, 2);
  float w = (float)luaL_checknumber(L, 3);
  float h = (float)luaL_checknumber(L, 4);
  lub_ui_set_next_window(api_ctx(), x, y, w, h);
  return 0;
}

static int l_ui_want_capture_mouse(lua_State *L) {
  lua_pushboolean(L, lub_ui_want_capture_mouse(api_ctx()));
  return 1;
}

// ---------------------------------------------------------------------------
// mesh (surface_nets / sdf_mesh): C API への詰め替え。

// surface_nets(grid, nx, ny, nz [, cell [, ox, oy, oz]]) -> mesh
static int l_surface_nets(lua_State *L) {
  luaL_checktype(L, 1, LUA_TTABLE);
  int32_t nx = (int32_t)luaL_checkinteger(L, 2);
  int32_t ny = (int32_t)luaL_checkinteger(L, 3);
  int32_t nz = (int32_t)luaL_checkinteger(L, 4);
  float cell = (float)luaL_optnumber(L, 5, 1.0);
  float ox = (float)luaL_optnumber(L, 6, 0.0);
  float oy = (float)luaL_optnumber(L, 7, 0.0);
  float oz = (float)luaL_optnumber(L, 8, 0.0);
  if (nx < 2 || ny < 2 || nz < 2)
    return luaL_error(L, "surface_nets: grid dims must be >= 2 (got %dx%dx%d)",
                      nx, ny, nz);
  size_t total = (size_t)nx * (size_t)ny * (size_t)nz;
  if (total > (size_t)1 << 27)
    return luaL_error(L, "surface_nets: grid too large (%dx%dx%d)", nx, ny, nz);
  if (lua_rawlen(L, 1) < total)
    return luaL_error(L,
                      "surface_nets: grid has %d entries, need nx*ny*nz = %d",
                      (int)lua_rawlen(L, 1), (int)total);
  float *g = (float *)malloc(total * sizeof(float));
  if (!g)
    return luaL_error(L, "surface_nets: out of memory");
  for (size_t i = 0; i < total; ++i) {
    lua_rawgeti(L, 1, (lua_Integer)(i + 1));
    g[i] = (float)lua_tonumber(L, -1);
    lua_pop(L, 1);
  }
  LubMeshData m;
  LubStatus st =
      lub_mesh_surface_nets(api_ctx(), g, nx, ny, nz, cell, ox, oy, oz, &m);
  free(g);
  if (st != LUB_OK)
    return api_raise(L);
  lua_createtable(L, 0, 5);
  push_mesh_fields(L, &m);
  return 1;
}

// sdf の木 (table) を LubSdfNode の配列に平らにする。
typedef struct SdfFlatten {
  LubSdfNode *nodes;
  int len, cap;
  int depth;
} SdfFlatten;

static const struct {
  const char *name;
  int op;
  const char *params[8];
  int n_params;
  int n_children; // 0 / 1 (c) / 2 (a, b)
} SDF_OPS[] = {
    {"sphere", LUB_SDF_OP_SPHERE, {"r"}, 1, 0},
    {"box", LUB_SDF_OP_BOX, {"hx", "hy", "hz"}, 3, 0},
    {"capsule",
     LUB_SDF_OP_CAPSULE,
     {"ax", "ay", "az", "bx", "by", "bz", "r"},
     7,
     0},
    {"torus", LUB_SDF_OP_TORUS, {"rmajor", "rminor"}, 2, 0},
    {"move", LUB_SDF_OP_MOVE, {"x", "y", "z"}, 3, 1},
    {"rotate", LUB_SDF_OP_ROTATE, {"qx", "qy", "qz", "qw"}, 4, 1},
    {"scale", LUB_SDF_OP_SCALE, {"s"}, 1, 1},
    {"mirror_x", LUB_SDF_OP_MIRROR_X, {NULL}, 0, 1},
    {"paint", LUB_SDF_OP_PAINT, {"cr", "cg", "cb"}, 3, 1},
    {"bone", LUB_SDF_OP_BONE, {"px", "py", "pz"}, 3, 1},
    {"union", LUB_SDF_OP_UNION, {NULL}, 0, 2},
    {"smin", LUB_SDF_OP_SMIN, {"k"}, 1, 2},
    {"subtract", LUB_SDF_OP_SUBTRACT, {NULL}, 0, 2},
    {"ssub", LUB_SDF_OP_SSUB, {"k"}, 1, 2},
    {"intersect", LUB_SDF_OP_INTERSECT, {NULL}, 0, 2},
};

static float sdf_need_num(lua_State *L, int t, const char *op, const char *k) {
  lua_getfield(L, t, k);
  if (!lua_isnumber(L, -1))
    luaL_error(L, "sdf_mesh: node '%s' needs number field '%s'", op, k);
  float v = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return v;
}

static float sdf_opt_num(lua_State *L, int t, const char *k, float def) {
  lua_getfield(L, t, k);
  float v = lua_isnumber(L, -1) ? (float)lua_tonumber(L, -1) : def;
  lua_pop(L, 1);
  return v;
}

static int sdf_flatten(lua_State *L, SdfFlatten *b, int t) {
  t = lua_absindex(L, t);
  if (++b->depth > 64)
    luaL_error(L, "sdf_mesh: tree deeper than 64");
  luaL_checkstack(L, 8, "sdf_mesh");
  if (b->len >= 4096)
    luaL_error(L, "sdf_mesh: tree has more than 4096 nodes");
  if (b->len >= b->cap) {
    int cap = b->cap ? b->cap * 2 : 64;
    LubSdfNode *grown =
        (LubSdfNode *)realloc(b->nodes, (size_t)cap * sizeof(LubSdfNode));
    if (!grown)
      luaL_error(L, "sdf_mesh: out of memory");
    b->nodes = grown;
    b->cap = cap;
  }
  int ni = b->len++;
  lua_getfield(L, t, "op");
  const char *op = lua_tostring(L, -1);
  if (!op)
    luaL_error(L, "sdf_mesh: node without string field 'op'");
  int oi = -1;
  for (size_t i = 0; i < sizeof(SDF_OPS) / sizeof(SDF_OPS[0]); ++i)
    if (strcmp(SDF_OPS[i].name, op) == 0)
      oi = (int)i;
  if (oi < 0)
    luaL_error(L, "sdf_mesh: unknown op '%s'", op);
  LubSdfNode node = {0};
  node.op = SDF_OPS[oi].op;
  node.a = -1;
  node.b = -1;
  for (int i = 0; i < SDF_OPS[oi].n_params; ++i)
    node.params[i] = sdf_need_num(L, t, op, SDF_OPS[oi].params[i]);
  if (node.op == LUB_SDF_OP_PAINT) {
    node.params[3] = sdf_opt_num(L, t, "metallic", 0.0f);
    node.params[4] = sdf_opt_num(L, t, "roughness", 0.8f);
  }
  if (node.op == LUB_SDF_OP_BONE) {
    lua_getfield(L, t, "name");
    size_t nl = 0;
    const char *bn = lua_tolstring(L, -1, &nl);
    if (!bn)
      luaL_error(L, "sdf_mesh: bone needs string field 'name'");
    // 文字列は木の table が生きている間 (呼び出しの間) 有効
    node.name.ptr = bn;
    node.name.len = (int32_t)nl;
    lua_pop(L, 1);
  }
  b->nodes[ni] = node;
  if (SDF_OPS[oi].n_children == 1) {
    lua_getfield(L, t, "c");
    if (!lua_istable(L, -1))
      luaL_error(L, "sdf_mesh: node '%s' needs child table 'c'", op);
    int a = sdf_flatten(L, b, lua_gettop(L));
    lua_pop(L, 1);
    b->nodes[ni].a = a;
  } else if (SDF_OPS[oi].n_children == 2) {
    lua_getfield(L, t, "a");
    if (!lua_istable(L, -1))
      luaL_error(L, "sdf_mesh: node '%s' needs child table 'a'", op);
    int a = sdf_flatten(L, b, lua_gettop(L));
    lua_pop(L, 1);
    lua_getfield(L, t, "b");
    if (!lua_istable(L, -1))
      luaL_error(L, "sdf_mesh: node '%s' needs child table 'b'", op);
    int c = sdf_flatten(L, b, lua_gettop(L));
    lua_pop(L, 1);
    b->nodes[ni].a = a;
    b->nodes[ni].b = c;
  }
  lua_pop(L, 1); // op string (kept anchored for the error messages above)
  --b->depth;
  return ni;
}

static void push_vec3_table(lua_State *L, const float v[3]) {
  lua_createtable(L, 3, 0);
  for (int i = 0; i < 3; ++i) {
    lua_pushnumber(L, v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

// sdf_mesh(tree, n [, skin_k]) -> mesh
static void push_sdf_mesh_result(lua_State *L, const LubSdfMesh *m) {
  lua_createtable(L, 0, 12);
  push_mesh_fields(L, &m->mesh);
  push_float_table(L, m->mesh.colors, m->mesh.vert_count * 3);
  lua_setfield(L, -2, "colors");
  push_float_table(L, m->mesh.metal_rough, m->mesh.vert_count * 2);
  lua_setfield(L, -2, "metal_rough");
  if (m->bone_count > 0) {
    lua_createtable(L, m->mesh.vert_count * 2, 0);
    for (int32_t i = 0; i < m->mesh.vert_count * 2; ++i) {
      lua_pushinteger(L, (lua_Integer)m->mesh.joints[i]);
      lua_rawseti(L, -2, i + 1);
    }
    lua_setfield(L, -2, "joints");
    push_float_table(L, m->mesh.weights, m->mesh.vert_count * 2);
    lua_setfield(L, -2, "weights");
    lua_createtable(L, m->bone_count, 0);
    for (int32_t i = 0; i < m->bone_count; ++i) {
      lua_createtable(L, 0, 4);
      lua_pushlstring(L, m->bones[i].name.ptr, (size_t)m->bones[i].name.len);
      lua_setfield(L, -2, "name");
      lua_pushnumber(L, m->bones[i].pivot[0]);
      lua_setfield(L, -2, "x");
      lua_pushnumber(L, m->bones[i].pivot[1]);
      lua_setfield(L, -2, "y");
      lua_pushnumber(L, m->bones[i].pivot[2]);
      lua_setfield(L, -2, "z");
      lua_rawseti(L, -2, i + 1);
    }
    lua_setfield(L, -2, "bones");
  }
  push_vec3_table(L, m->bounds_min);
  lua_setfield(L, -2, "bounds_min");
  push_vec3_table(L, m->bounds_max);
  lua_setfield(L, -2, "bounds_max");
  lua_pushnumber(L, m->cell);
  lua_setfield(L, -2, "cell");
}

// 平らな node 配列 (C# の List<SdfNodeDesc>: { op, a, b, params, name })。
// 子の index は 0 始まり。文字列は配列が生きている間 (呼び出しの間) 有効。
static LubSdfNode *sdf_read_flat(lua_State *L, int t, int *out_count) {
  int count = (int)lua_rawlen(L, t);
  if (count <= 0 || count > 4096)
    luaL_error(L, "sdf_mesh: nodes must hold 1..4096 nodes");
  LubSdfNode *nodes = (LubSdfNode *)calloc((size_t)count, sizeof(LubSdfNode));
  if (!nodes)
    luaL_error(L, "sdf_mesh: out of memory");
  for (int i = 0; i < count; ++i) {
    lua_rawgeti(L, t, i + 1);
    if (!lua_istable(L, -1)) {
      free(nodes);
      luaL_error(L, "sdf_mesh: node %d must be a table", i);
    }
    int nt = lua_gettop(L);
    LubSdfNode *node = &nodes[i];
    lua_getfield(L, nt, "op");
    node->op = (int32_t)lua_tointeger(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, nt, "a");
    node->a = lua_isinteger(L, -1) ? (int32_t)lua_tointeger(L, -1) : -1;
    lua_pop(L, 1);
    lua_getfield(L, nt, "b");
    node->b = lua_isinteger(L, -1) ? (int32_t)lua_tointeger(L, -1) : -1;
    lua_pop(L, 1);
    lua_getfield(L, nt, "params");
    if (lua_istable(L, -1)) {
      int pn = (int)lua_rawlen(L, -1);
      if (pn > 8)
        pn = 8;
      for (int k = 0; k < pn; ++k) {
        lua_rawgeti(L, -1, k + 1);
        node->params[k] = (float)lua_tonumber(L, -1);
        lua_pop(L, 1);
      }
    }
    lua_pop(L, 1);
    lua_getfield(L, nt, "name");
    if (lua_type(L, -1) == LUA_TSTRING) {
      size_t nl = 0;
      node->name.ptr = lua_tolstring(L, -1, &nl);
      node->name.len = (int32_t)nl;
    }
    lua_pop(L, 1);
    lua_pop(L, 1);
  }
  *out_count = count;
  return nodes;
}

static int l_sdf_mesh(lua_State *L) {
  luaL_checktype(L, 1, LUA_TTABLE);
  lua_rawgeti(L, 1, 1);
  bool flat = lua_istable(L, -1);
  lua_pop(L, 1);
  LubSdfNode *nodes = NULL;
  int count = 0;
  int32_t root = 0;
  int32_t n = 0;
  float skin_k = 0.1f;
  if (flat) {
    nodes = sdf_read_flat(L, 1, &count);
    root = (int32_t)luaL_checkinteger(L, 2);
    n = (int32_t)luaL_checkinteger(L, 3);
    skin_k = (float)luaL_optnumber(L, 4, 0.1);
  } else {
    // 入れ子の木 { version = 1, root = node } (Haxe の Sdf 向け)
    n = (int32_t)luaL_checkinteger(L, 2);
    skin_k = (float)luaL_optnumber(L, 3, 0.1);
    lua_getfield(L, 1, "version");
    if (!lua_isinteger(L, -1) || lua_tointeger(L, -1) != 1)
      return luaL_error(L, "sdf_mesh: tree.version must be 1");
    lua_pop(L, 1);
    lua_getfield(L, 1, "root");
    if (!lua_istable(L, -1))
      return luaL_error(L, "sdf_mesh: tree.root must be a node table");
    // NOTE: flatten raises Lua errors on invalid trees; b.nodes leaks on that
    // path. Acceptable: authoring-time errors are rare and small.
    SdfFlatten b = {0};
    root = sdf_flatten(L, &b, -1);
    lua_pop(L, 1);
    nodes = b.nodes;
    count = b.len;
  }
  LubSdfMesh m;
  LubStatus st = lub_mesh_sdf(api_ctx(), nodes, count, root, n, skin_k, &m);
  free(nodes);
  if (st != LUB_OK)
    return api_raise(L);
  push_sdf_mesh_result(L, &m);
  return 1;
}

void lua_api_register(lua_State *L) {
  lub_bytes_register(L);
  lub_readback_register(L);
  enums_register(L);
  // main_tex は { __lub_kind = "main_tex" } という sentinel テーブル
  lua_newtable(L);
  lua_pushstring(L, "main_tex");
  lua_setfield(L, -2, "__lub_kind");
  lua_setglobal(L, "main_tex");

  lua_pushcfunction(L, l_begin_pass);
  lua_setglobal(L, "begin_pass");
  lua_pushcfunction(L, l_end_pass);
  lua_setglobal(L, "end_pass");
  lua_pushcfunction(L, l_use_buffer);
  lua_setglobal(L, "use_buffer");
  lua_pushcfunction(L, l_use_texture);
  lua_setglobal(L, "use_texture");
  lua_pushcfunction(L, l_use_shader);
  lua_setglobal(L, "use_shader");
  lua_pushcfunction(L, l_use_shader_compute);
  lua_setglobal(L, "use_shader_compute");
  lua_pushcfunction(L, l_dispatch);
  lua_setglobal(L, "dispatch");
  lua_pushcfunction(L, l_draw);
  lua_setglobal(L, "draw");
  lua_pushcfunction(L, l_readback_new);
  lua_setglobal(L, "readback");
  lua_pushcfunction(L, l_readback_read_texture);
  lua_setglobal(L, "read_texture");
  lua_pushcfunction(L, l_key_down);
  lua_setglobal(L, "key_down");
  lua_pushcfunction(L, l_mouse_delta);
  lua_setglobal(L, "mouse_delta");
  lua_pushcfunction(L, l_mouse_down);
  lua_setglobal(L, "mouse_down");
  lua_pushcfunction(L, l_mouse_pressed);
  lua_setglobal(L, "mouse_pressed");
  lua_pushcfunction(L, l_mouse_released);
  lua_setglobal(L, "mouse_released");
  lua_pushcfunction(L, l_mouse_pos);
  lua_setglobal(L, "mouse_pos");
  lua_pushcfunction(L, l_key_pressed);
  lua_setglobal(L, "key_pressed");
  lua_pushcfunction(L, l_key_released);
  lua_setglobal(L, "key_released");
  lua_pushcfunction(L, l_gfx_size);
  lua_setglobal(L, "gfx_size");
  lua_pushcfunction(L, l_actual_fps);
  lua_setglobal(L, "actual_fps");
  lua_pushcfunction(L, l_profile_enabled);
  lua_setglobal(L, "profile_enabled");
  lua_pushcfunction(L, l_profile_begin);
  lua_setglobal(L, "profile_begin");
  lua_pushcfunction(L, l_profile_end);
  lua_setglobal(L, "profile_end");
  lua_pushcfunction(L, l_profile_reset);
  lua_setglobal(L, "profile_reset");
  lua_pushcfunction(L, l_profile_report);
  lua_setglobal(L, "profile_report");
  lua_pushcfunction(L, l_config);
  lua_setglobal(L, "config");
  lua_pushcfunction(L, l_quit);
  lua_setglobal(L, "quit");
  lua_pushcfunction(L, l_file_mtime);
  lua_setglobal(L, "file_mtime");
  lua_pushcfunction(L, l_request_file);
  lua_setglobal(L, "request_file");
  lua_pushcfunction(L, l_is_web);
  lua_setglobal(L, "is_web");
  lua_pushcfunction(L, l_fnv1a64);
  lua_setglobal(L, "fnv1a64");
  lua_pushcfunction(L, l_png_load);
  lua_setglobal(L, "png_load");
  lua_pushcfunction(L, l_png_write);
  lua_setglobal(L, "png_write");
  lua_pushcfunction(L, l_audio_snd);
  lua_setglobal(L, "audio_snd");
  lua_pushcfunction(L, l_audio_decode);
  lua_setglobal(L, "audio_decode");
  lua_pushcfunction(L, l_audio_play);
  lua_setglobal(L, "audio_play");
  lua_pushcfunction(L, l_audio_voice);
  lua_setglobal(L, "audio_voice");
  lua_pushcfunction(L, l_audio_master_volume);
  lua_setglobal(L, "audio_master_volume");
  lua_pushcfunction(L, l_audio_info);
  lua_setglobal(L, "audio_info");
  lua_pushcfunction(L, l_io_load_text);
  lua_setglobal(L, "io_load_text");
  lua_pushcfunction(L, l_io_load_floats);
  lua_setglobal(L, "io_load_floats");
  lua_pushcfunction(L, l_io_load_gltf);
  lua_setglobal(L, "io_load_gltf");
  lua_pushcfunction(L, l_io_interleave_pn);
  lua_setglobal(L, "io_interleave_pn");
  lua_pushcfunction(L, l_io_interleave_pnu);
  lua_setglobal(L, "io_interleave_pnu");
  lua_pushcfunction(L, l_io_interleave_pnut);
  lua_setglobal(L, "io_interleave_pnut");
  lua_pushcfunction(L, l_io_interleave_pncm);
  lua_setglobal(L, "io_interleave_pncm");
  lua_pushcfunction(L, l_io_interleave_pncmw);
  lua_setglobal(L, "io_interleave_pncmw");
  lua_pushcfunction(L, l_surface_nets);
  lua_setglobal(L, "surface_nets");
  lua_pushcfunction(L, l_sdf_mesh);
  lua_setglobal(L, "sdf_mesh");
  lua_pushcfunction(L, l_font_metrics);
  lua_setglobal(L, "font_metrics");
  lua_pushcfunction(L, l_font_glyph);
  lua_setglobal(L, "font_glyph");
  lua_pushcfunction(L, l_font_glyph_mesh);
  lua_setglobal(L, "font_glyph_mesh");
  lua_pushcfunction(L, l_font_kern);
  lua_setglobal(L, "font_kern");
  lua_pushcfunction(L, l_host_available);
  lua_setglobal(L, "host_available");
  lua_pushcfunction(L, l_host_send);
  lua_setglobal(L, "host_send");
  lua_pushcfunction(L, l_host_poll);
  lua_setglobal(L, "host_poll");
  static const struct {
    const char *name;
    lua_CFunction fn;
  } ui_fns[] = {
      {"ui_render", l_ui_render},
      {"ui_begin", l_ui_begin},
      {"ui_end", l_ui_end},
      {"ui_text", l_ui_text},
      {"ui_button", l_ui_button},
      {"ui_checkbox", l_ui_checkbox},
      {"ui_slider_float", l_ui_slider_float},
      {"ui_slider_int", l_ui_slider_int},
      {"ui_drag_float", l_ui_drag_float},
      {"ui_color_edit3", l_ui_color_edit3},
      {"ui_separator", l_ui_separator},
      {"ui_same_line", l_ui_same_line},
      {"ui_tree_node", l_ui_tree_node},
      {"ui_tree_pop", l_ui_tree_pop},
      {"ui_set_next_window", l_ui_set_next_window},
      {"ui_want_capture_mouse", l_ui_want_capture_mouse},
  };
  for (size_t i = 0; i < sizeof(ui_fns) / sizeof(ui_fns[0]); ++i) {
    lua_pushcfunction(L, ui_fns[i].fn);
    lua_setglobal(L, ui_fns[i].name);
  }
  phys2d_lua_register(L, lub_api_ctx(g_app_for_lua));
  phys3d_lua_register(L, lub_api_ctx(g_app_for_lua));
}

static void push_event_table(lua_State *L, const SDL_Event *e) {
  lua_newtable(L);
  lua_pushinteger(L, e->type);
  lua_setfield(L, -2, "type");
  // 詳細フィールドは後段で増やす
}

// Fetches the entry module from the registry, looks up the named field, and
// pcalls it with the existing top-of-stack args. samples declare callbacks
// without `self`; C-side does not push module table as first arg.
// entry callback は snake_case (on_init 等、tcs の出力) を正とし、無ければ
// camelCase (onInit 等、Haxe の出力) を引く。Haxe 撤去までの両対応。
static void call_module_field(LuaCtx *ctx, const char *name,
                              const char *legacy_name, int nargs) {
  lua_State *L = ctx->L;
  /* stack on entry: [..., arg1, arg2, ...] (nargs items on top) */
  if (ctx->module_ref == LUA_NOREF) {
    lua_pop(L, nargs);
    return;
  }
  lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->module_ref); /* +1: module */
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1 + nargs);
    return;
  }
  lua_getfield(L, -1, name); /* +1: fn */
  if (!lua_isfunction(L, -1) && legacy_name) {
    lua_pop(L, 1);
    lua_getfield(L, -1, legacy_name); /* +1: fn */
  }
  if (!lua_isfunction(L, -1)) {
    lua_pop(L, 2 + nargs);
    return;
  }
  lua_remove(L, -2); /* drop module: stack [..., args, fn] */
  if (nargs > 0)
    lua_insert(L, -1 - nargs); /* move fn before args */
  if (lua_pcall(L, nargs, 0, 0) != LUA_OK) {
    SDL_Log("lua error in %s: %s", name, lua_tostring(L, -1));
    lua_pop(L, 1);
  }
}

bool lua_ctx_init(LuaCtx *ctx, App *app) {
  g_app_for_lua = app;
  ctx->L = luaL_newstate();
  if (!ctx->L) {
    SDL_Log("luaL_newstate failed (out of memory)");
    return false;
  }
  ctx->module_ref = LUA_NOREF;
  luaL_openlibs(ctx->L);
  lua_api_register(ctx->L);
  return true;
}

// boot.lua を cwd 優先で探し、無ければ実行ファイル位置から lub root
// (<exe_dir>/..) を推定して探す。見つけた root は boot.lua へ渡し、
// lume / samples の package.path を root 基準で組めるようにする
// (cwd が lub root 以外でも .lua 直パス entry を動かすため)。
static bool load_boot_chunk(lua_State *L, char *root, size_t rootsz) {
  root[0] = '\0';
  if (luaL_loadfile(L, "samples/boot.lua") == LUA_OK)
    return true;
  lua_pop(L, 1);
#ifndef __EMSCRIPTEN__
  const char *base_path = SDL_GetBasePath();
  if (base_path) {
    char dir[768];
    SDL_strlcpy(dir, base_path, sizeof(dir));
    size_t n = SDL_strlen(dir);
    while (n > 0 && (dir[n - 1] == '/' || dir[n - 1] == '\\'))
      dir[--n] = '\0';
    char *cut = SDL_strrchr(dir, '/');
    char *cut_bs = SDL_strrchr(dir, '\\');
    if (cut_bs && (!cut || cut_bs > cut))
      cut = cut_bs;
    if (cut && cut > dir) {
      *cut = '\0';
      char bootpath[900];
      SDL_snprintf(bootpath, sizeof(bootpath), "%s/samples/boot.lua", dir);
      if (luaL_loadfile(L, bootpath) == LUA_OK) {
        SDL_strlcpy(root, dir, rootsz);
        return true;
      }
      lua_pop(L, 1);
    }
  }
#endif
  SDL_Log("boot.lua not found: tried samples/boot.lua and "
          "<exe>/../samples/boot.lua");
  return false;
}

bool lua_ctx_load_entry(LuaCtx *ctx, const char *entry_module_name) {
  if (!ctx || !ctx->L || !entry_module_name)
    return false;
  char root[768];
  if (!load_boot_chunk(ctx->L, root, sizeof(root))) {
    lua_close(ctx->L);
    ctx->L = NULL;
    return false;
  }
  lua_pushstring(ctx->L, entry_module_name);
  lua_pushstring(ctx->L, root);
  if (lua_pcall(ctx->L, 2, 1, 0) != LUA_OK) {
    SDL_Log("boot.lua run error: %s", lua_tostring(ctx->L, -1));
    lua_close(ctx->L);
    ctx->L = NULL;
    return false;
  }
  if (!lua_istable(ctx->L, -1)) {
    SDL_Log("boot.lua did not return a module table");
    lua_close(ctx->L);
    ctx->L = NULL;
    return false;
  }
  ctx->module_ref = luaL_ref(ctx->L, LUA_REGISTRYINDEX);
  return true;
}

static void package_path_prepend(LuaCtx *ctx, const char *pattern) {
  lua_State *L = ctx->L;
  lua_getglobal(L, "package"); /* +1 */
  lua_getfield(L, -1, "path"); /* +1 */
  const char *cur = lua_tostring(L, -1);
  char buf[1024];
  SDL_snprintf(buf, sizeof(buf), "%s;%s", pattern, cur ? cur : "");
  lua_pop(L, 1); /* drop old path */
  lua_pushstring(L, buf);
  lua_setfield(L, -2, "path"); /* set package.path */
  lua_pop(L, 1);               /* drop package */
}

void lua_ctx_add_package_path(LuaCtx *ctx, const char *entry_dir) {
  if (!ctx || !ctx->L || !entry_dir)
    return;
  char pat[1000];
  SDL_snprintf(pat, sizeof(pat), "%s/.lub/?.lua", entry_dir);
  package_path_prepend(ctx, pat);
}

void lua_ctx_add_package_dir(LuaCtx *ctx, const char *dir) {
  if (!ctx || !ctx->L || !dir)
    return;
  char pat[1000];
  SDL_snprintf(pat, sizeof(pat), "%s/?.lua", dir);
  package_path_prepend(ctx, pat);
}

void lua_ctx_call_init(LuaCtx *ctx) {
  if (!ctx->L)
    return;
  call_module_field(ctx, "on_init", "onInit", 0);
}
void lua_ctx_call_frame(LuaCtx *ctx, double dt) {
  if (!ctx->L)
    return;
  // onFrame(dt): dt は直近フレームの実測秒。引数なしの既存 onFrame() は
  // Lua が余分な引数を無視するのでそのまま動く。
  lua_pushnumber(ctx->L, dt);
  call_module_field(ctx, "on_frame", "onFrame", 1);
}
void lua_ctx_call_quit(LuaCtx *ctx) {
  if (!ctx->L)
    return;
  call_module_field(ctx, "on_quit", "onQuit", 0);
}

void lua_ctx_call_event(LuaCtx *ctx, const SDL_Event *e) {
  if (!ctx->L)
    return;
  push_event_table(ctx->L, e);
  call_module_field(ctx, "on_event", "onEvent", 1);
}

// Stack discipline:
//   entry:                       [...]
//   getglobal "lume":            [..., lume]                  pop 1 on bail
//   getfield "hotswap":          [..., lume, fn]              pop 2 on bail
//   push module_name:            [..., lume, fn, name]
//   pcall(1, 2) on error:        [..., lume, err]             pop 2 on bail
//   pcall(1, 2) on success:      [..., lume, ret, err_or_nil]
//     if ret is a table:         re-ref, pop 2 (ret + err), pop 1 (lume)
//     else (nil + err):          log err, pop 2, pop 1 (lume)
bool lua_ctx_hotswap(LuaCtx *ctx, const char *module_name) {
  if (!ctx || !ctx->L || !module_name)
    return false;
  lua_State *L = ctx->L;
  lua_getglobal(L, "lume");
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return false;
  }
  lua_getfield(L, -1, "hotswap");
  if (!lua_isfunction(L, -1)) {
    lua_pop(L, 2);
    return false;
  }
  lua_pushstring(L, module_name);
  // lume.hotswap returns (oldmod) on success or (nil, err) on failure.
  // Request 2 results so both branches are uniform on the stack.
  if (lua_pcall(L, 1, 2, 0) != LUA_OK) {
    SDL_Log("hotswap pcall failed: %s", lua_tostring(L, -1));
    lua_pop(L, 2); // err + lume
    return false;
  }
  // stack: [..., lume, ret, err_or_nil]
  bool ok = false;
  if (lua_istable(L, -2)) {
    // Success path. lume.hotswap mutates the existing module table in
    // place, so ctx->module_ref already points at the updated table.
    // Re-ref the returned value anyway in case a future lume version
    // returns a freshly-required table instead.
    lua_pop(L, 1); // drop err_or_nil
    luaL_unref(L, LUA_REGISTRYINDEX, ctx->module_ref);
    ctx->module_ref = luaL_ref(L, LUA_REGISTRYINDEX); // pops the table
    ok = true;
  } else {
    // Failure path: lume returned (nil, err). Log and keep the old module.
    const char *err = lua_isstring(L, -1) ? lua_tostring(L, -1) : "<unknown>";
    SDL_Log("hotswap failed for module '%s': %s", module_name, err);
    lua_pop(L, 2); // err + nil
  }
  lua_pop(L, 1); // lume
  return ok;
}

void lua_ctx_shutdown(LuaCtx *ctx) {
  if (ctx->L) {
    if (ctx->module_ref != LUA_NOREF) {
      luaL_unref(ctx->L, LUA_REGISTRYINDEX, ctx->module_ref);
      ctx->module_ref = LUA_NOREF;
    }
    lua_close(ctx->L);
  }
  ctx->L = NULL;
}
