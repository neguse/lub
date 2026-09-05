// core (config / quit)、input、sys、profiler の C API。
#include "api_internal.h"
#include "profile.h"
#include <SDL3/SDL.h>
#include <ctype.h>
#include <string.h>

#define LUB_READBACK_DEPTH_MAX 32

LubStatus lub_config(LubContext *ctx, const LubConfigOpts *d) {
  App *app = lub_api_app(ctx);
  if (!d)
    return lub_api_fail(app, "config: opts required");
  if (app->phase != APP_PHASE_PRE_BACKEND)
    return lub_api_fail(app, "config: must be called inside on_init");

  // "native" = そのプラットフォームの最短距離実装
  // (Windows: D3D12 / web: webgpu / Linux: 当面 sdlgpu が代行)。
  char name[16];
#ifdef __EMSCRIPTEN__
  // WASM: backend は webgpu 一択なので指定を無視する。
  (void)lub_str_copy(lub_str_c("webgpu"), name, sizeof(name));
#else
  if (d->backend.len <= 0) {
    (void)lub_str_copy(lub_str_c("native"), name, sizeof(name));
  } else if (!lub_str_copy(d->backend, name, sizeof(name)) ||
             (strcmp(name, "sdlgpu") != 0 && strcmp(name, "native") != 0)) {
    return lub_api_fail(
        app, "config: backend must be 'native' or 'sdlgpu', got '%.*s'",
        d->backend.len, d->backend.ptr ? d->backend.ptr : "");
  }
#endif
  strncpy(app->backend_name, name, sizeof(app->backend_name) - 1);
  app->backend_name[sizeof(app->backend_name) - 1] = '\0';

  if (d->has_resource_sweep_after_frames) {
    if (d->resource_sweep_after_frames < 0)
      return lub_api_fail(app,
                          "config: resource_sweep_after_frames must be >= 0");
    app->resource_sweep_after_frames = d->resource_sweep_after_frames;
  }
  if (d->has_readback_depth) {
    if (d->readback_depth < 1 || d->readback_depth > LUB_READBACK_DEPTH_MAX)
      return lub_api_fail(app, "config: readback_depth out of range (1..%d)",
                          LUB_READBACK_DEPTH_MAX);
    app->readback_depth = d->readback_depth;
  }
  if ((d->has_width && d->width < 0) || (d->has_height && d->height < 0))
    return lub_api_fail(app, "config: width/height must be >= 0");
  if (d->has_width && d->width > 0)
    app->cfg_w = d->width;
  if (d->has_height && d->height > 0)
    app->cfg_h = d->height;
  return LUB_OK;
}

void lub_quit(LubContext *ctx) { lub_api_app(ctx)->quit_requested = true; }

// ----------------------------------------------------------------- input

static SDL_Scancode scancode_from_name(LubStr name) {
  if (name.len <= 0 || !name.ptr)
    return SDL_SCANCODE_UNKNOWN;
  char key[32];
  size_t n = (size_t)name.len;
  if (n >= sizeof(key))
    n = sizeof(key) - 1;
  for (size_t i = 0; i < n; ++i)
    key[i] = (char)tolower((unsigned char)name.ptr[i]);
  key[n] = '\0';

  if (n == 1) {
    if (key[0] >= 'a' && key[0] <= 'z')
      return (SDL_Scancode)(SDL_SCANCODE_A + (key[0] - 'a'));
    if (key[0] >= '1' && key[0] <= '9')
      return (SDL_Scancode)(SDL_SCANCODE_1 + (key[0] - '1'));
    if (key[0] == '0')
      return SDL_SCANCODE_0;
  }
  if (strcmp(key, "left") == 0 || strcmp(key, "arrowleft") == 0)
    return SDL_SCANCODE_LEFT;
  if (strcmp(key, "right") == 0 || strcmp(key, "arrowright") == 0)
    return SDL_SCANCODE_RIGHT;
  if (strcmp(key, "up") == 0 || strcmp(key, "arrowup") == 0)
    return SDL_SCANCODE_UP;
  if (strcmp(key, "down") == 0 || strcmp(key, "arrowdown") == 0)
    return SDL_SCANCODE_DOWN;
  if (strcmp(key, "space") == 0 || strcmp(key, "spacebar") == 0)
    return SDL_SCANCODE_SPACE;
  if (strcmp(key, "enter") == 0 || strcmp(key, "return") == 0)
    return SDL_SCANCODE_RETURN;
  if (strcmp(key, "escape") == 0 || strcmp(key, "esc") == 0)
    return SDL_SCANCODE_ESCAPE;
  if (strcmp(key, "tab") == 0)
    return SDL_SCANCODE_TAB;
  if (strcmp(key, "backspace") == 0)
    return SDL_SCANCODE_BACKSPACE;
  return SDL_SCANCODE_UNKNOWN;
}

bool lub_input_key_down(LubContext *ctx, LubStr key) {
  (void)ctx;
  SDL_Scancode sc = scancode_from_name(key);
  if (sc == SDL_SCANCODE_UNKNOWN)
    return false;
  int key_count = 0;
  const bool *state = SDL_GetKeyboardState(&key_count);
  return state && sc >= 0 && sc < key_count && state[sc];
}

bool lub_input_key_pressed(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
         app->key_pressed[sc];
}

bool lub_input_key_released(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
         app->key_released[sc];
}

// button は省略で 1 (左)。
static int32_t mouse_button(const int32_t *button) {
  return button ? *button : 1;
}

bool lub_input_mouse_down(LubContext *ctx, const int32_t *button) {
  (void)ctx;
  int32_t b = mouse_button(button);
  if (b < 1)
    return false;
  SDL_MouseButtonFlags mask = SDL_GetMouseState(NULL, NULL);
  return (mask & SDL_BUTTON_MASK(b)) != 0;
}

bool lub_input_mouse_pressed(LubContext *ctx, const int32_t *button) {
  int32_t b = mouse_button(button);
  if (b < 1)
    return false;
  return (lub_api_app(ctx)->mouse_pressed_mask & SDL_BUTTON_MASK(b)) != 0;
}

bool lub_input_mouse_released(LubContext *ctx, const int32_t *button) {
  int32_t b = mouse_button(button);
  if (b < 1)
    return false;
  return (lub_api_app(ctx)->mouse_released_mask & SDL_BUTTON_MASK(b)) != 0;
}

void lub_input_mouse_pos(LubContext *ctx, float *x, float *y) {
  (void)ctx;
  float px = 0.0f, py = 0.0f;
  SDL_GetMouseState(&px, &py);
  if (x)
    *x = px;
  if (y)
    *y = py;
}

// 今 frame の相対移動の合計 (window px)。frame の中では何度読んでも同じ。
void lub_input_mouse_delta(LubContext *ctx, float *dx, float *dy) {
  App *app = lub_api_app(ctx);
  if (dx)
    *dx = app->mouse_rel_x;
  if (dy)
    *dy = app->mouse_rel_y;
}

// ------------------------------------------------------------------- sys

float lub_sys_actual_fps(LubContext *ctx) {
  return (float)lub_api_app(ctx)->actual_fps;
}

// 文字列の FNV-1a 64bit hash を int32 に畳む (version の identity claim 用)。
int32_t lub_sys_fnv1a64(LubContext *ctx, LubStr s) {
  (void)ctx;
  uint64_t h =
      lub_io_fnv1a64(s.ptr ? s.ptr : "", (size_t)(s.len > 0 ? s.len : 0));
  return (int32_t)(uint32_t)(h ^ (h >> 32));
}

bool lub_sys_is_web(LubContext *ctx) {
  (void)ctx;
#ifdef __EMSCRIPTEN__
  return true;
#else
  return false;
#endif
}

// -------------------------------------------------------------- profiler

bool lub_profiler_enabled(LubContext *ctx) {
  return lub_api_app(ctx)->profile.enabled;
}

void lub_profiler_begin_scope(LubContext *ctx, LubStr name) {
  char buf[128];
  if (!lub_str_copy(name, buf, sizeof(buf)))
    return;
  profile_begin_scope(&lub_api_app(ctx)->profile, buf);
}

void lub_profiler_end_scope(LubContext *ctx, LubStr name) {
  char buf[128];
  const char *n = NULL;
  if (name.len > 0 && lub_str_copy(name, buf, sizeof(buf)))
    n = buf;
  profile_end_scope(&lub_api_app(ctx)->profile, n);
}

void lub_profiler_reset(LubContext *ctx) {
  profile_reset(&lub_api_app(ctx)->profile);
}

void lub_profiler_report(LubContext *ctx, LubStr label) {
  char buf[128];
  const char *l = "manual";
  if (label.len > 0 && lub_str_copy(label, buf, sizeof(buf)))
    l = buf;
  profile_report(&lub_api_app(ctx)->profile, l);
}
