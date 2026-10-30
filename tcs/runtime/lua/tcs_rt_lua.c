/* tcs の Lua 出力が呼ぶ runtime の C 関数を Lua の table として出す module。
   tcs の Lua 出力と tinysystem.lua は global の tcs_rt を呼ぶので、Lua を動かす host は VM を作るときに登録する:
     luaL_requiref(L, "tcs_rt", luaopen_tcs_rt, 1); lua_pop(L, 1);
   runtime/fmt/f2s.c と一緒に build する。 */
#include <limits.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

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

/* ffmt(v, spec): float の書式 G / E の文字列化 (tcs2c と同じ tcs_format_f32_spec)。
   spec は書式の文字と省略できる精度 ("G"、"E2" など) */
static int tcs_rt_l_ffmt(lua_State *L) {
  size_t length, i;
  const char *spec = luaL_checklstring(L, 2, &length);
  int32_t precision = -1;
  float value = (float)luaL_checknumber(L, 1);
  luaL_Buffer buffer;
  char *out;
  luaL_argcheck(L, length >= 1 && length <= 10 && strchr("GgEe", spec[0]) != NULL,
                2, "unsupported float format");
  for (i = 1; i < length; i++) {
    luaL_argcheck(L, spec[i] >= '0' && spec[i] <= '9', 2,
                  "unsupported float format");
    precision = (precision < 0 ? 0 : precision * 10) + (spec[i] - '0');
  }
  out = luaL_buffinitsize(L, &buffer,
                          TCS_FORMAT_F32_SPEC_MAX(spec[0], precision));
  luaL_pushresultsize(&buffer,
                      tcs_format_f32_spec(value, spec[0], precision, out));
  return 1;
}

/* .NET の ordinal (UTF-16 の code unit の順) の 3 方向比較。
   UTF-8 の byte の順は code point の順で、UTF-16 の順と違うのは U+10000 以上 (lead byte F0-F4、UTF-16 では surrogate D800-DBFF) と U+E000-U+FFFF (lead byte EE-EF) の間だけ。
   最初に異なる byte がその 2 つの lead byte のときだけ逆にする */
static int tcs_rt_compare_utf16(const unsigned char *a, size_t la,
                                const unsigned char *b, size_t lb) {
  size_t n = la < lb ? la : lb;
  size_t i;
  for (i = 0; i < n; i++) {
    if (a[i] == b[i])
      continue;
    if (a[i] >= 0xF0 && a[i] <= 0xF4 && b[i] >= 0xEE && b[i] <= 0xEF)
      return -1;
    if (b[i] >= 0xF0 && b[i] <= 0xF4 && a[i] >= 0xEE && a[i] <= 0xEF)
      return 1;
    return a[i] < b[i] ? -1 : 1;
  }
  return la < lb ? -1 : la > lb ? 1 : 0;
}

/* string_compare(a, b): -1 / 0 / 1。
   Lua の `<` (strcoll) は collate locale で変わるので使わない */
static int tcs_rt_l_string_compare(lua_State *L) {
  size_t la, lb;
  const char *a = luaL_checklstring(L, 1, &la);
  const char *b = luaL_checklstring(L, 2, &lb);
  lua_pushinteger(L, tcs_rt_compare_utf16((const unsigned char *)a, la,
                                          (const unsigned char *)b, lb));
  return 1;
}

typedef struct TcsRtString {
  const unsigned char *data;
  size_t length;
  lua_Integer index;
} TcsRtString;

static int tcs_rt_string_order(const void *left, const void *right) {
  const TcsRtString *a = left;
  const TcsRtString *b = right;
  return tcs_rt_compare_utf16(a->data, a->length, b->data, b->length);
}

/* sort_strings(list): string の列 list[1..#list] を string_compare の順に並べる。
   等しい string は区別できないので、並べ方によらず結果は同じ。
   元の列を写した table が並べ替えの間 string を生かしておく */
static int tcs_rt_l_sort_strings(lua_State *L) {
  lua_Integer n, i;
  TcsRtString *items;
  luaL_checktype(L, 1, LUA_TTABLE);
  n = luaL_len(L, 1);
  if (n < 2)
    return 0;
  /* __len は任意の整数を返せる。確保の大きさが wrap しないよう table.sort と同じく止める */
  if ((lua_Unsigned)n >= INT_MAX || (size_t)n > SIZE_MAX / sizeof(TcsRtString))
    return luaL_error(L, "sort_strings: array too big");
  items =
      (TcsRtString *)lua_newuserdatauv(L, (size_t)n * sizeof(TcsRtString), 0);
  lua_createtable(L, (int)n, 0);
  for (i = 1; i <= n; i++) {
    size_t length;
    const char *data;
    if (lua_geti(L, 1, i) != LUA_TSTRING)
      return luaL_error(L, "sort_strings: element %d is not a string", (int)i);
    data = lua_tolstring(L, -1, &length);
    items[i - 1].data = (const unsigned char *)data;
    items[i - 1].length = length;
    items[i - 1].index = i;
    lua_seti(L, -2, i);
  }
  qsort(items, (size_t)n, sizeof(TcsRtString), tcs_rt_string_order);
  for (i = 1; i <= n; i++) {
    lua_geti(L, -1, items[i - 1].index);
    lua_seti(L, 1, i);
  }
  return 0;
}

static const luaL_Reg tcs_rt_funcs[] = {
    {"fstr", tcs_rt_l_fstr},
    {"ffmt", tcs_rt_l_ffmt},
    {"string_compare", tcs_rt_l_string_compare},
    {"sort_strings", tcs_rt_l_sort_strings},
    {NULL, NULL}};

LUAMOD_API int luaopen_tcs_rt(lua_State *L) {
  luaL_newlib(L, tcs_rt_funcs);
  return 1;
}
