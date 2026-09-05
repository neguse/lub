// host API (include/lub/lub_host.h)。player (src/main.c) と .NET 実行の host
// が同じ関数で runtime を回す。frame の骨格 (dt、profile、begin / end、入力
// latch の clear) はここが唯一の実装。
#include "host_api.h"
#include "api_internal.h"
#include "capture.h"
#include "lua_api.h"
#include "profile.h"
#include "ui.h"
#include <SDL3/SDL.h>
#include <stdlib.h>
#include <string.h>

LubContext *lub_host_create(const LubHostOpts *opts) {
  if (!SDL_Init(SDL_INIT_VIDEO)) {
    SDL_Log("SDL_Init failed: %s", SDL_GetError());
    return NULL;
  }
  App *app = (App *)calloc(1, sizeof(App));
  if (!app) {
    SDL_Quit();
    return NULL;
  }
  if (!app_init(app)) {
    free(app);
    SDL_Quit();
    return NULL;
  }
  if (opts) {
    app->digest.enabled = opts->digest;
    if (opts->backend.len > 0)
      lub_str_copy(opts->backend, app->backend_name, sizeof(app->backend_name));
    app->fixed_frame_dt = opts->fixed_dt > 0.0f ? (double)opts->fixed_dt : 0.0;
    if (app->fixed_frame_dt > 0.0)
      SDL_Log("fixed frame dt enabled: %.17g seconds", app->fixed_frame_dt);
    if (opts->capture_path.len > 0) {
      char path[1024];
      if (lub_str_copy(opts->capture_path, path, sizeof(path))) {
        capture_schedule(
            &app->capture, path,
            (uint64_t)(opts->capture_frame > 0 ? opts->capture_frame : 0));
        SDL_Log("capture scheduled: path=%s at_frame=%d", path,
                opts->capture_frame);
      }
    }
  }
  if (!lua_ctx_init(&app->lua, app)) {
    app_shutdown(app);
    free(app);
    SDL_Quit();
    return NULL;
  }
  return lub_api_ctx(app);
}

LubStatus lub_host_start(LubContext *ctx) {
  App *app = lub_api_app(ctx);
  if (app->cfg_w > 0 && app->cfg_h > 0)
    SDL_SetWindowSize(app->window, app->cfg_w, app->cfg_h);
  if (!app_backend_init(app))
    return lub_api_fail(app, "backend init failed");
  return LUB_OK;
}

bool lub_host_translate_event(App *app, const SDL_Event *e, LubEventData *out) {
  memset(out, 0, sizeof(*out));
  switch (e->type) {
  case SDL_EVENT_QUIT:
    app->quit_requested = true;
    out->kind = LUB_EVENT_KIND_QUIT;
    return true;
  case SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
    app->pending_resize = true;
    out->kind = LUB_EVENT_KIND_WINDOW_RESIZE;
    out->x = (float)e->window.data1;
    out->y = (float)e->window.data2;
    return true;
  case SDL_EVENT_KEY_DOWN:
    if (!e->key.repeat && e->key.scancode < SDL_SCANCODE_COUNT)
      app->key_pressed[e->key.scancode] = true;
    out->kind = LUB_EVENT_KIND_KEY_DOWN;
    out->key = (int32_t)e->key.scancode;
    return true;
  case SDL_EVENT_KEY_UP:
    if (e->key.scancode < SDL_SCANCODE_COUNT)
      app->key_released[e->key.scancode] = true;
    out->kind = LUB_EVENT_KIND_KEY_UP;
    out->key = (int32_t)e->key.scancode;
    return true;
  case SDL_EVENT_MOUSE_BUTTON_DOWN:
    app->mouse_pressed_mask |= SDL_BUTTON_MASK(e->button.button);
    out->kind = LUB_EVENT_KIND_MOUSE_BUTTON_DOWN;
    out->button = e->button.button;
    out->x = e->button.x;
    out->y = e->button.y;
    return true;
  case SDL_EVENT_MOUSE_BUTTON_UP:
    app->mouse_released_mask |= SDL_BUTTON_MASK(e->button.button);
    out->kind = LUB_EVENT_KIND_MOUSE_BUTTON_UP;
    out->button = e->button.button;
    out->x = e->button.x;
    out->y = e->button.y;
    return true;
  case SDL_EVENT_MOUSE_MOTION:
    app->mouse_rel_x += e->motion.xrel;
    app->mouse_rel_y += e->motion.yrel;
    out->kind = LUB_EVENT_KIND_MOUSE_MOTION;
    out->x = e->motion.x;
    out->y = e->motion.y;
    out->dx = e->motion.xrel;
    out->dy = e->motion.yrel;
    return true;
  case SDL_EVENT_MOUSE_WHEEL:
    app->mouse_wheel_x += e->wheel.x;
    app->mouse_wheel_y += e->wheel.y;
    out->kind = LUB_EVENT_KIND_MOUSE_WHEEL;
    out->dx = e->wheel.x;
    out->dy = e->wheel.y;
    return true;
  default:
    out->kind = LUB_EVENT_KIND_OTHER;
    return true;
  }
}

bool lub_host_poll_event(LubContext *ctx, LubEventData *out) {
  App *app = lub_api_app(ctx);
  SDL_Event e;
  while (SDL_PollEvent(&e)) {
    if (lub_host_translate_event(app, &e, out))
      return true;
  }
  return false;
}

bool lub_host_frame_begin(LubContext *ctx, float *dt) {
  App *app = lub_api_app(ctx);
  int w = 0, h = 0;
  SDL_GetWindowSizeInPixels(app->window, &w, &h);
  if (w == 0 || h == 0)
    return false;
  uint64_t now = SDL_GetPerformanceCounter();
  if (app->fixed_frame_dt > 0.0) {
    app->frame_dt = app->fixed_frame_dt;
  } else if (app->frame_prev_counter != 0) {
    app->frame_dt = (double)(now - app->frame_prev_counter) /
                    (double)SDL_GetPerformanceFrequency();
    if (app->frame_dt > 0.25)
      app->frame_dt = 0.25;
  } else {
    app->frame_dt = 1.0 / 60.0;
  }
  app->frame_prev_counter = now;
  profile_frame_begin(&app->profile, app->frame_index);
  profile_begin_scope(&app->profile, "runtime.begin_frame");
  app_frame_begin(app, &w, &h);
  profile_end_scope(&app->profile, "runtime.begin_frame");
  ui_new_frame(app, (float)app->frame_dt, w, h);
  profile_begin_scope(&app->profile, "script.onFrame");
  if (dt)
    *dt = (float)app->frame_dt;
  return true;
}

static void input_latch_clear(App *app) {
  memset(app->key_pressed, 0, sizeof(app->key_pressed));
  memset(app->key_released, 0, sizeof(app->key_released));
  app->mouse_pressed_mask = 0;
  app->mouse_released_mask = 0;
  app->mouse_rel_x = 0.0f;
  app->mouse_rel_y = 0.0f;
  app->mouse_wheel_x = 0.0f;
  app->mouse_wheel_y = 0.0f;
}

void lub_host_frame_end(LubContext *ctx) {
  App *app = lub_api_app(ctx);
  uint64_t profile_frame = app->frame_index;
  profile_end_scope(&app->profile, "script.onFrame");
  input_latch_clear(app);
  profile_begin_scope(&app->profile, "runtime.pass_guard");
  if (pass_state_in_pass(&app->pass))
    pass_state_end(&app->pass);
  profile_end_scope(&app->profile, "runtime.pass_guard");
  profile_begin_scope(&app->profile, "runtime.end_frame");
  app_frame_end(app);
  profile_end_scope(&app->profile, "runtime.end_frame");
  profile_frame_end(&app->profile, profile_frame);
}

bool lub_host_quit_requested(LubContext *ctx) {
  App *app = lub_api_app(ctx);
  return app->quit_requested || app->capture_then_exit;
}

void lub_host_destroy(LubContext *ctx) {
  if (!ctx)
    return;
  App *app = lub_api_app(ctx);
  ui_shutdown();
  app_shutdown(app);
  lua_ctx_shutdown(&app->lua);
  free(app);
  SDL_Quit();
}
