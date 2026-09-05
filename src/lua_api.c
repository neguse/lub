#include "lua_api.h"
#include "api_internal.h"
#include "app.h"
#include "backend.h"
#include "enums.h"
#include "enums_lua.h"
#include "font.h"
#include "gltf.h"
#include "host.h"
#include "pass.h"
#include "physics_box2d.h"
#include "physics_box3d.h"
#include "pipeline.h"
#include "resources.h"
#include "sdf.h"
#include "shader.h"
#include "stb_image.h"
#include "stb_image_write.h"
#include "surfacenets.h"
#include "ui.h"
#include <SDL3/SDL.h>
#ifdef __EMSCRIPTEN__
#include <emscripten/emscripten.h>
#endif
#include <ctype.h>
#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

static App *g_app_for_lua = NULL;

typedef struct LubBytes {
  uint8_t *data;
  size_t len;
} LubBytes;

#define LUB_BYTES_MT "lub.Bytes"
#define LUB_READBACK_MT "lub.Readback"
#define LUB_READBACK_MAX_DEPTH 32

static int is_sentinel(lua_State *L, int idx, const char *kind);

static LubBytes *lub_bytes_test(lua_State *L, int idx) {
  return (LubBytes *)luaL_testudata(L, idx, LUB_BYTES_MT);
}

static LubBytes *lub_bytes_check(lua_State *L, int idx) {
  return (LubBytes *)luaL_checkudata(L, idx, LUB_BYTES_MT);
}

static void lub_bytes_push(lua_State *L, uint8_t *data, size_t len) {
  LubBytes *b = (LubBytes *)lua_newuserdatauv(L, sizeof(LubBytes), 0);
  b->data = data;
  b->len = len;
  luaL_getmetatable(L, LUB_BYTES_MT);
  lua_setmetatable(L, -2);
}

const uint8_t *lub_bytes_arg(lua_State *L, int idx, size_t *len) {
  LubBytes *b = lub_bytes_test(L, idx);
  if (b) {
    *len = b->len;
    return b->data;
  }
  return (const uint8_t *)luaL_checklstring(L, idx, len);
}

static int l_bytes_gc(lua_State *L) {
  LubBytes *b = lub_bytes_check(L, 1);
  if (b->data) {
    free(b->data);
    b->data = NULL;
  }
  b->len = 0;
  return 0;
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
    lua_pushcfunction(L, l_bytes_gc);
    lua_setfield(L, -2, "__gc");
    lua_pushcfunction(L, l_bytes_len);
    lua_setfield(L, -2, "__len");
    lua_pushcfunction(L, l_bytes_index);
    lua_setfield(L, -2, "__index");
  }
  lua_pop(L, 1);
}

#ifdef __EMSCRIPTEN__
enum {
  LUB_FILE_PENDING = 0,
  LUB_FILE_READY = 1,
  LUB_FILE_ERROR = 2,
};

// EM_JS bodies are JavaScript (see shader.cpp's bridge note).
// clang-format off
EM_JS(int, lub_web_request_file_js, (const char *c_path), {
  var raw = UTF8ToString(c_path);
  if (!raw || raw.length == 0)
    return 2;

  var path = raw.charAt(0) == "/" ? raw.substring(1) : raw;
  var fs = Module["FS"];
  if (!fs && typeof FS != "undefined")
    fs = FS;
  if (!fs)
    return 2;

  try {
    fs.stat(path);
    return 1;
  }
  catch(e) {}

  var requests = Module["__lubFileRequests"];
  if (!requests)
    requests = Module["__lubFileRequests"] = Object.create(null);

  var req = requests[path];
  if (req)
    return req.status;

  req = requests[path] = {status : 0};
  fetch("/" + path)
      .then(function(response) {
        if (!response.ok)
          throw new Error(response.status + " " + response.statusText);
        return response.arrayBuffer();
      })
      .then(function(buffer) {
        var parts = path.split("/");
        var cur = "";
        for (var i = 0; i < parts.length - 1; ++i) {
          if (!parts[i])
            continue;
          cur = cur ? cur + "/" + parts[i] : parts[i];
          try { fs.mkdir(cur); }
          catch(e) {}
        }
        try { fs.unlink(path); }
        catch(e) {}
        fs.writeFile(path, new Uint8Array(buffer));
        req.status = 1;
      })
      .catch(function(e) {
        req.status = 2;
        req.error = String((e && e.message) || e);
        if (typeof console != "undefined" && console.error)
          console.error("[lub] request_file failed:", path, req.error);
      });
  return 0;
})
// clang-format on
#endif

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
    LubBytes *b = lub_bytes_test(L, 5);
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

// Readback: runtime の readback queue (key で宣言) の上に、Lua の id 値を
// token に結ぶ小さな表を載せる。
typedef struct LubReadback {
  char key[32];
  int32_t next_token;
  int id_refs[LUB_READBACK_MAX_DEPTH];
} LubReadback;

static int g_readback_serial = 0;

static LubReadback *lub_readback_check(lua_State *L, int idx) {
  return (LubReadback *)luaL_checkudata(L, idx, LUB_READBACK_MT);
}

static int rb_slot(int32_t token) {
  return (int)((uint32_t)token % LUB_READBACK_MAX_DEPTH);
}

static void rb_push_id(lua_State *L, LubReadback *rb, int32_t token) {
  int slot = rb_slot(token);
  if (rb->id_refs[slot] != LUA_NOREF) {
    lua_rawgeti(L, LUA_REGISTRYINDEX, rb->id_refs[slot]);
    luaL_unref(L, LUA_REGISTRYINDEX, rb->id_refs[slot]);
    rb->id_refs[slot] = LUA_NOREF;
  } else {
    lua_pushnil(L);
  }
}

// read_texture(rb, tex, id) -> status, bytes, w, h, format, stride, id,
// dropped, error (9 値)
static int l_readback_read_texture(lua_State *L) {
  LubReadback *rb = lub_readback_check(L, 1);
  bool has_id = !lua_isnoneornil(L, 3);
  LubHandle tex = 0;
  int32_t token = 0;
  if (has_id) {
    tex = ref_handle(L, 2, "texture");
    token = rb->next_token++;
    int slot = rb_slot(token);
    if (rb->id_refs[slot] != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, rb->id_refs[slot]);
    lua_pushvalue(L, 3);
    rb->id_refs[slot] = luaL_ref(L, LUA_REGISTRYINDEX);
  }
  LubGfxReadbackResult r;
  LubStr key = lub_str_c(rb->key);
  if (lub_gfx_readback(api_ctx(), key, has_id, tex, token, &r) != LUB_OK)
    return api_raise(L);
  switch (r.status) {
  case LUB_GFX_READBACK_STATUS_READY: {
    lua_pushstring(L, "ready");
    // view を Bytes (所有) に写す。frame を跨いで持てる従来の契約を保つ。
    uint8_t *copy =
        (uint8_t *)malloc(r.pixels.len > 0 ? (size_t)r.pixels.len : 1);
    if (!copy)
      return luaL_error(L, "read_texture: out of memory");
    if (r.pixels.len > 0)
      memcpy(copy, r.pixels.ptr, (size_t)r.pixels.len);
    lub_bytes_push(L, copy, (size_t)r.pixels.len);
    lua_pushinteger(L, r.w);
    lua_pushinteger(L, r.h);
    lua_pushinteger(L, r.format);
    lua_pushinteger(L, r.stride);
    rb_push_id(L, rb, r.token);
    lua_pushnil(L);
    lua_pushnil(L);
    return 9;
  }
  case LUB_GFX_READBACK_STATUS_ERROR:
    lua_pushstring(L, "error");
    for (int i = 0; i < 5; ++i)
      lua_pushnil(L);
    rb_push_id(L, rb, r.token);
    lua_pushnil(L);
    lua_pushlstring(L, r.error.ptr ? r.error.ptr : "", (size_t)r.error.len);
    return 9;
  case LUB_GFX_READBACK_STATUS_DROPPED:
    lua_pushstring(L, "dropped");
    for (int i = 0; i < 6; ++i)
      lua_pushnil(L);
    rb_push_id(L, rb, r.token);
    lua_pushnil(L);
    return 9;
  default:
    lua_pushstring(L, "processing");
    for (int i = 0; i < 8; ++i)
      lua_pushnil(L);
    return 9;
  }
}

static int l_readback_gc(lua_State *L) {
  LubReadback *rb = lub_readback_check(L, 1);
  for (int i = 0; i < LUB_READBACK_MAX_DEPTH; ++i) {
    if (rb->id_refs[i] != LUA_NOREF)
      luaL_unref(L, LUA_REGISTRYINDEX, rb->id_refs[i]);
    rb->id_refs[i] = LUA_NOREF;
  }
  return 0;
}

static int l_readback_new(lua_State *L) {
  LubReadback *rb = (LubReadback *)lua_newuserdatauv(L, sizeof(LubReadback), 0);
  SDL_snprintf(rb->key, sizeof(rb->key), "lua#%d", ++g_readback_serial);
  rb->next_token = 1;
  for (int i = 0; i < LUB_READBACK_MAX_DEPTH; ++i)
    rb->id_refs[i] = LUA_NOREF;
  luaL_getmetatable(L, LUB_READBACK_MT);
  lua_setmetatable(L, -2);
  return 1;
}

static void lub_readback_register(lua_State *L) {
  if (luaL_newmetatable(L, LUB_READBACK_MT)) {
    lua_pushcfunction(L, l_readback_gc);
    lua_setfield(L, -2, "__gc");
    lua_newtable(L);
    lua_pushcfunction(L, l_readback_read_texture);
    lua_setfield(L, -2, "read_texture");
    lua_setfield(L, -2, "__index");
  }
  lua_pop(L, 1);
}

static SDL_Scancode scancode_from_name(const char *name) {
  if (!name || !name[0])
    return SDL_SCANCODE_UNKNOWN;

  char key[32];
  size_t n = strlen(name);
  if (n >= sizeof(key))
    n = sizeof(key) - 1;
  for (size_t i = 0; i < n; ++i) {
    key[i] = (char)tolower((unsigned char)name[i]);
  }
  key[n] = '\0';

  if (n == 1) {
    if (key[0] >= 'a' && key[0] <= 'z') {
      return (SDL_Scancode)(SDL_SCANCODE_A + (key[0] - 'a'));
    }
    if (key[0] >= '1' && key[0] <= '9') {
      return (SDL_Scancode)(SDL_SCANCODE_1 + (key[0] - '1'));
    }
    if (key[0] == '0')
      return SDL_SCANCODE_0;
  }

  if (strcmp(key, "left") == 0 || strcmp(key, "arrowleft") == 0) {
    return SDL_SCANCODE_LEFT;
  }
  if (strcmp(key, "right") == 0 || strcmp(key, "arrowright") == 0) {
    return SDL_SCANCODE_RIGHT;
  }
  if (strcmp(key, "up") == 0 || strcmp(key, "arrowup") == 0) {
    return SDL_SCANCODE_UP;
  }
  if (strcmp(key, "down") == 0 || strcmp(key, "arrowdown") == 0) {
    return SDL_SCANCODE_DOWN;
  }
  if (strcmp(key, "space") == 0 || strcmp(key, "spacebar") == 0) {
    return SDL_SCANCODE_SPACE;
  }
  if (strcmp(key, "enter") == 0 || strcmp(key, "return") == 0) {
    return SDL_SCANCODE_RETURN;
  }
  if (strcmp(key, "escape") == 0 || strcmp(key, "esc") == 0) {
    return SDL_SCANCODE_ESCAPE;
  }
  if (strcmp(key, "tab") == 0)
    return SDL_SCANCODE_TAB;
  if (strcmp(key, "backspace") == 0)
    return SDL_SCANCODE_BACKSPACE;
  return SDL_SCANCODE_UNKNOWN;
}

static int l_key_down(lua_State *L) {
  const char *name = luaL_checkstring(L, 1);
  SDL_Scancode sc = scancode_from_name(name);
  if (sc == SDL_SCANCODE_UNKNOWN) {
    lua_pushboolean(L, 0);
    return 1;
  }

  int key_count = 0;
  const bool *state = SDL_GetKeyboardState(&key_count);
  lua_pushboolean(L, state && sc >= 0 && sc < key_count && state[sc]);
  return 1;
}

// mouse_delta() -> dx, dy : relative motion (window pixels) accumulated over
// the current frame. Idempotent within a frame; the latch is cleared by the
// runtime after onFrame.
static int l_mouse_delta(lua_State *L) {
  lua_pushnumber(L, (lua_Number)g_app_for_lua->mouse_rel_x);
  lua_pushnumber(L, (lua_Number)g_app_for_lua->mouse_rel_y);
  return 2;
}

static int check_mouse_button(lua_State *L, const char *fn) {
  int btn = (int)luaL_optinteger(L, 1, 1);
  if (btn < 1)
    luaL_error(L, "%s: button must be >= 1 (1=left, 2=middle, 3=right)", fn);
  return btn;
}

// mouse_down(button) -> bool. button: 1=left (default), 2=middle, 3=right.
static int l_mouse_down(lua_State *L) {
  int btn = check_mouse_button(L, "mouse_down");
  SDL_MouseButtonFlags mask = SDL_GetMouseState(NULL, NULL);
  lua_pushboolean(L, (mask & SDL_BUTTON_MASK(btn)) != 0);
  return 1;
}

// mouse_pressed(button) -> bool : pressed during the current frame (latched).
static int l_mouse_pressed(lua_State *L) {
  int btn = check_mouse_button(L, "mouse_pressed");
  lua_pushboolean(
      L, (g_app_for_lua->mouse_pressed_mask & SDL_BUTTON_MASK(btn)) != 0);
  return 1;
}

// mouse_released(button) -> bool : released during the current frame
// (latched).
static int l_mouse_released(lua_State *L) {
  int btn = check_mouse_button(L, "mouse_released");
  lua_pushboolean(
      L, (g_app_for_lua->mouse_released_mask & SDL_BUTTON_MASK(btn)) != 0);
  return 1;
}

// mouse_pos() -> x, y : absolute cursor position in window pixels.
static int l_mouse_pos(lua_State *L) {
  float x = 0.0f, y = 0.0f;
  SDL_GetMouseState(&x, &y);
  lua_pushnumber(L, (lua_Number)x);
  lua_pushnumber(L, (lua_Number)y);
  return 2;
}

// key_pressed(name) -> bool : pressed during the current frame (latched, so
// a press shorter than one frame is still observed).
static int l_key_pressed(lua_State *L) {
  const char *name = luaL_checkstring(L, 1);
  SDL_Scancode sc = scancode_from_name(name);
  lua_pushboolean(L, sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
                         g_app_for_lua->key_pressed[sc]);
  return 1;
}

// key_released(name) -> bool : released during the current frame (latched).
static int l_key_released(lua_State *L) {
  const char *name = luaL_checkstring(L, 1);
  SDL_Scancode sc = scancode_from_name(name);
  lua_pushboolean(L, sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
                         g_app_for_lua->key_released[sc]);
  return 1;
}

// gfx_size() -> w, h : current drawable size in pixels (the swapchain /
// canvas). Lets a sample size its offscreen render targets to the real output
// so it can render at a chosen resolution (e.g. smaller for weak devices).
static int l_gfx_size(lua_State *L) {
  int32_t w = 0, h = 0;
  lub_gfx_size(api_ctx(), &w, &h);
  lua_pushinteger(L, w);
  lua_pushinteger(L, h);
  return 2;
}

// actual_fps() -> measured frames per second, updated about once per second
// after the backend presents the frame.
static int l_actual_fps(lua_State *L) {
  double fps = g_app_for_lua ? g_app_for_lua->actual_fps : 0.0;
  lua_pushnumber(L, (lua_Number)fps);
  return 1;
}

static int l_profile_enabled(lua_State *L) {
  lua_pushboolean(L, g_app_for_lua && g_app_for_lua->profile.enabled);
  return 1;
}

static int l_profile_begin(lua_State *L) {
  const char *name = luaL_checkstring(L, 1);
  if (g_app_for_lua)
    profile_begin_scope(&g_app_for_lua->profile, name);
  return 0;
}

static int l_profile_end(lua_State *L) {
  const char *name = luaL_optstring(L, 1, NULL);
  if (g_app_for_lua)
    profile_end_scope(&g_app_for_lua->profile, name);
  return 0;
}

static int l_profile_reset(lua_State *L) {
  (void)L;
  if (g_app_for_lua)
    profile_reset(&g_app_for_lua->profile);
  return 0;
}

static int l_profile_report(lua_State *L) {
  const char *label = luaL_optstring(L, 1, "manual");
  if (g_app_for_lua)
    profile_report(&g_app_for_lua->profile, label);
  return 0;
}

static int l_config(lua_State *L) {
  if (g_app_for_lua->phase != APP_PHASE_PRE_BACKEND) {
    return luaL_error(L, "config: must be called inside onInit");
  }
  luaL_checktype(L, 1, LUA_TTABLE);
  lua_getfield(L, 1, "backend");
  const char *name =
      (lua_type(L, -1) == LUA_TSTRING) ? lua_tostring(L, -1) : NULL;
  // "native" = そのプラットフォームの最短距離実装
  // (Windows: D3D12 / web: webgpu / Linux: 当面 sdlgpu が代行)。
#ifdef __EMSCRIPTEN__
  // WASM: backend は webgpu 一択なので指定を無視する。
  name = "webgpu";
#else
  if (!name)
    name = "native";
  if (strcmp(name, "sdlgpu") != 0 && strcmp(name, "native") != 0) {
    return luaL_error(
        L, "config: backend must be 'native' or 'sdlgpu', got '%s'", name);
  }
#endif
  strncpy(g_app_for_lua->backend_name, name,
          sizeof(g_app_for_lua->backend_name) - 1);
  g_app_for_lua->backend_name[sizeof(g_app_for_lua->backend_name) - 1] = '\0';
  lua_pop(L, 1);

  lua_getfield(L, 1, "resource_sweep_after_frames");
  if (!lua_isnoneornil(L, -1)) {
    if (!lua_isinteger(L, -1)) {
      lua_pop(L, 1);
      return luaL_error(L,
                        "config: resource_sweep_after_frames must be integer");
    }
    lua_Integer v = lua_tointeger(L, -1);
    if (v < 0) {
      lua_pop(L, 1);
      return luaL_error(L, "config: resource_sweep_after_frames must be >= 0");
    }
    g_app_for_lua->resource_sweep_after_frames = (int)v;
  }
  lua_pop(L, 1);

  lua_getfield(L, 1, "readback_depth");
  if (!lua_isnoneornil(L, -1)) {
    if (!lua_isinteger(L, -1)) {
      lua_pop(L, 1);
      return luaL_error(L, "config: readback_depth must be integer");
    }
    lua_Integer v = lua_tointeger(L, -1);
    if (v < 1 || v > LUB_READBACK_MAX_DEPTH) {
      lua_pop(L, 1);
      return luaL_error(L, "config: readback_depth out of range (1..%d)",
                        LUB_READBACK_MAX_DEPTH);
    }
    g_app_for_lua->readback_depth = (int)v;
  }
  lua_pop(L, 1);

  lua_getfield(L, 1, "width");
  if (!lua_isnoneornil(L, -1)) {
    if (!lua_isinteger(L, -1)) {
      lua_pop(L, 1);
      return luaL_error(L, "config: width must be integer");
    }
    lua_Integer w = lua_tointeger(L, -1);
    if (w <= 0 || w > 32767) {
      lua_pop(L, 1);
      return luaL_error(L, "config: width out of range (1..32767)");
    }
    g_app_for_lua->cfg_w = (int)w;
  }
  lua_pop(L, 1);

  lua_getfield(L, 1, "height");
  if (!lua_isnoneornil(L, -1)) {
    if (!lua_isinteger(L, -1)) {
      lua_pop(L, 1);
      return luaL_error(L, "config: height must be integer");
    }
    lua_Integer h = lua_tointeger(L, -1);
    if (h <= 0 || h > 32767) {
      lua_pop(L, 1);
      return luaL_error(L, "config: height out of range (1..32767)");
    }
    g_app_for_lua->cfg_h = (int)h;
  }
  lua_pop(L, 1);
  if ((g_app_for_lua->cfg_w == 0) != (g_app_for_lua->cfg_h == 0)) {
    return luaL_error(
        L, "config: width and height must both be specified or neither");
  }
  return 0;
}

static int l_quit(lua_State *L) {
  (void)L;
  if (g_app_for_lua)
    g_app_for_lua->quit_requested = true;
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
#ifdef __EMSCRIPTEN__
  int status = lub_web_request_file_js(path);
  if (status == LUB_FILE_READY) {
    lua_pushstring(L, "ready");
    return 1;
  }
  if (status == LUB_FILE_PENDING) {
    lua_pushstring(L, "pending");
    return 1;
  }
#else
  if (app_file_mtime_ns(path) != 0) {
    lua_pushstring(L, "ready");
    return 1;
  }
#endif
  lua_pushstring(L, "error");
  lua_pushstring(L, "missing");
  return 2;
}

static int l_is_web(lua_State *L) {
#ifdef __EMSCRIPTEN__
  lua_pushboolean(L, 1);
#else
  lua_pushboolean(L, 0);
#endif
  return 1;
}

static int l_fnv1a64(lua_State *L) {
  size_t n;
  const char *s = luaL_checklstring(L, 1, &n);
  uint64_t h = 0xcbf29ce484222325ULL; // FNV offset basis
  for (size_t i = 0; i < n; ++i) {
    h ^= (unsigned char)s[i];
    h *= 0x100000001b3ULL; // FNV prime
  }
  lua_pushinteger(L, (lua_Integer)h); // Lua 5.5 integers are 64-bit signed
  return 1;
}

static int l_png_load(lua_State *L) {
  const char *path = luaL_checkstring(L, 1);
  int w, h;
  unsigned char *pixels = stbi_load(path, &w, &h, NULL, STBI_rgb_alpha);
  if (!pixels) {
    SDL_Log("png_load: %s: %s", path, stbi_failure_reason());
    lua_pushnil(L);
    return 1;
  }
  size_t n = (size_t)w * (size_t)h * STBI_rgb_alpha;
  lub_bytes_push(L, pixels, n);
  lua_pushinteger(L, w);
  lua_pushinteger(L, h);
  lua_pushinteger(L, SGL_PF_RGBA8);
  lua_pushinteger(L, w * STBI_rgb_alpha);
  return 5; // (bytes, w, h, fmt, stride)
}

static int l_png_write(lua_State *L) {
  const char *path = luaL_checkstring(L, 1);
  LubBytes *bytes = lub_bytes_check(L, 2);
  int w = (int)luaL_checkinteger(L, 3);
  int h = (int)luaL_checkinteger(L, 4);
  int stride = (int)luaL_optinteger(L, 5, w * 4);
  if (w <= 0 || h <= 0)
    return luaL_error(L, "png_write: invalid size %dx%d", w, h);
  if (stride < w * 4)
    return luaL_error(L, "png_write: stride %d is smaller than width*4 %d",
                      stride, w * 4);
  size_t needed = (size_t)stride * (size_t)h;
  if (bytes->len < needed) {
    return luaL_error(L, "png_write: byte buffer too small: got %zu, need %zu",
                      bytes->len, needed);
  }
  int ok = stbi_write_png(path, w, h, 4, bytes->data, stride);
  lua_pushboolean(L, ok != 0);
  return 1;
}

// ---------------------------------------------------------------------------
// audio: raw PCM だけを受ける core 契約 (docs/roadmap.md)。decode は
// png_load と同格の純関数 utility で snd handle を作らない。

static AudioState *audio_state_lazy(lua_State *L) {
  if (!g_app_for_lua)
    luaL_error(L, "audio: no app");
  if (!g_app_for_lua->audio) {
    g_app_for_lua->audio = audio_state_create();
    if (!g_app_for_lua->audio)
      luaL_error(L, "audio: state create failed");
  }
  return g_app_for_lua->audio;
}

// (data, channels, rate) -> snd。data は f32 の LubBytes / string、または
// サンプル値の table (コードで波形を作る経路)。
static int l_audio_pcm(lua_State *L) {
  AudioState *st = audio_state_lazy(L);
  uint32_t channels = (uint32_t)luaL_checkinteger(L, 2);
  uint32_t rate = (uint32_t)luaL_checkinteger(L, 3);
  const float *pcm = NULL;
  float *tmp = NULL;
  size_t samples = 0;
  if (lua_istable(L, 1)) {
    samples = lua_rawlen(L, 1);
    tmp = (float *)malloc(samples * sizeof(float));
    if (!tmp)
      return luaL_error(L, "audio_pcm: out of memory");
    for (size_t i = 0; i < samples; i++) {
      lua_rawgeti(L, 1, (lua_Integer)i + 1);
      tmp[i] = (float)lua_tonumber(L, -1);
      lua_pop(L, 1);
    }
    pcm = tmp;
  } else {
    LubBytes *b = lub_bytes_test(L, 1);
    const void *data;
    size_t len;
    if (b) {
      data = b->data;
      len = b->len;
    } else {
      data = luaL_checklstring(L, 1, &len);
    }
    if (len % sizeof(float) != 0)
      return luaL_error(L, "audio_pcm: byte length %zu is not f32-aligned",
                        len);
    pcm = (const float *)data;
    samples = len / sizeof(float);
  }
  if (channels == 0 || samples == 0 || samples % channels != 0) {
    free(tmp);
    return luaL_error(L, "audio_pcm: %zu samples not divisible by %u channels",
                      samples, (unsigned)channels);
  }
  int id = audio_snd_from_pcm(st, pcm, (uint32_t)(samples / channels), channels,
                              rate);
  free(tmp);
  if (id == 0)
    return luaL_error(L, "audio_pcm: rejected (registry full or bad args)");
  lua_pushinteger(L, id);
  return 1;
}

// (bytes|string) -> (bytes, channels, rate) | nil
static int l_audio_decode(lua_State *L) {
  const void *data;
  size_t len;
  LubBytes *b = lub_bytes_test(L, 1);
  if (b) {
    data = b->data;
    len = b->len;
  } else {
    data = luaL_checklstring(L, 1, &len);
  }
  uint32_t frames = 0, ch = 0, rate = 0;
  float *pcm = audio_decode_bytes(data, len, &frames, &ch, &rate);
  if (!pcm) {
    lua_pushnil(L);
    return 1;
  }
  lub_bytes_push(L, (uint8_t *)pcm, (size_t)frames * ch * sizeof(float));
  lua_pushinteger(L, ch);
  lua_pushinteger(L, rate);
  return 3;
}

static void audio_read_opts(lua_State *L, int idx, bool *loop, float *volume,
                            float *pitch, float *pan) {
  *volume = 1.0f;
  *pitch = 1.0f;
  *pan = 0.0f;
  if (loop)
    *loop = false;
  if (lua_isnoneornil(L, idx))
    return;
  luaL_checktype(L, idx, LUA_TTABLE);
  lua_getfield(L, idx, "volume");
  if (!lua_isnil(L, -1))
    *volume = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, idx, "pitch");
  if (!lua_isnil(L, -1))
    *pitch = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, idx, "pan");
  if (!lua_isnil(L, -1))
    *pan = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  if (loop) {
    lua_getfield(L, idx, "loop");
    *loop = lua_toboolean(L, -1);
    lua_pop(L, 1);
  }
}

static int l_audio_play(lua_State *L) {
  AudioState *st = audio_state_lazy(L);
  int snd = (int)luaL_checkinteger(L, 1);
  float volume, pitch, pan;
  audio_read_opts(L, 2, NULL, &volume, &pitch, &pan);
  lua_pushboolean(L, audio_play(st, snd, volume, pitch, pan));
  return 1;
}

static int l_audio_voice(lua_State *L) {
  AudioState *st = audio_state_lazy(L);
  const char *key = luaL_checkstring(L, 1);
  int snd = (int)luaL_checkinteger(L, 2);
  bool loop;
  float volume, pitch, pan;
  audio_read_opts(L, 3, &loop, &volume, &pitch, &pan);
  lua_pushboolean(L, audio_voice(st, key, snd, loop, volume, pitch, pan));
  return 1;
}

static int l_audio_free(lua_State *L) {
  AudioState *st = audio_state_lazy(L);
  lua_pushboolean(L, audio_snd_free(st, (int)luaL_checkinteger(L, 1)));
  return 1;
}

static int l_audio_master_volume(lua_State *L) {
  AudioState *st = audio_state_lazy(L);
  audio_master_volume(st, (float)luaL_checknumber(L, 1));
  return 0;
}

static int l_audio_info(lua_State *L) {
  AudioState *st = audio_state_lazy(L);
  AudioInfo info;
  audio_state_info(st, &info);
  lua_newtable(L);
  lua_pushboolean(L, info.device_ok);
  lua_setfield(L, -2, "device");
  lua_pushinteger(L, info.rate);
  lua_setfield(L, -2, "rate");
  lua_pushinteger(L, info.active_voices);
  lua_setfield(L, -2, "voices");
  lua_pushinteger(L, info.snds);
  lua_setfield(L, -2, "snds");
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
  lua_pushcfunction(L, l_audio_pcm);
  lua_setglobal(L, "audio_pcm");
  lua_pushcfunction(L, l_audio_decode);
  lua_setglobal(L, "audio_decode");
  lua_pushcfunction(L, l_audio_play);
  lua_setglobal(L, "audio_play");
  lua_pushcfunction(L, l_audio_voice);
  lua_setglobal(L, "audio_voice");
  lua_pushcfunction(L, l_audio_free);
  lua_setglobal(L, "audio_free");
  lua_pushcfunction(L, l_audio_master_volume);
  lua_setglobal(L, "audio_master_volume");
  lua_pushcfunction(L, l_audio_info);
  lua_setglobal(L, "audio_info");
  lua_pushcfunction(L, lub_load_gltf);
  lua_setglobal(L, "load_gltf");
  lua_pushcfunction(L, lub_surface_nets);
  lua_setglobal(L, "surface_nets");
  lua_pushcfunction(L, lub_sdf_mesh);
  lua_setglobal(L, "sdf_mesh");
  lua_pushcfunction(L, lub_font_metrics);
  lua_setglobal(L, "font_metrics");
  lua_pushcfunction(L, lub_font_glyph);
  lua_setglobal(L, "font_glyph");
  lua_pushcfunction(L, lub_font_glyph_mesh);
  lua_setglobal(L, "font_glyph_mesh");
  lua_pushcfunction(L, lub_font_kern);
  lua_setglobal(L, "font_kern");
  host_lua_register(L);
  ui_register_lua(L);
  phys2d_lua_register(L);
  phys3d_lua_register(L);
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
  phys2d_lua_set_state(&app->phys);
  phys3d_lua_set_state(&app->phys3);
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
