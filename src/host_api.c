// host API (include/lub/lub_host.h).
// The tcs VM player (src/vm_player.c), the .NET execution host, and the tcs2c
// execution host (src/tcs_host.c) run the runtime with the same functions. This
// is the only implementation of the frame skeleton (dt, profile, begin / end,
// clearing input latches).
#include "host_api.h"
#include "api_internal.h"
#include "backend.h"
#include "capture.h"
#include "pick.h"
#include "profile.h"
#include "ui.h"
#if defined(LUB_HAS_OPENXR)
#include "xr.h"
#endif
#include <SDL3/SDL.h>
#ifdef SDL_PLATFORM_ANDROID
#include "android.h"
#endif
#include <stdlib.h>
#include <string.h>
#ifdef __EMSCRIPTEN__
#include <emscripten.h>

// EM_JS bodies are JavaScript; keep clang-format away from them (same guard as
// src/host.c).
// clang-format off
// SDL looks at modifier keys only by physical position (code), so on setups where CapsLock is mapped to Ctrl, it does not treat the key ("Control", code "CapsLock")
// as a modifier. Re-dispatch the event with its code fixed from the logical key before SDL sees it.
EM_JS(void, lub_web_fix_modifier_codes, (void), {
  const names = {Control: "Control", Shift: "Shift", Alt: "Alt"};
  const side = (e) => (e.location === 2 ? "Right" : "Left");
  const fixedCode = (e) => {
    if (names[e.key])
      return e.code.startsWith(names[e.key]) ? null : names[e.key] + side(e);
    // The Hankaku/Zenkaku code is Backquote, the same as ` on US layouts, so left as is, SDL would make it a key that types `
    return e.key === "Zenkaku" || e.key === "Hankaku" ? "Lang5" : null;
  };
  const fix = (e) => {
    const code = fixedCode(e);
    if (!code || e.code === code || e.lubFixed)
      return;
    e.stopImmediatePropagation();
    const copy = new KeyboardEvent(e.type, {
      key: e.key, code, location: names[e.key] && e.location === 2 ? 2 : names[e.key] ? 1 : e.location, repeat: e.repeat,
      ctrlKey: e.ctrlKey, shiftKey: e.shiftKey, altKey: e.altKey, metaKey: e.metaKey,
      bubbles: true, cancelable: true,
    });
    copy.lubFixed = true;
    e.target.dispatchEvent(copy);
    if (copy.defaultPrevented)
      e.preventDefault();
  };
  window.addEventListener("keydown", fix, {capture: true});
  window.addEventListener("keyup", fix, {capture: true});
});
// clang-format on
#endif

static int gamepad_slot(Process *proc, SDL_JoystickID id) {
  for (int i = 0; i < LUB_GAMEPAD_SLOTS; ++i)
    if (proc->gamepads[i] && proc->gamepad_ids[i] == id)
      return i;
  return -1;
}

// Put it in the first free slot.
static void gamepad_add(Process *proc, SDL_JoystickID id) {
  if (gamepad_slot(proc, id) >= 0)
    return;
  for (int i = 0; i < LUB_GAMEPAD_SLOTS; ++i) {
    if (proc->gamepads[i])
      continue;
    proc->gamepads[i] = SDL_OpenGamepad(id);
    proc->gamepad_ids[i] = id;
    return;
  }
}

// SDL sends the releases of held buttons as button events before REMOVED.
static void gamepad_remove(Process *proc, SDL_JoystickID id) {
  int slot = gamepad_slot(proc, id);
  if (slot < 0)
    return;
  SDL_CloseGamepad(proc->gamepads[slot]);
  proc->gamepads[slot] = NULL;
}

_Static_assert(SDL_GAMEPAD_BUTTON_COUNT <= 32,
               "gamepad buttons are kept in a uint32_t bit mask");

static void gamepad_button(App *app, const SDL_GamepadButtonEvent *e) {
  int slot = gamepad_slot(app->proc, e->which);
  if (slot < 0 || e->button >= SDL_GAMEPAD_BUTTON_COUNT)
    return;
  uint32_t bit = UINT32_C(1) << e->button;
  if (e->down)
    app->input.pad_pressed[slot] |= bit;
  else
    app->input.pad_released[slot] |= bit;
}

// Convert an SDL axis (-32768..32767) to an Input.GamepadAxis value.
// Sticks are divided so the ends are -1 and 1, and triggers (0..32767) become
// 0..1.
static float pad_axis(SDL_Gamepad *pad, int axis) {
  int value = SDL_GetGamepadAxis(pad, (SDL_GamepadAxis)axis);
  if (axis == SDL_GAMEPAD_AXIS_LEFT_TRIGGER ||
      axis == SDL_GAMEPAD_AXIS_RIGHT_TRIGGER)
    return value > 0 ? (float)value / 32767.0f : 0.0f;
  return (float)value / (value < 0 ? 32768.0f : 32767.0f);
}

// Copy what is currently held (keys, mouse buttons and position, gamepads) from
// SDL's state. Press / release edges and motion totals are accumulated by
// events.
static void input_sample(App *app) {
  InputState *in = &app->input;
  int n = 0;
  const bool *keys = SDL_GetKeyboardState(&n);
  memset(in->keys, 0, sizeof(in->keys));
  if (keys)
    memcpy(in->keys, keys,
           (size_t)(n < SDL_SCANCODE_COUNT ? n : SDL_SCANCODE_COUNT));
  float px = 0.0f, py = 0.0f;
  in->mouse_buttons = SDL_GetMouseState(&px, &py);
  float density = lub_host_pixel_density(app->proc);
  int main_x = 0, main_y = 0, main_w = 0, main_h = 0;
  lub_host_main_rect(app->proc, &main_x, &main_y, &main_w, &main_h);
  in->mouse_x = px * density - (float)main_x;
  in->mouse_y = py * density - (float)main_y;
  for (int i = 0; i < LUB_GAMEPAD_SLOTS; ++i) {
    SDL_Gamepad *pad = app->proc->gamepads[i];
    in->pad_connected[i] = pad != NULL;
    in->pad_down[i] = 0;
    for (int a = 0; a < SDL_GAMEPAD_AXIS_COUNT; ++a)
      in->pad_axes[i][a] = pad ? pad_axis(pad, a) : 0.0f;
    for (int b = 0; pad && b < SDL_GAMEPAD_BUTTON_COUNT; ++b)
      if (SDL_GetGamepadButton(pad, (SDL_GamepadButton)b))
        in->pad_down[i] |= UINT32_C(1) << b;
  }
}

// Keep the runtime running even if gamepads are unavailable (even if subsystem
// init fails).
static void gamepads_init(Process *proc) {
  if (!SDL_InitSubSystem(SDL_INIT_GAMEPAD)) {
    SDL_Log("gamepad init failed: %s", SDL_GetError());
    return;
  }
  int count = 0;
  SDL_JoystickID *ids = SDL_GetGamepads(&count);
  for (int i = 0; ids && i < count; ++i)
    gamepad_add(proc, ids[i]);
  SDL_free(ids);
}

static void gamepads_close(Process *proc) {
  for (int i = 0; i < LUB_GAMEPAD_SLOTS; ++i) {
    if (proc->gamepads[i])
      SDL_CloseGamepad(proc->gamepads[i]);
    proc->gamepads[i] = NULL;
  }
}

LubContext *lub_host_create(const LubHostOpts *opts) {
  // For the .NET host (dotnet/Lub), this is the entry point to SDL.
  // As in the player (src/main.c), make assertions abort instead of showing a
  // dialog. The SDL_ASSERT environment variable takes precedence.
  SDL_SetHint(SDL_HINT_ASSERT, "abort");
#ifdef __EMSCRIPTEN__
  // The web tcs VM player goes through here before bringing up SDL
  lub_web_fix_modifier_codes();
#endif
#ifdef SDL_PLATFORM_ANDROID
  // Copy the APK data to internal storage and make it the cwd
  if (!android_prepare())
    return NULL;
#endif
#ifndef __EMSCRIPTEN__
  // Export does not use a display (the window only has a size).
  // The SDL_VIDEO_DRIVER environment variable takes precedence.
  if (export_active())
    SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "offscreen");
#endif
  if (!SDL_Init(SDL_INIT_VIDEO)) {
    SDL_Log("SDL_Init failed: %s", SDL_GetError());
    return NULL;
  }
  Process *proc = (Process *)calloc(1, sizeof(Process));
  App *app = (App *)calloc(1, sizeof(App));
  if (!proc || !app || !process_init(proc)) {
    free(app);
    free(proc);
    SDL_Quit();
    return NULL;
  }
  if (!app_init(app, proc)) {
    process_shutdown(proc);
    free(app);
    free(proc);
    SDL_Quit();
    return NULL;
  }
  if (opts) {
    app->digest.enabled = opts->digest;
    app->readback_fixed_latency = opts->readback_fixed_latency;
    if (opts->backend.len > 0)
      lub_str_copy(opts->backend, app->proc->backend_name,
                   sizeof(app->proc->backend_name));
    app->proc->fixed_frame_dt =
        opts->fixed_dt > 0.0f ? (double)opts->fixed_dt : 0.0;
    if (app->proc->fixed_frame_dt > 0.0)
      SDL_Log("fixed frame dt enabled: %.17g seconds",
              app->proc->fixed_frame_dt);
    if (opts->capture_path.len > 0) {
      char path[1024];
      if (lub_str_copy(opts->capture_path, path, sizeof(path))) {
        capture_schedule(
            &app->proc->capture, path,
            (uint64_t)(opts->capture_frame > 0 ? opts->capture_frame : 0));
        SDL_Log("capture scheduled: path=%s at_frame=%d", path,
                opts->capture_frame);
      }
    }
  }
  gamepads_init(proc);
  return lub_api_ctx(app);
}

LubStatus lub_host_start(LubContext *ctx) {
  App *app = lub_api_app(ctx);
  if (app->proc->cfg_w > 0 && app->proc->cfg_h > 0)
    SDL_SetWindowSize(app->proc->window, app->proc->cfg_w, app->proc->cfg_h);
  if (!app_backend_init(app))
    return lub_api_fail(app, "backend init failed");
  return LUB_OK;
}

// SDL reports the mouse in window coordinates. On a high pixel density window
// (metal backend: Retina, iPhone) those are points, while everything the game
// draws with is in framebuffer pixels (Gfx.Size), so input is scaled to match.
float lub_host_pixel_density(Process *proc) {
  float density = proc->window ? SDL_GetWindowPixelDensity(proc->window) : 0.0f;
  return density > 0.0f ? density : 1.0f;
}

void lub_host_main_rect(Process *proc, int *x, int *y, int *w, int *h) {
  *x = *y = *w = *h = 0;
  if (!proc->window)
    return;
  if (proc->fixed_w > 0 && proc->fixed_h > 0) {
    *w = proc->fixed_w;
    *h = proc->fixed_h;
    return;
  }
  SDL_GetWindowSizeInPixels(proc->window, w, h);
  SDL_Rect safe;
  if (!SDL_GetWindowSafeArea(proc->window, &safe) || safe.w <= 0 || safe.h <= 0)
    return;
  float density = lub_host_pixel_density(proc);
  int x0 = (int)(safe.x * density + 0.5f);
  int y0 = (int)(safe.y * density + 0.5f);
  int x1 = (int)((safe.x + safe.w) * density + 0.5f);
  int y1 = (int)((safe.y + safe.h) * density + 0.5f);
  if (x0 < 0 || y0 < 0 || x1 > *w || y1 > *h || x1 <= x0 || y1 <= y0)
    return;
  *x = x0;
  *y = y0;
  *w = x1 - x0;
  *h = y1 - y0;
}

// Values of EventData.Mods
static int32_t event_mods(SDL_Keymod mod) {
  return ((mod & SDL_KMOD_SHIFT) ? 1 : 0) | ((mod & SDL_KMOD_CTRL) ? 2 : 0) |
         ((mod & SDL_KMOD_ALT) ? 4 : 0);
}

// Values of EventData.Typed and Plain. The keymap is the OS layout on native;
// on web, SDL builds it from the keydown key
static int32_t event_char(SDL_Scancode scancode, SDL_Keymod mod) {
  SDL_Keycode k = SDL_GetKeyFromScancode(scancode, mod, false);
  if (k < 32 || k == 127 || (k & (SDLK_SCANCODE_MASK | SDLK_EXTENDED_MASK)))
    return 0;
  return (int32_t)k;
}

static bool translate_event(App *app, const SDL_Event *e, LubEventData *out) {
  memset(out, 0, sizeof(*out));
  out->mods = event_mods(SDL_GetModState());
  float density = lub_host_pixel_density(app->proc);
  int main_x = 0, main_y = 0, main_w = 0, main_h = 0;
  lub_host_main_rect(app->proc, &main_x, &main_y, &main_w, &main_h);
  switch (e->type) {
  case SDL_EVENT_QUIT:
    app->proc->quit_requested = true;
    out->kind = LUB_EVENT_KIND_QUIT;
    return true;
  case SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
    app->proc->pending_resize = true;
    out->kind = LUB_EVENT_KIND_WINDOW_RESIZE;
    out->x = (float)e->window.data1;
    out->y = (float)e->window.data2;
    return true;
  case SDL_EVENT_KEY_DOWN:
    if (!e->key.repeat && e->key.scancode < SDL_SCANCODE_COUNT)
      app->input.key_pressed[e->key.scancode] = true;
    out->kind = LUB_EVENT_KIND_KEY_DOWN;
    out->key = (int32_t)e->key.scancode;
    out->mods = event_mods(e->key.mod);
    out->typed = event_char(e->key.scancode, e->key.mod);
    out->plain = event_char(e->key.scancode, SDL_KMOD_NONE);
    return true;
  case SDL_EVENT_KEY_UP:
    if (e->key.scancode < SDL_SCANCODE_COUNT)
      app->input.key_released[e->key.scancode] = true;
    out->kind = LUB_EVENT_KIND_KEY_UP;
    out->key = (int32_t)e->key.scancode;
    out->mods = event_mods(e->key.mod);
    out->typed = event_char(e->key.scancode, e->key.mod);
    out->plain = event_char(e->key.scancode, SDL_KMOD_NONE);
    return true;
  case SDL_EVENT_MOUSE_BUTTON_DOWN:
    app->input.mouse_pressed_mask |= input_mouse_bit(e->button.button);
    out->kind = LUB_EVENT_KIND_MOUSE_BUTTON_DOWN;
    out->button = e->button.button;
    out->x = e->button.x * density - (float)main_x;
    out->y = e->button.y * density - (float)main_y;
    return true;
  case SDL_EVENT_MOUSE_BUTTON_UP:
    app->input.mouse_released_mask |= input_mouse_bit(e->button.button);
    out->kind = LUB_EVENT_KIND_MOUSE_BUTTON_UP;
    out->button = e->button.button;
    out->x = e->button.x * density - (float)main_x;
    out->y = e->button.y * density - (float)main_y;
    return true;
  case SDL_EVENT_MOUSE_MOTION:
    app->input.mouse_rel_x += e->motion.xrel * density;
    app->input.mouse_rel_y += e->motion.yrel * density;
    out->kind = LUB_EVENT_KIND_MOUSE_MOTION;
    out->x = e->motion.x * density - (float)main_x;
    out->y = e->motion.y * density - (float)main_y;
    out->dx = e->motion.xrel * density;
    out->dy = e->motion.yrel * density;
    return true;
  case SDL_EVENT_GAMEPAD_ADDED:
    gamepad_add(app->proc, e->gdevice.which);
    out->kind = LUB_EVENT_KIND_OTHER;
    return true;
  case SDL_EVENT_GAMEPAD_REMOVED:
    gamepad_remove(app->proc, e->gdevice.which);
    out->kind = LUB_EVENT_KIND_OTHER;
    return true;
  case SDL_EVENT_GAMEPAD_BUTTON_DOWN:
  case SDL_EVENT_GAMEPAD_BUTTON_UP:
    gamepad_button(app, &e->gbutton);
    out->kind = LUB_EVENT_KIND_OTHER;
    return true;
  case SDL_EVENT_MOUSE_WHEEL:
    app->input.mouse_wheel_x += e->wheel.x;
    app->input.mouse_wheel_y += e->wheel.y;
    out->kind = LUB_EVENT_KIND_MOUSE_WHEEL;
    out->dx = e->wheel.x;
    out->dy = e->wheel.y;
    return true;
  default:
    out->kind = LUB_EVENT_KIND_OTHER;
    return true;
  }
}

// A translated event is passed to OnEvent, so advance the declaration boundary.
bool lub_host_translate_event(App *app, const SDL_Event *e, LubEventData *out) {
  if (!translate_event(app, e, out))
    return false;
  input_sample(app);
  app->declare_epoch++;
  return true;
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
#if defined(LUB_HAS_OPENXR)
  bool xr = strcmp(app->proc->backend_name, "openxr") == 0;
  if (xr && !lubxr_poll(&app->proc->quit_requested))
    return false;
#endif
  int w = 0, h = 0;
  SDL_GetWindowSizeInPixels(app->proc->window, &w, &h);
  if (w == 0 || h == 0)
    return false;
  if (process_frame_waits_for_gpu(app->proc) || app_frame_waits_for_gpu(app))
    return false;
  uint64_t now = SDL_GetPerformanceCounter();
  if (app->proc->fixed_frame_dt > 0.0) {
    app->proc->frame_dt = app->proc->fixed_frame_dt;
  } else if (app->proc->frame_prev_counter != 0) {
    app->proc->frame_dt = (double)(now - app->proc->frame_prev_counter) /
                          (double)SDL_GetPerformanceFrequency();
    if (app->proc->frame_dt > 0.25)
      app->proc->frame_dt = 0.25;
  } else {
    app->proc->frame_dt = 1.0 / 60.0;
  }
  app->proc->frame_prev_counter = now;
  profile_frame_begin(&app->profile, app->frame_index);
  profile_begin_scope(&app->profile, "runtime.begin_frame");
  process_frame_begin(app->proc, &w, &h);
#if defined(LUB_HAS_OPENXR)
  if (xr)
    app->proc->frame_dt = lubxr_frame_dt();
#endif
  profile_end_scope(&app->profile, "runtime.begin_frame");
  if (app->quit_requested || app->proc->quit_requested) {
    profile_frame_end(&app->profile, app->frame_index);
    return false;
  }
  app->declare_epoch++;
  input_sample(app);
  ui_new_frame(app, (float)app->proc->frame_dt, w, h);
  pick_frame_begin(app);
  profile_begin_scope(&app->profile, "script.onFrame");
  if (dt)
    *dt = (float)app->proc->frame_dt;
  return true;
}

// Clear press / release edges and the motion and wheel totals (after OnFrame)
static void input_latch_clear(App *app) {
  InputState *in = &app->input;
  memset(in->key_pressed, 0, sizeof(in->key_pressed));
  memset(in->key_released, 0, sizeof(in->key_released));
  in->mouse_pressed_mask = 0;
  in->mouse_released_mask = 0;
  in->mouse_rel_x = 0.0f;
  in->mouse_rel_y = 0.0f;
  in->mouse_wheel_x = 0.0f;
  in->mouse_wheel_y = 0.0f;
  memset(in->pad_pressed, 0, sizeof(in->pad_pressed));
  memset(in->pad_released, 0, sizeof(in->pad_released));
}

void lub_host_frame_end(LubContext *ctx, bool frame_ok) {
  App *app = lub_api_app(ctx);
  uint64_t profile_frame = app->frame_index;
  profile_end_scope(&app->profile, "script.onFrame");
  input_latch_clear(app);
  profile_begin_scope(&app->profile, "runtime.pass_guard");
  if (pass_state_in_pass(&app->pass))
    pass_state_end(&app->pass);
  profile_end_scope(&app->profile, "runtime.pass_guard");
  pick_frame_end(app);
  profile_begin_scope(&app->profile, "runtime.end_frame");
  process_frame_submit(app->proc);
  app_frame_end(app, frame_ok);
  process_frame_end(app->proc);
  profile_end_scope(&app->profile, "runtime.end_frame");
  profile_frame_end(&app->profile, profile_frame);
}

bool lub_host_quit_requested(LubContext *ctx) {
  App *app = lub_api_app(ctx);
  return app->quit_requested || app->proc->quit_requested ||
         app->proc->capture_then_exit;
}

int32_t lub_host_exit_code(LubContext *ctx) {
  return lub_api_app(ctx)->exit_code;
}

static void write_exit_file(int32_t code) {
  const char *path = SDL_getenv("LUB_EXIT_FILE");
  if (!path || !*path)
    return;
  char line[32];
  int n = SDL_snprintf(line, sizeof(line), "%d\n", (int)code);
  if (!SDL_SaveFile(path, line, (size_t)n))
    SDL_Log("LUB_EXIT_FILE %s: %s", path, SDL_GetError());
}

void lub_host_destroy(LubContext *ctx) {
  if (!ctx)
    return;
  App *app = lub_api_app(ctx);
  Process *proc = app->proc;
  write_exit_file(app->exit_code);
  profile_report_at_exit(&app->profile);
  ui_shutdown();
  app_shutdown(app);
  free(app);
  gamepads_close(proc);
  process_shutdown(proc);
  free(proc);
  SDL_Quit();
}
