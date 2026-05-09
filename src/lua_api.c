#include "lua_api.h"
#include "enums_lua.h"
#include <lua.h>
#include <lualib.h>
#include <lauxlib.h>
#include <SDL3/SDL.h>

void lua_api_register(lua_State *L) {
    enums_register(L);
    // main_tex は { __sgl_kind = "main_tex" } という sentinel テーブル
    lua_newtable(L);
    lua_pushstring(L, "main_tex");
    lua_setfield(L, -2, "__sgl_kind");
    lua_setglobal(L, "main_tex");
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

bool lua_ctx_init(LuaCtx *ctx, const char *script_path) {
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
