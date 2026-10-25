#pragma once
#include "lub/lub_api.h"
#include <SDL3/SDL.h>
#include <lua.h>

struct App; // forward declaration

typedef struct LuaCtx {
  lua_State *L;
  int module_ref; // luaL_ref into LUA_REGISTRYINDEX for the entry module table
  // overlay (csproj の ProjectReference に Lub="Overlay" を付けた参照先の entry class)。
  // overlay_name は VM を作り直しても残り、lua_ctx_load_entry が同名の global を overlay_ref に引く。
  // overlay の callback はゲームと同じ名前で、OnInit と OnFrame はゲームの後、OnEvent と OnQuit はゲームの前に呼ぶ。
  char overlay_name[128];
  int overlay_ref;
} LuaCtx;

// L を作って openlibs + lua_api_register まで実施する。
// boot.lua の読み込みや entry module の require はここでは行わない (entry の種類ごとに package.path を inject できるよう、L 作成と entry resolution を分離)。
bool lua_ctx_init(LuaCtx *ctx, struct App *app);

// boot.lua を読み込み、entry_module_name を require して module table を ref する。
// lua_ctx_init 成功後 + (任意で lua_ctx_add_package_path 呼び出し後) に 1 度だけ呼ぶ。
bool lua_ctx_load_entry(LuaCtx *ctx, const char *entry_module_name);

// entry .csproj 経由の transpile 後、生成 .lua を find できるよう `<dir>/.lub/?.lua` を package.path の先頭に積む。
void lua_ctx_add_package_path(LuaCtx *ctx, const char *entry_dir);

// entry の実ディレクトリ (`<dir>/?.lua`) を package.path の先頭に積む。
// .lua 直パス entry (tcs 等の transpiler 出力) 用。
void lua_ctx_add_package_dir(LuaCtx *ctx, const char *dir);

// OnInit の中の error なら false。
bool lua_ctx_call_init(LuaCtx *ctx);
void lua_ctx_call_event(LuaCtx *ctx, const LubEventData *e);
// OnFrame(dt) を呼ぶ。
// dt は直近フレームの実測秒。
// OnFrame が error なく終わったら true。
// VM が無いときは false。
// overlay には overlay_dt を渡す (Rewind で止めた frame を作り直すときは、ゲームの dt は控えた値)。
bool lua_ctx_call_frame(LuaCtx *ctx, double dt, double overlay_dt);
// export の hook。
// OnExport が無ければ何もせず true、error なら false。
bool lua_ctx_call_export(LuaCtx *ctx);
void lua_ctx_call_quit(LuaCtx *ctx);
// reload chunk を当てた後の OnReload (ゲーム、overlay の順)。
void lua_ctx_call_reload(LuaCtx *ctx);

// 今の Lua の呼び出しの並びを、内側から 1 行に 1 つずつ "file:line" で返す (malloc、呼び出し側が free。VM が無ければ NULL)。
// tcs の --trace が関数ごとに置く local __tcs_at (C# の行) があればそれだけを並べ、1 つも無ければ Lua の source と行を並べる。
char *lua_ctx_call_sites(LuaCtx *ctx);
void lua_ctx_shutdown(LuaCtx *ctx);

// Reload `module_name` via lume.hotswap. lume.hotswap mutates the old module
// table in place (so the existing module_ref still points at live data), but
// we still re-ref the returned table to stay robust against future lume
// implementations. Errors are logged and the function returns false; the
// program is never aborted, so a syntax error in a saved file just means the
// next save retries.
bool lua_ctx_hotswap(LuaCtx *ctx, const char *module_name);

// tcs の reload chunk (`tcs --watch --reload-chunks`) を実行中の VM で実行する。
// chunk が class table を in-place で更新し、instance と static の値を移行するので、module table は差し替えない。
// 失敗は log して false。
bool lua_ctx_run_reload_chunk(LuaCtx *ctx, const char *lua, size_t len);

// entry が tcs の --reload-chunks 出力か (出力が __tcs_build を書く)。
// その entry は reload chunk で更新するので、全体出力を lume.hotswap で読み直さない。
bool lua_ctx_reload_managed(LuaCtx *ctx);

void lua_api_register(lua_State *L);
