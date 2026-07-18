/* interp 変種: 埋め込み bench.lua を lua32 で実行する。
   pd_api.h とは同居できない (lua_State の型衝突) ため main.c から分離 */

#include <stddef.h>
#include <string.h>

#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>

#include "common.h"
#include "pd_log.h"

#include "bench_lua.h"

static int
l_pd_clock(lua_State *L)
{
    lua_pushnumber(L, (lua_Number)perf_now_seconds());
    return 1;
}

static int
l_pd_exit(lua_State *L)
{
    return luaL_error(L, "os.exit is unavailable on Playdate");
}

static int
l_pd_write(lua_State *L)
{
    int arg_count = lua_gettop(L);
    luaL_Buffer buffer;
    const char *text;
    size_t length;
    char line[256];
    int i;

    luaL_buffinit(L, &buffer);
    for (i = 1; i <= arg_count; ++i) {
        size_t piece_length;
        const char *piece = luaL_checklstring(L, i, &piece_length);

        luaL_addlstring(&buffer, piece, piece_length);
    }
    luaL_pushresult(&buffer);

    text = lua_tolstring(L, -1, &length);
    while (length > 0 && text[length - 1] == '\n') {
        length--;
    }
    if (length >= sizeof line) {
        length = sizeof line - 1;
    }
    memcpy(line, text, length);
    line[length] = '\0';
    perf_pd_log("%s", line);
    return 0;
}

void
perf_interp_run(const char *kernel_name, unsigned count)
{
    lua_State *L = luaL_newstate();

    if (L == NULL) {
        perf_pd_log("%s,interp,%u,error,newstate failed", kernel_name, count);
        return;
    }
    luaL_openlibs(L);

    lua_createtable(L, 2, 0);
    lua_pushstring(L, kernel_name);
    lua_rawseti(L, -2, 1);
    lua_pushinteger(L, (lua_Integer)count);
    lua_rawseti(L, -2, 2);
    lua_setglobal(L, "arg");

    /* os.clock は Playdate タイマー、os.exit は封じる (usage エラー経路のみ) */
    lua_getglobal(L, "os");
    lua_pushcfunction(L, l_pd_clock);
    lua_setfield(L, -2, "clock");
    lua_pushcfunction(L, l_pd_exit);
    lua_setfield(L, -2, "exit");
    lua_pop(L, 1);

    /* 結果 CSV は io.write 経由で出るのでコンソールへ回す */
    lua_getglobal(L, "io");
    lua_pushcfunction(L, l_pd_write);
    lua_setfield(L, -2, "write");
    lua_pop(L, 1);

    if (luaL_loadbuffer(L, bench_lua, sizeof bench_lua, "@bench.lua")
            != LUA_OK
        || lua_pcall(L, 0, 0, 0) != LUA_OK) {
        perf_pd_log("%s,interp,%u,error,%s",
            kernel_name, count, lua_tostring(L, -1));
    }
    lua_close(L);
}
