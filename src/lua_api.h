#pragma once
#include <lua.h>
#include <SDL3/SDL.h>

struct App;  // forward declaration

typedef struct LuaCtx {
    lua_State *L;
    int        module_ref;  // luaL_ref into LUA_REGISTRYINDEX for the entry module table
} LuaCtx;

// L を作って openlibs + lua_api_register まで実施する。boot.lua の読み込みや
// entry module の require はここでは行わない (Task 23: .hxml dispatch から
// package.path を inject する余地を作るため、L 作成と entry resolution を分離)。
bool lua_ctx_init(LuaCtx *ctx, struct App *app);

// boot.lua を読み込み、entry_module_name を require して module table を ref
// する。lua_ctx_init 成功後 + (任意で lua_ctx_add_package_path 呼び出し後) に
// 1 度だけ呼ぶ。
bool lua_ctx_load_entry(LuaCtx *ctx, const char *entry_module_name);

// entry .hxml 経由の build 後、生成 .lua を find できるよう
// `<dir>/.lub/?.lua` を package.path の先頭に積む。
void lua_ctx_add_package_path(LuaCtx *ctx, const char *entry_dir);

void lua_ctx_call_init(LuaCtx *ctx);
void lua_ctx_call_event(LuaCtx *ctx, const SDL_Event *e);
void lua_ctx_call_frame(LuaCtx *ctx);
void lua_ctx_call_quit(LuaCtx *ctx);
void lua_ctx_shutdown(LuaCtx *ctx);

// Reload `module_name` via lume.hotswap. lume.hotswap mutates the old module
// table in place (so the existing module_ref still points at live data), but
// we still re-ref the returned table to stay robust against future lume
// implementations. Errors are logged and the function returns false; the
// program is never aborted, so a syntax error in a saved file just means the
// next save retries.
bool lua_ctx_hotswap(LuaCtx *ctx, const char *module_name);

void lua_api_register(lua_State *L);
