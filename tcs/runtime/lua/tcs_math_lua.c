/* TinyC# の Math の超越関数 (runtime/math) を Lua の table として出す module。
   tcs の Lua 出力は math.sin などの代わりに global の tcs_math を呼ぶので、
   Lua を動かす host は VM を作るときに登録する:
     luaL_requiref(L, "tcs_math", luaopen_tcs_math, 1); lua_pop(L, 1);
   runtime/math の C と一緒に build する。 */
#include "lauxlib.h"
#include "lua.h"

#include "../math/tcs_math.h"

#define TCS_MATH_UNARY(name, fn)                                               \
  static int name(lua_State *L) {                                              \
    lua_pushnumber(L, (lua_Number)fn((float)luaL_checknumber(L, 1)));          \
    return 1;                                                                  \
  }
#define TCS_MATH_BINARY(name, fn)                                              \
  static int name(lua_State *L) {                                              \
    lua_pushnumber(L, (lua_Number)fn((float)luaL_checknumber(L, 1),            \
                                     (float)luaL_checknumber(L, 2)));          \
    return 1;                                                                  \
  }

TCS_MATH_UNARY(tcs_math_l_sin, tcs_sinf)
TCS_MATH_UNARY(tcs_math_l_cos, tcs_cosf)
TCS_MATH_UNARY(tcs_math_l_tan, tcs_tanf)
TCS_MATH_UNARY(tcs_math_l_exp, tcs_expf)
TCS_MATH_UNARY(tcs_math_l_log, tcs_logf)
TCS_MATH_UNARY(tcs_math_l_log2, tcs_log2f)
TCS_MATH_UNARY(tcs_math_l_log10, tcs_log10f)
TCS_MATH_BINARY(tcs_math_l_atan2, tcs_atan2f)
TCS_MATH_BINARY(tcs_math_l_pow, tcs_powf)

static const luaL_Reg tcs_math_funcs[] = {
    {"sin", tcs_math_l_sin},     {"cos", tcs_math_l_cos},
    {"tan", tcs_math_l_tan},     {"exp", tcs_math_l_exp},
    {"log", tcs_math_l_log},     {"log2", tcs_math_l_log2},
    {"log10", tcs_math_l_log10}, {"atan2", tcs_math_l_atan2},
    {"pow", tcs_math_l_pow},     {NULL, NULL}};

LUAMOD_API int luaopen_tcs_math(lua_State *L) {
  luaL_newlib(L, tcs_math_funcs);
  return 1;
}
