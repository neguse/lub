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

// 型 table → 型の名前 (__tcs_trace_classes を裏返した table) を積み、その位置を返す
static int push_class_names(lua_State *L) {
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
  return names;
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
  int names = push_class_names(L);
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

// lub_sys_inspect の結果。次の呼び出しまで有効。1 件の文字列は 1 つの buffer に並べる
static char **g_inspect_bufs;
static LubInspectNode *g_inspect;
static int g_inspect_count, g_inspect_cap;

// tcs の module registry (__tcs_module_runtime.registry) を積む。無ければ nil
static void push_registry(lua_State *L) {
  lua_getglobal(L, "__tcs_module_runtime");
  if (lua_istable(L, -1))
    lua_getfield(L, -1, "registry");
  else
    lua_pushnil(L);
  lua_remove(L, -2);
}

// 節の子の key。is_int は List と配列の要素 (i は 0 始まり) か、Dictionary の整数の key
typedef struct InspectKey {
  bool is_int;
  bool is_list;
  lua_Integer i;
  const char *s;
} InspectKey;

typedef struct InspectKeys {
  InspectKey *k;
  int n, cap;
} InspectKeys;

static void inspect_key_add(InspectKeys *keys, InspectKey key) {
  if (keys->n == keys->cap) {
    int cap = keys->cap ? keys->cap * 2 : 32;
    InspectKey *k = (InspectKey *)realloc(keys->k, (size_t)cap * sizeof(*k));
    if (!k)
      return;
    keys->k = k;
    keys->cap = cap;
  }
  keys->k[keys->n++] = key;
}

static int inspect_key_cmp(const void *a, const void *b) {
  const InspectKey *x = (const InspectKey *)a, *y = (const InspectKey *)b;
  if (x->is_int != y->is_int)
    return x->is_int ? -1 : 1;
  if (x->is_int)
    return x->i < y->i ? -1 : x->i > y->i;
  return strcmp(x->s, y->s);
}

static size_t inspect_ident_len(const char *s) {
  size_t n = 0;
  if (!(isalpha((unsigned char)s[0]) || s[0] == '_'))
    return 0;
  while (isalnum((unsigned char)s[n]) || s[n] == '_')
    n++;
  return n;
}

static bool inspect_is_ident(const char *s) {
  size_t n = inspect_ident_len(s);
  return n > 0 && s[n] == '\0';
}

// 根の子: --trace で変換した class の名前。entry が先頭、ほかは名前の順
static void inspect_root_keys(lua_State *L, InspectKeys *keys) {
  const char *entry = NULL;
  push_registry(L);
  if (lua_istable(L, -1)) {
    lua_getfield(L, -1, "entry_type");
    if (lua_type(L, -1) == LUA_TSTRING) {
      const char *id = lua_tostring(L, -1);
      const char *hash = strrchr(id, '#');
      entry = hash ? hash + 1 : id;
    }
    lua_pop(L, 1);
  }
  lua_getglobal(L, "__tcs_trace_classes");
  int first = keys->n;
  if (lua_istable(L, -1)) {
    lua_pushnil(L);
    while (lua_next(L, -2)) {
      if (lua_type(L, -2) == LUA_TSTRING)
        inspect_key_add(keys, (InspectKey){false, false, 0, lua_tostring(L, -2)});
      lua_pop(L, 1);
    }
  }
  qsort(keys->k + first, (size_t)(keys->n - first), sizeof(InspectKey),
        inspect_key_cmp);
  for (int i = first; entry && i < keys->n; i++) {
    if (strcmp(keys->k[i].s, entry) == 0) {
      InspectKey e = keys->k[i];
      memmove(keys->k + first + 1, keys->k + first,
              (size_t)(i - first) * sizeof(InspectKey));
      keys->k[first] = e;
      break;
    }
  }
  // 名前の文字列は __tcs_trace_classes の key で、registry が型を持つ間は消えない
  lua_pop(L, 2);
}

// class table (idx) の static field の名前を宣言の順に足す (registry の module の型の記述から)
static bool inspect_static_keys(lua_State *L, int cls, InspectKeys *keys) {
  bool found = false;
  push_registry(L);
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return false;
  }
  int reg = lua_gettop(L);
  lua_getfield(L, reg, "types");
  lua_getfield(L, reg, "modules");
  if (lua_istable(L, -1) && lua_istable(L, -2)) {
    lua_pushnil(L);
    while (!found && lua_next(L, -2)) {
      lua_getfield(L, -1, "types");
      int n = lua_istable(L, -1) ? (int)lua_rawlen(L, -1) : 0;
      for (int i = 1; !found && i <= n; i++) {
        lua_rawgeti(L, -1, i);
        lua_getfield(L, -1, "id");
        lua_rawget(L, reg + 1);
        bool same = lua_rawequal(L, -1, cls);
        lua_pop(L, 1);
        if (same) {
          found = true;
          lua_getfield(L, -1, "statics");
          int m = lua_istable(L, -1) ? (int)lua_rawlen(L, -1) : 0;
          for (int j = 1; j <= m; j++) {
            lua_rawgeti(L, -1, j);
            lua_getfield(L, -1, "key");
            // 型の記述は registry が持つので、key の文字列は消えない
            if (lua_type(L, -1) == LUA_TSTRING)
              inspect_key_add(keys, (InspectKey){false, false, 0, lua_tostring(L, -1)});
            lua_pop(L, 2);
          }
          lua_pop(L, 1);
        }
        lua_pop(L, 1);
      }
      lua_pop(L, 2);
    }
  }
  lua_settop(L, reg - 1);
  return found;
}

// table (idx) の子の key を足す。List と配列は要素、それ以外は関数と "__" で始まる key を除いた key の順
static void inspect_table_keys(lua_State *L, int idx, InspectKeys *keys) {
  lua_Integer len = (lua_Integer)lua_rawlen(L, idx);
  if (len > 0) {
    for (lua_Integer i = 0; i < len; i++)
      inspect_key_add(keys, (InspectKey){true, true, i, NULL});
    return;
  }
  int first = keys->n;
  lua_pushnil(L);
  while (lua_next(L, idx)) {
    if (lua_type(L, -1) != LUA_TFUNCTION) {
      if (lua_type(L, -2) == LUA_TSTRING) {
        const char *k = lua_tostring(L, -2);
        if (strncmp(k, "__", 2) != 0)
          inspect_key_add(keys, (InspectKey){false, false, 0, k});
      } else if (lua_isinteger(L, -2)) {
        inspect_key_add(keys, (InspectKey){true, false, lua_tointeger(L, -2), NULL});
      }
    }
    lua_pop(L, 1);
  }
  qsort(keys->k + first, (size_t)(keys->n - first), sizeof(InspectKey),
        inspect_key_cmp);
}

// 値 (idx) の子の key を足す。class は root のときだけ (根の子)
static void inspect_keys(lua_State *L, int idx, bool cls, InspectKeys *keys) {
  if (!lua_istable(L, idx))
    return;
  if (cls && inspect_static_keys(L, idx, keys))
    return;
  inspect_table_keys(L, idx, keys);
}

// table (idx、絶対の位置) の子の値を積む
static void inspect_push_child(lua_State *L, int idx, const InspectKey *k) {
  if (k->is_list)
    lua_rawgeti(L, idx, k->i + 1);
  else if (k->is_int)
    lua_rawgeti(L, idx, k->i);
  else {
    lua_pushstring(L, k->s);
    lua_rawget(L, idx);
  }
}

typedef struct InspectText {
  char *s;
  size_t n, cap;
} InspectText;

static void inspect_put(InspectText *t, const char *s, size_t len) {
  if (t->n + len + 1 > t->cap) {
    size_t cap = t->cap ? t->cap * 2 : 128;
    while (cap < t->n + len + 1)
      cap *= 2;
    char *g = (char *)realloc(t->s, cap);
    if (!g)
      return;
    t->s = g;
    t->cap = cap;
  }
  memcpy(t->s + t->n, s, len);
  t->n += len;
  t->s[t->n] = '\0';
}

static void inspect_puts(InspectText *t, const char *s) {
  inspect_put(t, s, strlen(s));
}

// path を辿った値を積む。辿った table は ancestors (table → true) に入れる。
// 辿れなければ何も積まずに false。*is_class は値が根の class のとき true
static bool inspect_resolve(lua_State *L, const char *path, int ancestors,
                            bool *is_class) {
  size_t n = inspect_ident_len(path);
  *is_class = true;
  if (n == 0)
    return false;
  lua_getglobal(L, "__tcs_trace_classes");
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return false;
  }
  lua_pushlstring(L, path, n);
  lua_rawget(L, -2);
  lua_remove(L, -2);
  const char *p = path + n;
  while (*p) {
    if (!lua_istable(L, -1)) {
      lua_pop(L, 1);
      return false;
    }
    int t = lua_gettop(L);
    lua_pushvalue(L, t);
    lua_pushboolean(L, 1);
    lua_rawset(L, ancestors);
    *is_class = false;
    if (*p == '.') {
      p++;
      n = inspect_ident_len(p);
      if (n == 0) {
        lua_pop(L, 1);
        return false;
      }
      lua_pushlstring(L, p, n);
      lua_rawget(L, t);
      p += n;
    } else if (p[0] == '[' && p[1] == '"') {
      // ["key"]。key の中の " と \ は \ を前に置く
      InspectText key = {0};
      p += 2;
      while (*p && *p != '"') {
        if (*p == '\\' && p[1])
          p++;
        inspect_put(&key, p++, 1);
      }
      bool closed = p[0] == '"' && p[1] == ']';
      if (closed)
        lua_pushlstring(L, key.s ? key.s : "", key.n);
      free(key.s);
      if (!closed) {
        lua_pop(L, 1);
        return false;
      }
      p += 2;
      lua_rawget(L, t);
    } else if (*p == '[') {
      char *end = NULL;
      long long i = strtoll(p + 1, &end, 10);
      if (end == p + 1 || *end != ']') {
        lua_pop(L, 1);
        return false;
      }
      p = end + 1;
      // 要素の並び (List と配列) は 0 始まりで書く
      lua_rawgeti(L, t, lua_rawlen(L, t) > 0 ? (lua_Integer)i + 1 : (lua_Integer)i);
    } else {
      lua_pop(L, 1);
      return false;
    }
    lua_remove(L, t);
  }
  return true;
}

// 子の名前 (親から見た書き方) を書く。path は親の path に続けて書く形
static void inspect_put_name(InspectText *t, const InspectKey *k, bool in_path) {
  char num[32];
  if (k->is_int) {
    snprintf(num, sizeof(num), "[%lld]", (long long)k->i);
    inspect_puts(t, num);
  } else if (inspect_is_ident(k->s)) {
    if (in_path)
      inspect_puts(t, ".");
    inspect_puts(t, k->s);
  } else {
    inspect_puts(t, "[\"");
    for (const char *c = k->s; *c; c++) {
      if (*c == '"' || *c == '\\')
        inspect_puts(t, "\\");
      inspect_put(t, c, 1);
    }
    inspect_puts(t, "\"]");
  }
}

// 1 件を g_inspect に足す。子の値は stack の top
static void inspect_add(lua_State *L, const char *parent, const InspectKey *k,
                        int names, int ancestors, bool child_is_class) {
  if (g_inspect_count == g_inspect_cap) {
    int cap = g_inspect_cap ? g_inspect_cap * 2 : 64;
    char **bufs = (char **)realloc(g_inspect_bufs, (size_t)cap * sizeof(char *));
    if (bufs)
      g_inspect_bufs = bufs;
    LubInspectNode *nodes =
        (LubInspectNode *)realloc(g_inspect, (size_t)cap * sizeof(LubInspectNode));
    if (nodes)
      g_inspect = nodes;
    if (!bufs || !nodes)
      return;
    g_inspect_cap = cap;
  }
  int v = lua_gettop(L);
  InspectText path = {0}, name = {0};
  if (parent[0] == '\0') {
    inspect_puts(&path, k->s);
    inspect_puts(&name, k->s);
  } else {
    inspect_puts(&path, parent);
    inspect_put_name(&path, k, true);
    inspect_put_name(&name, k, false);
  }
  ValueText text = {{0}, 0};
  value_format(L, v, names, &text);
  const char *type = "";
  if (lua_istable(L, v) && lua_getmetatable(L, v)) {
    lua_rawget(L, names);
    if (lua_type(L, -1) == LUA_TSTRING)
      type = lua_tostring(L, -1);
    lua_pop(L, 1);
  }
  bool cycle = false;
  int count = 0;
  if (lua_istable(L, v)) {
    lua_pushvalue(L, v);
    lua_rawget(L, ancestors);
    cycle = lua_toboolean(L, -1);
    lua_pop(L, 1);
    if (!cycle) {
      InspectKeys keys = {0};
      inspect_keys(L, v, child_is_class, &keys);
      count = keys.n;
      free(keys.k);
    }
  }
  size_t tn = strlen(type);
  char *buf = (char *)malloc(path.n + name.n + text.n + tn + 4);
  if (buf && path.s && name.s) {
    char *p = buf;
    memcpy(p, path.s, path.n + 1);
    LubStr sp = {p, (int32_t)path.n};
    p += path.n + 1;
    memcpy(p, name.s, name.n + 1);
    LubStr sn = {p, (int32_t)name.n};
    p += name.n + 1;
    memcpy(p, text.s, text.n + 1);
    LubStr st = {p, (int32_t)text.n};
    p += text.n + 1;
    memcpy(p, type, tn + 1);
    LubStr sy = {p, (int32_t)tn};
    g_inspect_bufs[g_inspect_count] = buf;
    g_inspect[g_inspect_count++] = (LubInspectNode){sp, sn, st, sy, count, cycle};
  } else {
    free(buf);
  }
  free(path.s);
  free(name.s);
}

void lub_sys_inspect(LubContext *ctx, LubStr path, int32_t offset,
                     int32_t count, const LubInspectNode **out,
                     int32_t *out_count) {
  App *app = lub_api_app(ctx);
  for (int i = 0; i < g_inspect_count; ++i)
    free(g_inspect_bufs[i]);
  g_inspect_count = 0;
  *out = NULL;
  *out_count = 0;
  lua_State *L = app->lua.L;
  if (!L || offset < 0 || count <= 0)
    return;
  int top = lua_gettop(L);
  char *p = (char *)malloc((size_t)path.len + 1);
  if (!p)
    return;
  memcpy(p, path.ptr, (size_t)path.len);
  p[path.len] = '\0';
  int names = push_class_names(L);
  lua_newtable(L);
  int ancestors = lua_gettop(L);
  InspectKeys keys = {0};
  bool root = p[0] == '\0';
  bool is_class = false;
  int node = 0;
  if (root) {
    inspect_root_keys(L, &keys);
    lua_getglobal(L, "__tcs_trace_classes");
    node = lua_gettop(L);
  } else if (inspect_resolve(L, p, ancestors, &is_class)) {
    node = lua_gettop(L);
    if (lua_istable(L, node)) {
      lua_pushvalue(L, node);
      lua_pushboolean(L, 1);
      lua_rawset(L, ancestors);
    }
    inspect_keys(L, node, is_class, &keys);
  }
  for (int i = offset; node && i < keys.n && i < offset + count; i++) {
    inspect_push_child(L, node, &keys.k[i]);
    inspect_add(L, p, &keys.k[i], names, ancestors, root);
    lua_pop(L, 1);
  }
  free(keys.k);
  free(p);
  lua_settop(L, top);
  *out = g_inspect;
  *out_count = g_inspect_count;
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
