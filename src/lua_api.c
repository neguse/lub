#include "lua_api.h"
#include "api_internal.h"
#include "app.h"
#include "backend.h"
#include "enums.h"
#include "lua_gen_support.h"
#include "lub_math.h"
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

// Bytes: runtime が所有する byte 列への view (所有権の規則 2)。返した frame の
// 終わりまで有効で、古い view を渡された API は stale として error にする。
// 跨いで持ちたい内容はゲームが自分の memory に写す。

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

// Fetches the entry module from the registry, looks up the named field, and
// pcalls it with the existing top-of-stack args. samples declare callbacks
// without `self`; C-side does not push module table as first arg.
// entry callback は snake_case (on_init 等)。
static void call_module_field(LuaCtx *ctx, const char *name,
                              const char *legacy_name, int nargs) {
  lua_State *L = ctx->L;
  /* stack on entry: [..., arg1, arg2, ...] (nargs items on top) */
  if (ctx->module_ref == LUA_NOREF) {
    lua_pop(L, nargs);
    return;
  }
  lua_rawgeti(L, LUA_REGISTRYINDEX, ctx->module_ref); /* +1: module */
  if (!lua_istable(L, -1)) {
    lua_pop(L, 1 + nargs);
    return;
  }
  lua_getfield(L, -1, name); /* +1: fn */
  if (!lua_isfunction(L, -1) && legacy_name) {
    lua_pop(L, 1);
    lua_getfield(L, -1, legacy_name); /* +1: fn */
  }
  if (!lua_isfunction(L, -1)) {
    lua_pop(L, 2 + nargs);
    return;
  }
  lua_remove(L, -2); /* drop module: stack [..., args, fn] */
  if (nargs > 0)
    lua_insert(L, -1 - nargs); /* move fn before args */
  if (lua_pcall(L, nargs, 0, 0) != LUA_OK) {
    SDL_Log("lua error in %s: %s", name, lua_tostring(L, -1));
    lua_pop(L, 1);
  }
}

// math.* の超越関数を、どの OS でも同じ結果を返す lub_math にする。
// 引数の扱いは Lua の lmathlib と同じ。^ は src/lua_pow.h が lub_powf にする。
static int math_sin(lua_State *L) {
  lua_pushnumber(L, lub_sinf(luaL_checknumber(L, 1)));
  return 1;
}

static int math_cos(lua_State *L) {
  lua_pushnumber(L, lub_cosf(luaL_checknumber(L, 1)));
  return 1;
}

static int math_tan(lua_State *L) {
  lua_pushnumber(L, lub_tanf(luaL_checknumber(L, 1)));
  return 1;
}

static int math_asin(lua_State *L) {
  lua_pushnumber(L, lub_asinf(luaL_checknumber(L, 1)));
  return 1;
}

static int math_acos(lua_State *L) {
  lua_pushnumber(L, lub_acosf(luaL_checknumber(L, 1)));
  return 1;
}

static int math_atan(lua_State *L) {
  lua_Number y = luaL_checknumber(L, 1);
  lua_Number x = luaL_optnumber(L, 2, 1);
  lua_pushnumber(L, lub_atan2f(y, x));
  return 1;
}

static int math_exp(lua_State *L) {
  lua_pushnumber(L, lub_expf(luaL_checknumber(L, 1)));
  return 1;
}

static int math_log(lua_State *L) {
  lua_Number x = luaL_checknumber(L, 1);
  lua_Number res;
  if (lua_isnoneornil(L, 2)) {
    res = lub_logf(x);
  } else {
    lua_Number base = luaL_checknumber(L, 2);
    if (base == 2.0f)
      res = lub_log2f(x);
    else if (base == 10.0f)
      res = lub_log10f(x);
    else
      res = lub_logf(x) / lub_logf(base);
  }
  lua_pushnumber(L, res);
  return 1;
}

static void open_lub_math(lua_State *L) {
  static const luaL_Reg funcs[] = {
      {"sin", math_sin},   {"cos", math_cos},   {"tan", math_tan},
      {"asin", math_asin}, {"acos", math_acos}, {"atan", math_atan},
      {"exp", math_exp},   {"log", math_log},   {NULL, NULL},
  };
  lua_getglobal(L, "math");
  luaL_setfuncs(L, funcs, 0);
  lua_pop(L, 1);
}

bool lua_ctx_init(LuaCtx *ctx, App *app) {
  g_app_for_lua = app;
  ctx->L = luaL_newstate();
  if (!ctx->L) {
    SDL_Log("luaL_newstate failed (out of memory)");
    return false;
  }
  ctx->module_ref = LUA_NOREF;
  luaL_openlibs(ctx->L);
  open_lub_math(ctx->L);
  lua_api_register(ctx->L);
  return true;
}

// boot.lua を cwd 優先で探し、無ければ実行ファイル位置から lub root
// (<exe_dir>/..) を推定して探す。見つけた root は boot.lua へ渡し、
// lume / samples の package.path を root 基準で組めるようにする
// (cwd が lub root 以外でも .lua 直パス entry を動かすため)。
static bool load_boot_chunk(lua_State *L, char *root, size_t rootsz) {
  root[0] = '\0';
  if (luaL_loadfile(L, "samples/boot.lua") == LUA_OK)
    return true;
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

bool lua_ctx_load_entry(LuaCtx *ctx, const char *entry_module_name) {
  if (!ctx || !ctx->L || !entry_module_name)
    return false;
  char root[768];
  if (!load_boot_chunk(ctx->L, root, sizeof(root))) {
    lua_close(ctx->L);
    ctx->L = NULL;
    return false;
  }
  lua_pushstring(ctx->L, entry_module_name);
  lua_pushstring(ctx->L, root);
  if (lua_pcall(ctx->L, 2, 1, 0) != LUA_OK) {
    SDL_Log("boot.lua run error: %s", lua_tostring(ctx->L, -1));
    lua_close(ctx->L);
    ctx->L = NULL;
    return false;
  }
  if (!lua_istable(ctx->L, -1)) {
    SDL_Log("boot.lua did not return a module table");
    lua_close(ctx->L);
    ctx->L = NULL;
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

void lua_ctx_call_init(LuaCtx *ctx) {
  if (!ctx->L)
    return;
  call_module_field(ctx, "on_init", NULL, 0);
}
void lua_ctx_call_frame(LuaCtx *ctx, double dt) {
  if (!ctx->L)
    return;
  // 生成 binding は入口で lgen_mark、出口で lgen_release するが、引数変換の
  // 途中の luaL_error は release を longjmp で飛び越す。毎 frame 失敗する
  // 呼び出し (編集中によくある) で arena が伸び続けないよう、binding が
  // 動いていないここで巻き戻す。on_event の中では戻さない (SDL の呼び出しの
  // 内側から event が届くことがあり、その時は binding が動いている)。
  lgen_release(0);
  // onFrame(dt): dt は直近フレームの実測秒。引数なしの既存 onFrame() は
  // Lua が余分な引数を無視するのでそのまま動く。
  lua_pushnumber(ctx->L, dt);
  call_module_field(ctx, "on_frame", NULL, 1);
}
void lua_ctx_call_quit(LuaCtx *ctx) {
  if (!ctx->L)
    return;
  call_module_field(ctx, "on_quit", NULL, 0);
}

void lua_ctx_call_event(LuaCtx *ctx, const LubEventData *e) {
  if (!ctx->L)
    return;
  lub_lua_push_event(ctx->L, e);
  call_module_field(ctx, "on_event", NULL, 1);
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
  if (!ctx || !ctx->L || !lua)
    return false;
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
    lua_close(ctx->L);
  }
  ctx->L = NULL;
}
