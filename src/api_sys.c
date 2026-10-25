// core (config / quit)、input、sys、profiler の C API。
#include "api_internal.h"
#include "backend.h"
#include "host_api.h"
#include "profile.h"
#include <SDL3/SDL.h>
#include <ctype.h>
#include <stdlib.h>
#include <string.h>

#define LUB_READBACK_DEPTH_MAX 32

LubStatus lub_app_config(LubContext *ctx, const LubConfigOpts *d) {
  App *app = lub_api_app(ctx);
  if (!d)
    return lub_api_fail(app, "config: opts required");
  // VM を作り直した後の OnInit (entry_restarting) は backend の後なので、backend と window の大きさは変えられない。
  // 違う値なら log だけ出す
  bool restarting = app->phase != APP_PHASE_PRE_BACKEND;
  if (restarting && !app->entry_restarting)
    return lub_api_fail(app, "config: must be called inside OnInit");

  // backend は d3d12 / vulkan / metal / sdlgpu。
  // 未指定なら app_init が決めた既定 (env LUB_BACKEND、無ければプラットフォーム既定) を維持する。
  char name[16];
#ifdef __EMSCRIPTEN__
  // WASM: backend は webgpu 一択なので指定を無視する (native と共用のサンプルが native 向け指定を持っていても壊さない)。
  (void)lub_str_copy(lub_str_c("webgpu"), name, sizeof(name));
  strncpy(app->backend_name, name, sizeof(app->backend_name) - 1);
  app->backend_name[sizeof(app->backend_name) - 1] = '\0';
#else
  if (d->backend.len > 0 && !export_active()) {
    if (!lub_str_copy(d->backend, name, sizeof(name)) ||
        (strcmp(name, "d3d12") != 0 && strcmp(name, "vulkan") != 0 &&
         strcmp(name, "metal") != 0 && strcmp(name, "sdlgpu") != 0)) {
      return lub_api_fail(app,
                          "config: backend must be 'd3d12', 'vulkan', 'metal' "
                          "or 'sdlgpu', got '%.*s'",
                          d->backend.len, d->backend.ptr ? d->backend.ptr : "");
    }
    if (restarting) {
      if (strcmp(name, app->backend_name) != 0)
        SDL_Log("config: backend '%s' applies after restarting lub", name);
    } else {
      strncpy(app->backend_name, name, sizeof(app->backend_name) - 1);
      app->backend_name[sizeof(app->backend_name) - 1] = '\0';
    }
  }
#endif

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
  if (d->has_readback_fixed_latency)
    app->readback_fixed_latency = d->readback_fixed_latency;
  if ((d->has_width && d->width < 0) || (d->has_height && d->height < 0))
    return lub_api_fail(app, "config: width/height must be >= 0");
  int w = d->has_width && d->width > 0 ? d->width : app->cfg_w;
  int h = d->has_height && d->height > 0 ? d->height : app->cfg_h;
  if (restarting) {
    if (w != app->cfg_w || h != app->cfg_h)
      SDL_Log("config: size %dx%d applies after restarting lub", w, h);
    return LUB_OK;
  }
  app->cfg_w = w;
  app->cfg_h = h;
  return LUB_OK;
}

void lub_app_quit(LubContext *ctx, int32_t code) {
  App *app = lub_api_app(ctx);
  app->quit_requested = true;
  app->exit_code = code;
}

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
  if (key[0] == 'f' && n >= 2 && n <= 3) {
    int fn = atoi(key + 1);
    if (fn >= 1 && fn <= 12)
      return (SDL_Scancode)(SDL_SCANCODE_F1 + (fn - 1));
  }
  return SDL_GetScancodeFromName(key);
}

// overlay が Input.Grab で取っている入力は、overlay の外 (ゲーム) からは何も押されていないように見せる。
static bool keyboard_hidden(const App *app) {
  return app->overlay_grab_keyboard && !app->in_overlay;
}

static bool mouse_hidden(const App *app) {
  return app->overlay_grab_mouse && !app->in_overlay;
}

void lub_input_grab(LubContext *ctx, bool keyboard, bool mouse) {
  App *app = lub_api_app(ctx);
  if (!app->in_overlay)
    return;
  app->overlay_grab_keyboard = keyboard;
  app->overlay_grab_mouse = mouse;
}

bool lub_input_key_down(LubContext *ctx, LubStr key) {
  if (keyboard_hidden(lub_api_app(ctx)))
    return false;
  SDL_Scancode sc = scancode_from_name(key);
  if (sc == SDL_SCANCODE_UNKNOWN)
    return false;
  int key_count = 0;
  const bool *state = SDL_GetKeyboardState(&key_count);
  return state && (int)sc < key_count && state[sc];
}

bool lub_input_key_pressed(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  if (keyboard_hidden(app))
    return false;
  SDL_Scancode sc = scancode_from_name(key);
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
         app->key_pressed[sc];
}

bool lub_input_key_released(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  if (keyboard_hidden(app))
    return false;
  SDL_Scancode sc = scancode_from_name(key);
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
         app->key_released[sc];
}

// button は省略で 1 (左)。
static int32_t mouse_button(const int32_t *button) {
  return button ? *button : 1;
}

bool lub_input_mouse_down(LubContext *ctx, const int32_t *button) {
  int32_t b = mouse_button(button);
  if (b < 1 || mouse_hidden(lub_api_app(ctx)))
    return false;
  SDL_MouseButtonFlags mask = SDL_GetMouseState(NULL, NULL);
  return (mask & SDL_BUTTON_MASK(b)) != 0;
}

bool lub_input_mouse_pressed(LubContext *ctx, const int32_t *button) {
  int32_t b = mouse_button(button);
  if (b < 1 || mouse_hidden(lub_api_app(ctx)))
    return false;
  return (lub_api_app(ctx)->mouse_pressed_mask & SDL_BUTTON_MASK(b)) != 0;
}

bool lub_input_mouse_released(LubContext *ctx, const int32_t *button) {
  int32_t b = mouse_button(button);
  if (b < 1 || mouse_hidden(lub_api_app(ctx)))
    return false;
  return (lub_api_app(ctx)->mouse_released_mask & SDL_BUTTON_MASK(b)) != 0;
}

void lub_input_mouse_pos(LubContext *ctx, float *x, float *y) {
  App *app = lub_api_app(ctx);
  float density = lub_host_pixel_density(app);
  int main_x = 0, main_y = 0, main_w = 0, main_h = 0;
  lub_host_main_rect(app, &main_x, &main_y, &main_w, &main_h);
  float px = 0.0f, py = 0.0f;
  SDL_GetMouseState(&px, &py);
  if (x)
    *x = px * density - (float)main_x;
  if (y)
    *y = py * density - (float)main_y;
}

// 今 frame の相対移動の合計 (framebuffer px)。
// frame の中では何度読んでも同じ。
void lub_input_mouse_delta(LubContext *ctx, float *dx, float *dy) {
  App *app = lub_api_app(ctx);
  bool hidden = mouse_hidden(app);
  if (dx)
    *dx = hidden ? 0.0f : app->mouse_rel_x;
  if (dy)
    *dy = hidden ? 0.0f : app->mouse_rel_y;
}

bool lub_input_focused(LubContext *ctx) {
  App *app = lub_api_app(ctx);
  return app->window &&
         (SDL_GetWindowFlags(app->window) & SDL_WINDOW_INPUT_FOCUS) != 0;
}

// web では SDL が Pointer Lock の要求を次の入力 event まで遅らせる (Pointer Lock はユーザーの操作の中でしか通らない)。
bool lub_input_capture_mouse(LubContext *ctx, bool captured) {
  App *app = lub_api_app(ctx);
  return app->window && SDL_SetWindowRelativeMouseMode(app->window, captured);
}

// lub の PadAxis / PadButton は SDL の番号をそのまま使う。
_Static_assert((int)LUB_INPUT_PAD_AXIS_RIGHT_TRIGGER ==
                   (int)SDL_GAMEPAD_AXIS_RIGHT_TRIGGER,
               "PadAxis follows SDL_GamepadAxis");
_Static_assert((int)LUB_INPUT_PAD_BUTTON_DPAD_RIGHT ==
                   (int)SDL_GAMEPAD_BUTTON_DPAD_RIGHT,
               "PadButton follows SDL_GamepadButton");

static SDL_Gamepad *gamepad(App *app, int32_t player) {
  if (player < 0 || player >= LUB_GAMEPAD_SLOTS)
    return NULL;
  return app->gamepads[player];
}

static bool gamepad_button_valid(int32_t button) {
  return button >= 0 && button < SDL_GAMEPAD_BUTTON_COUNT;
}

bool lub_input_gamepad_connected(LubContext *ctx, int32_t player) {
  return gamepad(lub_api_app(ctx), player) != NULL;
}

// SDL の軸は -32768..32767。stick は両端が -1 と 1 になるように割り、trigger (0..32767) は 0..1 にする。
float lub_input_gamepad_axis(LubContext *ctx, int32_t player, int32_t axis) {
  SDL_Gamepad *pad = gamepad(lub_api_app(ctx), player);
  if (!pad || axis < 0 || axis >= SDL_GAMEPAD_AXIS_COUNT)
    return 0.0f;
  int value = SDL_GetGamepadAxis(pad, (SDL_GamepadAxis)axis);
  if (axis == SDL_GAMEPAD_AXIS_LEFT_TRIGGER ||
      axis == SDL_GAMEPAD_AXIS_RIGHT_TRIGGER)
    return value > 0 ? (float)value / 32767.0f : 0.0f;
  return (float)value / (value < 0 ? 32768.0f : 32767.0f);
}

bool lub_input_gamepad_down(LubContext *ctx, int32_t player, int32_t button) {
  SDL_Gamepad *pad = gamepad(lub_api_app(ctx), player);
  return pad && gamepad_button_valid(button) &&
         SDL_GetGamepadButton(pad, (SDL_GamepadButton)button);
}

bool lub_input_gamepad_pressed(LubContext *ctx, int32_t player,
                               int32_t button) {
  App *app = lub_api_app(ctx);
  return player >= 0 && player < LUB_GAMEPAD_SLOTS &&
         gamepad_button_valid(button) &&
         (app->gamepad_pressed[player] & (UINT32_C(1) << button)) != 0;
}

bool lub_input_gamepad_released(LubContext *ctx, int32_t player,
                                int32_t button) {
  App *app = lub_api_app(ctx);
  return player >= 0 && player < LUB_GAMEPAD_SLOTS &&
         gamepad_button_valid(button) &&
         (app->gamepad_released[player] & (UINT32_C(1) << button)) != 0;
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
