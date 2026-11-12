// core (config / quit)、input、sys、profiler の C API。
#include "api_internal.h"
#include "backend.h"
#include "gpu_time.h"
#include "host_api.h"
#include "profile.h"
#include "rewind.h"
#include <SDL3/SDL.h>
#include <ctype.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define LUB_READBACK_DEPTH_MAX 32

LubStatus lub_app_config(LubContext *ctx, const LubConfigOpts *d) {
  App *app = lub_api_app(ctx);
  if (!d)
    return lub_api_fail(app, "config: opts required");
  // VM を作り直した後の OnInit (entry_restarting) は backend の後なので、backend と window の大きさは変えられない。
  // 違う値なら log だけ出す
  bool restarting = app->proc->phase != APP_PHASE_PRE_BACKEND;
  if (restarting && !app->entry_restarting)
    return lub_api_fail(app, "config: must be called inside OnInit");

  // backend は d3d12 / vulkan / metal / sdlgpu。
  // 未指定なら process_init が決めた既定 (env LUB_BACKEND、無ければプラットフォーム既定) を維持する。
  char name[16];
#ifdef __EMSCRIPTEN__
  // WASM: backend は webgpu 一択なので指定を無視する (native と共用のサンプルが native 向け指定を持っていても壊さない)。
  (void)lub_str_copy(lub_str_c("webgpu"), name, sizeof(name));
  strncpy(app->proc->backend_name, name, sizeof(app->proc->backend_name) - 1);
  app->proc->backend_name[sizeof(app->proc->backend_name) - 1] = '\0';
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
      if (strcmp(name, app->proc->backend_name) != 0)
        SDL_Log("config: backend '%s' applies after restarting lub", name);
    } else {
      strncpy(app->proc->backend_name, name,
              sizeof(app->proc->backend_name) - 1);
      app->proc->backend_name[sizeof(app->proc->backend_name) - 1] = '\0';
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
  int w = d->has_width && d->width > 0 ? d->width : app->proc->cfg_w;
  int h = d->has_height && d->height > 0 ? d->height : app->proc->cfg_h;
  if (restarting) {
    if (w != app->proc->cfg_w || h != app->proc->cfg_h)
      SDL_Log("config: size %dx%d applies after restarting lub", w, h);
    return LUB_OK;
  }
  app->proc->cfg_w = w;
  app->proc->cfg_h = h;
  return LUB_OK;
}

void lub_app_quit(LubContext *ctx, int32_t code) {
  App *app = lub_api_app(ctx);
  app->quit_requested = true;
  app->exit_code = code;
}

void lub_app_restart(LubContext *ctx) {
  lub_api_app(ctx)->restart_requested = true;
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

// ゲームから読む入力。Rewind で止めている間のゲームの callback では控えた入力 (*replay が true)
static const InputState *input_of(App *app, bool *replay) {
  const InputState *in = rewind_input(app);
  *replay = in != NULL;
  return in ? in : &app->input;
}

bool lub_input_key_down(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  bool replay;
  const InputState *in = input_of(app, &replay);
  if (!replay && keyboard_hidden(app))
    return false;
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT && in->keys[sc];
}

bool lub_input_key_pressed(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  bool replay;
  const InputState *in = input_of(app, &replay);
  if (!replay && keyboard_hidden(app))
    return false;
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
         in->key_pressed[sc];
}

bool lub_input_key_released(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  bool replay;
  const InputState *in = input_of(app, &replay);
  if (!replay && keyboard_hidden(app))
    return false;
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
         in->key_released[sc];
}

// button (省略で 1 = 左) の bit を *bit に書き、読む入力を返す。
// button が範囲の外か、overlay が mouse を取っていれば NULL
static const InputState *mouse_input(LubContext *ctx, const int32_t *button,
                                     uint32_t *bit) {
  App *app = lub_api_app(ctx);
  int32_t b = button ? *button : 1;
  bool replay;
  const InputState *in = input_of(app, &replay);
  *bit = input_mouse_bit(b);
  return *bit == 0 || (!replay && mouse_hidden(app)) ? NULL : in;
}

bool lub_input_mouse_down(LubContext *ctx, const int32_t *button) {
  uint32_t bit = 0;
  const InputState *in = mouse_input(ctx, button, &bit);
  return in && (in->mouse_buttons & bit) != 0;
}

bool lub_input_mouse_pressed(LubContext *ctx, const int32_t *button) {
  uint32_t bit = 0;
  const InputState *in = mouse_input(ctx, button, &bit);
  return in && (in->mouse_pressed_mask & bit) != 0;
}

bool lub_input_mouse_released(LubContext *ctx, const int32_t *button) {
  uint32_t bit = 0;
  const InputState *in = mouse_input(ctx, button, &bit);
  return in && (in->mouse_released_mask & bit) != 0;
}

void lub_input_mouse_pos(LubContext *ctx, float *x, float *y) {
  bool replay;
  const InputState *in = input_of(lub_api_app(ctx), &replay);
  if (x)
    *x = in->mouse_x;
  if (y)
    *y = in->mouse_y;
}

// 今 frame の相対移動の合計 (framebuffer px)。
// frame の中では何度読んでも同じ。
void lub_input_mouse_delta(LubContext *ctx, float *dx, float *dy) {
  App *app = lub_api_app(ctx);
  bool replay;
  const InputState *in = input_of(app, &replay);
  bool hidden = !replay && mouse_hidden(app);
  if (dx)
    *dx = hidden ? 0.0f : in->mouse_rel_x;
  if (dy)
    *dy = hidden ? 0.0f : in->mouse_rel_y;
}

bool lub_input_focused(LubContext *ctx) {
  App *app = lub_api_app(ctx);
  return app->proc->window &&
         (SDL_GetWindowFlags(app->proc->window) & SDL_WINDOW_INPUT_FOCUS) != 0;
}

// web では SDL が Pointer Lock の要求を次の入力 event まで遅らせる (Pointer Lock はユーザーの操作の中でしか通らない)。
bool lub_input_capture_mouse(LubContext *ctx, bool captured) {
  App *app = lub_api_app(ctx);
  return app->proc->window &&
         SDL_SetWindowRelativeMouseMode(app->proc->window, captured);
}

// lub の PadAxis / PadButton は SDL の番号をそのまま使う。
_Static_assert((int)LUB_INPUT_PAD_AXIS_RIGHT_TRIGGER ==
                   (int)SDL_GAMEPAD_AXIS_RIGHT_TRIGGER,
               "PadAxis follows SDL_GamepadAxis");
_Static_assert((int)LUB_INPUT_PAD_BUTTON_DPAD_RIGHT ==
                   (int)SDL_GAMEPAD_BUTTON_DPAD_RIGHT,
               "PadButton follows SDL_GamepadButton");

static bool player_valid(int32_t player) {
  return player >= 0 && player < LUB_GAMEPAD_SLOTS;
}

static bool gamepad_button_valid(int32_t button) {
  return button >= 0 && button < SDL_GAMEPAD_BUTTON_COUNT;
}

bool lub_input_gamepad_connected(LubContext *ctx, int32_t player) {
  bool replay;
  const InputState *in = input_of(lub_api_app(ctx), &replay);
  return player_valid(player) && in->pad_connected[player];
}

float lub_input_gamepad_axis(LubContext *ctx, int32_t player, int32_t axis) {
  bool replay;
  const InputState *in = input_of(lub_api_app(ctx), &replay);
  return player_valid(player) && axis >= 0 && axis < SDL_GAMEPAD_AXIS_COUNT
             ? in->pad_axes[player][axis]
             : 0.0f;
}

// 読む入力。player か button が範囲の外なら NULL
static const InputState *pad_input(LubContext *ctx, int32_t player,
                                   int32_t button) {
  bool replay;
  const InputState *in = input_of(lub_api_app(ctx), &replay);
  return player_valid(player) && gamepad_button_valid(button) ? in : NULL;
}

bool lub_input_gamepad_down(LubContext *ctx, int32_t player, int32_t button) {
  const InputState *in = pad_input(ctx, player, button);
  return in && (in->pad_down[player] & (UINT32_C(1) << button)) != 0;
}

bool lub_input_gamepad_pressed(LubContext *ctx, int32_t player,
                               int32_t button) {
  const InputState *in = pad_input(ctx, player, button);
  return in && (in->pad_pressed[player] & (UINT32_C(1) << button)) != 0;
}

bool lub_input_gamepad_released(LubContext *ctx, int32_t player,
                                int32_t button) {
  const InputState *in = pad_input(ctx, player, button);
  return in && (in->pad_released[player] & (UINT32_C(1) << button)) != 0;
}

// ------------------------------------------------------------------- sys

float lub_sys_actual_fps(LubContext *ctx) {
  return (float)lub_api_app(ctx)->proc->actual_fps;
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

float lub_sys_seconds(LubContext *ctx) {
  (void)ctx;
  return (float)((double)SDL_GetTicksNS() / (double)SDL_NS_PER_SECOND);
}

// ---- 検査 (player の LubDebugHooks。無ければ空) ----

void lub_sys_take_trace(LubContext *ctx, LubHandle of, const LubStr **out,
                        int32_t *out_count) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  *out = NULL;
  *out_count = 0;
  if (app && app->debug_hooks && app->debug_hooks->take_trace)
    app->debug_hooks->take_trace(app, (LubStr){"", 0}, out, out_count);
}

void lub_sys_take_trace_for(LubContext *ctx, LubStr key, LubHandle of,
                            const LubStr **out, int32_t *out_count) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  *out = NULL;
  *out_count = 0;
  if (app && app->debug_hooks && app->debug_hooks->take_trace)
    app->debug_hooks->take_trace(app, key, out, out_count);
}

void lub_sys_take_values(LubContext *ctx, LubHandle of,
                         const LubTraceValue **out, int32_t *out_count) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  *out = NULL;
  *out_count = 0;
  if (app && app->debug_hooks && app->debug_hooks->take_values)
    app->debug_hooks->take_values(app, out, out_count);
}

void lub_sys_inspect(LubContext *ctx, LubStr path, int32_t offset,
                     int32_t count, LubHandle of, const LubInspectNode **out,
                     int32_t *out_count) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  *out = NULL;
  *out_count = 0;
  if (app && app->debug_hooks && app->debug_hooks->inspect && count > 0)
    app->debug_hooks->inspect(app, path, offset, count, out, out_count);
}

void lub_sys_inspect_instances(LubContext *ctx, int32_t max_depth,
                               int32_t max_children, LubHandle of,
                               const LubInspectInstance **out,
                               int32_t *out_count) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  *out = NULL;
  *out_count = 0;
  if (app && app->debug_hooks && app->debug_hooks->inspect_instances &&
      max_depth > 0 && max_children > 0)
    app->debug_hooks->inspect_instances(app, max_depth, max_children, out,
                                        out_count);
}

void lub_sys_profile_lines(LubContext *ctx, bool on, LubHandle of) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  if (!app)
    return;
  gpu_time_enable(app, on);
  if (app->debug_hooks && app->debug_hooks->profile_lines)
    app->debug_hooks->profile_lines(app, on);
}

void lub_sys_take_gpu_passes(LubContext *ctx, LubHandle of,
                             const LubGpuPass **out, int32_t *out_count) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  *out = NULL;
  *out_count = 0;
  if (app)
    gpu_time_take(app, out, out_count);
}

void lub_sys_take_profile(LubContext *ctx, LubHandle of,
                          const LubProfileLine **out, int32_t *out_count) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  *out = NULL;
  *out_count = 0;
  if (app && app->debug_hooks && app->debug_hooks->take_profile)
    app->debug_hooks->take_profile(app, out, out_count);
}

void lub_sys_take_gc_pauses(LubContext *ctx, LubHandle of,
                            const LubGcPause **out, int32_t *out_count) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  *out = NULL;
  *out_count = 0;
  if (app && app->debug_hooks && app->debug_hooks->take_gc_pauses)
    app->debug_hooks->take_gc_pauses(app, out, out_count);
}

void lub_sys_take_kept(LubContext *ctx, LubHandle of, const LubKeptObject **out,
                       int32_t *out_count) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  *out = NULL;
  *out_count = 0;
  if (app && app->debug_hooks && app->debug_hooks->take_kept)
    app->debug_hooks->take_kept(app, out, out_count);
}

void lub_sys_inspect_pin(LubContext *ctx, LubStr key, LubStr path,
                         LubHandle of) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  if (app && app->debug_hooks && app->debug_hooks->inspect_pin)
    app->debug_hooks->inspect_pin(app, key, path);
}

bool lub_sys_inspect_pinned(LubContext *ctx, LubStr key, LubStr id,
                            LubHandle of) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  return app && app->debug_hooks && app->debug_hooks->inspect_pinned &&
         app->debug_hooks->inspect_pinned(app, key, id);
}

// 直近の値の列 (次の呼び出しまで有効)
static float *g_history;
static int32_t g_history_cap;

void lub_sys_inspect_history(LubContext *ctx, LubStr path, int32_t count,
                             const float **out, int32_t *out_count) {
  App *app = lub_api_app(ctx);
  *out = NULL;
  *out_count = 0;
  const LubDebugHooks *h = app->debug_hooks;
  if (!h || !h->snapshot_number || count <= 0)
    return;
  int32_t n = 0;
  while (n < count && rewind_snapshot(app, n))
    n++;
  if (n > g_history_cap) {
    float *grown = (float *)realloc(g_history, (size_t)n * sizeof(float));
    if (!grown)
      return;
    g_history = grown;
    g_history_cap = n;
  }
  // 古い順
  for (int32_t i = 0; i < n; i++) {
    float v;
    g_history[i] =
        h->snapshot_number(app, rewind_snapshot(app, n - 1 - i), path, &v)
            ? v
            : NAN;
  }
  *out = g_history;
  *out_count = n;
}

void lub_rewind_record(LubContext *ctx, int32_t frames) {
  rewind_record(lub_api_app(ctx), frames);
}

int32_t lub_rewind_count(LubContext *ctx) {
  return rewind_count(lub_api_app(ctx));
}

void lub_rewind_pause(LubContext *ctx, int32_t back) {
  rewind_pause(lub_api_app(ctx), back);
}

int32_t lub_rewind_position(LubContext *ctx) {
  return rewind_position(lub_api_app(ctx));
}
