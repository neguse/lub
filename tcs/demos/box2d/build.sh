#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"

ROOT="../.."
LUA_DIR="$ROOT/deps/lua"
B2D_DIR="$ROOT/deps/box2d"
OUT="./lua_b2"

echo "=== Building box2d ==="
B2D_OBJS=""
for src in "$B2D_DIR"/src/*.c; do
    obj="/tmp/b2d_$(basename "$src" .c).o"
    cc -c -O2 -std=c17 \
        -I"$B2D_DIR/include" -I"$B2D_DIR/src" \
        -DNDEBUG -D_POSIX_C_SOURCE=199309L \
        "$src" -o "$obj"
    B2D_OBJS="$B2D_OBJS $obj"
done

echo "=== Building b2lua binding ==="
cc -c -O2 -std=c17 \
    -I"$LUA_DIR" -I"$B2D_DIR/include" \
    b2lua.c -o /tmp/b2lua.o

echo "=== Building custom Lua with box2d ==="
# Build all Lua source except lua.c (main) first
LUA_OBJS=""
for src in "$LUA_DIR"/*.c; do
    base=$(basename "$src" .c)
    [ "$base" = "lua" ] && continue      # skip main, we'll link it separately
    [ "$base" = "onelua" ] && continue   # skip single-file build
    obj="/tmp/lua_${base}.o"
    cc -c -O2 -std=c99 -DLUA_USE_POSIX -DLUA_USE_DLOPEN \
        -I"$LUA_DIR" \
        "$src" -o "$obj"
    LUA_OBJS="$LUA_OBJS $obj"
done

# Build custom main that preloads b2
cat > /tmp/lua_b2_main.c << 'MAIN_EOF'
#include "lua.h"
#include "lauxlib.h"
#include "lualib.h"

extern int luaopen_b2(lua_State *L);

int main(int argc, char **argv) {
    lua_State *L = luaL_newstate();
    luaL_openlibs(L);

    // Preload b2 module: require("b2") will find it
    luaL_getsubtable(L, LUA_REGISTRYINDEX, LUA_PRELOAD_TABLE);
    lua_pushcfunction(L, luaopen_b2);
    lua_setfield(L, -2, "b2");
    lua_pop(L, 1);

    // Run as standard lua interpreter
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
MAIN_EOF

cc -c -O2 -std=c99 -DLUA_USE_POSIX -DLUA_USE_DLOPEN \
    -I"$LUA_DIR" \
    /tmp/lua_b2_main.c -o /tmp/lua_b2_main.o

echo "=== Linking ==="
cc -o "$OUT" \
    /tmp/lua_b2_main.o /tmp/b2lua.o \
    $LUA_OBJS $B2D_OBJS \
    -lm -ldl

echo "=== Done: $OUT ==="
ls -lh "$OUT"
