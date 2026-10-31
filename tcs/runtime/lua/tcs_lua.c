/* tcs のテストと perf が使う lua / lua32 の interpreter。
   標準の lua.c に、global の tcs_math (runtime/lua/tcs_math_lua.c)、tcs_rt (runtime/lua/tcs_rt_lua.c)、tcs_utf8 (runtime/lua/tcs_utf8_lua.c) の登録を足したもの。 */
#include "lauxlib.h"
#include "lua.h"
#include "lualib.h"

LUAMOD_API int luaopen_tcs_math(lua_State *L);
LUAMOD_API int luaopen_tcs_rt(lua_State *L);
LUAMOD_API int luaopen_tcs_utf8(lua_State *L);

static void tcs_openlibs(lua_State *L) {
  luaL_openlibs(L);
  luaL_requiref(L, "tcs_math", luaopen_tcs_math, 1);
  lua_pop(L, 1);
  luaL_requiref(L, "tcs_rt", luaopen_tcs_rt, 1);
  lua_pop(L, 1);
  luaL_requiref(L, "tcs_utf8", luaopen_tcs_utf8, 1);
  lua_pop(L, 1);
}

#define luai_openlibs(L) tcs_openlibs(L)
#include "lua.c"
