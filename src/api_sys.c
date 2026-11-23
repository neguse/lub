// C API for core (config / quit), input, sys, and profiler.
#include "api_internal.h"
#include "backend.h"
#include "gpu_time.h"
#include "host_api.h"
#include "profile.h"
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
  // OnInit after the VM is recreated (entry_restarting) runs after the backend
  // starts, so the backend and window size cannot be changed. If the values
  // differ, only log
  bool restarting = app->proc->phase != APP_PHASE_PRE_BACKEND;
  if (restarting && !app->entry_restarting)
    return lub_api_fail(app, "config: must be called inside OnInit");

  // backend is d3d12 / vulkan / metal / sdlgpu.
  // If unspecified, keep the default chosen by process_init (env LUB_BACKEND,
  // or the platform default if missing).
  char name[16];
#ifdef __EMSCRIPTEN__
  // WASM: the backend is always webgpu, so ignore the setting (so that samples
  // shared with native do not break even if they have a native-specific
  // setting).
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

void lub_app_restart(LubContext *ctx, LubHandle of) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  if (app)
    app->restart_requested = true;
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

bool lub_input_key_down(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  const InputState *in = &app->input;
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT && in->keys[sc];
}

bool lub_input_key_pressed(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  const InputState *in = &app->input;
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
         in->key_pressed[sc];
}

bool lub_input_key_released(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  const InputState *in = &app->input;
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
         in->key_released[sc];
}

// Write the bit for button (default 1 = left) to *bit and return the input to
// read. NULL if button is out of range
static const InputState *mouse_input(LubContext *ctx, const int32_t *button,
                                     uint32_t *bit) {
  App *app = lub_api_app(ctx);
  int32_t b = button ? *button : 1;
  const InputState *in = &app->input;
  *bit = input_mouse_bit(b);
  return *bit == 0 ? NULL : in;
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
  const InputState *in = &lub_api_app(ctx)->input;
  if (x)
    *x = in->mouse_x;
  if (y)
    *y = in->mouse_y;
}

// Total relative motion in the current frame (framebuffer px).
// Reading it multiple times within a frame gives the same value.
void lub_input_mouse_delta(LubContext *ctx, float *dx, float *dy) {
  App *app = lub_api_app(ctx);
  const InputState *in = &app->input;
  if (dx)
    *dx = in->mouse_rel_x;
  if (dy)
    *dy = in->mouse_rel_y;
}

bool lub_input_focused(LubContext *ctx) {
  App *app = lub_api_app(ctx);
  return app->proc->window &&
         (SDL_GetWindowFlags(app->proc->window) & SDL_WINDOW_INPUT_FOCUS) != 0;
}

// On web, SDL defers the Pointer Lock request until the next input event
// (Pointer Lock is granted only during a user action).
bool lub_input_capture_mouse(LubContext *ctx, bool captured) {
  App *app = lub_api_app(ctx);
  return app->proc->window &&
         SDL_SetWindowRelativeMouseMode(app->proc->window, captured);
}

// lub's PadAxis / PadButton use SDL's numbers as is.
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
  const InputState *in = &lub_api_app(ctx)->input;
  return player_valid(player) && in->pad_connected[player];
}

float lub_input_gamepad_axis(LubContext *ctx, int32_t player, int32_t axis) {
  const InputState *in = &lub_api_app(ctx)->input;
  return player_valid(player) && axis >= 0 && axis < SDL_GAMEPAD_AXIS_COUNT
             ? in->pad_axes[player][axis]
             : 0.0f;
}

// The input to read. NULL if player or button is out of range
static const InputState *pad_input(LubContext *ctx, int32_t player,
                                   int32_t button) {
  const InputState *in = &lub_api_app(ctx)->input;
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

// Fold the FNV-1a 64bit hash of a string into int32 (for version identity
// claims).
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

// ---- Inspection (the player's LubDebugHooks. Empty if none) ----

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

bool lub_sys_inspect_set(LubContext *ctx, LubStr path, LubStr text,
                         LubHandle of, LubStr *error) {
  // Keep the reason until the next call
  static char reason[512];
  App *app = api_app_observed(lub_api_app(ctx), of);
  bool ok = false;
  reason[0] = '\0';
  if (!app)
    SDL_snprintf(reason, sizeof reason, "the app is closed");
  else if (!app->debug_hooks || !app->debug_hooks->inspect_set)
    SDL_snprintf(reason, sizeof reason,
                 "this player cannot write the values of an app");
  else
    ok = app->debug_hooks->inspect_set(app, path, text, reason, sizeof reason);
  error->ptr = ok ? NULL : reason;
  error->len = ok ? 0 : (int32_t)strlen(reason);
  return ok;
}

bool lub_sys_inspect_pinned(LubContext *ctx, LubStr key, LubStr id,
                            LubHandle of) {
  App *app = api_app_observed(lub_api_app(ctx), of);
  return app && app->debug_hooks && app->debug_hooks->inspect_pinned &&
         app->debug_hooks->inspect_pinned(app, key, id);
}
