// Rewind (rewind.h)。
#include "rewind.h"
#include "app.h"
#include <lauxlib.h>
#include <lua.h>
#include <stdlib.h>
#include <string.h>

typedef struct RewindFrame {
  int snapshot; // registry ref (original table -> 中身の写し)
  double dt;
  RewindInput input;
} RewindFrame;

typedef struct RewindState {
  int capacity;
  RewindFrame *frames; // ring
  int head, count;     // head = 一番古い控え
  int paused;          // 止めている位置 (古い方からの添字、-1 = 止めていない)
  bool replaying;      // 止めた frame のゲームの OnFrame の間
} RewindState;

static RewindState *state(App *app) {
  if (!app->rewind) {
    app->rewind = (RewindState *)calloc(1, sizeof(RewindState));
    if (app->rewind)
      app->rewind->paused = -1;
  }
  return app->rewind;
}

static RewindFrame *frame_at(RewindState *r, int i) {
  return &r->frames[(r->head + i) % r->capacity];
}

static void drop_frame(App *app, RewindFrame *f) {
  if (app->lua.L && f->snapshot != LUA_NOREF)
    luaL_unref(app->lua.L, LUA_REGISTRYINDEX, f->snapshot);
  f->snapshot = LUA_NOREF;
}

static void drop_all(App *app, RewindState *r) {
  for (int i = 0; i < r->count; ++i)
    drop_frame(app, frame_at(r, i));
  r->count = 0;
  r->head = 0;
  r->paused = -1;
}

void rewind_record(App *app, int frames) {
  RewindState *r = state(app);
  if (!r || !app->in_overlay || frames < 0 || frames == r->capacity)
    return;
  drop_all(app, r);
  free(r->frames);
  r->frames = NULL;
  r->capacity = 0;
  if (frames == 0)
    return;
  r->frames = (RewindFrame *)calloc((size_t)frames, sizeof(RewindFrame));
  if (r->frames)
    r->capacity = frames;
}

int rewind_count(const App *app) {
  return app->rewind ? app->rewind->count : 0;
}

int rewind_position(const App *app) {
  const RewindState *r = app->rewind;
  if (!r || r->paused < 0)
    return -1;
  return r->count - 1 - r->paused;
}

bool rewind_paused(const App *app) {
  return app->rewind && app->rewind->paused >= 0;
}

// ---------------------------------------------------------------- snapshot

// 状態の根: entry の class と、global の __tcs_trace_classes の値の table (overlay の class を除く)
static void push_roots(App *app, lua_State *L) {
  lua_newtable(L);
  int roots = lua_gettop(L);
  int n = 0;
  lua_ctx_push_entry_class(&app->lua);
  if (lua_istable(L, -1))
    lua_rawseti(L, roots, ++n);
  else
    lua_pop(L, 1);
  // overlay の class は (ゲームと同じディレクトリにあって登録されていても) 戻さない
  if (app->lua.overlay_ref != LUA_NOREF)
    lua_rawgeti(L, LUA_REGISTRYINDEX, app->lua.overlay_ref);
  else
    lua_pushnil(L);
  int overlay = lua_gettop(L);
  lua_getglobal(L, "__tcs_trace_classes");
  if (lua_istable(L, -1)) {
    lua_pushnil(L);
    while (lua_next(L, -2)) {
      if (lua_istable(L, -1) && !lua_rawequal(L, -1, overlay)) {
        lua_pushvalue(L, -1);
        lua_rawseti(L, roots, ++n);
      }
      lua_pop(L, 1);
    }
  }
  lua_pop(L, 2);
}

// 表から辿れる table を全部集め、それぞれの中身 (関数の値を除く) の写しを作る。
// 返すのは {original = 写し} の table (stack の上に積む)
static void push_snapshot(App *app, lua_State *L) {
  lua_newtable(L); // snap
  int snap = lua_gettop(L);
  push_roots(app, L); // queue (配列)
  int queue = lua_gettop(L);
  int tail = (int)lua_rawlen(L, queue);
  for (int head = 1; head <= tail; ++head) {
    lua_rawgeti(L, queue, head); // t
    int t = lua_gettop(L);
    lua_pushvalue(L, t);
    lua_rawget(L, snap);
    bool seen = !lua_isnil(L, -1);
    lua_pop(L, 1);
    if (seen) {
      lua_pop(L, 1);
      continue;
    }
    lua_newtable(L); // copy
    int copy = lua_gettop(L);
    lua_pushnil(L);
    while (lua_next(L, t)) {
      // key -2, value -1
      if (lua_type(L, -1) != LUA_TFUNCTION) {
        lua_pushvalue(L, -2);
        lua_pushvalue(L, -2);
        lua_rawset(L, copy);
        if (lua_istable(L, -1)) {
          lua_pushvalue(L, -1);
          lua_rawseti(L, queue, ++tail);
        }
        if (lua_istable(L, -2)) {
          lua_pushvalue(L, -2);
          lua_rawseti(L, queue, ++tail);
        }
      }
      lua_pop(L, 1);
    }
    lua_pushvalue(L, t);
    lua_pushvalue(L, copy);
    lua_rawset(L, snap);
    lua_pop(L, 2); // copy, t
  }
  lua_pop(L, 1); // queue
}

// 写しの中身を元の table に書き戻す (関数の値はそのまま残す。hot reload で差し替えた method を戻さない)
static void restore_snapshot(lua_State *L, int ref) {
  lua_rawgeti(L, LUA_REGISTRYINDEX, ref);
  int snap = lua_gettop(L);
  lua_pushnil(L);
  while (lua_next(L, snap)) {
    // original -2, copy -1
    int copy = lua_gettop(L);
    int orig = copy - 1;
    lua_pushnil(L);
    while (lua_next(L, orig)) {
      if (lua_type(L, -1) != LUA_TFUNCTION) {
        lua_pushvalue(L, -2);
        lua_pushnil(L);
        lua_rawset(L, orig);
      }
      lua_pop(L, 1);
    }
    lua_pushnil(L);
    while (lua_next(L, copy)) {
      lua_pushvalue(L, -2);
      lua_pushvalue(L, -2);
      lua_rawset(L, orig);
      lua_pop(L, 1);
    }
    lua_pop(L, 1); // copy
  }
  lua_pop(L, 1); // snap
}

// ---------------------------------------------------------------- input

// SDL の軸 (-32768..32767) を Input.GamepadAxis と同じ値にする
static float pad_axis(SDL_Gamepad *pad, int axis) {
  int value = SDL_GetGamepadAxis(pad, (SDL_GamepadAxis)axis);
  if (axis == SDL_GAMEPAD_AXIS_LEFT_TRIGGER ||
      axis == SDL_GAMEPAD_AXIS_RIGHT_TRIGGER)
    return value > 0 ? (float)value / 32767.0f : 0.0f;
  return (float)value / (value < 0 ? 32768.0f : 32767.0f);
}

// その frame にゲームから見える入力を控える (overlay が取っている種類は何も押されていない)
static void capture_input(App *app, RewindInput *in) {
  memset(in, 0, sizeof(*in));
  if (!app->overlay_grab_keyboard) {
    int n = 0;
    const bool *keys = SDL_GetKeyboardState(&n);
    if (keys)
      memcpy(in->keys, keys,
             (size_t)(n < SDL_SCANCODE_COUNT ? n : SDL_SCANCODE_COUNT));
    memcpy(in->key_pressed, app->key_pressed, sizeof(in->key_pressed));
    memcpy(in->key_released, app->key_released, sizeof(in->key_released));
  }
  uint32_t buttons = SDL_GetMouseState(&in->mouse_x, &in->mouse_y);
  if (!app->overlay_grab_mouse) {
    in->mouse_buttons = buttons;
    in->mouse_pressed_mask = app->mouse_pressed_mask;
    in->mouse_released_mask = app->mouse_released_mask;
    in->mouse_rel_x = app->mouse_rel_x;
    in->mouse_rel_y = app->mouse_rel_y;
  }
  for (int i = 0; i < LUB_GAMEPAD_SLOTS; ++i) {
    SDL_Gamepad *pad = app->gamepads[i];
    in->pad_pressed[i] = app->gamepad_pressed[i];
    in->pad_released[i] = app->gamepad_released[i];
    if (!pad)
      continue;
    in->pad_connected[i] = true;
    for (int a = 0; a < SDL_GAMEPAD_AXIS_COUNT; ++a)
      in->pad_axes[i][a] = pad_axis(pad, a);
    for (int b = 0; b < SDL_GAMEPAD_BUTTON_COUNT; ++b)
      if (SDL_GetGamepadButton(pad, (SDL_GamepadButton)b))
        in->pad_down[i] |= UINT32_C(1) << b;
  }
}

const RewindInput *rewind_input(const App *app) {
  const RewindState *r = app->rewind;
  if (!r || !r->replaying || app->in_overlay)
    return NULL;
  return &((RewindState *)r)->frames[(r->head + r->paused) % r->capacity].input;
}

// ---------------------------------------------------------------- frame

void rewind_pause(App *app, int back) {
  RewindState *r = state(app);
  lua_State *L = app->lua.L;
  if (!r || !app->in_overlay || !L)
    return;
  if (back >= 0) {
    if (back >= r->count)
      return;
    r->paused = r->count - 1 - back;
    return;
  }
  if (r->paused < 0)
    return;
  // 止めた位置の状態に戻し、それより後の控えを捨てる (その位置の控えも次の frame で取り直す)
  restore_snapshot(L, frame_at(r, r->paused)->snapshot);
  for (int i = r->paused; i < r->count; ++i)
    drop_frame(app, frame_at(r, i));
  r->count = r->paused;
  r->paused = -1;
}

bool rewind_frame_begin(App *app, double *dt) {
  RewindState *r = app->rewind;
  lua_State *L = app->lua.L;
  if (!r || r->capacity == 0 || !L)
    return false;
  if (r->paused >= 0) {
    RewindFrame *f = frame_at(r, r->paused);
    restore_snapshot(L, f->snapshot);
    *dt = f->dt;
    r->replaying = true;
    return true;
  }
  if (r->count == r->capacity) {
    drop_frame(app, frame_at(r, 0));
    r->head = (r->head + 1) % r->capacity;
    r->count--;
  }
  RewindFrame *f = frame_at(r, r->count);
  push_snapshot(app, L);
  f->snapshot = luaL_ref(L, LUA_REGISTRYINDEX);
  f->dt = *dt;
  capture_input(app, &f->input);
  r->count++;
  return false;
}

void rewind_frame_end(App *app) {
  if (app->rewind)
    app->rewind->replaying = false;
}

void rewind_shutdown(App *app) {
  RewindState *r = app->rewind;
  if (!r)
    return;
  drop_all(app, r);
  free(r->frames);
  free(r);
  app->rewind = NULL;
}
