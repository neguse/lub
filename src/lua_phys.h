#pragma once
// 物理 (phys2d / phys3d) の Lua 面。C API (lub_phys2d_* / lub_phys3d_*) への
// 詰め替えで、table の解釈と sentinel table の参照だけを持つ。
#include "lub/lub_api.h"
#include <lua.h>

void phys2d_lua_register(lua_State *L, LubContext *ctx);
void phys3d_lua_register(lua_State *L, LubContext *ctx);
