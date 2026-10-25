// lub が require で読む native の Lua module (lubnative.so)。
// tests/cs/require の LubNative.ref.cs の型 (lubnative.api) に合わせて global を置く。
// lub の実行ファイルが出す Lua の C API を使う (Lua は lub の中の 1 つだけ)。
#include <lauxlib.h>
#include <lua.h>

static int add(lua_State *L) {
  lua_pushinteger(L, luaL_checkinteger(L, 1) + luaL_checkinteger(L, 2));
  return 1;
}

int luaopen_lubnative(lua_State *L) {
  lua_newtable(L);
  lua_newtable(L);
  lua_pushcfunction(L, add);
  lua_setfield(L, -2, "add");
  lua_setfield(L, -2, "api");
  lua_pushvalue(L, -1);
  lua_setglobal(L, "lubnative");
  return 1;
}
