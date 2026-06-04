#include "lua_api.h"
#include "app.h"
#include "backend.h"
#include "capture.h"
#include "enums.h"
#include "enums_lua.h"
#include "gltf.h"
#include "pass.h"
#include "pipeline.h"
#include "resources.h"
#include "shader.h"
#include "stb_image.h"
#include <SDL3/SDL.h>
#include <ctype.h>
#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

static App *g_app_for_lua = NULL;

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

// Helper: push a BufferRef sentinel table { __lub_kind = "buffer", key = key }
static void push_buffer_ref(lua_State *L, const char *key) {
  lua_newtable(L);
  lua_pushstring(L, "buffer");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, key);
  lua_setfield(L, -2, "key");
}

// Helper: push a ShaderRef sentinel table { __lub_kind = "shader", key = key }
static void push_shader_ref(lua_State *L, const char *key) {
  lua_newtable(L);
  lua_pushstring(L, "shader");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, key);
  lua_setfield(L, -2, "key");
}

// Helper: push a TextureRef sentinel table { __lub_kind = "texture", key = key
// }
static void push_texture_ref(lua_State *L, const char *key) {
  lua_newtable(L);
  lua_pushstring(L, "texture");
  lua_setfield(L, -2, "__lub_kind");
  lua_pushstring(L, key);
  lua_setfield(L, -2, "key");
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

  lua_getfield(L, 1, "clear_depth");
  if (lua_isnumber(L, -1))
    clear_depth = (float)lua_tonumber(L, -1);
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
                        clear_depth);
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
                     c[1], c[2], c[3]);
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
                        clear_depth);
  } else {
    float clears[1][4] = {{0, 0, 0, 1}};
    pass_state_begin_ex(&g_app_for_lua->pass, 0, NULL, NULL, depth_w, depth_h,
                        (const float (*)[4])clears, depth_image, depth_fmt,
                        clear_depth);
  }
  return 0;
}

static int l_use_buffer(lua_State *L) {
  const char *key = luaL_checkstring(L, 1);
  int type = (int)luaL_checkinteger(L, 2);
  int64_t version = (int64_t)luaL_checkinteger(L, 4);

  if (type != SGL_BUFFER_VERTEX && type != SGL_BUFFER_INDEX &&
      type != SGL_BUFFER_STORAGE) {
    return luaL_error(L, "use_buffer: only VERTEX/INDEX/STORAGE are supported");
  }

  ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_BUFFER);
  if (!e)
    return luaL_error(L, "use_buffer: key '%s' already used as different kind",
                      key);

  res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

  if (e->version == version && e->u.buf.h != 0) {
    // Skip upload — return existing BufferRef
    push_buffer_ref(L, key);
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
    int n = (int)lua_rawlen(L, 3);
    if (n <= 0)
      return luaL_error(L, "use_buffer: empty data");
    uint32_t *idx = (uint32_t *)malloc((size_t)n * sizeof(uint32_t));
    if (!idx)
      return luaL_error(L, "use_buffer: out of memory");
    for (int i = 0; i < n; ++i) {
      lua_rawgeti(L, 3, i + 1);
      idx[i] = (uint32_t)lua_tonumber(L, -1);
      lua_pop(L, 1);
    }
    new_bytes = (size_t)n * sizeof(uint32_t);
    data = idx;
  } else {
    // VERTEX / STORAGE with data
    luaL_checktype(L, 3, LUA_TTABLE);
    int n = (int)lua_rawlen(L, 3);
    if (n <= 0)
      return luaL_error(L, "use_buffer: empty data");
    float *fdata = (float *)malloc((size_t)n * sizeof(float));
    if (!fdata)
      return luaL_error(L, "use_buffer: out of memory");
    for (int i = 0; i < n; ++i) {
      lua_rawgeti(L, 3, i + 1);
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

  push_buffer_ref(L, key);
  return 1;
}

static int l_use_texture(lua_State *L) {
  const char *key = luaL_checkstring(L, 1);
  int w = (int)luaL_checkinteger(L, 2);
  int h = (int)luaL_checkinteger(L, 3);
  int fmt = (int)luaL_checkinteger(L, 4);
  int has_data = !lua_isnoneornil(L, 5);
  if (has_data)
    luaL_checktype(L, 5, LUA_TTABLE);
  int64_t version = (int64_t)luaL_checkinteger(L, 6);

  // optional 7th arg: { filter = LINEAR|NEAREST, wrap = REPEAT|CLAMP, target =
  // bool }
  SglFilter filter = SGL_FILTER_LINEAR;
  SglWrap wrap = SGL_WRAP_REPEAT;
  bool is_target = false;
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
  }
  if (is_target && has_data) {
    return luaL_error(
        L, "use_texture: render target cannot be initialized with data");
  }
  bool depth_fmt = is_depth_format((SglPixelFormat)fmt);
  if (depth_fmt && !is_target) {
    return luaL_error(
        L, "use_texture: depth formats are only supported with {target=true}");
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

  if (e->version == version && e->u.tex.h != 0 && !sampler_changed &&
      !target_changed) {
    push_texture_ref(L, key);
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
  case SGL_PF_RGBA16F:
  case SGL_PF_RGBA32F:
  case SGL_PF_DEPTH16:
  case SGL_PF_DEPTH24_STENCIL8:
  case SGL_PF_DEPTH32F:
    bpp = 0;
    break;
  default:
    return luaL_error(L,
                      "use_texture: format not supported "
                      "(RGBA8/R8/RGBA16F/RGBA32F/depth target formats only)");
  }

  uint8_t *pixels = NULL;
  if (has_data) {
    if (bpp == 0) {
      return luaL_error(L, "use_texture: this texture format cannot be "
                           "initialized with byte data");
    }
    int n = (int)lua_rawlen(L, 5);
    if (n != w * h * bpp) {
      return luaL_error(L,
                        "use_texture: data size mismatch: got %d, expected %d",
                        n, w * h * bpp);
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
  }

  size_t new_bytes = pixels ? (size_t)w * (size_t)h * (size_t)bpp : 0;
  bool same_shape = (e->u.tex.h != 0) && (e->u.tex.w == w) &&
                    (e->u.tex.h_ == h) && (e->u.tex.fmt == (SglPixelFormat)fmt);
  if (same_shape && !sampler_changed && !target_changed && pixels &&
      new_bytes > 0) {
    // in-place update
    g_backend->update_image(e->u.tex.h, pixels, new_bytes);
  } else {
    if (e->u.tex.h != 0)
      g_backend->destroy_image(e->u.tex.h);
    ImageDesc d = {
        .fmt = (SglPixelFormat)fmt,
        .w = w,
        .h = h,
        .data = pixels,
        .data_bytes = new_bytes,
        .filter = filter,
        .wrap = wrap,
        .render_target = is_target,
    };
    e->u.tex.h = g_backend->make_image(&d);
    e->u.tex.w = w;
    e->u.tex.h_ = h;
    e->u.tex.fmt = (SglPixelFormat)fmt;
  }
  e->u.tex.filter = filter;
  e->u.tex.wrap = wrap;
  e->u.tex.is_target = is_target;
  e->version = version;

  if (pixels)
    free(pixels);

  push_texture_ref(L, key);
  return 1;
}

static int l_use_shader(lua_State *L) {
  const char *key = luaL_checkstring(L, 1);
  const char *vs = luaL_checkstring(L, 2);
  const char *fs = luaL_checkstring(L, 3);
  int64_t version = (int64_t)luaL_checkinteger(L, 4);

  ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_SHADER);
  if (!e)
    return luaL_error(L, "use_shader: key '%s' already used as different kind",
                      key);
  res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

  if (e->version == version && e->u.sh.h != 0) {
    push_shader_ref(L, key);
    return 1;
  }

  // version mismatch path. Compile into LOCAL temporaries first; only swap on
  // success.
  char err[1024];
  ShaderBlob vsb = {0}, fsb = {0};
  ShaderReflection new_refl;
  // Patch SPIR-V descriptor sets for the active backend.
  ShaderTargetBackend tgt =
      (g_backend && g_backend->name && strcmp(g_backend->name, "sdlgpu") == 0)
          ? SHADER_TARGET_SDLGPU
          : SHADER_TARGET_SOKOL;
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
    push_shader_ref(L, key);
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
    push_shader_ref(L, key);
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

  push_shader_ref(L, key);
  return 1;
}

static int l_use_shader_compute(lua_State *L) {
  const char *key = luaL_checkstring(L, 1);
  const char *cs = luaL_checkstring(L, 2);
  int64_t version = (int64_t)luaL_checkinteger(L, 3);

  ResEntry *e = res_table_get_or_create(&g_app_for_lua->res, key, RES_SHADER);
  if (!e)
    return luaL_error(
        L, "use_shader_compute: key '%s' already used as different kind", key);
  res_table_touch(e, (int64_t)g_app_for_lua->frame_index);

  if (e->version == version && e->u.sh.h != 0) {
    push_shader_ref(L, key);
    return 1;
  }

  char err[1024];
  ShaderBlob csb = {0};
  ShaderReflection new_refl;
  ShaderTargetBackend tgt =
      (g_backend && g_backend->name && strcmp(g_backend->name, "sdlgpu") == 0)
          ? SHADER_TARGET_SDLGPU
          : SHADER_TARGET_SOKOL;
  if (!shader_compile_compute(cs, tgt, &csb, &new_refl, err, sizeof(err))) {
    shader_blob_free(&csb);
    if (e->u.sh.h == 0) {
      return luaL_error(L, "compute shader compile error: %s", err);
    }
    SDL_Log(
        "use_shader_compute: recompile failed for key '%s': %s (keeping old)",
        key, err);
    push_shader_ref(L, key);
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
    push_shader_ref(L, key);
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
  push_shader_ref(L, key);
  return 1;
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
  dd.uniform_slot = -1;

  // Walk resources: storage buffers (by name) + uniforms (single block).
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
      }
    }
    lua_pop(L, 1);
  }

  // Uniforms: same packing as draw — first UB, std140 floats.
  float ubuf[256];
  lua_getfield(L, 4, "uniforms");
  if (lua_istable(L, -1) && sh_e->u.sh.refl.ub_count > 0) {
    const ShaderUniformBlock *ub = &sh_e->u.sh.refl.ubs[0];
    int total_floats = ub->size_floats < 0 ? 0 : ub->size_floats;
    if (total_floats > (int)(sizeof(ubuf) / sizeof(ubuf[0]))) {
      return luaL_error(L, "dispatch: uniform block too large (%d floats)",
                        total_floats);
    }
    memset(ubuf, 0, sizeof(ubuf));
    for (int m = 0; m < ub->member_count; ++m) {
      const ShaderUniformMember *mem = &ub->members[m];
      lua_getfield(L, -1, mem->name);
      if (lua_istable(L, -1)) {
        int n_provided = (int)lua_rawlen(L, -1);
        int copy = n_provided < mem->comp_count ? n_provided : mem->comp_count;
        for (int j = 0; j < copy; ++j) {
          lua_rawgeti(L, -1, j + 1);
          if (lua_isnumber(L, -1)) {
            ubuf[mem->offset_floats + j] = (float)lua_tonumber(L, -1);
          }
          lua_pop(L, 1);
        }
      }
      lua_pop(L, 1);
    }
    dd.uniform_slot = ub->slot;
    dd.uniform_data = ubuf;
    dd.uniform_bytes = (size_t)total_floats * sizeof(float);
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

  // bindings: walk resources table FIRST so we know whether the draw is
  // indexed (bind.ibuf != 0) before picking a pipeline.
  BindingsDesc bind = {0};
  bind.refl = &sh_e->u.sh.refl;

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
      g_app_for_lua->pass.current_depth_fmt, (bind.ibuf != 0),
      (int64_t)g_app_for_lua->frame_index);
  g_backend->apply_pipeline(pip);
  g_backend->apply_bindings(&bind);

  // uniforms: read resources.uniforms = { ub_member_name = {floats...} } and
  // pack into the shader's first uniform block. Multi-block binding is not yet
  // exposed.
  lua_getfield(L, 2, "uniforms");
  if (lua_istable(L, -1) && sh_e->u.sh.refl.ub_count > 0) {
    const ShaderUniformBlock *ub = &sh_e->u.sh.refl.ubs[0];
    int total_floats = ub->size_floats;
    if (total_floats < 0)
      total_floats = 0;
    // Stack-buffer up to a sensible cap; for matrices total is small.
    enum { UB_MAX_FLOATS = 256 };
    float buf[UB_MAX_FLOATS];
    memset(buf, 0, sizeof(buf));
    if (total_floats > UB_MAX_FLOATS) {
      return luaL_error(L, "draw: uniform block too large (%d floats > %d)",
                        total_floats, UB_MAX_FLOATS);
    }
    for (int m = 0; m < ub->member_count; ++m) {
      const ShaderUniformMember *mem = &ub->members[m];
      lua_getfield(L, -1, mem->name);
      if (lua_istable(L, -1)) {
        int n_provided = (int)lua_rawlen(L, -1);
        int copy = n_provided < mem->comp_count ? n_provided : mem->comp_count;
        for (int j = 0; j < copy; ++j) {
          lua_rawgeti(L, -1, j + 1);
          if (lua_isnumber(L, -1)) {
            buf[mem->offset_floats + j] = (float)lua_tonumber(L, -1);
          }
          lua_pop(L, 1);
        }
      }
      lua_pop(L, 1); // pop the field (or nil)
    }
    g_backend->apply_uniforms(ub->slot, buf,
                              (size_t)total_floats * sizeof(float));
  }
  lua_pop(L, 1); // pop "uniforms" field (or nil)

  g_backend->draw(0, count);
  return 0;
}

static int l_end_pass(lua_State *L) {
  (void)L;
  pass_state_end(&g_app_for_lua->pass);
  return 0;
}

static int l_capture(lua_State *L) {
  const char *path = luaL_checkstring(L, 1);
  capture_schedule(&g_app_for_lua->capture, path, 0); // 0 = next frame
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

// mouse_delta() -> dx, dy : relative motion (window pixels) since the last
// call. Consumes the accumulated delta, so call it once per frame.
static int l_mouse_delta(lua_State *L) {
  float dx = 0.0f, dy = 0.0f;
  SDL_GetRelativeMouseState(&dx, &dy);
  lua_pushnumber(L, (lua_Number)dx);
  lua_pushnumber(L, (lua_Number)dy);
  return 2;
}

// mouse_down(button) -> bool. button: 1=left (default), 2=middle, 3=right.
static int l_mouse_down(lua_State *L) {
  int btn = (int)luaL_optinteger(L, 1, 1);
  if (btn < 1)
    btn = 1;
  SDL_MouseButtonFlags mask = SDL_GetMouseState(NULL, NULL);
  lua_pushboolean(L, (mask & SDL_BUTTON_MASK(btn)) != 0);
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
      (lua_type(L, -1) == LUA_TSTRING) ? lua_tostring(L, -1) : "sokol";
  if (strcmp(name, "sokol") != 0 && strcmp(name, "sdlgpu") != 0) {
    return luaL_error(
        L, "config: backend must be 'sokol' or 'sdlgpu', got '%s'", name);
  }
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

static int l_load_png(lua_State *L) {
  const char *path = luaL_checkstring(L, 1);
  int w, h;
  unsigned char *pixels = stbi_load(path, &w, &h, NULL, STBI_rgb_alpha);
  if (!pixels) {
    SDL_Log("load_png: %s: %s", path, stbi_failure_reason());
    lua_pushnil(L);
    return 1;
  }
  int n = w * h * STBI_rgb_alpha;
  lua_createtable(L, n, 0);
  for (int i = 0; i < n; ++i) {
    lua_pushinteger(L, pixels[i]);
    lua_rawseti(L, -2, i + 1);
  }
  stbi_image_free(pixels);
  lua_pushinteger(L, w);
  lua_pushinteger(L, h);
  lua_pushinteger(L, SGL_PF_RGBA8);
  return 4; // (table, w, h, fmt)
}

void lua_api_register(lua_State *L) {
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
  lua_pushcfunction(L, l_capture);
  lua_setglobal(L, "capture");
  lua_pushcfunction(L, l_key_down);
  lua_setglobal(L, "key_down");
  lua_pushcfunction(L, l_mouse_delta);
  lua_setglobal(L, "mouse_delta");
  lua_pushcfunction(L, l_mouse_down);
  lua_setglobal(L, "mouse_down");
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
  lua_pushcfunction(L, l_fnv1a64);
  lua_setglobal(L, "fnv1a64");
  lua_pushcfunction(L, l_load_png);
  lua_setglobal(L, "load_png");
  lua_pushcfunction(L, lub_load_gltf);
  lua_setglobal(L, "load_gltf");
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
  lua_api_register(ctx->L);
  return true;
}

bool lua_ctx_load_entry(LuaCtx *ctx, const char *entry_module_name) {
  if (!ctx || !ctx->L || !entry_module_name)
    return false;
  if (luaL_loadfile(ctx->L, "samples/boot.lua") != LUA_OK) {
    SDL_Log("boot.lua load error: %s", lua_tostring(ctx->L, -1));
    lua_close(ctx->L);
    ctx->L = NULL;
    return false;
  }
  lua_pushstring(ctx->L, entry_module_name);
  if (lua_pcall(ctx->L, 1, 1, 0) != LUA_OK) {
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

void lua_ctx_add_package_path(LuaCtx *ctx, const char *entry_dir) {
  if (!ctx || !ctx->L || !entry_dir)
    return;
  lua_State *L = ctx->L;
  lua_getglobal(L, "package"); /* +1 */
  lua_getfield(L, -1, "path"); /* +1 */
  const char *cur = lua_tostring(L, -1);
  char buf[1024];
  SDL_snprintf(buf, sizeof(buf), "%s/.lub/?.lua;%s", entry_dir, cur ? cur : "");
  lua_pop(L, 1); /* drop old path */
  lua_pushstring(L, buf);
  lua_setfield(L, -2, "path"); /* set package.path */
  lua_pop(L, 1);               /* drop package */
}

void lua_ctx_call_init(LuaCtx *ctx) {
  if (!ctx->L)
    return;
  call_module_field(ctx, "onInit", 0);
}
void lua_ctx_call_frame(LuaCtx *ctx) {
  if (!ctx->L)
    return;
  call_module_field(ctx, "onFrame", 0);
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
