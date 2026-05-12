#pragma once
#include <lua.h>
#include <SDL3/SDL.h>

struct App;  // forward declaration

typedef struct LuaCtx {
    lua_State *L;
    int        module_ref;  // luaL_ref into LUA_REGISTRYINDEX for the entry module table
} LuaCtx;

bool lua_ctx_init(LuaCtx *ctx, const char *entry_module_name, struct App *app);
void lua_ctx_call_init(LuaCtx *ctx);
void lua_ctx_call_event(LuaCtx *ctx, const SDL_Event *e);
void lua_ctx_call_frame(LuaCtx *ctx);
void lua_ctx_call_quit(LuaCtx *ctx);
void lua_ctx_shutdown(LuaCtx *ctx);

void lua_api_register(lua_State *L);
