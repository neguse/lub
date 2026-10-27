// core (config / quit)、input、sys、profiler の C API。
#include "api_internal.h"
#include "backend.h"
#include "host_api.h"
#include "profile.h"
#include "rewind.h"
#include <SDL3/SDL.h>
#include <ctype.h>
#include <lua.h>
#include <stdio.h>
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

// Rewind で止めた frame を作り直している間は、控えた入力を返す (控えるときに Input.Grab を当ててあるので、今の Grab は見ない)
bool lub_input_key_down(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  if (sc == SDL_SCANCODE_UNKNOWN)
    return false;
  const RewindInput *replay = rewind_input(app);
  if (replay)
    return sc < SDL_SCANCODE_COUNT && replay->keys[sc];
  if (keyboard_hidden(app))
    return false;
  int key_count = 0;
  const bool *state = SDL_GetKeyboardState(&key_count);
  return state && (int)sc < key_count && state[sc];
}

bool lub_input_key_pressed(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  const RewindInput *replay = rewind_input(app);
  if (!replay && keyboard_hidden(app))
    return false;
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
         (replay ? replay->key_pressed[sc] : app->key_pressed[sc]);
}

bool lub_input_key_released(LubContext *ctx, LubStr key) {
  App *app = lub_api_app(ctx);
  SDL_Scancode sc = scancode_from_name(key);
  const RewindInput *replay = rewind_input(app);
  if (!replay && keyboard_hidden(app))
    return false;
  return sc != SDL_SCANCODE_UNKNOWN && sc < SDL_SCANCODE_COUNT &&
         (replay ? replay->key_released[sc] : app->key_released[sc]);
}

// button は省略で 1 (左)。
static int32_t mouse_button(const int32_t *button) {
  return button ? *button : 1;
}

bool lub_input_mouse_down(LubContext *ctx, const int32_t *button) {
  int32_t b = mouse_button(button);
  const RewindInput *replay = rewind_input(lub_api_app(ctx));
  if (b < 1 || (!replay && mouse_hidden(lub_api_app(ctx))))
    return false;
  SDL_MouseButtonFlags mask =
      replay ? replay->mouse_buttons : SDL_GetMouseState(NULL, NULL);
  return (mask & SDL_BUTTON_MASK(b)) != 0;
}

bool lub_input_mouse_pressed(LubContext *ctx, const int32_t *button) {
  int32_t b = mouse_button(button);
  const RewindInput *replay = rewind_input(lub_api_app(ctx));
  if (b < 1 || (!replay && mouse_hidden(lub_api_app(ctx))))
    return false;
  uint32_t mask = replay ? replay->mouse_pressed_mask
                         : lub_api_app(ctx)->mouse_pressed_mask;
  return (mask & SDL_BUTTON_MASK(b)) != 0;
}

bool lub_input_mouse_released(LubContext *ctx, const int32_t *button) {
  int32_t b = mouse_button(button);
  const RewindInput *replay = rewind_input(lub_api_app(ctx));
  if (b < 1 || (!replay && mouse_hidden(lub_api_app(ctx))))
    return false;
  uint32_t mask = replay ? replay->mouse_released_mask
                         : lub_api_app(ctx)->mouse_released_mask;
  return (mask & SDL_BUTTON_MASK(b)) != 0;
}

void lub_input_mouse_pos(LubContext *ctx, float *x, float *y) {
  App *app = lub_api_app(ctx);
  float density = lub_host_pixel_density(app);
  int main_x = 0, main_y = 0, main_w = 0, main_h = 0;
  lub_host_main_rect(app, &main_x, &main_y, &main_w, &main_h);
  float px = 0.0f, py = 0.0f;
  const RewindInput *replay = rewind_input(app);
  if (replay) {
    px = replay->mouse_x;
    py = replay->mouse_y;
  } else {
    SDL_GetMouseState(&px, &py);
  }
  if (x)
    *x = px * density - (float)main_x;
  if (y)
    *y = py * density - (float)main_y;
}

// 今 frame の相対移動の合計 (framebuffer px)。
// frame の中では何度読んでも同じ。
void lub_input_mouse_delta(LubContext *ctx, float *dx, float *dy) {
  App *app = lub_api_app(ctx);
  const RewindInput *replay = rewind_input(app);
  bool hidden = !replay && mouse_hidden(app);
  if (dx)
    *dx = hidden ? 0.0f : (replay ? replay->mouse_rel_x : app->mouse_rel_x);
  if (dy)
    *dy = hidden ? 0.0f : (replay ? replay->mouse_rel_y : app->mouse_rel_y);
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

static bool player_valid(int32_t player) {
  return player >= 0 && player < LUB_GAMEPAD_SLOTS;
}

bool lub_input_gamepad_connected(LubContext *ctx, int32_t player) {
  const RewindInput *replay = rewind_input(lub_api_app(ctx));
  if (replay)
    return player_valid(player) && replay->pad_connected[player];
  return gamepad(lub_api_app(ctx), player) != NULL;
}

// SDL の軸は -32768..32767。stick は両端が -1 と 1 になるように割り、trigger (0..32767) は 0..1 にする。
float lub_input_gamepad_axis(LubContext *ctx, int32_t player, int32_t axis) {
  const RewindInput *replay = rewind_input(lub_api_app(ctx));
  if (replay)
    return player_valid(player) && axis >= 0 && axis < SDL_GAMEPAD_AXIS_COUNT
               ? replay->pad_axes[player][axis]
               : 0.0f;
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
  const RewindInput *replay = rewind_input(lub_api_app(ctx));
  if (replay)
    return player_valid(player) && gamepad_button_valid(button) &&
           (replay->pad_down[player] & (UINT32_C(1) << button)) != 0;
  SDL_Gamepad *pad = gamepad(lub_api_app(ctx), player);
  return pad && gamepad_button_valid(button) &&
         SDL_GetGamepadButton(pad, (SDL_GamepadButton)button);
}

bool lub_input_gamepad_pressed(LubContext *ctx, int32_t player,
                               int32_t button) {
  App *app = lub_api_app(ctx);
  const RewindInput *replay = rewind_input(app);
  uint32_t mask = !player_valid(player) ? 0
                  : replay              ? replay->pad_pressed[player]
                                        : app->gamepad_pressed[player];
  return gamepad_button_valid(button) && (mask & (UINT32_C(1) << button)) != 0;
}

bool lub_input_gamepad_released(LubContext *ctx, int32_t player,
                                int32_t button) {
  App *app = lub_api_app(ctx);
  const RewindInput *replay = rewind_input(app);
  uint32_t mask = !player_valid(player) ? 0
                  : replay              ? replay->pad_released[player]
                                        : app->gamepad_released[player];
  return gamepad_button_valid(button) && (mask & (UINT32_C(1) << button)) != 0;
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

// lub_sys_take_trace の結果。
// 次の呼び出しまで有効 (binding はすぐ写す)
static char **g_trace_keys;
float lub_sys_seconds(LubContext *ctx) {
  (void)ctx;
  return (float)((double)SDL_GetTicksNS() / (double)SDL_NS_PER_SECOND);
}

LubStatus lub_sys_require(LubContext *ctx, LubStr module) {
  App *app = lub_api_app(ctx);
  lua_State *L = app->lua.L;
  char name[256];
  if (!L || !lub_str_copy(module, name, sizeof(name)))
    return lub_api_fail(app, "sys.require: invalid module name");
  lua_getglobal(L, "require");
  lua_pushstring(L, name);
  if (lua_pcall(L, 1, 0, 0) != LUA_OK) {
    LubStatus st = lub_api_fail(app, "sys.require: %s", lua_tostring(L, -1));
    lua_pop(L, 1);
    return st;
  }
  return LUB_OK;
}

static LubStr *g_trace_strs;
static int g_trace_count, g_trace_cap;

// tcs の --trace の出力が、実行した行を key に true を置く global table (__tcs_trace)。
// 読んだ key は table から消す (table は差し替えない。出力は chunk の local に同じ table を持つ)
void lub_sys_take_trace(LubContext *ctx, const LubStr **out,
                        int32_t *out_count) {
  App *app = lub_api_app(ctx);
  for (int i = 0; i < g_trace_count; ++i)
    free(g_trace_keys[i]);
  g_trace_count = 0;
  *out = NULL;
  *out_count = 0;
  lua_State *L = app->lua.L;
  if (!L)
    return;
  lua_getglobal(L, "__tcs_trace");
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return;
  }
  lua_pushnil(L);
  while (lua_next(L, -2)) {
    lua_pop(L, 1);
    if (lua_type(L, -1) == LUA_TSTRING && g_trace_count == g_trace_cap) {
      int cap = g_trace_cap ? g_trace_cap * 2 : 256;
      char **keys =
          (char **)realloc(g_trace_keys, (size_t)cap * sizeof(char *));
      LubStr *strs =
          (LubStr *)realloc(g_trace_strs, (size_t)cap * sizeof(LubStr));
      if (keys)
        g_trace_keys = keys;
      if (strs)
        g_trace_strs = strs;
      if (keys && strs)
        g_trace_cap = cap;
    }
    if (lua_type(L, -1) == LUA_TSTRING && g_trace_count < g_trace_cap) {
      size_t n = 0;
      const char *k = lua_tolstring(L, -1, &n);
      char *copy = (char *)malloc(n + 1);
      if (copy) {
        memcpy(copy, k, n);
        copy[n] = '\0';
        g_trace_keys[g_trace_count] = copy;
        g_trace_strs[g_trace_count] = (LubStr){copy, (int32_t)n};
        g_trace_count++;
      }
    }
    // 走査中の key に nil を置くのは lua_next が許す
    lua_pushvalue(L, -1);
    lua_pushnil(L);
    lua_rawset(L, -4);
  }
  lua_pop(L, 1);
  *out = g_trace_strs;
  *out_count = g_trace_count;
}

// lub_sys_take_values の結果。
// 次の呼び出しまで有効 (binding はすぐ写す)。1 件の site と text は 1 つの buffer に並べる
static char **g_value_bufs;
static LubTraceValue *g_values;
static int g_value_count, g_value_cap;

#define VALUE_TEXT_MAX 128
#define VALUE_STRING_MAX 40
#define VALUE_FIELDS_MAX 4

typedef struct ValueText {
  char s[VALUE_TEXT_MAX];
  size_t n;
} ValueText;

static void value_put(ValueText *t, const char *s, size_t len) {
  if (len > VALUE_TEXT_MAX - 1 - t->n)
    len = VALUE_TEXT_MAX - 1 - t->n;
  memcpy(t->s + t->n, s, len);
  t->n += len;
  t->s[t->n] = '\0';
}

static void value_puts(ValueText *t, const char *s) {
  value_put(t, s, strlen(s));
}

// 数と bool を書く (それ以外なら false)
static bool value_put_scalar(lua_State *L, int idx, ValueText *t) {
  char num[32];
  if (lua_type(L, idx) == LUA_TBOOLEAN) {
    value_puts(t, lua_toboolean(L, idx) ? "true" : "false");
    return true;
  }
  if (lua_type(L, idx) != LUA_TNUMBER)
    return false;
  if (lua_isinteger(L, idx))
    snprintf(num, sizeof(num), LUA_INTEGER_FMT,
             (LUAI_UACINT)lua_tointeger(L, idx));
  else
    snprintf(num, sizeof(num), "%.6g", (double)lua_tonumber(L, idx));
  value_puts(t, num);
  return true;
}

typedef struct ValueField {
  const char *key;
  char text[24];
} ValueField;

static int value_field_cmp(const void *a, const void *b) {
  return strcmp(((const ValueField *)a)->key, ((const ValueField *)b)->key);
}

// table の field が VALUE_FIELDS_MAX 個以下で、key が文字列、値が数か bool なら
// {X=1 Y=2} を書いて true
static bool value_put_fields(lua_State *L, int idx, ValueText *t) {
  ValueField fields[VALUE_FIELDS_MAX];
  int count = 0;
  bool small = true;
  lua_pushnil(L);
  while (lua_next(L, idx)) {
    ValueText v = {{0}, 0};
    if (count == VALUE_FIELDS_MAX || lua_type(L, -2) != LUA_TSTRING ||
        !value_put_scalar(L, -1, &v)) {
      small = false;
      lua_pop(L, 2);
      break;
    }
    // key の文字列は table が持っているので、読み終えるまで残る
    fields[count].key = lua_tostring(L, -2);
    snprintf(fields[count].text, sizeof(fields[count].text), "%s", v.s);
    count++;
    lua_pop(L, 1);
  }
  if (!small)
    return false;
  qsort(fields, (size_t)count, sizeof(fields[0]), value_field_cmp);
  value_puts(t, "{");
  for (int i = 0; i < count; i++) {
    if (i > 0)
      value_puts(t, " ");
    value_puts(t, fields[i].key);
    value_puts(t, "=");
    value_puts(t, fields[i].text);
  }
  value_puts(t, "}");
  return true;
}

// 値を短い文字にする (lub_sys_take_values の summary の規則)。
// names は型 table → 型の名前の table の stack の位置
static void value_format(lua_State *L, int idx, int names, ValueText *t) {
  idx = lua_absindex(L, idx);
  if (value_put_scalar(L, idx, t))
    return;
  switch (lua_type(L, idx)) {
  case LUA_TNIL:
    value_puts(t, "null");
    return;
  case LUA_TSTRING: {
    size_t len = 0;
    const char *s = lua_tolstring(L, idx, &len);
    size_t cut = len;
    if (cut > VALUE_STRING_MAX) {
      cut = VALUE_STRING_MAX;
      // UTF-8 の文字の途中で切らない
      while (cut > 0 && ((unsigned char)s[cut] & 0xC0) == 0x80)
        cut--;
    }
    value_puts(t, "\"");
    value_put(t, s, cut);
    value_puts(t, cut < len ? "…\"" : "\"");
    return;
  }
  case LUA_TTABLE: {
    bool named = false;
    if (lua_getmetatable(L, idx)) {
      lua_rawget(L, names);
      if (lua_type(L, -1) == LUA_TSTRING) {
        value_puts(t, lua_tostring(L, -1));
        named = true;
      }
      lua_pop(L, 1);
    }
    if (named) {
      value_put_fields(L, idx, t);
      return;
    }
    if (lua_rawlen(L, idx) == 0 && value_put_fields(L, idx, t))
      return;
    if (lua_rawlen(L, idx) > 0) {
      char len[32];
      snprintf(len, sizeof(len), "[%lld]", (long long)lua_rawlen(L, idx));
      value_puts(t, len);
      return;
    }
    value_puts(t, "{…}");
    return;
  }
  default:
    value_puts(t, lua_typename(L, lua_type(L, idx)));
    return;
  }
}

// tcs の --trace の出力が、代入した値を "file:line:col:代入先" の key に置く global
// table (__tcs_vals)。読んだ key は __tcs_trace と同じく table から消す
void lub_sys_take_values(LubContext *ctx, const LubTraceValue **out,
                         int32_t *out_count) {
  App *app = lub_api_app(ctx);
  for (int i = 0; i < g_value_count; ++i)
    free(g_value_bufs[i]);
  g_value_count = 0;
  *out = NULL;
  *out_count = 0;
  lua_State *L = app->lua.L;
  if (!L)
    return;
  lua_getglobal(L, "__tcs_vals");
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return;
  }
  int vals = lua_gettop(L);
  // 型 table → 型の名前 (__tcs_trace_classes を裏返す)
  lua_newtable(L);
  int names = lua_gettop(L);
  lua_getglobal(L, "__tcs_trace_classes");
  if (lua_istable(L, -1)) {
    lua_pushnil(L);
    while (lua_next(L, -2)) {
      lua_pushvalue(L, -2);
      lua_rawset(L, names);
    }
  }
  lua_pop(L, 1);
  lua_pushnil(L);
  while (lua_next(L, vals)) {
    if (lua_type(L, -2) == LUA_TSTRING && g_value_count == g_value_cap) {
      int cap = g_value_cap ? g_value_cap * 2 : 256;
      char **bufs =
          (char **)realloc(g_value_bufs, (size_t)cap * sizeof(char *));
      if (bufs)
        g_value_bufs = bufs;
      LubTraceValue *values = (LubTraceValue *)realloc(
          g_values, (size_t)cap * sizeof(LubTraceValue));
      if (values)
        g_values = values;
      if (bufs && values)
        g_value_cap = cap;
    }
    if (lua_type(L, -2) == LUA_TSTRING && g_value_count < g_value_cap) {
      size_t n = 0;
      const char *site = lua_tolstring(L, -2, &n);
      ValueText text = {{0}, 0};
      value_format(L, -1, names, &text);
      char *buf = (char *)malloc(n + 1 + text.n + 1);
      if (buf) {
        memcpy(buf, site, n);
        buf[n] = '\0';
        memcpy(buf + n + 1, text.s, text.n + 1);
        g_value_bufs[g_value_count] = buf;
        g_values[g_value_count] =
            (LubTraceValue){{buf, (int32_t)n}, {buf + n + 1, (int32_t)text.n}};
        g_value_count++;
      }
    }
    lua_pop(L, 1);
    // 走査中の key に nil を置くのは lua_next が許す
    lua_pushvalue(L, -1);
    lua_pushnil(L);
    lua_rawset(L, vals);
  }
  lua_pop(L, 2);
  *out = g_values;
  *out_count = g_value_count;
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
