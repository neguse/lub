#include "enums.h"
#include "enums_lua.h"
#include <assert.h>
#include <lua.h>
#include <lauxlib.h>

static_assert((int)SGL_BLEND_NONE == (int)SGL_CULL_NONE, "NONE values must match for Lua-side sharing");

static void set_int(lua_State *L, const char *name, int v) {
    lua_pushinteger(L, v);
    lua_setglobal(L, name);
}

void enums_register(lua_State *L) {
    // Buffer types
    set_int(L, "VERTEX", SGL_BUFFER_VERTEX);
    set_int(L, "INDEX", SGL_BUFFER_INDEX);
    set_int(L, "UNIFORM", SGL_BUFFER_UNIFORM);
    set_int(L, "STORAGE", SGL_BUFFER_STORAGE);
    // Pixel formats
    set_int(L, "RGBA8", SGL_PF_RGBA8);
    set_int(L, "R8", SGL_PF_R8);
    set_int(L, "RG8", SGL_PF_RG8);
    set_int(L, "RGBA16F", SGL_PF_RGBA16F);
    set_int(L, "RGBA32F", SGL_PF_RGBA32F);
    set_int(L, "DEPTH16", SGL_PF_DEPTH16);
    set_int(L, "DEPTH24_STENCIL8", SGL_PF_DEPTH24_STENCIL8);
    set_int(L, "DEPTH32F", SGL_PF_DEPTH32F);
    // Load/store actions
    // DONTCARE/STORE/LOAD/CLEAR share Lua names across SglLoadAction and SglStoreAction.
    // Each is registered once with the LOAD-side value. The pipeline builder remaps
    // values to the correct enum (SglStore* vs SglLoad*) based on the field they appear
    // in (load_action vs store_action). This is by design — see tasks.md Task 4 step 2.
    set_int(L, "CLEAR", SGL_LOAD_CLEAR);
    set_int(L, "LOAD", SGL_LOAD_LOAD);
    set_int(L, "DONTCARE", SGL_LOAD_DONTCARE);
    set_int(L, "STORE", SGL_STORE_STORE);

    // Blend / Cull
    // NONE is shared by SglBlend and SglCull. Both happen to be value 1 in their
    // respective enums, so a single Lua "NONE" works for either. The pipeline builder
    // disambiguates by which option key it appears under (blend vs cull).
    set_int(L, "NONE", SGL_BLEND_NONE);
    set_int(L, "ALPHA", SGL_BLEND_ALPHA);
    set_int(L, "ADDITIVE", SGL_BLEND_ADDITIVE);
    set_int(L, "MULTIPLY", SGL_BLEND_MULTIPLY);
    set_int(L, "BACK", SGL_CULL_BACK);
    set_int(L, "FRONT", SGL_CULL_FRONT);
    // Primitive
    set_int(L, "TRIANGLES", SGL_PRIM_TRIANGLES);
    set_int(L, "TRIANGLE_STRIP", SGL_PRIM_TRIANGLE_STRIP);
    set_int(L, "LINES", SGL_PRIM_LINES);
    set_int(L, "LINE_STRIP", SGL_PRIM_LINE_STRIP);
    set_int(L, "POINTS", SGL_PRIM_POINTS);
}
