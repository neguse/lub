#include "lua_api.h"
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
static bool is_depth_format(SglPixelFormat fmt);

typedef struct LubReadbackItem {
  int id_ref;
  BackendReadback req;
  ReadbackResult rb;
  char *error;
  enum {
    LUB_READBACK_ITEM_EMPTY = 0,
    LUB_READBACK_ITEM_PENDING,
    LUB_READBACK_ITEM_READY,
    LUB_READBACK_ITEM_ERROR,
  } state;
} LubReadbackItem;

typedef struct LubReadback {
  int depth;
  int head;
  int count;
  LubReadbackItem items[LUB_READBACK_MAX_DEPTH];
} LubReadback;

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

static void lub_readback_item_init(LubReadbackItem *it) {
  memset(it, 0, sizeof(*it));
  it->id_ref = LUA_NOREF;
}

static void lub_readback_item_clear(lua_State *L, LubReadbackItem *it) {
  if (it->id_ref != LUA_NOREF) {
    luaL_unref(L, LUA_REGISTRYINDEX, it->id_ref);
  }
  it->id_ref = LUA_NOREF;
  if (it->req && g_backend && g_backend->destroy_readback) {
    g_backend->destroy_readback(it->req);
  }
  it->req = 0;
  if (it->rb.data) {
    free(it->rb.data);
  }
  memset(&it->rb, 0, sizeof(it->rb));
  if (it->error) {
    free(it->error);
    it->error = NULL;
  }
  it->state = LUB_READBACK_ITEM_EMPTY;
}

static LubReadback *lub_readback_check(lua_State *L, int idx) {
  return (LubReadback *)luaL_checkudata(L, idx, LUB_READBACK_MT);
}

static void lub_readback_push_processing(lua_State *L) {
  lua_pushstring(L, "processing");
  for (int i = 0; i < 8; ++i)
    lua_pushnil(L);
}

static void lub_readback_push_dropped(lua_State *L, int dropped_idx) {
  lua_pushstring(L, "dropped");
  for (int i = 0; i < 6; ++i)
    lua_pushnil(L);
  lua_pushvalue(L, dropped_idx);
  lua_pushnil(L);
}

static int lub_readback_push_item(lua_State *L, LubReadbackItem *it) {
  if (it->state == LUB_READBACK_ITEM_ERROR || it->error) {
    lua_pushstring(L, "error");
    for (int i = 0; i < 5; ++i)
      lua_pushnil(L);
    if (it->id_ref != LUA_NOREF)
      lua_rawgeti(L, LUA_REGISTRYINDEX, it->id_ref);
    else
      lua_pushnil(L);
    lua_pushnil(L);
    lua_pushstring(L, it->error);
    lub_readback_item_clear(L, it);
    return 9;
  }

  lua_pushstring(L, "ready");
  lub_bytes_push(L, it->rb.data, it->rb.data_bytes);
  it->rb.data = NULL;
  it->rb.data_bytes = 0;
  lua_pushinteger(L, it->rb.w);
  lua_pushinteger(L, it->rb.h);
  lua_pushinteger(L, it->rb.fmt);
  lua_pushinteger(L, it->rb.stride);
  if (it->id_ref != LUA_NOREF)
    lua_rawgeti(L, LUA_REGISTRYINDEX, it->id_ref);
  else
    lua_pushnil(L);
  lua_pushnil(L);
  lua_pushnil(L);
  lub_readback_item_clear(L, it);
  return 9;
}

static bool lub_readback_poll_item(lua_State *L, LubReadbackItem *it) {
  (void)L;
  if (it->state == LUB_READBACK_ITEM_READY ||
      it->state == LUB_READBACK_ITEM_ERROR) {
    return true;
  }
  if (it->state != LUB_READBACK_ITEM_PENDING || !it->req) {
    it->error = SDL_strdup("read_texture: invalid readback request");
    it->state = LUB_READBACK_ITEM_ERROR;
    return true;
  }
  if (!g_backend || !g_backend->poll_readback || !g_backend->destroy_readback) {
    it->error = SDL_strdup("read_texture: backend does not support readback");
    it->state = LUB_READBACK_ITEM_ERROR;
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
    it->state = LUB_READBACK_ITEM_READY;
  } else {
    it->error = SDL_strdup("read_texture: backend readback failed");
    it->state = LUB_READBACK_ITEM_ERROR;
  }
  return true;
}

static bool lub_readback_enqueue(lua_State *L, LubReadback *rb, int tex_idx,
                                 int id_idx) {
  if (rb->count >= rb->depth)
    return false;

  int tail = (rb->head + rb->count) % LUB_READBACK_MAX_DEPTH;
  LubReadbackItem *it = &rb->items[tail];
  lub_readback_item_clear(L, it);
  lua_pushvalue(L, id_idx);
  it->id_ref = luaL_ref(L, LUA_REGISTRYINDEX);

  if (!is_sentinel(L, tex_idx, "texture")) {
    it->error = SDL_strdup("read_texture: expected a TextureRef");
    it->state = LUB_READBACK_ITEM_ERROR;
    rb->count++;
    return true;
  }
  lua_getfield(L, tex_idx, "key");
  const char *key = lua_tostring(L, -1);
  ResEntry *e = key ? res_table_get(&g_app_for_lua->res, key) : NULL;
  lua_pop(L, 1);
  if (!e || e->kind != RES_TEXTURE || e->u.tex.h == 0) {
    char buf[256];
    SDL_snprintf(buf, sizeof(buf), "read_texture: texture not found: %s",
                 key ? key : "?");
    it->error = SDL_strdup(buf);
    it->state = LUB_READBACK_ITEM_ERROR;
    rb->count++;
    return true;
  }
  if (is_depth_format(e->u.tex.fmt)) {
    it->error = SDL_strdup("read_texture: depth textures are not supported");
    it->state = LUB_READBACK_ITEM_ERROR;
    rb->count++;
    return true;
  }
  if (!g_backend || !g_backend->request_readback_image) {
    it->error = SDL_strdup("read_texture: backend does not support readback");
    it->state = LUB_READBACK_ITEM_ERROR;
    rb->count++;
    return true;
  }

  BackendReadback req = 0;
  if (!g_backend->request_readback_image(g_app_for_lua, e->u.tex.h, e->u.tex.w,
                                         e->u.tex.h_, e->u.tex.fmt, &req) ||
      !req) {
    it->error = SDL_strdup("read_texture: backend readback request failed");
    it->state = LUB_READBACK_ITEM_ERROR;
    rb->count++;
    return true;
  }
  it->req = req;
  it->state = LUB_READBACK_ITEM_PENDING;
  rb->count++;
  return true;
}

static int l_readback_read_texture(lua_State *L) {
  LubReadback *rb = lub_readback_check(L, 1);
  if (pass_state_in_pass(&g_app_for_lua->pass)) {
    return luaL_error(L, "read_texture: cannot read while a pass is active");
  }

  bool has_id = !lua_isnoneornil(L, 3);
  if (rb->count > 0 && lub_readback_poll_item(L, &rb->items[rb->head])) {
    int idx = rb->head;
    rb->head = (rb->head + 1) % LUB_READBACK_MAX_DEPTH;
    rb->count--;
    if (has_id)
      lub_readback_enqueue(L, rb, 2, 3);
    return lub_readback_push_item(L, &rb->items[idx]);
  }

  if (has_id && !lub_readback_enqueue(L, rb, 2, 3)) {
    lub_readback_push_dropped(L, 3);
    return 9;
  }

  lub_readback_push_processing(L);
  return 9;
}

static int l_readback_gc(lua_State *L) {
  LubReadback *rb = lub_readback_check(L, 1);
  for (int i = 0; i < LUB_READBACK_MAX_DEPTH; ++i)
    lub_readback_item_clear(L, &rb->items[i]);
  rb->depth = 0;
  rb->head = 0;
  rb->count = 0;
  return 0;
}

static int l_readback_new(lua_State *L) {
  int depth = g_app_for_lua ? g_app_for_lua->readback_depth : 8;
  if (depth < 1)
    depth = 1;
  if (depth > LUB_READBACK_MAX_DEPTH)
    depth = LUB_READBACK_MAX_DEPTH;

  LubReadback *rb = (LubReadback *)lua_newuserdatauv(L, sizeof(LubReadback), 0);
  rb->depth = depth;
  rb->head = 0;
  rb->count = 0;
  for (int i = 0; i < LUB_READBACK_MAX_DEPTH; ++i)
    lub_readback_item_init(&rb->items[i]);
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

// Helper: read a vec4 (rgba) from table at idx field name `key`. Defaults
// for missing fields go to `defaults[]`. Caller pops nothing — the helper
// pushes/pops internally and returns with stack unchanged.
static void desc_get_float4(lua_State *L, int idx, const char *key,
                            float out[4], const float defaults[4]) {
  out[0] = defaults[0];
  out[1] = defaults[1];
  out[2] = defaults[2];
  out[3] = defaults[3];
  lua_getfield(L, idx, key);
  if (lua_istable(L, -1)) {
    for (int i = 0; i < 4; ++i) {
      lua_rawgeti(L, -1, i + 1);
      if (lua_isnumber(L, -1))
        out[i] = (float)lua_tonumber(L, -1);
      lua_pop(L, 1);
    }
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

// Helper: push a BufferRef sentinel table { __lub_kind = "buffer", key = key,
// version = effective stored version }.  `version` lets a caller re-assert
// "unchanged" on later use_* calls without holding its own version state.
static void push_buffer_ref(lua_State *L, const char *key, int64_t version) {
  lua_newtable(L);
  lua_pushstring(L, "buffer");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, key);
  lua_setfield(L, -2, "key");
  lua_pushinteger(L, (lua_Integer)version);
  lua_setfield(L, -2, "version");
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

// Helper: push a ShaderRef sentinel table { __lub_kind = "shader", key = key,
// version = effective stored version }
static void push_shader_ref(lua_State *L, const char *key, int64_t version) {
  lua_newtable(L);
  lua_pushstring(L, "shader");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, key);
  lua_setfield(L, -2, "key");
  lua_pushinteger(L, (lua_Integer)version);
  lua_setfield(L, -2, "version");
}

// Helper: push a TextureRef sentinel table { __lub_kind = "texture", key =
// key, version = effective stored version }
static void push_texture_ref(lua_State *L, const char *key, int64_t version) {
  lua_newtable(L);
  lua_pushstring(L, "texture");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, key);
  lua_setfield(L, -2, "key");
  lua_pushinteger(L, (lua_Integer)version);
  lua_setfield(L, -2, "version");
}

static bool is_depth_format(SglPixelFormat fmt) {
  return fmt == SGL_PF_DEPTH16 || fmt == SGL_PF_DEPTH24_STENCIL8 ||
         fmt == SGL_PF_DEPTH32F;
}

static int l_begin_pass(lua_State *L) {
  luaL_checktype(L, 1, LUA_TTABLE);

  uintptr_t depth_image = 0;
  SglPixelFormat depth_fmt = SGL_PF_DEPTH24_STENCIL8;
  int depth_w = 0, depth_h = 0;
  float clear_depth = 1.0f;
  SglLoadAction load = SGL_LOAD_CLEAR;

  lua_getfield(L, 1, "clear_depth");
  if (lua_isnumber(L, -1))
    clear_depth = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);

  lua_getfield(L, 1, "load");
  if (!lua_isnoneornil(L, -1)) {
    int lv = (int)lua_tointeger(L, -1);
    if (lv != SGL_LOAD_CLEAR && lv != SGL_LOAD_LOAD) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: load must be Gfx.CLEAR or Gfx.LOAD");
    }
    load = (SglLoadAction)lv;
  }
  lua_pop(L, 1);

  lua_getfield(L, 1, "depth_target");
  if (!lua_isnoneornil(L, -1)) {
    if (!is_sentinel(L, -1, "texture")) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: depth_target must be a TextureRef");
    }
    lua_getfield(L, -1, "key");
    const char *dk = lua_tostring(L, -1);
    ResEntry *de = dk ? res_table_get(&g_app_for_lua->res, dk) : NULL;
    lua_pop(L, 1);
    if (!de || de->kind != RES_TEXTURE) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: depth target texture not found: %s",
                        dk ? dk : "?");
    }
    if (!de->u.tex.is_target || !is_depth_format(de->u.tex.fmt)) {
      lua_pop(L, 1);
      return luaL_error(L,
                        "begin_pass: depth_target '%s' must be a depth texture "
                        "declared with {target=true}",
                        dk ? dk : "?");
    }
    depth_image = de->u.tex.h;
    depth_fmt = de->u.tex.fmt;
    depth_w = de->u.tex.w;
    depth_h = de->u.tex.h_;
  }
  lua_pop(L, 1);

  // MRT path: { targets = {t1, t2, ...}, clear_colors = {{r,g,b,a},...} }
  lua_getfield(L, 1, "targets");
  if (lua_istable(L, -1)) {
    int n = (int)lua_rawlen(L, -1);
    if (n < 1) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: targets table is empty");
    }
    if (n > SGL_MAX_COLOR_TARGETS) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: too many targets (%d > %d)", n,
                        SGL_MAX_COLOR_TARGETS);
    }
    uintptr_t targets[SGL_MAX_COLOR_TARGETS] = {0};
    SglPixelFormat fmts[SGL_MAX_COLOR_TARGETS] = {0};
    float clears[SGL_MAX_COLOR_TARGETS][4] = {0};
    int tw = 0, th = 0;
    for (int i = 0; i < n; ++i) {
      lua_rawgeti(L, -1, i + 1);
      if (!is_sentinel(L, -1, "texture")) {
        lua_pop(L, 2);
        return luaL_error(L, "begin_pass: targets[%d] must be a TextureRef",
                          i + 1);
      }
      lua_getfield(L, -1, "key");
      const char *tk = lua_tostring(L, -1);
      ResEntry *te = tk ? res_table_get(&g_app_for_lua->res, tk) : NULL;
      lua_pop(L, 1);
      if (!te || te->kind != RES_TEXTURE) {
        lua_pop(L, 2);
        return luaL_error(L, "begin_pass: target texture not found: %s",
                          tk ? tk : "?");
      }
      if (!te->u.tex.is_target) {
        lua_pop(L, 2);
        return luaL_error(L,
                          "begin_pass: target texture '%s' was not declared "
                          "with {target=true}",
                          tk);
      }
      if (is_depth_format(te->u.tex.fmt)) {
        lua_pop(L, 2);
        return luaL_error(L, "begin_pass: targets[%d] must be a color texture",
                          i + 1);
      }
      if (i == 0) {
        tw = te->u.tex.w;
        th = te->u.tex.h_;
      } else if (te->u.tex.w != tw || te->u.tex.h_ != th) {
        lua_pop(L, 2);
        return luaL_error(L,
                          "begin_pass: targets must share the same size (got "
                          "%dx%d at [%d], expected %dx%d)",
                          te->u.tex.w, te->u.tex.h_, i + 1, tw, th);
      }
      targets[i] = te->u.tex.h;
      fmts[i] = te->u.tex.fmt;
      lua_pop(L, 1);
    }
    lua_pop(L, 1); // targets table

    // clear_colors: optional. nil -> all clear to {0,0,0,1}. If shorter than
    // n_targets, the missing entries default to {0,0,0,1}.
    for (int i = 0; i < n; ++i) {
      clears[i][0] = 0;
      clears[i][1] = 0;
      clears[i][2] = 0;
      clears[i][3] = 1;
    }
    lua_getfield(L, 1, "clear_colors");
    if (lua_istable(L, -1)) {
      int m = (int)lua_rawlen(L, -1);
      if (m > n)
        m = n;
      for (int i = 0; i < m; ++i) {
        lua_rawgeti(L, -1, i + 1);
        if (lua_istable(L, -1)) {
          for (int j = 0; j < 4; ++j) {
            lua_rawgeti(L, -1, j + 1);
            if (lua_isnumber(L, -1))
              clears[i][j] = (float)lua_tonumber(L, -1);
            lua_pop(L, 1);
          }
        }
        lua_pop(L, 1);
      }
    }
    lua_pop(L, 1); // clear_colors

    if (depth_image && (depth_w != tw || depth_h != th)) {
      return luaL_error(
          L,
          "begin_pass: depth_target size %dx%d must match color targets %dx%d",
          depth_w, depth_h, tw, th);
    }
    pass_state_begin_ex(&g_app_for_lua->pass, n, targets, fmts, tw, th,
                        (const float (*)[4])clears, depth_image, depth_fmt,
                        clear_depth, load);
    return 0;
  }
  lua_pop(L, 1); // targets (was not a table)

  lua_getfield(L, 1, "target");

  uintptr_t target_image = 0;
  SglPixelFormat fmt = SGL_PF_RGBA8;
  int tw = 0, th = 0;
  bool is_main = false;
  if (is_sentinel(L, -1, "main_tex")) {
    if (depth_image) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: main_tex uses the swapchain depth "
                           "buffer; depth_target is only for offscreen passes");
    }
    is_main = true;
    // pass_state_begin will resolve swapchain format itself.
  } else if (is_sentinel(L, -1, "texture")) {
    lua_getfield(L, -1, "key");
    const char *tk = lua_tostring(L, -1);
    ResEntry *te = tk ? res_table_get(&g_app_for_lua->res, tk) : NULL;
    lua_pop(L, 1);
    if (!te || te->kind != RES_TEXTURE) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: target texture not found: %s",
                        tk ? tk : "?");
    }
    if (!te->u.tex.is_target) {
      lua_pop(L, 1);
      return luaL_error(
          L,
          "begin_pass: target texture '%s' was not declared with {target=true}",
          tk);
    }
    if (is_depth_format(te->u.tex.fmt)) {
      lua_pop(L, 1);
      return luaL_error(L, "begin_pass: target must be a color texture; use "
                           "depth_target for depth textures");
    }
    target_image = te->u.tex.h;
    fmt = te->u.tex.fmt;
    tw = te->u.tex.w;
    th = te->u.tex.h_;
  } else if (lua_isnoneornil(L, -1) && depth_image) {
    // Depth-only offscreen pass.
  } else {
    lua_pop(L, 1);
    return luaL_error(L, "begin_pass: target must be main_tex, a color "
                         "TextureRef, or omitted for a depth-only pass");
  }
  lua_pop(L, 1);

  static const float defaults[4] = {0, 0, 0, 1};
  float c[4];
  desc_get_float4(L, 1, "clear_color", c, defaults);

  if (is_main) {
    pass_state_begin(&g_app_for_lua->pass, target_image, fmt, tw, th, c[0],
                     c[1], c[2], c[3], load);
  } else if (target_image) {
    if (depth_image && (depth_w != tw || depth_h != th)) {
      return luaL_error(
          L,
          "begin_pass: depth_target size %dx%d must match color target %dx%d",
          depth_w, depth_h, tw, th);
    }
    uintptr_t targets[1] = {target_image};
    SglPixelFormat fmts[1] = {fmt};
    float clears[1][4] = {{c[0], c[1], c[2], c[3]}};
    pass_state_begin_ex(&g_app_for_lua->pass, 1, targets, fmts, tw, th,
                        (const float (*)[4])clears, depth_image, depth_fmt,
                        clear_depth, load);
  } else {
    float clears[1][4] = {{0, 0, 0, 1}};
    pass_state_begin_ex(&g_app_for_lua->pass, 0, NULL, NULL, depth_w, depth_h,
                        (const float (*)[4])clears, depth_image, depth_fmt,
                        clear_depth, load);
  }
  return 0;
}

// use_* version argument.  A caller-supplied int is an identity claim for the
// key's current content ("equal to stored → may skip upload").  nil/omitted
// is a "content changed" declaration: the runtime issues a fresh effective
// version, so the upload can never be skipped against a stale claim.
static int64_t use_version_arg(lua_State *L, int idx, bool *declared) {
  if (lua_isnoneornil(L, idx)) {
    *declared = true;
    return res_table_next_revision(&g_app_for_lua->res);
  }
  *declared = false;
  return (int64_t)luaL_checkinteger(L, idx);
}

static int l_use_buffer(lua_State *L) {
  const char *key = luaL_checkstring(L, 1);
  int type = (int)luaL_checkinteger(L, 2);
  bool declared = false;
  int64_t version = use_version_arg(L, 4, &declared);

  if (type != SGL_BUFFER_VERTEX && type != SGL_BUFFER_INDEX &&
      type != SGL_BUFFER_STORAGE) {
    return luaL_error(L, "use_buffer: only VERTEX/INDEX/STORAGE are supported");
  }

  ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_BUFFER);
  if (!e)
    return luaL_error(L, "use_buffer: key '%s' already used as different kind",
                      key);

  res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

  if (!declared && e->version == version && e->u.buf.h != 0) {
    // Skip upload — return existing BufferRef
    push_buffer_ref(L, key, e->version);
    return 1;
  }

  // STORAGE may be allocated empty by passing an integer float-count as arg #3
  // (the compute shader populates it). VERTEX/INDEX must always have a data
  // table.
  bool allocate_empty = (type == SGL_BUFFER_STORAGE) && lua_isinteger(L, 3);
  size_t new_bytes = 0;
  void *data = NULL;
  if (allocate_empty) {
    lua_Integer n = lua_tointeger(L, 3);
    if (n <= 0)
      return luaL_error(L, "use_buffer: STORAGE float-count must be > 0");
    new_bytes = (size_t)n * sizeof(float);
  } else if (type == SGL_BUFFER_INDEX) {
    luaL_checktype(L, 3, LUA_TTABLE);
    bool zero_based = false;
    int n = numeric_table_len(L, 3, &zero_based);
    if (n <= 0)
      return luaL_error(L, "use_buffer: empty data");
    uint32_t *idx = (uint32_t *)malloc((size_t)n * sizeof(uint32_t));
    if (!idx)
      return luaL_error(L, "use_buffer: out of memory");
    for (int i = 0; i < n; ++i) {
      lua_rawgeti(L, 3, zero_based ? i : i + 1);
      idx[i] = (uint32_t)lua_tonumber(L, -1);
      lua_pop(L, 1);
    }
    new_bytes = (size_t)n * sizeof(uint32_t);
    data = idx;
  } else {
    // VERTEX / STORAGE with data
    luaL_checktype(L, 3, LUA_TTABLE);
    bool zero_based = false;
    int n = numeric_table_len(L, 3, &zero_based);
    if (n <= 0)
      return luaL_error(L, "use_buffer: empty data");
    float *fdata = (float *)malloc((size_t)n * sizeof(float));
    if (!fdata)
      return luaL_error(L, "use_buffer: out of memory");
    for (int i = 0; i < n; ++i) {
      lua_rawgeti(L, 3, zero_based ? i : i + 1);
      fdata[i] = (float)lua_tonumber(L, -1);
      lua_pop(L, 1);
    }
    new_bytes = (size_t)n * sizeof(float);
    data = fdata;
  }

  if (e->u.buf.h != 0 && e->u.buf.size_bytes == new_bytes &&
      e->u.buf.type == (SglBufferType)type && data) {
    // in-place update
    g_backend->update_buffer(e->u.buf.h, data, new_bytes);
  } else {
    if (e->u.buf.h != 0)
      g_backend->destroy_buffer(e->u.buf.h);
    e->u.buf.h = g_backend->make_buffer((SglBufferType)type, data, new_bytes);
    e->u.buf.type = (SglBufferType)type;
    e->u.buf.size_bytes = new_bytes;
  }
  e->version = version;
  if (data)
    free(data);

  push_buffer_ref(L, key, e->version);
  return 1;
}

static int l_use_texture(lua_State *L) {
  const char *key = luaL_checkstring(L, 1);
  int w = (int)luaL_checkinteger(L, 2);
  int h = (int)luaL_checkinteger(L, 3);
  int fmt = (int)luaL_checkinteger(L, 4);
  int has_data = !lua_isnoneornil(L, 5);
  LubBytes *byte_data = NULL;
  if (has_data) {
    byte_data = lub_bytes_test(L, 5);
    if (!byte_data)
      luaL_checktype(L, 5, LUA_TTABLE);
  }
  bool declared = false;
  int64_t version = use_version_arg(L, 6, &declared);

  // optional 7th arg: { filter = LINEAR|NEAREST, wrap = REPEAT|CLAMP, target =
  // bool, storage = bool }
  SglFilter filter = SGL_FILTER_LINEAR;
  SglWrap wrap = SGL_WRAP_REPEAT;
  bool filter_explicit = false;
  bool is_target = false;
  bool storage = false;
  if (!lua_isnoneornil(L, 7)) {
    luaL_checktype(L, 7, LUA_TTABLE);
    lua_getfield(L, 7, "filter");
    if (lua_isinteger(L, -1)) {
      int v = (int)lua_tointeger(L, -1);
      if (v != SGL_FILTER_LINEAR && v != SGL_FILTER_NEAREST) {
        lua_pop(L, 1);
        return luaL_error(L,
                          "use_texture: opts.filter must be LINEAR or NEAREST");
      }
      filter = (SglFilter)v;
      filter_explicit = true;
    }
    lua_pop(L, 1);
    lua_getfield(L, 7, "wrap");
    if (lua_isinteger(L, -1)) {
      int v = (int)lua_tointeger(L, -1);
      if (v != SGL_WRAP_REPEAT && v != SGL_WRAP_CLAMP) {
        lua_pop(L, 1);
        return luaL_error(L, "use_texture: opts.wrap must be REPEAT or CLAMP");
      }
      wrap = (SglWrap)v;
    }
    lua_pop(L, 1);
    lua_getfield(L, 7, "target");
    if (!lua_isnoneornil(L, -1))
      is_target = lua_toboolean(L, -1);
    lua_pop(L, 1);
    lua_getfield(L, 7, "storage");
    if (!lua_isnoneornil(L, -1))
      storage = lua_toboolean(L, -1);
    lua_pop(L, 1);
  }
  if (is_target && has_data) {
    return luaL_error(
        L, "use_texture: render target cannot be initialized with data");
  }
  if (storage && has_data) {
    return luaL_error(
        L, "use_texture: storage texture cannot be initialized with data");
  }
  bool depth_fmt = is_depth_format((SglPixelFormat)fmt);
  if (depth_fmt && !is_target) {
    return luaL_error(
        L, "use_texture: depth formats are only supported with {target=true}");
  }
  if (depth_fmt && storage) {
    return luaL_error(L, "use_texture: depth formats cannot use storage=true");
  }
  if (depth_fmt) {
    // WebGPU can only sample depth as unfilterable-float; a filtering sampler
    // is a validation error there (and LINEAR on D32 is optional in Vulkan).
    if (filter_explicit && filter == SGL_FILTER_LINEAR) {
      return luaL_error(L,
                        "use_texture: depth textures must use NEAREST filter");
    }
    filter = SGL_FILTER_NEAREST;
  }

  if (w <= 0 || h <= 0)
    return luaL_error(L, "use_texture: invalid size %dx%d", w, h);

  ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_TEXTURE);
  if (!e)
    return luaL_error(L, "use_texture: key '%s' already used as different kind",
                      key);
  res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

  bool sampler_changed =
      (e->u.tex.h != 0) && (e->u.tex.filter != filter || e->u.tex.wrap != wrap);
  bool target_changed = (e->u.tex.h != 0) && (e->u.tex.is_target != is_target);
  bool storage_changed = (e->u.tex.h != 0) && (e->u.tex.storage != storage);

  if (!declared && e->version == version && e->u.tex.h != 0 &&
      !sampler_changed && !target_changed && !storage_changed) {
    push_texture_ref(L, key, e->version);
    return 1;
  }

  int bpp;
  switch (fmt) {
  case SGL_PF_RGBA8:
    bpp = 4;
    break;
  case SGL_PF_R8:
    bpp = 1;
    break;
  case SGL_PF_RG8:
    bpp = 2;
    break;
  case SGL_PF_R16F:
  case SGL_PF_RG16F:
  case SGL_PF_R32F:
  case SGL_PF_RGBA16F:
  case SGL_PF_RGBA32F:
  case SGL_PF_DEPTH16:
  case SGL_PF_DEPTH24_STENCIL8:
  case SGL_PF_DEPTH32F:
    bpp = 0;
    break;
  default:
    return luaL_error(L, "use_texture: format not supported "
                         "(RGBA8/R8/RG8/R16F/RG16F/R32F/RGBA16F/RGBA32F/"
                         "depth target formats only)");
  }

  uint8_t *pixels = NULL;
  const uint8_t *pixel_src = NULL;
  size_t new_bytes = 0;
  if (has_data) {
    if (bpp == 0) {
      return luaL_error(L, "use_texture: this texture format cannot be "
                           "initialized with byte data");
    }
    size_t expected = (size_t)w * (size_t)h * (size_t)bpp;
    if (byte_data) {
      if (byte_data->len != expected) {
        return luaL_error(
            L, "use_texture: byte size mismatch: got %zu, expected %zu",
            byte_data->len, expected);
      }
      pixel_src = byte_data->data;
      new_bytes = expected;
    } else {
      int n = (int)lua_rawlen(L, 5);
      if ((size_t)n != expected) {
        return luaL_error(
            L, "use_texture: data size mismatch: got %d, expected %zu", n,
            expected);
      }
      pixels = (uint8_t *)malloc((size_t)n);
      if (!pixels)
        return luaL_error(L, "use_texture: out of memory");
      for (int i = 0; i < n; ++i) {
        lua_rawgeti(L, 5, i + 1);
        int v = (int)lua_tointeger(L, -1);
        if (v < 0)
          v = 0;
        else if (v > 255)
          v = 255;
        pixels[i] = (uint8_t)v;
        lua_pop(L, 1);
      }
      pixel_src = pixels;
      new_bytes = expected;
    }
  }

  bool same_shape = (e->u.tex.h != 0) && (e->u.tex.w == w) &&
                    (e->u.tex.h_ == h) && (e->u.tex.fmt == (SglPixelFormat)fmt);
  if (same_shape && !sampler_changed && !target_changed && !storage_changed &&
      pixel_src && new_bytes > 0) {
    // in-place update
    g_backend->update_image(e->u.tex.h, pixel_src, new_bytes);
  } else {
    if (e->u.tex.h != 0)
      g_backend->destroy_image(e->u.tex.h);
    ImageDesc d = {
        .fmt = (SglPixelFormat)fmt,
        .w = w,
        .h = h,
        .data = pixel_src,
        .data_bytes = new_bytes,
        .filter = filter,
        .wrap = wrap,
        .render_target = is_target,
        .storage = storage,
    };
    e->u.tex.h = g_backend->make_image(&d);
    e->u.tex.w = w;
    e->u.tex.h_ = h;
    e->u.tex.fmt = (SglPixelFormat)fmt;
  }
  e->u.tex.filter = filter;
  e->u.tex.wrap = wrap;
  e->u.tex.is_target = is_target;
  e->u.tex.storage = storage;
  e->version = version;

  if (pixels)
    free(pixels);

  push_texture_ref(L, key, e->version);
  return 1;
}

static ShaderTargetBackend shader_target_for_backend(void) {
#if defined(__EMSCRIPTEN__)
  // wasm: webgpu backend 一択。slang-wasm が WGSL を出す。
  return SHADER_TARGET_WGSL;
#elif defined(_WIN32)
  // dx12 の vtable name は "native"。
  if (g_backend && g_backend->name && strcmp(g_backend->name, "native") == 0)
    return SHADER_TARGET_DX12;
  return SHADER_TARGET_SDLGPU;
#else
  // Linux の "native" (Vulkan 直接) も SDLGPU target の SPIR-V を食う
  // (descriptor set 規約が SDL_GPU 準拠のため)。
  return SHADER_TARGET_SDLGPU;
#endif
}

static int l_use_shader(lua_State *L) {
  const char *key = luaL_checkstring(L, 1);
  const char *vs = luaL_checkstring(L, 2);
  const char *fs = luaL_checkstring(L, 3);
  bool declared = false;
  int64_t version = use_version_arg(L, 4, &declared);

  ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_SHADER);
  if (!e)
    return luaL_error(L, "use_shader: key '%s' already used as different kind",
                      key);
  res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

  if (!declared && e->version == version && e->u.sh.h != 0) {
    push_shader_ref(L, key, e->version);
    return 1;
  }

  // version mismatch path. Compile into LOCAL temporaries first; only swap on
  // success.
  char err[1024];
  ShaderBlob vsb = {0}, fsb = {0};
  ShaderReflection new_refl;
  ShaderTargetBackend tgt = shader_target_for_backend();
  if (!shader_compile(vs, fs, tgt, &vsb, &fsb, &new_refl, err, sizeof(err))) {
    shader_blob_free(&vsb);
    shader_blob_free(&fsb);
    // No old shader to fall back to → fail loud so the caller doesn't draw
    // with handle 0. Once we have a working shader, later failures keep the
    // old one and just log.
    if (e->u.sh.h == 0) {
      return luaL_error(L, "shader compile error: %s", err);
    }
    SDL_Log("use_shader: recompile failed for key '%s': %s (keeping old)", key,
            err);
    push_shader_ref(L, key, e->version);
    return 1;
  }
  ShaderDesc sd = {
      .vs_spirv = vsb.spirv,
      .vs_bytes = vsb.bytes,
      .fs_spirv = fsb.spirv,
      .fs_bytes = fsb.bytes,
      .refl = &new_refl,
  };
  BackendShader new_h = g_backend->make_shader(&sd);
  shader_blob_free(&vsb);
  shader_blob_free(&fsb);
  if (!new_h) {
    if (e->u.sh.h == 0) {
      return luaL_error(L, "use_shader: make_shader failed for key '%s'", key);
    }
    SDL_Log("use_shader: make_shader failed for key '%s' (keeping old)", key);
    push_shader_ref(L, key, e->version);
    return 1;
  }

  // success: sweep pipeline cache for old shader, then destroy.
  BackendShader old_h = e->u.sh.h;
  if (old_h) {
    pipeline_cache_invalidate_shader(&g_app_for_lua->pip_cache,
                                     (uintptr_t)old_h);
    g_backend->destroy_shader(old_h);
  }
  e->u.sh.h = new_h;
  e->u.sh.refl = new_refl;
  e->version = version;

  push_shader_ref(L, key, e->version);
  return 1;
}

static int l_use_shader_compute(lua_State *L) {
  const char *key = luaL_checkstring(L, 1);
  const char *cs = luaL_checkstring(L, 2);
  bool declared = false;
  int64_t version = use_version_arg(L, 3, &declared);

  ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_SHADER);
  if (!e)
    return luaL_error(
        L, "use_shader_compute: key '%s' already used as different kind", key);
  res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

  if (!declared && e->version == version && e->u.sh.h != 0) {
    push_shader_ref(L, key, e->version);
    return 1;
  }

  char err[1024];
  ShaderBlob csb = {0};
  ShaderReflection new_refl;
  ShaderTargetBackend tgt = shader_target_for_backend();
  if (!shader_compile_compute(cs, tgt, &csb, &new_refl, err, sizeof(err))) {
    shader_blob_free(&csb);
    if (e->u.sh.h == 0) {
      return luaL_error(L, "compute shader compile error: %s", err);
    }
    SDL_Log(
        "use_shader_compute: recompile failed for key '%s': %s (keeping old)",
        key, err);
    push_shader_ref(L, key, e->version);
    return 1;
  }
  ShaderDesc sd = {
      .cs_spirv = csb.spirv,
      .cs_bytes = csb.bytes,
      .refl = &new_refl,
  };
  BackendShader new_h = g_backend->make_shader(&sd);
  shader_blob_free(&csb);
  if (!new_h) {
    if (e->u.sh.h == 0) {
      return luaL_error(
          L, "use_shader_compute: make_shader failed for key '%s'", key);
    }
    SDL_Log("use_shader_compute: make_shader failed for key '%s' (keeping old)",
            key);
    push_shader_ref(L, key, e->version);
    return 1;
  }
  BackendShader old_h = e->u.sh.h;
  if (old_h) {
    pipeline_cache_invalidate_shader(&g_app_for_lua->pip_cache,
                                     (uintptr_t)old_h);
    g_backend->destroy_shader(old_h);
  }
  e->u.sh.h = new_h;
  e->u.sh.refl = new_refl;
  e->version = version;
  push_shader_ref(L, key, e->version);
  return 1;
}

static void pack_uniform_block(lua_State *L, int uniforms_idx,
                               const ShaderUniformBlock *ub, float *dst,
                               int max_floats, const char *ctx) {
  int total_floats = ub->size_floats < 0 ? 0 : ub->size_floats;
  if (total_floats > max_floats) {
    luaL_error(L, "%s: uniform block too large (%d floats > %d)", ctx,
               total_floats, max_floats);
    return;
  }
  memset(dst, 0, (size_t)max_floats * sizeof(float));
  for (int m = 0; m < ub->member_count; ++m) {
    const ShaderUniformMember *mem = &ub->members[m];
    lua_getfield(L, uniforms_idx, mem->name);
    if (lua_istable(L, -1)) {
      int n_provided = (int)lua_rawlen(L, -1);
      int copy = n_provided < mem->comp_count ? n_provided : mem->comp_count;
      for (int j = 0; j < copy; ++j) {
        lua_rawgeti(L, -1, j + 1);
        if (lua_isnumber(L, -1)) {
          dst[mem->offset_floats + j] = (float)lua_tonumber(L, -1);
        }
        lua_pop(L, 1);
      }
    }
    lua_pop(L, 1);
  }
}

static bool refl_has_sampled_texture(const ShaderReflection *refl,
                                     const char *name) {
  if (!refl || !name)
    return false;
  for (int i = 0; i < refl->tex_count; ++i) {
    if (strcmp(refl->texs[i].name, name) == 0)
      return true;
  }
  return false;
}

static bool refl_has_storage_texture(const ShaderReflection *refl,
                                     const char *name) {
  if (!refl || !name)
    return false;
  for (int i = 0; i < refl->storage_tex_count; ++i) {
    if (strcmp(refl->storage_texs[i].name, name) == 0)
      return true;
  }
  return false;
}

static int l_dispatch(lua_State *L) {
  if (pass_state_in_pass(&g_app_for_lua->pass)) {
    return luaL_error(L,
                      "dispatch: must be called outside begin_pass/end_pass");
  }
  int gx = (int)luaL_checkinteger(L, 1);
  int gy = (int)luaL_checkinteger(L, 2);
  int gz = (int)luaL_checkinteger(L, 3);
  luaL_checktype(L, 4, LUA_TTABLE); // resources
  luaL_checktype(L, 5, LUA_TTABLE); // options

  lua_getfield(L, 5, "shader");
  if (!is_sentinel(L, -1, "shader")) {
    lua_pop(L, 1);
    return luaL_error(L, "dispatch: options.shader required (ShaderRef)");
  }
  lua_getfield(L, -1, "key");
  const char *shader_key = lua_tostring(L, -1);
  char shader_key_buf[128];
  if (shader_key) {
    strncpy(shader_key_buf, shader_key, sizeof(shader_key_buf) - 1);
    shader_key_buf[sizeof(shader_key_buf) - 1] = '\0';
  } else {
    shader_key_buf[0] = '\0';
  }
  lua_pop(L, 2);

  ResEntry *sh_e = res_table_get(&g_app_for_lua->res, shader_key_buf);
  if (!sh_e || sh_e->kind != RES_SHADER) {
    return luaL_error(L, "dispatch: shader not found: %s", shader_key_buf);
  }
  if (!sh_e->u.sh.refl.is_compute) {
    return luaL_error(L, "dispatch: shader '%s' is not a compute shader",
                      shader_key_buf);
  }

  BackendPipeline pip = pipeline_cache_get_compute(
      &g_app_for_lua->pip_cache, sh_e->u.sh.h, &sh_e->u.sh.refl,
      (int64_t)g_app_for_lua->frame_index);

  ComputeDispatchDesc dd = {0};
  dd.pipeline = pip;
  dd.refl = &sh_e->u.sh.refl;
  dd.groups_x = gx;
  dd.groups_y = gy;
  dd.groups_z = gz;

  // Walk resources: storage buffers/textures/sampled textures by reflected
  // name. The Lua API does not expose binding kind.
  lua_pushnil(L);
  while (lua_next(L, 4) != 0) {
    if (lua_istable(L, -1)) {
      lua_getfield(L, -1, "__lub_kind");
      const char *kind = lua_isstring(L, -1) ? lua_tostring(L, -1) : "";
      char kind_buf[16];
      strncpy(kind_buf, kind, sizeof(kind_buf) - 1);
      kind_buf[sizeof(kind_buf) - 1] = '\0';
      lua_pop(L, 1);

      if (strcmp(kind_buf, "buffer") == 0) {
        const char *res_name =
            lua_type(L, -2) == LUA_TSTRING ? lua_tostring(L, -2) : NULL;
        lua_getfield(L, -1, "key");
        const char *bk = lua_tostring(L, -1);
        ResEntry *be = bk ? res_table_get(&g_app_for_lua->res, bk) : NULL;
        lua_pop(L, 1);
        if (be && be->kind == RES_BUFFER &&
            be->u.buf.type == SGL_BUFFER_STORAGE && res_name &&
            dd.n_storage_bufs < SGL_MAX_STORAGE_BUFS) {
          dd.storage_bufs[dd.n_storage_bufs].name = res_name;
          dd.storage_bufs[dd.n_storage_bufs].buf = be->u.buf.h;
          dd.n_storage_bufs++;
        }
      } else if (strcmp(kind_buf, "texture") == 0) {
        const char *res_name =
            lua_type(L, -2) == LUA_TSTRING ? lua_tostring(L, -2) : NULL;
        lua_getfield(L, -1, "key");
        const char *tk = lua_tostring(L, -1);
        ResEntry *te = tk ? res_table_get(&g_app_for_lua->res, tk) : NULL;
        lua_pop(L, 1);
        if (te && te->kind == RES_TEXTURE && res_name) {
          if (refl_has_storage_texture(&sh_e->u.sh.refl, res_name)) {
            if (!te->u.tex.storage) {
              return luaL_error(
                  L, "dispatch: texture '%s' must be created with storage=true",
                  tk ? tk : "?");
            }
            if (dd.n_storage_textures < SGL_MAX_STORAGE_TEXTURES) {
              dd.storage_textures[dd.n_storage_textures].name = res_name;
              dd.storage_textures[dd.n_storage_textures].image = te->u.tex.h;
              dd.n_storage_textures++;
            }
          } else if (refl_has_sampled_texture(&sh_e->u.sh.refl, res_name)) {
            if (dd.texture_count < SGL_MAX_TEXTURES) {
              dd.textures[dd.texture_count].name = res_name;
              dd.textures[dd.texture_count].image = te->u.tex.h;
              dd.texture_count++;
            }
          }
        }
      }
    }
    lua_pop(L, 1);
  }

  for (int i = 0; i < dd.texture_count; ++i) {
    for (int j = 0; j < dd.n_storage_textures; ++j) {
      if (dd.textures[i].image &&
          dd.textures[i].image == dd.storage_textures[j].image) {
        return luaL_error(L,
                          "dispatch: same texture cannot be read and written "
                          "in one dispatch");
      }
    }
  }

  enum { UB_MAX_FLOATS = 256 };
  float ubufs[SGL_MAX_UNIFORM_BLOCKS][UB_MAX_FLOATS];
  lua_getfield(L, 4, "uniforms");
  if (lua_istable(L, -1) && sh_e->u.sh.refl.ub_count > 0) {
    for (int i = 0; i < sh_e->u.sh.refl.ub_count &&
                    dd.uniform_count < SGL_MAX_UNIFORM_BLOCKS;
         ++i) {
      const ShaderUniformBlock *ub = &sh_e->u.sh.refl.ubs[i];
      pack_uniform_block(L, lua_gettop(L), ub, ubufs[dd.uniform_count],
                         UB_MAX_FLOATS, "dispatch");
      dd.uniforms[dd.uniform_count].stage = ub->stage;
      dd.uniforms[dd.uniform_count].slot = ub->slot;
      dd.uniforms[dd.uniform_count].data = ubufs[dd.uniform_count];
      dd.uniforms[dd.uniform_count].bytes =
          (size_t)(ub->size_floats < 0 ? 0 : ub->size_floats) * sizeof(float);
      dd.uniform_count++;
    }
  }
  lua_pop(L, 1); // uniforms field (or nil)

  g_backend->dispatch(g_app_for_lua, &dd);
  return 0;
}

static int l_draw(lua_State *L) {
  if (!pass_state_in_pass(&g_app_for_lua->pass)) {
    return luaL_error(L, "draw: must be called inside begin_pass/end_pass");
  }
  int count = (int)luaL_checkinteger(L, 1);
  luaL_checktype(L, 2, LUA_TTABLE); // resources
  luaL_checktype(L, 3, LUA_TTABLE); // options

  // options.shader is required and must be a ShaderRef
  lua_getfield(L, 3, "shader");
  if (!is_sentinel(L, -1, "shader")) {
    lua_pop(L, 1);
    return luaL_error(L, "draw: options.shader required (ShaderRef)");
  }
  lua_getfield(L, -1, "key");
  const char *shader_key = lua_tostring(L, -1);
  char shader_key_buf[128];
  if (shader_key) {
    strncpy(shader_key_buf, shader_key, sizeof(shader_key_buf) - 1);
    shader_key_buf[sizeof(shader_key_buf) - 1] = '\0';
  } else {
    shader_key_buf[0] = '\0';
  }
  lua_pop(L, 2); // pop "key" string and the shader ref

  ResEntry *sh_e = res_table_get(&g_app_for_lua->res, shader_key_buf);
  if (!sh_e || sh_e->kind != RES_SHADER) {
    return luaL_error(L, "draw: shader not found: %s", shader_key_buf);
  }

  // pipeline state options (with defaults)
  int blend = SGL_BLEND_NONE;
  int cull = SGL_CULL_BACK;
  int prim = SGL_PRIM_TRIANGLES;
  bool depth_test = true;
  bool depth_write = true;
  int instance_count = 1;

  lua_getfield(L, 3, "blend");
  if (lua_isinteger(L, -1))
    blend = (int)lua_tointeger(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, 3, "cull");
  if (lua_isinteger(L, -1))
    cull = (int)lua_tointeger(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, 3, "primitive");
  if (lua_isinteger(L, -1))
    prim = (int)lua_tointeger(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, 3, "depth");
  if (!lua_isnoneornil(L, -1))
    depth_test = lua_toboolean(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, 3, "depth_write");
  if (!lua_isnoneornil(L, -1))
    depth_write = lua_toboolean(L, -1);
  lua_pop(L, 1);
  lua_getfield(L, 3, "instance_count");
  if (lua_isinteger(L, -1))
    instance_count = (int)lua_tointeger(L, -1);
  lua_pop(L, 1);
  if (instance_count <= 0)
    return 0;

  // bindings: walk resources table FIRST so we know whether the draw is
  // indexed (bind.ibuf != 0) before picking a pipeline.
  BindingsDesc bind = {0};
  bind.refl = &sh_e->u.sh.refl;
  uint8_t depth_tex_mask = 0;

  lua_pushnil(L);
  while (lua_next(L, 2) != 0) {
    // stack: -2 = key, -1 = value
    if (lua_istable(L, -1)) {
      lua_getfield(L, -1, "__lub_kind");
      const char *kind = lua_isstring(L, -1) ? lua_tostring(L, -1) : "";
      char kind_buf[16];
      strncpy(kind_buf, kind, sizeof(kind_buf) - 1);
      kind_buf[sizeof(kind_buf) - 1] = '\0';
      lua_pop(L, 1);

      if (strcmp(kind_buf, "buffer") == 0) {
        // Use lua_type rather than lua_isstring — the latter returns true for
        // numbers and lua_tostring would coerce the value in place, which
        // corrupts lua_next iteration.
        const char *res_name =
            lua_type(L, -2) == LUA_TSTRING ? lua_tostring(L, -2) : NULL;
        lua_getfield(L, -1, "key");
        const char *bk = lua_tostring(L, -1);
        ResEntry *be = bk ? res_table_get(&g_app_for_lua->res, bk) : NULL;
        lua_pop(L, 1);
        if (be && be->kind == RES_BUFFER) {
          if (res_name && strcmp(res_name, "indices") == 0) {
            if (be->u.buf.type != SGL_BUFFER_INDEX) {
              return luaL_error(
                  L, "draw: 'indices' must be an INDEX buffer (got type %d)",
                  (int)be->u.buf.type);
            }
            bind.ibuf = be->u.buf.h;
          } else if (res_name && strcmp(res_name, "instances") == 0) {
            if (be->u.buf.type == SGL_BUFFER_VERTEX ||
                be->u.buf.type == SGL_BUFFER_STORAGE) {
              bind.instance_vbuf = be->u.buf.h;
            }
          } else if (be->u.buf.type == SGL_BUFFER_VERTEX ||
                     be->u.buf.type == SGL_BUFFER_STORAGE) {
            // STORAGE buffers can also serve as a vertex source — they
            // are declared with both vertex_buffer + storage_buffer usage
            // so the same buffer can flow from compute write to draw read.
            bind.vbuf = be->u.buf.h;
          }
        }
      } else if (strcmp(kind_buf, "texture") == 0) {
        const char *res_name =
            lua_type(L, -2) == LUA_TSTRING ? lua_tostring(L, -2) : NULL;
        lua_getfield(L, -1, "key");
        const char *tk = lua_tostring(L, -1);
        ResEntry *te = tk ? res_table_get(&g_app_for_lua->res, tk) : NULL;
        lua_pop(L, 1);
        if (te && te->kind == RES_TEXTURE && res_name &&
            bind.texture_count <
                (int)(sizeof(bind.textures) / sizeof(bind.textures[0]))) {
          bind.textures[bind.texture_count].name = res_name;
          bind.textures[bind.texture_count].image = te->u.tex.h;
          bind.texture_count++;
          if (is_depth_format(te->u.tex.fmt)) {
            for (int k = 0; k < sh_e->u.sh.refl.tex_count; ++k) {
              if (strcmp(sh_e->u.sh.refl.texs[k].name, res_name) == 0) {
                depth_tex_mask |= (uint8_t)(1u << k);
                break;
              }
            }
          }
        }
      }
      // uniforms processing handled separately below (resources.uniforms key)
    }
    lua_pop(L, 1); // value, key stays for lua_next
  }

  // pipeline lookup (after bindings walk so we know is_indexed)
  BackendPipeline pip = pipeline_cache_get(
      &g_app_for_lua->pip_cache, sh_e->u.sh.h, &sh_e->u.sh.refl,
      (SglBlend)blend, depth_test, depth_write, (SglCull)cull,
      (SglPrimitive)prim, g_app_for_lua->pass.current_n_color_targets,
      g_app_for_lua->pass.current_color_fmts,
      g_app_for_lua->pass.current_has_depth,
      g_app_for_lua->pass.current_depth_fmt, (bind.ibuf != 0), depth_tex_mask,
      (int64_t)g_app_for_lua->frame_index);
  g_backend->apply_pipeline(pip);
  g_backend->apply_bindings(&bind);

  // uniforms: read resources.uniforms = { ub_member_name = {floats...} } and
  // pack every reflected graphics uniform block. Blocks are not exposed to Lua;
  // each block picks only its own members from the shared table.
  lua_getfield(L, 2, "uniforms");
  if (lua_istable(L, -1) && sh_e->u.sh.refl.ub_count > 0) {
    enum { UB_MAX_FLOATS = 256 };
    float buf[UB_MAX_FLOATS];
    for (int i = 0; i < sh_e->u.sh.refl.ub_count; ++i) {
      const ShaderUniformBlock *ub = &sh_e->u.sh.refl.ubs[i];
      if (ub->stage == SGL_STAGE_COMPUTE)
        continue;
      pack_uniform_block(L, lua_gettop(L), ub, buf, UB_MAX_FLOATS, "draw");
      g_backend->apply_uniforms(
          ub->stage, ub->slot, buf,
          (size_t)(ub->size_floats < 0 ? 0 : ub->size_floats) * sizeof(float));
    }
  }
  lua_pop(L, 1); // pop "uniforms" field (or nil)

  g_backend->draw(0, count, instance_count);
  return 0;
}

static int l_end_pass(lua_State *L) {
  (void)L;
  pass_state_end(&g_app_for_lua->pass);
  return 0;
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
  int w = 0, h = 0;
  if (g_app_for_lua && g_app_for_lua->window) {
    SDL_GetWindowSizeInPixels(g_app_for_lua->window, &w, &h);
  }
  if (w <= 0)
    w = 1280;
  if (h <= 0)
    h = 720;
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
static void call_module_field(LuaCtx *ctx, const char *name, int nargs) {
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
  call_module_field(ctx, "onInit", 0);
}
void lua_ctx_call_frame(LuaCtx *ctx, double dt) {
  if (!ctx->L)
    return;
  // onFrame(dt): dt は直近フレームの実測秒。引数なしの既存 onFrame() は
  // Lua が余分な引数を無視するのでそのまま動く。
  lua_pushnumber(ctx->L, dt);
  call_module_field(ctx, "onFrame", 1);
}
void lua_ctx_call_quit(LuaCtx *ctx) {
  if (!ctx->L)
    return;
  call_module_field(ctx, "onQuit", 0);
}

void lua_ctx_call_event(LuaCtx *ctx, const SDL_Event *e) {
  if (!ctx->L)
    return;
  push_event_table(ctx->L, e);
  call_module_field(ctx, "onEvent", 1);
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
