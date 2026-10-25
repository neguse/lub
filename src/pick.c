// Gfx.Pick (pick.h)。
#include "pick.h"
#include "api_internal.h"
#include "app.h"
#include "lua_api.h"
#include "pass.h"
#include "pipeline.h"
#include "resources.h"
#include "shader.h"
#if defined(LUB_HAS_OPENXR)
#include "xr.h"
#endif
#include <SDL3/SDL.h>
#include <stdlib.h>
#include <string.h>

// 流し直しの fragment shader。
// 頂点の出力は読まず、draw の番号を色にして書く (r が下位 8 bit)
static const char PICK_FS[] =
    "struct LubPick { float4 id; };\n"
    "ConstantBuffer<LubPick> lub_pick;\n"
    "[shader(\"fragment\")] float4 fs_main(float4 pos : SV_Position) : "
    "SV_Target {\n"
    "  return lub_pick.id;\n"
    "}\n";

typedef struct PickUniform {
  int stage, slot;
  size_t bytes;
  float *data;
} PickUniform;

typedef struct PickDraw {
  int index; // frame の中の draw の番号
  int target;
  int32_t shader; // LubHandle
  BindingsDesc bind;
  int blend, cull, prim;
  bool depth_test, depth_write;
  int count, instance_count;
  int n_uniforms;
  PickUniform uniforms[SGL_MAX_UNIFORM_BLOCKS];
  char *site;
} PickDraw;

typedef struct PickState {
  // 要求 (次の frame を記録する)
  bool requested;
  int req_target, req_x, req_y;
  // 記録中か、流し直し中の要求
  int target, x, y;
  // 記録中の frame
  bool recording;
  int pass_target;
  int draw_count;
  PickDraw *draws;
  int n_draws, cap_draws;
  // 流し直しの先
  BackendImage color, depth;
  int w, h;
  // 待っている読み戻し。
  // stale は、その後に新しい要求が来たので結果を捨てる印
  bool waiting;
  bool stale;
  BackendReadback readback;
  int rb_x, rb_y;
  // target の大きさが分からず流し直せなかった frame の数 (要求ごと)
  int retries;
  // 結果
  bool ready;
  int result_draw;
  char *result_site;
  // 読み戻しが届くまでの draw の呼び出し元 (番号で引く)
  char **sites;
  int n_sites;
  // 記録した draw が使っていて、その frame の間に作り直しか書き換えになった buffer と image。
  // 流し直した後に壊す
  BackendBuffer *kept_bufs;
  int n_kept_bufs, cap_kept_bufs;
  BackendImage *kept_images;
  int n_kept_images, cap_kept_images;
} PickState;

// target の大きさが分からないまま記録し直すのは、この frame 数まで (XR の眼が出ないままなど)
#define PICK_MAX_RETRIES 120

static PickState *state(App *app) {
  if (!app->pick)
    app->pick = (PickState *)calloc(1, sizeof(PickState));
  return app->pick;
}

static void free_draws(PickState *p) {
  for (int i = 0; i < p->n_draws; ++i) {
    for (int u = 0; u < p->draws[i].n_uniforms; ++u)
      free(p->draws[i].uniforms[u].data);
    free(p->draws[i].site);
  }
  p->n_draws = 0;
}

static void free_sites(PickState *p) {
  for (int i = 0; i < p->n_sites; ++i)
    free(p->sites[i]);
  free(p->sites);
  p->sites = NULL;
  p->n_sites = 0;
}

void pick_request(App *app, int target, int x, int y) {
  PickState *p = state(app);
  if (!p)
    return;
  // 前の要求を記録しているか読み戻しを待っていたら、その結果は捨てる (この要求は、それが済んだ後の frame で記録する)
  if (p->recording || p->waiting)
    p->stale = true;
  p->requested = true;
  p->req_target = target;
  p->req_x = x;
  p->req_y = y;
  p->retries = 0;
  p->ready = false;
  free(p->result_site);
  p->result_site = NULL;
}

void pick_frame_begin(App *app) {
  PickState *p = app->pick;
  if (!p || p->recording || !p->requested || p->waiting)
    return;
  p->requested = false;
  p->target = p->req_target;
  p->x = p->req_x;
  p->y = p->req_y;
  p->recording = true;
  p->draw_count = 0;
  p->pass_target = -2;
  free_draws(p);
}

void pick_begin_pass(App *app, int target) {
  if (app->pick)
    app->pick->pass_target = target;
}

void pick_record_draw(App *app, ResEntry *sh, const BindingsDesc *bind,
                      int blend, int cull, int prim, bool depth_test,
                      bool depth_write, int count, int instance_count) {
  PickState *p = app->pick;
  if (!p || !p->recording || app->in_overlay)
    return;
  int index = p->draw_count++;
  if (p->pass_target != p->target || !sh->u.sh.vs_src)
    return;
  if (p->n_draws == p->cap_draws) {
    int cap = p->cap_draws ? p->cap_draws * 2 : 64;
    PickDraw *grown =
        (PickDraw *)realloc(p->draws, (size_t)cap * sizeof(*grown));
    if (!grown)
      return;
    p->draws = grown;
    p->cap_draws = cap;
  }
  PickDraw *d = &p->draws[p->n_draws++];
  memset(d, 0, sizeof(*d));
  d->index = index;
  d->target = p->pass_target;
  d->shader = sh->handle;
  d->bind = *bind;
  d->blend = blend;
  d->cull = cull;
  d->prim = prim;
  d->depth_test = depth_test;
  d->depth_write = depth_write;
  d->count = count;
  d->instance_count = instance_count;
  d->site = lua_ctx_call_sites(&app->lua);
}

void pick_record_uniform(App *app, int stage, int slot, const float *data,
                         size_t bytes) {
  PickState *p = app->pick;
  if (!p || !p->recording || app->in_overlay || p->n_draws == 0)
    return;
  PickDraw *d = &p->draws[p->n_draws - 1];
  // 直前の pick_record_draw が記録しなかった draw の uniform は捨てる
  if (d->index != p->draw_count - 1 || stage != SGL_STAGE_VERTEX ||
      d->n_uniforms >= SGL_MAX_UNIFORM_BLOCKS)
    return;
  float *copy = (float *)malloc(bytes);
  if (!copy)
    return;
  memcpy(copy, data, bytes);
  d->uniforms[d->n_uniforms++] = (PickUniform){stage, slot, bytes, copy};
}

static bool draws_use(const PickState *p, uintptr_t obj) {
  for (int i = 0; i < p->n_draws; ++i) {
    const BindingsDesc *b = &p->draws[i].bind;
    if (b->ibuf == obj)
      return true;
    for (int t = 0; t < b->texture_count; ++t)
      if (b->textures[t].image == obj)
        return true;
    for (int s = 0; s < b->storage_buf_count; ++s)
      if (b->storage_bufs[s].buf == obj)
        return true;
  }
  return false;
}

static bool grow(void **items, int *cap, int n, size_t size) {
  if (n < *cap)
    return true;
  int next = *cap ? *cap * 2 : 8;
  void *grown = realloc(*items, (size_t)next * size);
  if (!grown)
    return false;
  *items = grown;
  *cap = next;
  return true;
}

bool pick_take_buffer(App *app, BackendBuffer buf) {
  PickState *p = app->pick;
  if (!p || !p->recording || !buf || !draws_use(p, buf) ||
      !grow((void **)&p->kept_bufs, &p->cap_kept_bufs, p->n_kept_bufs,
            sizeof(*p->kept_bufs)))
    return false;
  p->kept_bufs[p->n_kept_bufs++] = buf;
  return true;
}

bool pick_take_image(App *app, BackendImage image) {
  PickState *p = app->pick;
  if (!p || !p->recording || !image || !draws_use(p, image) ||
      !grow((void **)&p->kept_images, &p->cap_kept_images, p->n_kept_images,
            sizeof(*p->kept_images)))
    return false;
  p->kept_images[p->n_kept_images++] = image;
  return true;
}

static void destroy_kept(PickState *p) {
  for (int i = 0; i < p->n_kept_bufs; ++i)
    g_backend->destroy_buffer(p->kept_bufs[i]);
  for (int i = 0; i < p->n_kept_images; ++i)
    g_backend->destroy_image(p->kept_images[i]);
  p->n_kept_bufs = 0;
  p->n_kept_images = 0;
}

// 元の shader の vertex shader と PICK_FS を組んだ shader を作る (一度だけ)
static bool ensure_pick_shader(ResEntry *sh) {
  if (sh->u.sh.pick_h)
    return true;
  char err[1024];
  ShaderBlob vsb = {0}, fsb = {0};
  ShaderReflection refl;
  if (!shader_compile_for_backend(sh->u.sh.vs_src, PICK_FS, &vsb, &fsb, &refl,
                                  err, sizeof(err))) {
    SDL_Log("pick: cannot compile the pick shader for '%s': %s", sh->key, err);
    shader_blob_free(&vsb);
    shader_blob_free(&fsb);
    return false;
  }
  ShaderDesc sd = {.vs_spirv = vsb.spirv,
                   .vs_bytes = vsb.bytes,
                   .fs_spirv = fsb.spirv,
                   .fs_bytes = fsb.bytes,
                   .refl = &refl};
  sh->u.sh.pick_h = g_backend->make_shader(&sd);
  shader_blob_free(&vsb);
  shader_blob_free(&fsb);
  if (!sh->u.sh.pick_h)
    return false;
  sh->u.sh.pick_refl = refl;
  return true;
}

// 元の束縛を pick の shader の reflection で引き直す (fragment shader だけが使う texture は落ちる)
static BindingsDesc rebind(const PickDraw *d, const ShaderReflection *refl) {
  BindingsDesc b = {0};
  b.refl = refl;
  b.ibuf = d->bind.ibuf;
  for (int i = 0; i < d->bind.texture_count; ++i)
    for (int t = 0; t < refl->tex_count; ++t)
      if (strcmp(refl->texs[t].name, d->bind.textures[i].name) == 0) {
        b.textures[b.texture_count].name = refl->texs[t].name;
        b.textures[b.texture_count].image = d->bind.textures[i].image;
        b.texture_count++;
      }
  for (int i = 0; i < d->bind.storage_buf_count; ++i)
    for (int s = 0; s < refl->storage_buf_count; ++s)
      if (strcmp(refl->storage_bufs[s].name, d->bind.storage_bufs[i].name) ==
          0) {
        b.storage_bufs[b.storage_buf_count].name = refl->storage_bufs[s].name;
        b.storage_bufs[b.storage_buf_count].buf = d->bind.storage_bufs[i].buf;
        b.storage_buf_count++;
      }
  return b;
}

static bool target_size(App *app, int target, int *w, int *h) {
  if (target < 0) {
    *w = app->last_w;
    *h = app->last_h;
    return *w > 0 && *h > 0;
  }
#if defined(LUB_HAS_OPENXR)
  XrEyeImage eye;
  if (lubxr_eye_image(target, &eye)) {
    *w = eye.width;
    *h = eye.height;
    return true;
  }
#endif
  return false;
}

static bool ensure_images(PickState *p, int w, int h) {
  if (p->color && p->w == w && p->h == h)
    return true;
  if (p->color)
    g_backend->destroy_image(p->color);
  if (p->depth)
    g_backend->destroy_image(p->depth);
  ImageDesc cd = {.fmt = SGL_PF_RGBA8, .w = w, .h = h, .render_target = true};
  ImageDesc dd = {
      .fmt = SGL_PF_DEPTH24_STENCIL8, .w = w, .h = h, .render_target = true};
  p->color = g_backend->make_image(&cd);
  p->depth = g_backend->make_image(&dd);
  p->w = w;
  p->h = h;
  return p->color && p->depth;
}

typedef enum ReplayResult {
  REPLAY_STARTED, // 読み戻しを待つ
  REPLAY_RETRY,   // target がこの frame に描かれていない (大きさが無い)
  REPLAY_NOTHING, // 指した所に何も無い (範囲の外) か、流し直せない
} ReplayResult;

static ReplayResult replay(App *app, PickState *p) {
  int w = 0, h = 0;
  if (!target_size(app, p->target, &w, &h))
    return REPLAY_RETRY;
  if (p->x < 0 || p->y < 0 || p->x >= w || p->y >= h || !ensure_images(p, w, h))
    return REPLAY_NOTHING;
  SglPixelFormat fmt = SGL_PF_RGBA8;
  const float clear[1][4] = {{0, 0, 0, 0}};
  pass_state_begin_ex(&app->pass, 1, &p->color, &fmt, w, h, clear, p->depth,
                      SGL_PF_DEPTH24_STENCIL8, 1.0f, SGL_LOAD_CLEAR);
  g_backend->set_scissor(p->x, p->y, 1, 1);
  for (int i = 0; i < p->n_draws; ++i) {
    const PickDraw *d = &p->draws[i];
    ResEntry *sh = res_table_get_by_handle(&app->res, d->shader);
    if (!sh || sh->kind != RES_SHADER || !sh->u.sh.vs_src ||
        !ensure_pick_shader(sh))
      continue;
    const ShaderReflection *refl = &sh->u.sh.pick_refl;
    BackendPipeline pip = pipeline_cache_get(
        &app->pip_cache, sh->u.sh.pick_h, refl, SGL_BLEND_NONE, d->depth_test,
        d->depth_write, (SglCull)d->cull, (SglPrimitive)d->prim, 1, &fmt, true,
        SGL_PF_DEPTH24_STENCIL8, 0, app->resource_frame);
    if (!pip)
      continue;
    g_backend->apply_pipeline(pip);
    BindingsDesc b = rebind(d, refl);
    g_backend->apply_bindings(&b);
    for (int u = 0; u < d->n_uniforms; ++u)
      g_backend->apply_uniforms((SglShaderStage)d->uniforms[u].stage,
                                d->uniforms[u].slot, d->uniforms[u].data,
                                d->uniforms[u].bytes);
    int id = d->index + 1;
    float color[4] = {(float)(id & 255) / 255.0f,
                      (float)((id >> 8) & 255) / 255.0f,
                      (float)((id >> 16) & 255) / 255.0f, 1.0f};
    for (int k = 0; k < refl->ub_count; ++k)
      if (refl->ubs[k].stage == SGL_STAGE_FRAGMENT)
        g_backend->apply_uniforms(SGL_STAGE_FRAGMENT, refl->ubs[k].slot, color,
                                  sizeof(color));
    g_backend->draw(0, d->count, d->instance_count);
  }
  pass_state_end(&app->pass);
  if (!g_backend->request_readback_image ||
      !g_backend->request_readback_image(app, p->color, w, h, SGL_PF_RGBA8,
                                         &p->readback))
    return REPLAY_NOTHING;
  // 結果の呼び出し元は draw の番号で引くので、読み戻しが届くまで残す
  free_sites(p);
  p->sites = (char **)calloc((size_t)(p->draw_count ? p->draw_count : 1),
                             sizeof(char *));
  if (p->sites) {
    p->n_sites = p->draw_count;
    for (int i = 0; i < p->n_draws; ++i) {
      p->sites[p->draws[i].index] = p->draws[i].site;
      p->draws[i].site = NULL;
    }
  }
  p->waiting = true;
  p->rb_x = p->x;
  p->rb_y = p->y;
  return REPLAY_STARTED;
}

// 指した所に何も無いという結果にする
static void deliver_nothing(PickState *p) {
  p->ready = true;
  p->result_draw = -1;
  free(p->result_site);
  p->result_site = NULL;
}

static void poll(PickState *p) {
  ReadbackResult r = {0};
  ReadbackPollStatus st = g_backend->poll_readback(p->readback, false, &r);
  if (st == READBACK_POLL_PENDING)
    return;
  g_backend->destroy_readback(p->readback);
  p->waiting = false;
  if (p->stale) {
    p->stale = false;
    free(r.data);
    free_sites(p);
    return;
  }
  p->ready = true;
  p->result_draw = -1;
  if (st == READBACK_POLL_READY && r.data && p->rb_x < r.w && p->rb_y < r.h) {
    const uint8_t *px =
        r.data + (size_t)p->rb_y * (size_t)r.stride + (size_t)p->rb_x * 4;
    int id = px[0] | (px[1] << 8) | (px[2] << 16);
    p->result_draw = id - 1;
  }
  free(r.data);
  free(p->result_site);
  p->result_site = NULL;
  if (p->result_draw >= 0 && p->result_draw < p->n_sites &&
      p->sites[p->result_draw])
    p->result_site = strdup(p->sites[p->result_draw]);
  free_sites(p);
}

void pick_frame_end(App *app) {
  PickState *p = app->pick;
  if (!p)
    return;
  if (p->recording) {
    p->recording = false;
    ReplayResult rr = replay(app, p);
    free_draws(p);
    destroy_kept(p);
    if (rr != REPLAY_STARTED) {
      // 記録の間に新しい要求が来ていれば、そちらを次の frame で記録する
      bool newer = p->stale;
      p->stale = false;
      if (!newer && rr == REPLAY_RETRY && ++p->retries < PICK_MAX_RETRIES) {
        // target が描かれなかった frame (swapchain が取れなかったなど) は、同じ要求を次の frame で記録し直す
        p->requested = true;
        p->req_target = p->target;
        p->req_x = p->x;
        p->req_y = p->y;
      } else if (!newer) {
        deliver_nothing(p);
      }
    }
  }
  if (p->waiting)
    poll(p);
}

bool pick_result(App *app, int *draw, const char **site) {
  PickState *p = app->pick;
  if (!p || !p->ready)
    return false;
  *draw = p->result_draw;
  *site = p->result_site ? p->result_site : "";
  return true;
}

void pick_shutdown(App *app) {
  PickState *p = app->pick;
  if (!p)
    return;
  free_draws(p);
  free(p->draws);
  free_sites(p);
  free(p->result_site);
  if (g_backend)
    destroy_kept(p);
  free(p->kept_bufs);
  free(p->kept_images);
  if (p->waiting && g_backend)
    g_backend->destroy_readback(p->readback);
  if (p->color && g_backend)
    g_backend->destroy_image(p->color);
  if (p->depth && g_backend)
    g_backend->destroy_image(p->depth);
  free(p);
  app->pick = NULL;
}
