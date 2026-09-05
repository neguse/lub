#define SDL_MAIN_USE_CALLBACKS 1
#include "api_internal.h"
#include "app.h"
#include "host_api.h"
#include "lua_api.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#ifndef __EMSCRIPTEN__
#include "path_util.h"
#include "serve.h"
#include "tcs_build.h"
#endif

// player の runtime。frame の骨格は host API (src/host_api.c) が持ち、ここは
// entry の解決と Lua の呼び出しだけ。
static LubContext *g_ctx;
static App *g_app;
#ifndef __EMSCRIPTEN__
static TcsPipeline g_tcs;
#endif

#ifndef __EMSCRIPTEN__
static bool g_serve_mode = false;
static ServeState g_serve;
#endif

static bool has_extension(const char *path, const char *ext) {
  size_t n = SDL_strlen(path), m = SDL_strlen(ext);
  return n >= m && SDL_strcasecmp(path + n - m, ext) == 0;
}

SDL_AppResult SDL_AppInit(void **appstate, int argc, char *argv[]) {
  (void)appstate;

#ifndef __EMSCRIPTEN__
  // Pre-scan for --serve before SDL_Init (serve mode skips video)
  bool want_serve = false;
  for (int i = 1; i < argc; i++) {
    if (strcmp(argv[i], "--serve") == 0) {
      want_serve = true;
      break;
    }
  }

  if (want_serve) {
    // Serve mode: headless, no window/GPU
    if (!SDL_Init(0)) {
      SDL_Log("SDL_Init failed: %s", SDL_GetError());
      return SDL_APP_FAILURE;
    }

    const char *script = NULL;
    const char *wasm_dir = NULL;
    const char *slang_dir = NULL;
    int port = 8080;
    for (int i = 1; i < argc; i++) {
      if (strcmp(argv[i], "--serve") == 0)
        continue;
      if (strcmp(argv[i], "--port") == 0 && i + 1 < argc) {
        port = atoi(argv[++i]);
      } else if (strcmp(argv[i], "--wasm-dir") == 0 && i + 1 < argc) {
        wasm_dir = argv[++i];
      } else if (strcmp(argv[i], "--slang-dir") == 0 && i + 1 < argc) {
        slang_dir = argv[++i];
      } else {
        script = argv[i];
      }
    }
    if (!script || !has_extension(script, ".csproj")) {
      SDL_Log("FATAL: --serve requires a .csproj path");
      return SDL_APP_FAILURE;
    }

    // Auto-detect wasm_dir and slang_dir from executable path if not specified
    const char *base_path = SDL_GetBasePath();
    char auto_wasm[768] = {0};
    char auto_slang[768] = {0};
    if (base_path) {
      // base_path is e.g. "/path/to/lub/build/" — strip trailing slash copy
      char bp_copy[768];
      SDL_strlcpy(bp_copy, base_path, sizeof(bp_copy));
      size_t blen = strlen(bp_copy);
      if (blen > 0 && bp_copy[blen - 1] == '/')
        bp_copy[blen - 1] = '\0';
      char lub_root[768];
      path_dirname(bp_copy, lub_root, sizeof(lub_root));
      if (!wasm_dir) {
        SDL_snprintf(auto_wasm, sizeof(auto_wasm), "%s/build/wasm", lub_root);
        wasm_dir = auto_wasm;
      }
      if (!slang_dir) {
        SDL_snprintf(auto_slang, sizeof(auto_slang), "%s/web/public/slang",
                     lub_root);
        slang_dir = auto_slang;
      }
    }
    if (!wasm_dir) {
      SDL_Log("FATAL: --wasm-dir required (could not auto-detect)");
      return SDL_APP_FAILURE;
    }
    if (!slang_dir) {
      SDL_Log("FATAL: --slang-dir required (could not auto-detect)");
      return SDL_APP_FAILURE;
    }

    if (!serve_start(&g_serve, script, wasm_dir, slang_dir, port)) {
      return SDL_APP_FAILURE;
    }
    g_serve_mode = true;
    return SDL_APP_CONTINUE;
  }
#endif // !__EMSCRIPTEN__

  const char *script = NULL;
  LubHostOpts opts;
  memset(&opts, 0, sizeof(opts));
  opts.capture_frame = 30;
  for (int i = 1; i < argc; ++i) {
    if (strcmp(argv[i], "--capture") == 0 && i + 1 < argc) {
      opts.capture_path = lub_str_c(argv[++i]);
    } else if (strcmp(argv[i], "--capture-frame") == 0 && i + 1 < argc) {
      opts.capture_frame = (int32_t)strtoul(argv[++i], NULL, 10);
    } else if (strcmp(argv[i], "--digest") == 0) {
      opts.digest = true;
    } else if (strcmp(argv[i], "--fixed-dt") == 0) {
      if (i + 1 >= argc) {
        SDL_Log("FATAL: --fixed-dt requires a value in (0, 0.25] seconds");
        return SDL_APP_FAILURE;
      }
      const char *value = argv[++i];
      char *end = NULL;
      double fixed_dt = SDL_strtod(value, &end);
      if (end == value || *end != '\0' || SDL_isnan(fixed_dt) ||
          SDL_isinf(fixed_dt) || fixed_dt <= 0.0 || fixed_dt > 0.25) {
        SDL_Log("FATAL: invalid --fixed-dt '%s' (expected a finite value in "
                "(0, 0.25] seconds)",
                value);
        return SDL_APP_FAILURE;
      }
      opts.fixed_dt = (float)fixed_dt;
    } else {
      script = argv[i];
    }
  }

  // Normal mode: window + GPU. Parse test-clock arguments first so invalid
  // values fail without creating a window or partially initializing App.
  g_ctx = lub_host_create(&opts);
  if (!g_ctx)
    return SDL_APP_FAILURE;
  g_app = lub_api_app(g_ctx);
  const char *entry_path = script ? script : "00_hello";

  char modbuf[256] = {0};

#ifndef __EMSCRIPTEN__
  // .csproj entry: tcs で transpile + watch
  // し、以降は生成 .lua の直パス entry と同じ扱いにする (mtime poll が
  // hotswap を担う)。
  char cs_out[768];
  if (has_extension(entry_path, ".csproj")) {
    if (!tcs_pipeline_start(&g_tcs, entry_path, cs_out, sizeof(cs_out)))
      return SDL_APP_FAILURE;
    entry_path = cs_out;
  }
  if (has_extension(entry_path, ".lua")) {
    // 任意パスの .lua entry。tcs 等の transpiler 出力を staging なしで
    // 直接ロードする。module 名は basename、実パスを mtime poll 対象にする。
    path_basename_noext(entry_path, modbuf, sizeof(modbuf));
    SDL_strlcpy(g_app->entry_module_name, modbuf,
                sizeof(g_app->entry_module_name));
    SDL_snprintf(g_app->entry_path, sizeof(g_app->entry_path), "%s",
                 entry_path);
    g_app->entry_mtime_cache = 0;
    char dir[512];
    path_dirname(entry_path, dir, sizeof(dir));
    lua_ctx_add_package_dir(&g_app->lua, dir);
  } else
#endif
  {
    const char *raw = entry_path;
    const char *base = strrchr(raw, '/');
    base = base ? base + 1 : raw;
    size_t n = strlen(base);
    if (n >= 4 && strcmp(base + n - 4, ".lua") == 0)
      n -= 4;
    if (n >= sizeof(modbuf))
      n = sizeof(modbuf) - 1;
    memcpy(modbuf, base, n);
    modbuf[n] = '\0';

    SDL_strlcpy(g_app->entry_module_name, modbuf,
                sizeof(g_app->entry_module_name));
    char sample_dir[512];
    SDL_snprintf(sample_dir, sizeof(sample_dir), "samples/%s", modbuf);
    SDL_snprintf(g_app->entry_path, sizeof(g_app->entry_path), "%s/.lub/%s.lua",
                 sample_dir, modbuf);
    g_app->entry_mtime_cache = 0;
    lua_ctx_add_package_path(&g_app->lua, sample_dir);
  }

  if (!lua_ctx_load_entry(&g_app->lua, modbuf))
    return SDL_APP_FAILURE;
  lua_ctx_call_init(&g_app->lua);
  if (lub_host_start(g_ctx) != LUB_OK)
    return SDL_APP_FAILURE;
  return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {
  (void)appstate;
#ifndef __EMSCRIPTEN__
  if (g_serve_mode) {
    if (event->type == SDL_EVENT_QUIT)
      return SDL_APP_SUCCESS;
    return SDL_APP_CONTINUE;
  }
#endif
  LubEventData ev;
  if (!lub_host_translate_event(g_app, event, &ev))
    return SDL_APP_CONTINUE;
  if (ev.kind == LUB_EVENT_KIND_QUIT)
    return SDL_APP_SUCCESS;
  lua_ctx_call_event(&g_app->lua, &ev);
  return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppIterate(void *appstate) {
  (void)appstate;
#ifndef __EMSCRIPTEN__
  if (g_serve_mode) {
    if (!serve_tick(&g_serve))
      return SDL_APP_SUCCESS;
    return SDL_APP_CONTINUE;
  }
#endif
  float dt = 0.0f;
  if (!lub_host_frame_begin(g_ctx, &dt)) {
    SDL_Delay(16);
    return SDL_APP_CONTINUE;
  }
  lua_ctx_call_frame(&g_app->lua, g_app->frame_dt);
  lub_host_frame_end(g_ctx);
  if (lub_host_quit_requested(g_ctx))
    return SDL_APP_SUCCESS;
  return SDL_APP_CONTINUE;
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
  (void)appstate;
  (void)result;
#ifndef __EMSCRIPTEN__
  if (g_serve_mode) {
    serve_stop(&g_serve);
    SDL_Quit();
    return;
  }
#endif
  if (!g_ctx) {
    SDL_Quit();
    return;
  }
  lua_ctx_call_quit(&g_app->lua);
#ifndef __EMSCRIPTEN__
  tcs_pipeline_stop(&g_tcs);
#endif
  lub_host_destroy(g_ctx);
  g_ctx = NULL;
  g_app = NULL;
}
