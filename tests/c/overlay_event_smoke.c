// overlay の OnEvent と OnQuit の呼び順と、Input.Grab の効き方を、Lua で書いたゲームと overlay の table で確かめる。
// overlay はゲームより先に event を受け取り、overlay がその場で Grab した種類の event はゲームに渡らない。
// ゲームが呼んだ Input.Grab は効かない。
#include "api_internal.h"
#include "app.h"
#include "lua_api.h"
#include <lauxlib.h>
#include <lua.h>
#include <lub/lub_host.h>
#include <stdio.h>
#include <string.h>

static int failures;

static void check(bool ok, const char *message) {
  if (!ok) {
    fprintf(stderr, "FAIL: %s\n", message);
    ++failures;
  }
}

static const char SCRIPT[] =
    "order = ''\n"
    "grab_keyboard = false\n"
    "Tool = {\n"
    "  OnEvent = function(e)\n"
    "    order = order .. 'tool '\n"
    "    if grab_keyboard then lub.input.grab(true, false) end\n"
    "  end,\n"
    "  OnQuit = function() order = order .. 'tool-quit ' end,\n"
    "}\n"
    "Game = {\n"
    "  OnEvent = function(e)\n"
    "    order = order .. 'game '\n"
    "    lub.input.grab(true, true)\n"
    "  end,\n"
    "  OnQuit = function() order = order .. 'game-quit ' end,\n"
    "}\n";

// order を読んで空にする
static void take_order(lua_State *L, char *out, size_t cap) {
  lua_getglobal(L, "order");
  SDL_strlcpy(out, lua_tostring(L, -1) ? lua_tostring(L, -1) : "", cap);
  lua_pop(L, 1);
  lua_pushstring(L, "");
  lua_setglobal(L, "order");
}

static void send(LuaCtx *lc, LubEventKind kind) {
  LubEventData e;
  memset(&e, 0, sizeof(e));
  e.kind = kind;
  lua_ctx_call_event(lc, &e);
}

int main(void) {
  SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "offscreen");
  LubContext *ctx = lub_host_create(NULL);
  check(ctx != NULL, "lub_host_create");
  if (!ctx)
    return 1;
  App *app = lub_api_app(ctx);
  LuaCtx *lc = &app->lua;
  check(lua_ctx_init(lc, app), "lua_ctx_init");
  lua_State *L = lc->L;
  check(luaL_dostring(L, SCRIPT) == LUA_OK, "the script runs");
  lua_getglobal(L, "Game");
  lc->module_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  lua_getglobal(L, "Tool");
  lc->overlay_ref = luaL_ref(L, LUA_REGISTRYINDEX);
  char order[256];

  send(lc, LUB_EVENT_KIND_KEY_DOWN);
  take_order(L, order, sizeof(order));
  check(strcmp(order, "tool game ") == 0, "the overlay gets the event first");
  check(!app->overlay_grab_keyboard && !app->overlay_grab_mouse,
        "Input.Grab from the game is ignored");

  lua_pushboolean(L, 1);
  lua_setglobal(L, "grab_keyboard");
  send(lc, LUB_EVENT_KIND_KEY_DOWN);
  take_order(L, order, sizeof(order));
  check(strcmp(order, "tool ") == 0,
        "a key event grabbed by the overlay in its OnEvent reaches the game");
  check(app->overlay_grab_keyboard && !app->overlay_grab_mouse,
        "Input.Grab from the overlay takes effect");
  send(lc, LUB_EVENT_KIND_MOUSE_BUTTON_DOWN);
  take_order(L, order, sizeof(order));
  check(strcmp(order, "tool game ") == 0,
        "a mouse event does not reach the game while only the keyboard is "
        "grabbed");

  lua_ctx_call_quit(lc);
  take_order(L, order, sizeof(order));
  check(strcmp(order, "tool-quit game-quit ") == 0,
        "the overlay gets OnQuit first");

  lua_ctx_shutdown(lc);
  lub_host_destroy(ctx);
  if (!failures)
    puts("overlay event smoke: PASS");
  return failures ? 1 : 0;
}
