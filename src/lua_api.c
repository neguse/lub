#include "lua_api.h"
#include "api_internal.h"
#include "app.h"
#include "backend.h"
#include "enums.h"
#include "lua_gen_support.h"
#include "pass.h"
#include "physics_box3d.h"
#include "pipeline.h"
#include "resources.h"
#include "shader.h"
#include "ui.h"
#include <SDL3/SDL.h>
#include <ctype.h>
#include <lauxlib.h>
#include <lua.h>
#include <lualib.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

static App *g_app_for_lua = NULL;

// ---------------------------------------------------------------------------
// API 面は src/gen/lua_api_gen.c (cs-lib/lub_stub.cs からの生成物) が lub
// table に登録する。ここに残るのは Lua VM の glue だけ。

int64_t app_file_mtime_ns(const char *path) {
  if (!path)
    return 0;
#ifdef _WIN32
  // MSVC の struct _stat64 の st_mtime は秒精度なので、同じ秒の内の 2 回目の
  // 保存を取りこぼす。SDL は GetFileAttributesExW の ftLastWriteTime
  // (100ns 精度) を ns に直して返す。
  SDL_PathInfo info;
  if (!SDL_GetPathInfo(path, &info))
    return 0;
  return (int64_t)info.modify_time;
#else
  struct stat st;
  if (stat(path, &st) != 0)
    return 0;
#ifdef __APPLE__
  struct timespec mtime = st.st_mtimespec;
#else
  struct timespec mtime = st.st_mtim;
#endif
  return (int64_t)mtime.tv_sec * 1000000000LL + (int64_t)mtime.tv_nsec;
#endif
}

void lua_api_register(lua_State *L) {
  lub_api_gen_register(L);
  lgen_support_register(L, lub_api_ctx(g_app_for_lua));
}

// 失敗した callback の処置を entry の OnError に聞く (無ければ Log)。OnError
// 自身が失敗するか Log 以外を返したら Abort (終了コード 1 で終わる)。
static void call_on_error(LuaCtx *ctx, const char *message) {
  lua_State *L = ctx->L;
  int action = LUB_ERROR_ACTION_LOG;
  lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->module_ref); /* +1: module */
  lua_getfield(L, -1, "OnError");                     /* +1: fn */
  lua_remove(L, -2);
  if (lua_isfunction(L, -1)) {
    lua_pushstring(L, message);
    if (lua_pcall(L, 1, 1, 0) != LUA_OK) {
      SDL_Log("lua error in OnError: %s", lua_tostring(L, -1));
      action = LUB_ERROR_ACTION_ABORT;
    } else {
      int isnum = 0;
      lua_Integer v = lua_tointegerx(L, -1, &isnum);
      if (!isnum || v != LUB_ERROR_ACTION_LOG)
        action = LUB_ERROR_ACTION_ABORT;
    }
  }
  lua_pop(L, 1);
  if (action == LUB_ERROR_ACTION_ABORT) {
    lub_app_quit(lub_api_ctx(g_app_for_lua), 1);
  }
}

// Fetches the entry module from the registry, looks up the named field, and
// pcalls it with the existing top-of-stack args. samples declare callbacks
// without `self`; C-side does not push module table as first arg.
// entry callback は C# の名前 (OnInit 等)。
// false は callback の中の error (callback が無いのは true)。
static bool call_module_field(LuaCtx *ctx, const char *name,
                              const char *legacy_name, int nargs) {
  lua_State *L = ctx->L;
  /* stack on entry: [..., arg1, arg2, ...] (nargs items on top) */
  if (ctx->module_ref == LUA_NOREF) {
    lua_pop(L, nargs);
    return true;
  }
  lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->module_ref); /* +1: module */
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1 + nargs);
    return true;
  }
  lua_getfield(L, -1, name); /* +1: fn */
  if (!lua_isfunction(L, -1) && legacy_name) {
    lua_pop(L, 1);
    lua_getfield(L, -1, legacy_name); /* +1: fn */
  }
  if (!lua_isfunction(L, -1)) {
    lua_pop(L, 2 + nargs);
    return true;
  }
  lua_remove(L, -2); /* drop module: stack [..., args, fn] */
  if (nargs > 0)
    lua_insert(L, -1 - nargs); /* move fn before args */
  if (lua_pcall(L, nargs, 0, 0) != LUA_OK) {
    const char *message = lua_tostring(L, -1);
    if (!message)
      message = "(error object is not a string)";
    SDL_Log("lua error in %s: %s", name, message);
    call_on_error(ctx, message);
    lua_pop(L, 1);
    return false;
  }
  return true;
}

// tcs の Lua 出力は Math の超越関数で global の tcs_math
// (tcs の runtime/lua) を呼ぶ。tcs2c の生成 C と .NET の
// TinySystem.Math と同じ実装。
int luaopen_tcs_math(lua_State *L);
// float の文字列化と string の順序は global の tcs_rt を呼ぶ
// (.NET と tcs2c と同じ文字列と ordinal の順序にするため)。
int luaopen_tcs_rt(lua_State *L);

bool lua_ctx_init(LuaCtx *ctx, App *app) {
  g_app_for_lua = app;
  ctx->L = luaL_newstate();
  if (!ctx->L) {
    SDL_Log("luaL_newstate failed (out of memory)");
    return false;
  }
  ctx->module_ref = LUA_NOREF;
  luaL_openlibs(ctx->L);
  luaL_requiref(ctx->L, "tcs_math", luaopen_tcs_math, 1);
  lua_pop(ctx->L, 1);
  luaL_requiref(ctx->L, "tcs_rt", luaopen_tcs_rt, 1);
  lua_pop(ctx->L, 1);
  lua_api_register(ctx->L);
  return true;
}

// boot.lua を cwd 優先で探し、無ければ実行ファイル位置から lub root
// (<exe_dir>/..) を推定して探す。見つけた root は boot.lua へ渡し、
// lume / samples の package.path を root 基準で組めるようにする
// (cwd が lub root 以外でも .lua 直パス entry を動かすため)。
static bool load_boot_chunk(lua_State *L, char *root, size_t rootsz) {
  root[0] = '\0';
  if (luaL_loadfile(L, "samples/boot.lua") == LUA_OK) {
#ifndef __EMSCRIPTEN__
    export_copy("samples/boot.lua", "samples/boot.lua");
#endif
    return true;
  }
  lua_pop(L, 1);
#ifndef __EMSCRIPTEN__
  const char *base_path = SDL_GetBasePath();
  if (base_path) {
    char dir[768];
    SDL_strlcpy(dir, base_path, sizeof(dir));
    size_t n = SDL_strlen(dir);
    while (n > 0 && (dir[n - 1] == '/' || dir[n - 1] == '\\'))
      dir[--n] = '\0';
    char *cut = SDL_strrchr(dir, '/');
    char *cut_bs = SDL_strrchr(dir, '\\');
    if (cut_bs && (!cut || cut_bs > cut))
      cut = cut_bs;
    if (cut && cut > dir) {
      *cut = '\0';
      char bootpath[900];
      SDL_snprintf(bootpath, sizeof(bootpath), "%s/samples/boot.lua", dir);
      if (luaL_loadfile(L, bootpath) == LUA_OK) {
        SDL_strlcpy(root, dir, rootsz);
        export_copy(bootpath, "samples/boot.lua");
        return true;
      }
      lua_pop(L, 1);
    }
  }
#endif
  SDL_Log("boot.lua not found: tried samples/boot.lua and "
          "<exe>/../samples/boot.lua");
  return false;
}

static void close_state(LuaCtx *ctx) {
  lgen_support_close();
  lua_close(ctx->L);
  ctx->L = NULL;
}

#ifndef __EMSCRIPTEN__
// export: require が読んだ Lua file を snapshot に写す searcher。upvalue は
// 元の file searcher、lub root、entry の module 名、entry のディレクトリ
// (entry を読むまで nil)。entry は snapshot の game.lua になり、その隣の
// module も snapshot の直下に置く (player は game.lua のディレクトリから
// require する)。lub root の下の file (lume、samples/*.lua) は root からの
// 相対 path、それ以外は読んだ path のまま。
static int export_searcher(lua_State *L) {
  const char *name = luaL_checkstring(L, 1);
  lua_pushvalue(L, lua_upvalueindex(1));
  lua_pushvalue(L, 1);
  lua_call(L, 1, 2);
  if (!lua_isfunction(L, -2) || !lua_isstring(L, -1))
    return 2;
  const char *file = lua_tostring(L, -1);
  const char *rel = file;
  if (strncmp(rel, "./", 2) == 0)
    rel += 2;
  if (strcmp(name, lua_tostring(L, lua_upvalueindex(3))) == 0) {
    const char *slash = strrchr(rel, '/');
    lua_pushlstring(L, rel, slash ? (size_t)(slash - rel + 1) : 0);
    lua_replace(L, lua_upvalueindex(4));
    export_copy(file, "game.lua");
    return 2;
  }
  const char *entry_dir = lua_tostring(L, lua_upvalueindex(4));
  size_t root_len = 0;
  const char *root = lua_tolstring(L, lua_upvalueindex(2), &root_len);
  if (entry_dir && *entry_dir &&
      strncmp(rel, entry_dir, strlen(entry_dir)) == 0)
    rel += strlen(entry_dir);
  else if (root_len > 0 && strncmp(rel, root, root_len) == 0 &&
           rel[root_len] == '/')
    rel += root_len + 1;
  export_copy(file, rel);
  return 2;
}

static void install_export_searcher(lua_State *L, const char *root,
                                    const char *entry_module_name) {
  lua_getglobal(L, "package");
  lua_getfield(L, -1, "searchers");
  lua_rawgeti(L, -1, 2);
  lua_pushstring(L, root);
  lua_pushstring(L, entry_module_name);
  lua_pushnil(L);
  lua_pushcclosure(L, export_searcher, 4);
  lua_rawseti(L, -2, 2);
  lua_pop(L, 2);
}
#endif

bool lua_ctx_load_entry(LuaCtx *ctx, const char *entry_module_name) {
  if (!ctx || !ctx->L || !entry_module_name)
    return false;
  char root[768];
  if (!load_boot_chunk(ctx->L, root, sizeof(root))) {
    close_state(ctx);
    return false;
  }
#ifndef __EMSCRIPTEN__
  if (export_active())
    install_export_searcher(ctx->L, root, entry_module_name);
#endif
  lua_pushstring(ctx->L, entry_module_name);
  lua_pushstring(ctx->L, root);
  if (lua_pcall(ctx->L, 2, 1, 0) != LUA_OK) {
    SDL_Log("boot.lua run error: %s", lua_tostring(ctx->L, -1));
    close_state(ctx);
    return false;
  }
  if (!lua_istable(ctx->L, -1)) {
    SDL_Log("boot.lua did not return a module table");
    close_state(ctx);
    return false;
  }
  ctx->module_ref = luaL_ref(ctx->L, LUA_REGISTRYINDEX);
  return true;
}

static void package_path_prepend(LuaCtx *ctx, const char *pattern) {
  lua_State *L = ctx->L;
  lua_getglobal(L, "package"); /* +1 */
  lua_getfield(L, -1, "path"); /* +1 */
  const char *cur = lua_tostring(L, -1);
  char buf[1024];
  SDL_snprintf(buf, sizeof(buf), "%s;%s", pattern, cur ? cur : "");
  lua_pop(L, 1); /* drop old path */
  lua_pushstring(L, buf);
  lua_setfield(L, -2, "path"); /* set package.path */
  lua_pop(L, 1);               /* drop package */
}

void lua_ctx_add_package_path(LuaCtx *ctx, const char *entry_dir) {
  if (!ctx || !ctx->L || !entry_dir)
    return;
  char pat[1000];
  SDL_snprintf(pat, sizeof(pat), "%s/.lub/?.lua", entry_dir);
  package_path_prepend(ctx, pat);
}

void lua_ctx_add_package_dir(LuaCtx *ctx, const char *dir) {
  if (!ctx || !ctx->L || !dir)
    return;
  char pat[1000];
  SDL_snprintf(pat, sizeof(pat), "%s/?.lua", dir);
  package_path_prepend(ctx, pat);
}

bool lua_ctx_call_init(LuaCtx *ctx) {
  if (!ctx->L)
    return false;
  return call_module_field(ctx, "OnInit", NULL, 0);
}
bool lua_ctx_call_frame(LuaCtx *ctx, double dt) {
  if (!ctx->L)
    return false;
  // 生成 binding は入口で lgen_mark、出口で lgen_release するが、引数変換の
  // 途中の luaL_error は release を longjmp で飛び越す。毎 frame 失敗する
  // 呼び出し (編集中によくある) で arena が伸び続けないよう、binding が
  // 動いていないここで巻き戻す。OnEvent の中では戻さない (SDL の呼び出しの
  // 内側から event が届くことがあり、その時は binding が動いている)。
  lgen_release(0);
  // OnFrame(dt): dt は直近フレームの実測秒。引数なしの OnFrame() は
  // Lua が余分な引数を無視するのでそのまま動く。
  lua_pushnumber(ctx->L, dt);
  return call_module_field(ctx, "OnFrame", NULL, 1);
}
bool lua_ctx_call_export(LuaCtx *ctx) {
  if (!ctx->L)
    return false;
  lgen_release(0);
  return call_module_field(ctx, "OnExport", NULL, 0);
}
void lua_ctx_call_quit(LuaCtx *ctx) {
  if (!ctx->L)
    return;
  call_module_field(ctx, "OnQuit", NULL, 0);
}

void lua_ctx_call_event(LuaCtx *ctx, const LubEventData *e) {
  if (!ctx->L)
    return;
  lub_lua_push_event(ctx->L, e);
  call_module_field(ctx, "OnEvent", NULL, 1);
}

// Stack discipline:
//   entry:                       [...]
//   getglobal "lume":            [..., lume]                  pop 1 on bail
//   getfield "hotswap":          [..., lume, fn]              pop 2 on bail
//   push module_name:            [..., lume, fn, name]
//   pcall(1, 2) on error:        [..., lume, err]             pop 2 on bail
//   pcall(1, 2) on success:      [..., lume, ret, err_or_nil]
//     if ret is a table:         re-ref, pop 2 (ret + err), pop 1 (lume)
//     else (nil + err):          log err, pop 2, pop 1 (lume)
bool lua_ctx_hotswap(LuaCtx *ctx, const char *module_name) {
  if (!ctx || !ctx->L || !module_name)
    return false;
  lua_State *L = ctx->L;
  lua_getglobal(L, "lume");
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1);
    return false;
  }
  lua_getfield(L, -1, "hotswap");
  if (!lua_isfunction(L, -1)) {
    lua_pop(L, 2);
    return false;
  }
  lua_pushstring(L, module_name);
  // lume.hotswap returns (oldmod) on success or (nil, err) on failure.
  // Request 2 results so both branches are uniform on the stack.
  if (lua_pcall(L, 1, 2, 0) != LUA_OK) {
    SDL_Log("hotswap pcall failed: %s", lua_tostring(L, -1));
    lua_pop(L, 2); // err + lume
    return false;
  }
  // stack: [..., lume, ret, err_or_nil]
  bool ok = false;
  if (lua_istable(L, -2)) {
    // Success path. lume.hotswap mutates the existing module table in
    // place, so ctx->module_ref already points at the updated table.
    // Re-ref the returned value anyway in case a future lume version
    // returns a freshly-required table instead.
    lua_pop(L, 1); // drop err_or_nil
    luaL_unref(L, LUA_REGISTRYINDEX, ctx->module_ref);
    ctx->module_ref = luaL_ref(L, LUA_REGISTRYINDEX); // pops the table
    ok = true;
  } else {
    // Failure path: lume returned (nil, err). Log and keep the old module.
    const char *err = lua_isstring(L, -1) ? lua_tostring(L, -1) : "<unknown>";
    SDL_Log("hotswap failed for module '%s': %s", module_name, err);
    lua_pop(L, 2); // err + nil
  }
  lua_pop(L, 1); // lume
  return ok;
}

bool lua_ctx_run_reload_chunk(LuaCtx *ctx, const char *lua, size_t len) {
  if (!ctx || !lua)
    return false;
  if (!ctx->L) {
    // entry を読み込めず VM が無い。native の tcs entry は出力が書き直された
    // ときに作り直す (main.c)
    SDL_Log("reload chunk dropped: the Lua VM is not running");
    return false;
  }
  lua_State *L = ctx->L;
  if (luaL_loadbuffer(L, lua, len, "=reload chunk") != LUA_OK ||
      lua_pcall(L, 0, 0, 0) != LUA_OK) {
    SDL_Log("reload chunk failed: %s", lua_tostring(L, -1));
    lua_pop(L, 1);
    return false;
  }
  return true;
}

bool lua_ctx_reload_managed(LuaCtx *ctx) {
  if (!ctx || !ctx->L)
    return false;
  lua_getglobal(ctx->L, "__tcs_build");
  bool managed = !lua_isnil(ctx->L, -1);
  lua_pop(ctx->L, 1);
  return managed;
}

void lua_ctx_shutdown(LuaCtx *ctx) {
  if (ctx->L) {
    if (ctx->module_ref != LUA_NOREF) {
      luaL_unref(ctx->L, LUA_REGISTRYINDEX, ctx->module_ref);
      ctx->module_ref = LUA_NOREF;
    }
    close_state(ctx);
  }
}
