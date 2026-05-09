#pragma once
#include <lua.h>
#include <SDL3/SDL.h>

typedef struct LuaCtx {
    lua_State *L;
} LuaCtx;

bool lua_ctx_init(LuaCtx *ctx, const char *script_path);
void lua_ctx_call_init(LuaCtx *ctx);
void lua_ctx_call_event(LuaCtx *ctx, const SDL_Event *e);
void lua_ctx_call_frame(LuaCtx *ctx);
void lua_ctx_call_quit(LuaCtx *ctx);
void lua_ctx_shutdown(LuaCtx *ctx);
