#include "lua.h"
#include "lauxlib.h"
#include "lualib.h"
#include <stdio.h>

extern int luaopen_b2(lua_State *L);

int main(int argc, char **argv) {
    lua_State *L = luaL_newstate();
    luaL_openlibs(L);

    /* Preload b2 module: require("b2") will find it */
    luaL_getsubtable(L, LUA_REGISTRYINDEX, LUA_PRELOAD_TABLE);
    lua_pushcfunction(L, luaopen_b2);
    lua_setfield(L, -2, "b2");
    lua_pop(L, 1);

    /* Run as standard lua interpreter */
    if (argc > 1) {
        if (luaL_dofile(L, argv[1]) != LUA_OK) {
            fprintf(stderr, "%s\n", lua_tostring(L, -1));
            lua_close(L);
            return 1;
        }
    }
    lua_close(L);
    return 0;
}
