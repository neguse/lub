// App.Use / App.Step: 同じ process の中に別の app を開き、その絵を texture に描かせる。
// 開いた app は自分の App (resource の表、物理、音、入力の控え) を持ち、Process (window、backend) を開いた側と分け合う。
// app の中身 (tcs VM と entry の呼び出し) は player が LubAppHooks で作る。hook が無い host では error。
#include "api_internal.h"
#include "backend.h"
#include "ui.h"
#include <stdlib.h>
#include <string.h>

typedef struct OpenApp {
  char key[LUB_KEY_MAX];
  char module[1024];
  char entry[256];
  // entry の寿命の間は同じ値 (1 始まりの通し番号)
  int32_t handle;
  App *app;
  // player の状態。最初の Step で作る (NULL はまだ開いていない)
  void *player;
  // main の pass の depth。target の大きさに合わせて作り直す
  BackendImage depth;
  int depth_w, depth_h;
  int64_t last_seen_frame;
} OpenApp;

// App.Save が書いた状態の控え
typedef struct StateSlot {
  int32_t slot;
  uint8_t *bytes;
  size_t size;
} StateSlot;

struct OpenApps {
  OpenApp *e;
  int n, cap;
  int32_t next_handle;
  StateSlot *states;
  int nstates, capstates;
  // Save / Load が返す理由 (次の呼び出しまで有効)
  char state_error[1024];
};

static void open_app_close(App *owner, OpenApp *o) {
  if (o->player)
    owner->app_hooks->close(o->player);
  if (o->depth)
    g_backend->destroy_image(o->depth);
  app_shutdown(o->app);
  free(o->app);
}

LubStatus lub_app_use(LubContext *ctx, LubStr key, LubStr module, LubStr entry,
                      LubHandle *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (app->opened_by)
    return lub_api_fail(app, "app_use: an opened app cannot open another app");
  if (!app->app_hooks)
    return lub_api_fail(app, "app_use: this player cannot open an app");
  OpenApp want = {0};
  if (key.len <= 0 || !lub_str_copy(key, want.key, sizeof(want.key)))
    return lub_api_fail(app, "app_use: key must be 1 to %d bytes",
                        LUB_KEY_MAX - 1);
  if (module.len <= 0 ||
      !lub_str_copy(module, want.module, sizeof(want.module)))
    return lub_api_fail(app, "app_use: module must be 1 to %d bytes",
                        (int)sizeof(want.module) - 1);
  if (!lub_str_copy(entry, want.entry, sizeof(want.entry)))
    return lub_api_fail(app, "app_use: entry is too long");
  if (!app->open_apps)
    app->open_apps = (struct OpenApps *)calloc(1, sizeof(struct OpenApps));
  struct OpenApps *s = app->open_apps;
  if (!s)
    return lub_api_fail(app, "app_use: out of memory");
  OpenApp *o = NULL;
  for (int i = 0; i < s->n; ++i)
    if (strcmp(s->e[i].key, want.key) == 0)
      o = &s->e[i];
  if (o && (strcmp(o->module, want.module) != 0 ||
            strcmp(o->entry, want.entry) != 0)) {
    // 同じ key の別の app: 閉じて、同じ場所に開き直す
    open_app_close(app, o);
    *o = s->e[s->n - 1];
    s->n--;
    o = NULL;
  }
  if (!o) {
    if (s->n == s->cap) {
      int cap = s->cap ? s->cap * 2 : 4;
      OpenApp *grown = (OpenApp *)realloc(s->e, sizeof(OpenApp) * (size_t)cap);
      if (!grown)
        return lub_api_fail(app, "app_use: out of memory");
      s->e = grown;
      s->cap = cap;
    }
    want.app = (App *)calloc(1, sizeof(App));
    if (!want.app || !app_init(want.app, app->proc)) {
      free(want.app);
      return lub_api_fail(app, "app_use: out of memory");
    }
    want.app->opened_by = app;
    want.handle = ++s->next_handle;
    o = &s->e[s->n++];
    *o = want;
  }
  o->last_seen_frame = app->resource_frame;
  *out = o->handle;
  return LUB_OK;
}

// main の pass の depth を target の大きさに合わせる
static bool open_app_depth(OpenApp *o, int w, int h) {
  if (o->depth && o->depth_w == w && o->depth_h == h)
    return true;
  if (o->depth)
    g_backend->destroy_image(o->depth);
  ImageDesc id = {
      .fmt = SGL_PF_DEPTH24_STENCIL8,
      .w = w,
      .h = h,
      .filter = SGL_FILTER_NEAREST,
      .render_target = true,
  };
  o->depth = g_backend->make_image(&id);
  o->depth_w = w;
  o->depth_h = h;
  return o->depth != 0;
}

static OpenApp *open_app_find(App *app, LubHandle handle) {
  struct OpenApps *s = app->open_apps;
  for (int i = 0; s && i < s->n; ++i)
    if (s->e[i].handle == handle)
      return &s->e[i];
  return NULL;
}

// module を読み込み、OnInit を呼ぶ (まだなら)。
// ImGui の context は開いた側のものなので、開かれた app の Ui.* は何もしない
static void open_app_player(App *app, OpenApp *o) {
  if (o->player)
    return;
  ui_set_enabled(false);
  o->player = app->app_hooks->open(app, o->app, o->module, o->entry);
  ui_set_enabled(true);
}

LubStatus lub_app_step(LubContext *ctx, LubHandle handle, LubHandle target,
                       float dt, bool *out) {
  App *app = lub_api_app(ctx);
  *out = false;
  OpenApp *o = open_app_find(app, handle);
  if (!o)
    return lub_api_fail(app, "app_step: the app is closed (declare it with "
                             "App.Use every frame)");
  if (!app->proc->in_frame)
    return lub_api_fail(app, "app_step: must be called inside OnFrame");
  if (pass_state_in_pass(&app->pass))
    return lub_api_fail(app, "app_step: must be called outside a pass");
  ResEntry *te = res_table_get_by_handle(&app->res, target);
  if (!te || te->kind != RES_TEXTURE || !te->u.tex.is_target ||
      te->u.tex.fmt == SGL_PF_DEPTH16 ||
      te->u.tex.fmt == SGL_PF_DEPTH24_STENCIL8 ||
      te->u.tex.fmt == SGL_PF_DEPTH32F)
    return lub_api_fail(app, "app_step: target must be a color texture "
                             "declared with {target=true}");
  if (!open_app_depth(o, te->u.tex.w, te->u.tex.h_))
    return lub_api_fail(app, "app_step: cannot create the depth target");
  res_table_touch(te, app->resource_frame);
  o->last_seen_frame = app->resource_frame;

  App *child = o->app;
  child->main_color = te->u.tex.h;
  child->main_depth = o->depth;
  child->main_fmt = te->u.tex.fmt;
  child->main_w = te->u.tex.w;
  child->main_h = te->u.tex.h_;
  child->input = app->input;
  open_app_player(app, o);
  ui_set_enabled(false);
  bool ok = false;
  if (o->player && !child->quit_requested) {
    child->declare_epoch++;
    ok = app->app_hooks->step(o->player, dt);
    if (pass_state_in_pass(&child->pass))
      pass_state_end(&child->pass);
    app_frame_end(child, ok);
  }
  ui_set_enabled(true);
  child->main_color = 0;
  child->main_depth = 0;
  *out = ok;
  return LUB_OK;
}

static StateSlot *state_find(struct OpenApps *s, int32_t slot) {
  for (int i = 0; i < s->nstates; ++i)
    if (s->states[i].slot == slot)
      return &s->states[i];
  return NULL;
}

static void state_result(struct OpenApps *s, bool ok, LubStr *error,
                         bool *out) {
  *out = ok;
  error->ptr = ok ? NULL : s->state_error;
  error->len = ok ? 0 : (int32_t)strlen(s->state_error);
}

LubStatus lub_app_save(LubContext *ctx, LubHandle handle, int32_t slot,
                       LubStr *error, bool *out) {
  App *app = lub_api_app(ctx);
  *out = false;
  error->ptr = NULL;
  error->len = 0;
  OpenApp *o = open_app_find(app, handle);
  if (!o)
    return lub_api_fail(app, "app_save: the app is closed (declare it with "
                             "App.Use every frame)");
  struct OpenApps *s = app->open_apps;
  open_app_player(app, o);
  uint8_t *bytes = NULL;
  size_t size = 0;
  s->state_error[0] = '\0';
  if (!o->player) {
    SDL_snprintf(s->state_error, sizeof(s->state_error), "out of memory");
  } else if (app->app_hooks->save(o->player, &bytes, &size, s->state_error,
                                  sizeof(s->state_error))) {
    StateSlot *st = state_find(s, slot);
    if (!st && s->nstates == s->capstates) {
      int cap = s->capstates ? s->capstates * 2 : 8;
      StateSlot *grown =
          (StateSlot *)realloc(s->states, sizeof(StateSlot) * (size_t)cap);
      if (grown) {
        s->states = grown;
        s->capstates = cap;
      }
    }
    if (!st && s->nstates < s->capstates) {
      st = &s->states[s->nstates++];
      st->slot = slot;
      st->bytes = NULL;
    }
    if (st) {
      free(st->bytes);
      st->bytes = bytes;
      st->size = size;
      state_result(s, true, error, out);
      return LUB_OK;
    }
    free(bytes);
    SDL_snprintf(s->state_error, sizeof(s->state_error), "out of memory");
  }
  state_result(s, false, error, out);
  return LUB_OK;
}

LubStatus lub_app_load(LubContext *ctx, LubHandle handle, int32_t slot,
                       LubStr *error, bool *out) {
  App *app = lub_api_app(ctx);
  *out = false;
  error->ptr = NULL;
  error->len = 0;
  OpenApp *o = open_app_find(app, handle);
  if (!o)
    return lub_api_fail(app, "app_load: the app is closed (declare it with "
                             "App.Use every frame)");
  struct OpenApps *s = app->open_apps;
  StateSlot *st = state_find(s, slot);
  s->state_error[0] = '\0';
  bool ok = false;
  if (!st) {
    SDL_snprintf(s->state_error, sizeof(s->state_error), "slot %d is empty",
                 (int)slot);
  } else {
    open_app_player(app, o);
    if (!o->player)
      SDL_snprintf(s->state_error, sizeof(s->state_error), "out of memory");
    else
      ok = app->app_hooks->load(o->player, st->bytes, st->size, s->state_error,
                                sizeof(s->state_error));
  }
  state_result(s, ok, error, out);
  return LUB_OK;
}

void lub_app_drop(LubContext *ctx, int32_t slot) {
  App *app = lub_api_app(ctx);
  struct OpenApps *s = app->open_apps;
  StateSlot *st = s ? state_find(s, slot) : NULL;
  if (!st)
    return;
  free(st->bytes);
  *st = s->states[--s->nstates];
}

void api_app_frame_end(App *app) {
  struct OpenApps *s = app->open_apps;
  if (!s || app->resource_sweep_after_frames <= 0)
    return;
  int64_t cf = app->resource_frame;
  int64_t thr = (int64_t)app->resource_sweep_after_frames;
  for (int i = 0; i < s->n;) {
    if (cf - s->e[i].last_seen_frame > thr) {
      open_app_close(app, &s->e[i]);
      s->e[i] = s->e[s->n - 1];
      s->n--;
    } else {
      ++i;
    }
  }
}

void api_app_shutdown(App *app) {
  struct OpenApps *s = app->open_apps;
  if (!s)
    return;
  for (int i = 0; i < s->n; ++i)
    open_app_close(app, &s->e[i]);
  for (int i = 0; i < s->nstates; ++i)
    free(s->states[i].bytes);
  free(s->states);
  free(s->e);
  free(s);
  app->open_apps = NULL;
}
