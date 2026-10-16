/* tcs の Lua 出力が呼ぶ runtime の C 関数を Lua の table として出す module。
   tcs の Lua 出力と tinysystem.lua は global の tcs_rt を呼ぶので、Lua を動かす host は VM を作るときに登録する:
     luaL_requiref(L, "tcs_rt", luaopen_tcs_rt, 1); lua_pop(L, 1);
   runtime/fmt/f2s.c と一緒に build する。 */
#include "lauxlib.h"
#include "lua.h"

#include "../fmt/tcs_fmt.h"

/* float の既定の文字列化 (tcs2c と同じ tcs_format_f32)。
   integer の値も C# の float への変換と同じく float に丸めてから書く */
static int tcs_rt_l_fstr(lua_State *L) {
  char buffer[TCS_FORMAT_F32_MAX];
  float value = (float)luaL_checknumber(L, 1);
  lua_pushlstring(L, buffer, tcs_format_f32(value, buffer));
  return 1;
}

static const luaL_Reg tcs_rt_funcs[] = {{"fstr", tcs_rt_l_fstr},
                                        {NULL, NULL}};

LUAMOD_API int luaopen_tcs_rt(lua_State *L) {
  luaL_newlib(L, tcs_rt_funcs);
  return 1;
}
