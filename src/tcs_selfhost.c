// `lub tcsc`: セルフホストの tcs compiler (third_party/tcs/SelfHost) を lub の Lua で動かす。
// dotnet の無い環境で、tcs の CLI の代わりに tcs_build.c が自分を `lub tcsc ...` で起動する。
// compiler は lub と同じ Lua 5.5 (LUA_32BITS) の別の state で動き、窓も GPU も作らない。
// driver (tcsc.lua) が引数を読み、変換と --watch の見張り (restart_required を書く) を受け持つ。
#include "tcs_build.h"
#include <SDL3/SDL.h>
#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>

// compiler も TinyC# の program なので、ゲームの VM と同じ tcs の runtime (lua_api.c と同じ module) を使う
int luaopen_tcs_math(lua_State *L);
int luaopen_tcs_rt(lua_State *L);
int luaopen_tcs_utf8(lua_State *L);

// driver が --watch の間に呼ぶ (ms だけ眠る)
static int l_tcsc_sleep(lua_State *L) {
  lua_Integer ms = luaL_checkinteger(L, 1);
  SDL_Delay(ms > 0 ? (Uint32)ms : 0);
  return 0;
}

int tcs_selfhost_main(int argc, char *argv[]) {
  TcsSelfHost h;
  if (!tcs_resolve_selfhost(&h)) {
    SDL_Log("tcsc: the self-hosted compiler was not found (build it with "
            "third_party/tcs/SelfHost/build.sh, or set LUB_TCS_SELFHOST_DIR)");
    return 2;
  }
  lua_State *L = luaL_newstate();
  if (!L) {
    SDL_Log("tcsc: out of memory");
    return 2;
  }
  luaL_openlibs(L);
  luaL_requiref(L, "tcs_math", luaopen_tcs_math, 1);
  lua_pop(L, 1);
  luaL_requiref(L, "tcs_rt", luaopen_tcs_rt, 1);
  lua_pop(L, 1);
  luaL_requiref(L, "tcs_utf8", luaopen_tcs_utf8, 1);
  lua_pop(L, 1);
  lua_register(L, "tcsc_sleep", l_tcsc_sleep);
  // arg: [0] = driver、[1] = compiler、[2] = prelude、--lib Linq.cs、その後に `lub tcsc` に渡した引数
  lua_createtable(L, argc + 4, 1);
  lua_pushstring(L, h.driver);
  lua_rawseti(L, -2, 0);
  const char *fixed[4] = {h.compiler, h.prelude, "--lib", h.linq};
  int n = 0;
  for (int i = 0; i < 4; i++) {
    lua_pushstring(L, fixed[i]);
    lua_rawseti(L, -2, ++n);
  }
  for (int i = 2; i < argc; i++) {
    lua_pushstring(L, argv[i]);
    lua_rawseti(L, -2, ++n);
  }
  lua_setglobal(L, "arg");
  int code = 0;
  if (luaL_dofile(L, h.driver) != LUA_OK) {
    SDL_Log("tcsc: %s", lua_tostring(L, -1));
    code = 1;
  }
  lua_close(L);
  return code;
}
