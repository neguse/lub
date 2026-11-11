// host API (include/lub/lub_host.h)。
// tcs VM の player (src/vm_player.c)、.NET 実行の host、tcs2c 実行の host (src/tcs_host.c) が同じ関数で runtime を回す。
// frame の骨格 (dt、profile、begin / end、入力 latch の clear) はここが唯一の実装。
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

// EM_JS bodies are JavaScript; keep clang-format away from them (same guard as src/host.c).
// clang-format off
// SDL は修飾 key を物理の位置 (code) でしか見ないので、CapsLock を Ctrl にした環境の key ("Control"、code は "CapsLock") を
// 修飾 key と思わない。論理の key から code を直した event を、SDL より先に流し直す。
EM_JS(void, lub_web_fix_modifier_codes, (void), {
  const names = {Control: "Control", Shift: "Shift", Alt: "Alt"};
  const side = (e) => (e.location === 2 ? "Right" : "Left");
  const fixedCode = (e) => {
    if (names[e.key])
      return e.code.startsWith(names[e.key]) ? null : names[e.key] + side(e);
    // 半角/全角の code は US の ` と同じ Backquote なので、そのままだと SDL は ` を打つ key にする
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

static int gamepad_slot(App *app, SDL_JoystickID id) {
  for (int i = 0; i < LUB_GAMEPAD_SLOTS; ++i)
    if (app->gamepads[i] && app->gamepad_ids[i] == id)
      return i;
  return -1;
}

// 空いている最初の slot に入れる。
static void gamepad_add(App *app, SDL_JoystickID id) {
  if (gamepad_slot(app, id) >= 0)
    return;
  for (int i = 0; i < LUB_GAMEPAD_SLOTS; ++i) {
    if (app->gamepads[i])
      continue;
    app->gamepads[i] = SDL_OpenGamepad(id);
    app->gamepad_ids[i] = id;
    return;
  }
}

// 押していた button の release は、SDL が REMOVED より先に button の event で送る。
static void gamepad_remove(App *app, SDL_JoystickID id) {
  int slot = gamepad_slot(app, id);
  if (slot < 0)
    return;
  SDL_CloseGamepad(app->gamepads[slot]);
  app->gamepads[slot] = NULL;
}

_Static_assert(SDL_GAMEPAD_BUTTON_COUNT <= 32,
               "gamepad buttons are kept in a uint32_t bit mask");

static void gamepad_button(App *app, const SDL_GamepadButtonEvent *e) {
  int slot = gamepad_slot(app, e->which);
  if (slot < 0 || e->button >= SDL_GAMEPAD_BUTTON_COUNT)
    return;
  uint32_t bit = UINT32_C(1) << e->button;
  if (e->down)
    app->input.pad_pressed[slot] |= bit;
  else
    app->input.pad_released[slot] |= bit;
}

// SDL の軸 (-32768..32767) を Input.GamepadAxis の値にする。
// stick は両端が -1 と 1 になるように割り、trigger (0..32767) は 0..1 にする。
static float pad_axis(SDL_Gamepad *pad, int axis) {
  int value = SDL_GetGamepadAxis(pad, (SDL_GamepadAxis)axis);
  if (axis == SDL_GAMEPAD_AXIS_LEFT_TRIGGER ||
      axis == SDL_GAMEPAD_AXIS_RIGHT_TRIGGER)
    return value > 0 ? (float)value / 32767.0f : 0.0f;
  return (float)value / (value < 0 ? 32768.0f : 32767.0f);
}

// 今押されているもの (key、mouse の button と位置、gamepad) を SDL の状態から写す。
// 押した・離した瞬間と移動の合計は event が積む。
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
  float density = lub_host_pixel_density(app);
  int main_x = 0, main_y = 0, main_w = 0, main_h = 0;
  lub_host_main_rect(app, &main_x, &main_y, &main_w, &main_h);
  in->mouse_x = px * density - (float)main_x;
  in->mouse_y = py * density - (float)main_y;
  for (int i = 0; i < LUB_GAMEPAD_SLOTS; ++i) {
    SDL_Gamepad *pad = app->gamepads[i];
    in->pad_connected[i] = pad != NULL;
    in->pad_down[i] = 0;
    for (int a = 0; a < SDL_GAMEPAD_AXIS_COUNT; ++a)
      in->pad_axes[i][a] = pad ? pad_axis(pad, a) : 0.0f;
    for (int b = 0; pad && b < SDL_GAMEPAD_BUTTON_COUNT; ++b)
      if (SDL_GetGamepadButton(pad, (SDL_GamepadButton)b))
        in->pad_down[i] |= UINT32_C(1) << b;
  }
}

// gamepad が使えなくても (subsystem の初期化に失敗しても) runtime は動かす。
static void gamepads_init(App *app) {
  if (!SDL_InitSubSystem(SDL_INIT_GAMEPAD)) {
    SDL_Log("gamepad init failed: %s", SDL_GetError());
    return;
  }
  int count = 0;
  SDL_JoystickID *ids = SDL_GetGamepads(&count);
  for (int i = 0; ids && i < count; ++i)
    gamepad_add(app, ids[i]);
  SDL_free(ids);
}

static void gamepads_close(App *app) {
  for (int i = 0; i < LUB_GAMEPAD_SLOTS; ++i) {
    if (app->gamepads[i])
      SDL_CloseGamepad(app->gamepads[i]);
    app->gamepads[i] = NULL;
  }
}

LubContext *lub_host_create(const LubHostOpts *opts) {
  // .NET の host (dotnet/Lub) はここが SDL の入口。
  // player (src/main.c) と同じく assertion を dialog にせず abort させる。
  // 環境変数 SDL_ASSERT が優先。
  SDL_SetHint(SDL_HINT_ASSERT, "abort");
#ifdef __EMSCRIPTEN__
  // web の tcs VM の player は、ここを通ってから SDL を立てる
  lub_web_fix_modifier_codes();
#endif
#ifdef SDL_PLATFORM_ANDROID
  // APK のデータを内部ストレージに写して cwd にする
  if (!android_prepare())
    return NULL;
#endif
#ifndef __EMSCRIPTEN__
  // export は display を使わない (window は大きさを持つだけ)。
  // 環境変数 SDL_VIDEO_DRIVER が優先。
  if (export_active())
    SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "offscreen");
#endif
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
    app->readback_fixed_latency = opts->readback_fixed_latency;
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
  gamepads_init(app);
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

// SDL reports the mouse in window coordinates. On a high pixel density window
// (metal backend: Retina, iPhone) those are points, while everything the game
// draws with is in framebuffer pixels (Gfx.Size), so input is scaled to match.
float lub_host_pixel_density(App *app) {
  float density = app->window ? SDL_GetWindowPixelDensity(app->window) : 0.0f;
  return density > 0.0f ? density : 1.0f;
}

void lub_host_main_rect(App *app, int *x, int *y, int *w, int *h) {
  *x = *y = *w = *h = 0;
  if (!app->window)
    return;
  if (app->fixed_w > 0 && app->fixed_h > 0) {
    *w = app->fixed_w;
    *h = app->fixed_h;
    return;
  }
  SDL_GetWindowSizeInPixels(app->window, w, h);
  SDL_Rect safe;
  if (!SDL_GetWindowSafeArea(app->window, &safe) || safe.w <= 0 || safe.h <= 0)
    return;
  float density = lub_host_pixel_density(app);
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

// EventData.Mods の値
static int32_t event_mods(SDL_Keymod mod) {
  return ((mod & SDL_KMOD_SHIFT) ? 1 : 0) | ((mod & SDL_KMOD_CTRL) ? 2 : 0) |
         ((mod & SDL_KMOD_ALT) ? 4 : 0);
}

// EventData.Typed と Plain の値。keymap は native では OS の配列、web では keydown の key から SDL が作る
static int32_t event_char(SDL_Scancode scancode, SDL_Keymod mod) {
  SDL_Keycode k = SDL_GetKeyFromScancode(scancode, mod, false);
  if (k < 32 || k == 127 || (k & (SDLK_SCANCODE_MASK | SDLK_EXTENDED_MASK)))
    return 0;
  return (int32_t)k;
}

static bool translate_event(App *app, const SDL_Event *e, LubEventData *out) {
  memset(out, 0, sizeof(*out));
  out->mods = event_mods(SDL_GetModState());
  float density = lub_host_pixel_density(app);
  int main_x = 0, main_y = 0, main_w = 0, main_h = 0;
  lub_host_main_rect(app, &main_x, &main_y, &main_w, &main_h);
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
    app->input.mouse_pressed_mask |= SDL_BUTTON_MASK(e->button.button);
    out->kind = LUB_EVENT_KIND_MOUSE_BUTTON_DOWN;
    out->button = e->button.button;
    out->x = e->button.x * density - (float)main_x;
    out->y = e->button.y * density - (float)main_y;
    return true;
  case SDL_EVENT_MOUSE_BUTTON_UP:
    app->input.mouse_released_mask |= SDL_BUTTON_MASK(e->button.button);
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
    gamepad_add(app, e->gdevice.which);
    out->kind = LUB_EVENT_KIND_OTHER;
    return true;
  case SDL_EVENT_GAMEPAD_REMOVED:
    gamepad_remove(app, e->gdevice.which);
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

// 変換できた event は OnEvent に渡るので、宣言の区切りを進める。
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
  bool xr = strcmp(app->backend_name, "openxr") == 0;
  if (xr && !lubxr_poll(&app->quit_requested))
    return false;
#endif
  int w = 0, h = 0;
  SDL_GetWindowSizeInPixels(app->window, &w, &h);
  if (w == 0 || h == 0)
    return false;
  if (app_frame_waits_for_gpu(app))
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
#if defined(LUB_HAS_OPENXR)
  if (xr)
    app->frame_dt = lubxr_frame_dt();
#endif
  profile_end_scope(&app->profile, "runtime.begin_frame");
  if (app->quit_requested) {
    profile_frame_end(&app->profile, app->frame_index);
    return false;
  }
  app->declare_epoch++;
  input_sample(app);
  ui_new_frame(app, (float)app->frame_dt, w, h);
  pick_frame_begin(app);
  profile_begin_scope(&app->profile, "script.onFrame");
  if (dt)
    *dt = (float)app->frame_dt;
  return true;
}

// 押した・離した瞬間と、移動と wheel の合計を消す (OnFrame の後)
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
  app_frame_end(app, frame_ok);
  profile_end_scope(&app->profile, "runtime.end_frame");
  profile_frame_end(&app->profile, profile_frame);
}

bool lub_host_quit_requested(LubContext *ctx) {
  App *app = lub_api_app(ctx);
  return app->quit_requested || app->capture_then_exit;
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
  write_exit_file(app->exit_code);
  profile_report_at_exit(&app->profile);
  ui_shutdown();
  app_shutdown(app);
  gamepads_close(app);
  free(app);
  SDL_Quit();
}
