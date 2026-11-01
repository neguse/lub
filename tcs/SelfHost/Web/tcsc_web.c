/* セルフホストの tcs compiler を WebAssembly (Emscripten) で動かす入口。
   Lua 5.5 (LUA_32BITS) の state を 1 つ持ち、tcs の runtime の C の module (tcs_math / tcs_rt / tcs_utf8) を登録する。
   JS は tcsc_load で glue (web.lua) と compiler (stage2.lua) を読ませ、tcsc_call で glue の関数を呼ぶ。
   文字列は呼び出し側が malloc した UTF-8 で渡し、結果は次の tcsc_call まで有効な UTF-8 で返す。 */
#include <emscripten.h>
#include <stdlib.h>
#include <string.h>

#include "lauxlib.h"
#include "lua.h"
#include "lualib.h"

LUAMOD_API int luaopen_tcs_math(lua_State *L);
LUAMOD_API int luaopen_tcs_rt(lua_State *L);
LUAMOD_API int luaopen_tcs_utf8(lua_State *L);

static lua_State *L;
static char *last;

static const char *keep(const char *s, size_t n) {
  free(last);
  last = malloc(n + 1);
  if (!last) return "";
  memcpy(last, s, n);
  last[n] = 0;
  return last;
}

/* glue と compiler の chunk を読む。失敗なら error の文字列、成功なら "" */
EMSCRIPTEN_KEEPALIVE const char *tcsc_load(const char *glue, const char *compiler) {
  if (L) lua_close(L);
  L = luaL_newstate();
  luaL_openlibs(L);
  luaL_requiref(L, "tcs_math", luaopen_tcs_math, 1);
  lua_pop(L, 1);
  luaL_requiref(L, "tcs_rt", luaopen_tcs_rt, 1);
  lua_pop(L, 1);
  luaL_requiref(L, "tcs_utf8", luaopen_tcs_utf8, 1);
  lua_pop(L, 1);
  if (luaL_loadbuffer(L, compiler, strlen(compiler), "=compiler") != LUA_OK || lua_pcall(L, 0, 0, 0) != LUA_OK) {
    size_t n;
    const char *e = lua_tolstring(L, -1, &n);
    return keep(e ? e : "compiler load failed", e ? n : 20);
  }
  if (luaL_loadbuffer(L, glue, strlen(glue), "=web.lua") != LUA_OK || lua_pcall(L, 0, 0, 0) != LUA_OK) {
    size_t n;
    const char *e = lua_tolstring(L, -1, &n);
    return keep(e ? e : "glue load failed", e ? n : 16);
  }
  return keep("", 0);
}

/* glue の global の関数 fn を、文字列の引数 2 つ (NULL は渡さない) で呼び、文字列の結果を返す */
EMSCRIPTEN_KEEPALIVE const char *tcsc_call(const char *fn, const char *a, const char *b) {
  if (!L) return keep("{\"ok\":false,\"error\":\"not loaded\"}", 33);
  int base = lua_gettop(L);
  lua_getglobal(L, fn);
  int n = 0;
  if (a) { lua_pushstring(L, a); n++; }
  if (b) { lua_pushstring(L, b); n++; }
  const char *out;
  size_t len;
  if (lua_pcall(L, n, 1, 0) != LUA_OK) {
    lua_getglobal(L, "tcsc_error_json");
    lua_insert(L, -2);
    if (lua_pcall(L, 1, 1, 0) != LUA_OK) {
      lua_settop(L, base);
      return keep("{\"ok\":false,\"error\":\"internal error\"}", 37);
    }
  }
  out = lua_tolstring(L, -1, &len);
  const char *r = keep(out ? out : "", out ? len : 0);
  lua_settop(L, base);
  return r;
}
