#include "lua_api.h"
#include "enums_lua.h"
#include "app.h"
#include "pass.h"
#include <lua.h>
#include <lualib.h>
#include <lauxlib.h>
#include <SDL3/SDL.h>
#include <string.h>

static App *g_app_for_lua = NULL;

static int l_begin_pass(lua_State *L) {
    luaL_checktype(L, 1, LUA_TTABLE);
    lua_getfield(L, 1, "target");
    if (lua_isnil(L, -1)) return luaL_error(L, "begin_pass: target required");
    int is_main = 0;
    if (lua_istable(L, -1)) {
        lua_getfield(L, -1, "__sgl_kind");
        if (lua_isstring(L, -1) && strcmp(lua_tostring(L, -1), "main_tex") == 0) is_main = 1;
        lua_pop(L, 1);
    }
    lua_pop(L, 1);
    if (!is_main) return luaL_error(L, "begin_pass: only main_tex supported in PoC");

    float r = 0, g = 0, b = 0, a = 1;
    lua_getfield(L, 1, "clear_color");
    if (lua_istable(L, -1)) {
        lua_geti(L, -1, 1); r = (float)lua_tonumber(L, -1); lua_pop(L, 1);
        lua_geti(L, -1, 2); g = (float)lua_tonumber(L, -1); lua_pop(L, 1);
        lua_geti(L, -1, 3); b = (float)lua_tonumber(L, -1); lua_pop(L, 1);
        lua_geti(L, -1, 4); a = (float)lua_tonumber(L, -1); lua_pop(L, 1);
    }
    lua_pop(L, 1);

    pass_state_begin_main(&g_app_for_lua->pass, r, g, b, a);
    return 0;
}

static int l_end_pass(lua_State *L) {
    (void)L;
    pass_state_end(&g_app_for_lua->pass);
    return 0;
}

void lua_api_register(lua_State *L) {
    enums_register(L);
    // main_tex は { __sgl_kind = "main_tex" } という sentinel テーブル
    lua_newtable(L);
    lua_pushstring(L, "main_tex");
    lua_setfield(L, -2, "__sgl_kind");
    lua_setglobal(L, "main_tex");

    lua_pushcfunction(L, l_begin_pass);
    lua_setglobal(L, "begin_pass");
    lua_pushcfunction(L, l_end_pass);
    lua_setglobal(L, "end_pass");
}

static void push_event_table(lua_State *L, const SDL_Event *e) {
    lua_newtable(L);
    lua_pushinteger(L, e->type);
    lua_setfield(L, -2, "type");
    // 詳細フィールドは後段で増やす
}

static void call_global_if_present(lua_State *L, const char *name, int nargs) {
    lua_getglobal(L, name);
    if (!lua_isfunction(L, -1)) {
        lua_pop(L, 1 + nargs);
        return;
    }
    if (nargs > 0) {
        lua_insert(L, -1 - nargs);
    }
    if (lua_pcall(L, nargs, 0, 0) != LUA_OK) {
        SDL_Log("lua error in %s: %s", name, lua_tostring(L, -1));
        lua_pop(L, 1);
    }
}

bool lua_ctx_init(LuaCtx *ctx, const char *script_path, App *app) {
    g_app_for_lua = app;
    ctx->L = luaL_newstate();
    if (!ctx->L) {
        SDL_Log("luaL_newstate failed (out of memory)");
        return false;
    }
    luaL_openlibs(ctx->L);
    lua_api_register(ctx->L);
    if (luaL_dofile(ctx->L, script_path) != LUA_OK) {
        SDL_Log("lua load error: %s", lua_tostring(ctx->L, -1));
        lua_close(ctx->L);
        ctx->L = NULL;
        return false;
    }
    return true;
}

void lua_ctx_call_init(LuaCtx *ctx)  { if (!ctx->L) return; call_global_if_present(ctx->L, "on_init", 0); }
void lua_ctx_call_frame(LuaCtx *ctx) { if (!ctx->L) return; call_global_if_present(ctx->L, "on_frame", 0); }
void lua_ctx_call_quit(LuaCtx *ctx)  { if (!ctx->L) return; call_global_if_present(ctx->L, "on_quit", 0); }

void lua_ctx_call_event(LuaCtx *ctx, const SDL_Event *e) {
    if (!ctx->L) return;
    push_event_table(ctx->L, e);
    call_global_if_present(ctx->L, "on_event", 1);
}

void lua_ctx_shutdown(LuaCtx *ctx) {
    if (ctx->L) lua_close(ctx->L);
    ctx->L = NULL;
}
