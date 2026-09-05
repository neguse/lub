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
#include <stdlib.h>
#include <string.h>

#define LUB_KEY_MAX 256

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

static ResEntry *entry_from_handle(App *app, LubHandle h, ResKind kind,
                                   const char *fn, const char *what) {
  ResEntry *e = res_table_get_by_handle(&app->res, h);
  if (!e) {
    lub_api_fail(app, "%s: %s handle %d is stale or invalid", fn, what, (int)h);
    return NULL;
  }
  if (e->kind != kind) {
    lub_api_fail(app, "%s: %s handle %d is not a %s", fn, what, (int)h,
                 kind == RES_TEXTURE  ? "texture"
                 : kind == RES_BUFFER ? "buffer"
                                      : "shader");
    return NULL;
  }
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

LubHandle lub_gfx_lookup(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  if (key.len <= 0)
    return 0;
  ResEntry *e = res_table_get_n(&app->res, key.ptr, (size_t)key.len);
  return e ? e->handle : 0;
}

LubStatus lub_gfx_resource_info(LubContext *ctx, LubHandle handle, LubStr *key,
                                int32_t *version) {
  App *app = lub_api_app(ctx);
  ResEntry *e = res_table_get_by_handle(&app->res, handle);
  if (!e)
    return lub_api_fail(app, "resource_info: handle %d is stale or invalid",
                        (int)handle);
  if (key)
    *key = lub_str_c(e->key);
  if (version)
    *version = (int32_t)e->version;
  return LUB_OK;
}

// ------------------------------------------------------------ resources

LubStatus lub_gfx_use_buffer(LubContext *ctx, LubStr key, int32_t type,
                             const void *data, int32_t bytes,
                             const int32_t *version, LubHandle *out) {
  App *app = lub_api_app(ctx);
  char kbuf[LUB_KEY_MAX];
  if (!key_arg(app, key, kbuf, "use_buffer"))
    return LUB_ERROR;
  if (type != SGL_BUFFER_VERTEX && type != SGL_BUFFER_INDEX &&
      type != SGL_BUFFER_STORAGE)
    return lub_api_fail(app,
                        "use_buffer: only VERTEX/INDEX/STORAGE are supported");
  if (bytes <= 0)
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
  res_table_touch(e, (int64_t)app->frame_index);

  if (!declared && e->version == ver && e->u.buf.h != 0) {
    *out = e->handle;
    return LUB_OK;
  }

  size_t new_bytes = (size_t)bytes;
  if (e->u.buf.h != 0 && e->u.buf.size_bytes == new_bytes &&
      e->u.buf.type == (SglBufferType)type && data) {
    g_backend->update_buffer(e->u.buf.h, data, new_bytes);
  } else {
    if (e->u.buf.h != 0)
      g_backend->destroy_buffer(e->u.buf.h);
    e->u.buf.h = g_backend->make_buffer((SglBufferType)type, data, new_bytes);
    e->u.buf.type = (SglBufferType)type;
    e->u.buf.size_bytes = new_bytes;
  }
  e->version = ver;
  *out = e->handle;
  return LUB_OK;
}

LubStatus lub_gfx_use_texture(LubContext *ctx, LubStr key,
                              const LubGfxTextureDesc *d,
                              const int32_t *version, LubHandle *out) {
  App *app = lub_api_app(ctx);
  char kbuf[LUB_KEY_MAX];
  if (!key_arg(app, key, kbuf, "use_texture"))
    return LUB_ERROR;
  if (!d)
    return lub_api_fail(app, "use_texture: desc required");
  if (!is_known_format(d->format))
    return lub_api_fail(app, "use_texture: format not supported "
                             "(RGBA8/R8/RG8/R16F/RG16F/R32F/RGBA16F/RGBA32F/"
                             "depth target formats only)");
  SglPixelFormat fmt = (SglPixelFormat)d->format;
  SglFilter filter = SGL_FILTER_LINEAR;
  SglWrap wrap = SGL_WRAP_REPEAT;
  bool filter_explicit = false;
  if (d->filter != 0) {
    if (d->filter != SGL_FILTER_LINEAR && d->filter != SGL_FILTER_NEAREST)
      return lub_api_fail(app,
                          "use_texture: opts.filter must be LINEAR or NEAREST");
    filter = (SglFilter)d->filter;
    filter_explicit = true;
  }
  if (d->wrap != 0) {
    if (d->wrap != SGL_WRAP_REPEAT && d->wrap != SGL_WRAP_CLAMP)
      return lub_api_fail(app,
                          "use_texture: opts.wrap must be REPEAT or CLAMP");
    wrap = (SglWrap)d->wrap;
  }
  bool has_data = d->pixels != NULL;
  if (d->target && has_data)
    return lub_api_fail(
        app, "use_texture: render target cannot be initialized with data");
  if (d->storage && has_data)
    return lub_api_fail(
        app, "use_texture: storage texture cannot be initialized with data");
  bool depth = is_depth_format(fmt);
  if (depth && !d->target)
    return lub_api_fail(
        app,
        "use_texture: depth formats are only supported with {target=true}");
  if (depth && d->storage)
    return lub_api_fail(app,
                        "use_texture: depth formats cannot use storage=true");
  if (depth) {
    // WebGPU can only sample depth as unfilterable-float; a filtering sampler
    // is a validation error there (and LINEAR on D32 is optional in Vulkan).
    if (filter_explicit && filter == SGL_FILTER_LINEAR)
      return lub_api_fail(
          app, "use_texture: depth textures must use NEAREST filter");
    filter = SGL_FILTER_NEAREST;
  }
  if (d->w <= 0 || d->h <= 0)
    return lub_api_fail(app, "use_texture: invalid size %dx%d", d->w, d->h);

  bool declared = false;
  int64_t ver = effective_version(app, version, &declared);
  ResEntry *e = res_table_get_or_create(&app->res, kbuf, RES_TEXTURE);
  if (!e)
    return lub_api_fail(
        app, "use_texture: key '%s' already used as different kind", kbuf);
  res_table_touch(e, (int64_t)app->frame_index);

  bool sampler_changed =
      (e->u.tex.h != 0) && (e->u.tex.filter != filter || e->u.tex.wrap != wrap);
  bool target_changed = (e->u.tex.h != 0) && (e->u.tex.is_target != d->target);
  bool storage_changed = (e->u.tex.h != 0) && (e->u.tex.storage != d->storage);
  if (!declared && e->version == ver && e->u.tex.h != 0 && !sampler_changed &&
      !target_changed && !storage_changed) {
    *out = e->handle;
    return LUB_OK;
  }

  size_t new_bytes = 0;
  if (has_data) {
    int bpp = bytes_per_pixel(fmt);
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

  bool same_shape = (e->u.tex.h != 0) && (e->u.tex.w == d->w) &&
                    (e->u.tex.h_ == d->h) && (e->u.tex.fmt == fmt);
  if (same_shape && !sampler_changed && !target_changed && !storage_changed &&
      has_data && new_bytes > 0) {
    g_backend->update_image(e->u.tex.h, d->pixels, new_bytes);
  } else {
    if (e->u.tex.h != 0)
      g_backend->destroy_image(e->u.tex.h);
    ImageDesc id = {
        .fmt = fmt,
        .w = d->w,
        .h = d->h,
        .data = has_data ? d->pixels : NULL,
        .data_bytes = new_bytes,
        .filter = filter,
        .wrap = wrap,
        .render_target = d->target,
        .storage = d->storage,
    };
    e->u.tex.h = g_backend->make_image(&id);
    e->u.tex.w = d->w;
    e->u.tex.h_ = d->h;
    e->u.tex.fmt = fmt;
  }
  e->u.tex.filter = filter;
  e->u.tex.wrap = wrap;
  e->u.tex.is_target = d->target;
  e->u.tex.storage = d->storage;
  e->version = ver;
  *out = e->handle;
  return LUB_OK;
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
  return use_shader_impl(lub_api_app(ctx), "use_shader", key, vs, fs, none,
                         version, out);
}

LubStatus lub_gfx_use_shader_compute(LubContext *ctx, LubStr key, LubStr cs,
                                     const int32_t *version, LubHandle *out) {
  LubStr none = {NULL, 0};
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

LubStatus lub_gfx_begin_pass(LubContext *ctx, const LubGfxPassDesc *d) {
  App *app = lub_api_app(ctx);
  if (!d)
    return lub_api_fail(app, "begin_pass: desc required");
  if (pass_state_in_pass(&app->pass))
    return lub_api_fail(app, "begin_pass: already inside a pass");
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
  App *app = lub_api_app(ctx);
  if (!pass_state_in_pass(&app->pass))
    return lub_api_fail(app, "end_pass: no pass is active");
  pass_state_end(&app->pass);
  return LUB_OK;
}

// ----------------------------------------------------------------- draw

enum { UB_MAX_FLOATS = 256 };

// reflection の uniform block を、名前つきの値の列から詰める。無い member は
// 0 のまま。
static void pack_uniform_block(const ShaderUniformBlock *ub,
                               const LubGfxUniform *uniforms, int32_t n,
                               float *dst) {
  memset(dst, 0, (size_t)UB_MAX_FLOATS * sizeof(float));
  for (int m = 0; m < ub->member_count; ++m) {
    const ShaderUniformMember *mem = &ub->members[m];
    for (int32_t i = 0; i < n; ++i) {
      if (!lub_str_eq(uniforms[i].name, mem->name))
        continue;
      int copy = uniforms[i].count < mem->comp_count ? uniforms[i].count
                                                     : mem->comp_count;
      if (mem->offset_floats + copy > UB_MAX_FLOATS)
        copy = UB_MAX_FLOATS - mem->offset_floats;
      for (int j = 0; j < copy; ++j)
        dst[mem->offset_floats + j] = uniforms[i].values[j];
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

static bool refl_has_storage_texture(const ShaderReflection *refl,
                                     LubStr name) {
  for (int i = 0; i < refl->storage_tex_count; ++i)
    if (lub_str_eq(name, refl->storage_texs[i].name))
      return true;
  return false;
}

LubStatus lub_gfx_draw(LubContext *ctx, const LubGfxDrawDesc *d) {
  App *app = lub_api_app(ctx);
  if (!d)
    return lub_api_fail(app, "draw: desc required");
  if (!pass_state_in_pass(&app->pass))
    return lub_api_fail(app, "draw: must be called inside begin_pass/end_pass");
  ResEntry *sh =
      entry_from_handle(app, d->shader, RES_SHADER, "draw", "shader");
  if (!sh)
    return LUB_ERROR;
  if (sh->u.sh.refl.is_compute)
    return lub_api_fail(app, "draw: shader '%s' is a compute shader", sh->key);
  int instance_count = d->instance_count <= 0 ? 1 : d->instance_count;
  int blend = d->blend ? d->blend : SGL_BLEND_NONE;
  int cull = d->cull ? d->cull : SGL_CULL_BACK;
  int prim = d->primitive ? d->primitive : SGL_PRIM_TRIANGLES;
  if (blend < SGL_BLEND_NONE || blend > SGL_BLEND_MULTIPLY)
    return lub_api_fail(app, "draw: invalid blend %d", blend);
  if (cull < SGL_CULL_NONE || cull > SGL_CULL_FRONT)
    return lub_api_fail(app, "draw: invalid cull %d", cull);
  if (prim < SGL_PRIM_TRIANGLES || prim > SGL_PRIM_POINTS)
    return lub_api_fail(app, "draw: invalid primitive %d", prim);

  BindingsDesc bind = {0};
  bind.refl = &sh->u.sh.refl;
  uint8_t depth_tex_mask = 0;
  // buffers: name で役割を決める ("indices" / "instances" / それ以外は vertex)
  for (int32_t i = 0; i < d->n_buffers; ++i) {
    const LubGfxBinding *b = &d->buffers[i];
    ResEntry *be =
        entry_from_handle(app, b->handle, RES_BUFFER, "draw", "buffer");
    if (!be)
      return LUB_ERROR;
    if (lub_str_eq(b->name, "indices")) {
      if (be->u.buf.type != SGL_BUFFER_INDEX)
        return lub_api_fail(
            app, "draw: 'indices' must be an INDEX buffer (got type %d)",
            (int)be->u.buf.type);
      bind.ibuf = be->u.buf.h;
    } else if (lub_str_eq(b->name, "instances")) {
      if (be->u.buf.type == SGL_BUFFER_VERTEX ||
          be->u.buf.type == SGL_BUFFER_STORAGE)
        bind.instance_vbuf = be->u.buf.h;
    } else if (be->u.buf.type == SGL_BUFFER_VERTEX ||
               be->u.buf.type == SGL_BUFFER_STORAGE) {
      // STORAGE buffers can also serve as a vertex source — they are declared
      // with both vertex_buffer + storage_buffer usage so the same buffer can
      // flow from compute write to draw read.
      bind.vbuf = be->u.buf.h;
    }
  }
  const int max_tex = (int)(sizeof(bind.textures) / sizeof(bind.textures[0]));
  for (int32_t i = 0; i < d->n_textures; ++i) {
    const LubGfxBinding *t = &d->textures[i];
    ResEntry *te =
        entry_from_handle(app, t->handle, RES_TEXTURE, "draw", "texture");
    if (!te)
      return LUB_ERROR;
    if (bind.texture_count >= max_tex)
      return lub_api_fail(app, "draw: too many textures (max %d)", max_tex);
    // name は shader の reflection 名 (NUL 終端の保証が要る)
    int ti = 0;
    if (!refl_texture_index(&sh->u.sh.refl, t->name, &ti))
      continue; // shader が使わない texture は無視 (従来どおり)
    bind.textures[bind.texture_count].name = sh->u.sh.refl.texs[ti].name;
    bind.textures[bind.texture_count].image = te->u.tex.h;
    bind.texture_count++;
    if (is_depth_format(te->u.tex.fmt))
      depth_tex_mask |= (uint8_t)(1u << ti);
  }

  BackendPipeline pip = pipeline_cache_get(
      &app->pip_cache, sh->u.sh.h, &sh->u.sh.refl, (SglBlend)blend,
      d->depth_test, d->depth_write, (SglCull)cull, (SglPrimitive)prim,
      app->pass.current_n_color_targets, app->pass.current_color_fmts,
      app->pass.current_has_depth, app->pass.current_depth_fmt,
      (bind.ibuf != 0), depth_tex_mask, (int64_t)app->frame_index);
  g_backend->apply_pipeline(pip);
  g_backend->apply_bindings(&bind);

  if (d->n_uniforms > 0 && sh->u.sh.refl.ub_count > 0) {
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
      pack_uniform_block(ub, d->uniforms, d->n_uniforms, buf);
      g_backend->apply_uniforms(ub->stage, ub->slot, buf,
                                (size_t)size * sizeof(float));
    }
  }
  g_backend->draw(0, d->vertex_count, instance_count);
  return LUB_OK;
}

LubStatus lub_gfx_dispatch(LubContext *ctx, const LubGfxDispatchDesc *d) {
  App *app = lub_api_app(ctx);
  if (!d)
    return lub_api_fail(app, "dispatch: desc required");
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
  const ShaderReflection *refl = &sh->u.sh.refl;
  BackendPipeline pip = pipeline_cache_get_compute(
      &app->pip_cache, sh->u.sh.h, refl, (int64_t)app->frame_index);
  ComputeDispatchDesc dd = {0};
  dd.pipeline = pip;
  dd.refl = refl;
  dd.groups_x = d->groups_x;
  dd.groups_y = d->groups_y;
  dd.groups_z = d->groups_z;

  for (int32_t i = 0; i < d->n_buffers; ++i) {
    const LubGfxBinding *b = &d->buffers[i];
    ResEntry *be =
        entry_from_handle(app, b->handle, RES_BUFFER, "dispatch", "buffer");
    if (!be)
      return LUB_ERROR;
    if (be->u.buf.type != SGL_BUFFER_STORAGE)
      continue;
    for (int k = 0; k < refl->storage_buf_count; ++k) {
      if (!lub_str_eq(b->name, refl->storage_bufs[k].name))
        continue;
      if (dd.n_storage_bufs < SGL_MAX_STORAGE_BUFS) {
        dd.storage_bufs[dd.n_storage_bufs].name = refl->storage_bufs[k].name;
        dd.storage_bufs[dd.n_storage_bufs].buf = be->u.buf.h;
        dd.n_storage_bufs++;
      }
      break;
    }
  }
  for (int32_t i = 0; i < d->n_textures; ++i) {
    const LubGfxBinding *t = &d->textures[i];
    ResEntry *te =
        entry_from_handle(app, t->handle, RES_TEXTURE, "dispatch", "texture");
    if (!te)
      return LUB_ERROR;
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
  if (d->n_uniforms > 0 && refl->ub_count > 0) {
    for (int i = 0;
         i < refl->ub_count && dd.uniform_count < SGL_MAX_UNIFORM_BLOCKS; ++i) {
      const ShaderUniformBlock *ub = &refl->ubs[i];
      int size = ub->size_floats < 0 ? 0 : ub->size_floats;
      if (size > UB_MAX_FLOATS)
        return lub_api_fail(
            app, "dispatch: uniform block too large (%d floats > %d)", size,
            UB_MAX_FLOATS);
      pack_uniform_block(ub, d->uniforms, d->n_uniforms,
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
  RbItem items[RB_MAX_DEPTH];
  // 直近に返した view の実体。次の呼び出しか shutdown で解放する。
  uint8_t *view_data;
  char *view_error;
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

static void rb_queue_drop_view(RbQueue *q) {
  free(q->view_data);
  q->view_data = NULL;
  free(q->view_error);
  q->view_error = NULL;
}

void api_gfx_shutdown(App *app) {
  struct GfxReadbackQueues *qs = app->readbacks;
  if (!qs)
    return;
  for (int i = 0; i < qs->n; ++i) {
    for (int k = 0; k < RB_MAX_DEPTH; ++k)
      rb_item_clear(&qs->q[i].items[k]);
    rb_queue_drop_view(&qs->q[i]);
  }
  free(qs);
  app->readbacks = NULL;
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
    if (lub_str_eq(key, qs->q[i].key))
      return &qs->q[i];
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
    it->error = SDL_strdup("read_texture: texture handle is stale or invalid");
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

// 完了した item を out に写し、view の実体を queue に預けて item を空にする。
static void rb_take(App *app, RbQueue *q, RbItem *it,
                    LubGfxReadbackResult *out) {
  rb_queue_drop_view(q);
  memset(out, 0, sizeof(*out));
  out->token = it->token;
  if (it->state == RB_ERROR || it->error) {
    out->status = LUB_GFX_READBACK_STATUS_ERROR;
    q->view_error = it->error ? it->error : SDL_strdup("read_texture: error");
    it->error = NULL;
    out->error = lub_str_c(q->view_error);
  } else {
    out->status = LUB_GFX_READBACK_STATUS_READY;
    q->view_data = it->rb.data;
    it->rb.data = NULL;
    out->pixels.ptr = q->view_data;
    out->pixels.len = (int32_t)it->rb.data_bytes;
    out->pixels.frame = (int32_t)app->frame_index;
    out->w = it->rb.w;
    out->h = it->rb.h;
    out->format = (int32_t)it->rb.fmt;
    out->stride = it->rb.stride;
  }
  rb_item_clear(it);
}

LubStatus lub_gfx_readback(LubContext *ctx, LubStr key, bool has_request,
                           LubHandle tex, int32_t token,
                           LubGfxReadbackResult *out) {
  App *app = lub_api_app(ctx);
  if (!out)
    return lub_api_fail(app, "readback: out required");
  if (pass_state_in_pass(&app->pass))
    return lub_api_fail(app,
                        "read_texture: cannot read while a pass is active");
  RbQueue *q = rb_queue_get(app, key);
  if (!q)
    return LUB_ERROR;
  memset(out, 0, sizeof(*out));
  if (q->count > 0 && rb_poll_item(&q->items[q->head])) {
    int idx = q->head;
    q->head = (q->head + 1) % RB_MAX_DEPTH;
    q->count--;
    if (has_request && q->count < q->depth)
      rb_enqueue(app, q, tex, token);
    rb_take(app, q, &q->items[idx], out);
    return LUB_OK;
  }
  if (has_request) {
    if (q->count >= q->depth) {
      out->status = LUB_GFX_READBACK_STATUS_DROPPED;
      out->token = token;
      return LUB_OK;
    }
    rb_enqueue(app, q, tex, token);
  }
  out->status = LUB_GFX_READBACK_STATUS_PROCESSING;
  return LUB_OK;
}
