#define SDL_MAIN_USE_CALLBACKS 1
#include "app.h"
#include "capture.h"
#include "lua_api.h"
#include "profile.h"
#include "ui.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#ifndef __EMSCRIPTEN__
#include "haxe_build.h" // path_basename_noext / path_dirname
#include "serve.h"
#endif

static App g_app;

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
    if (!script || !has_extension(script, ".hxml")) {
      SDL_Log("FATAL: --serve requires a .hxml path");
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

  // Normal mode: window + GPU
  if (!SDL_Init(SDL_INIT_VIDEO)) {
    SDL_Log("SDL_Init failed: %s", SDL_GetError());
    return SDL_APP_FAILURE;
  }
  if (!app_init(&g_app))
    return SDL_APP_FAILURE;

  const char *script = NULL;
  const char *capture_path = NULL;
  uint64_t capture_frame = 30;
  for (int i = 1; i < argc; ++i) {
    if (strcmp(argv[i], "--capture") == 0 && i + 1 < argc) {
      capture_path = argv[++i];
    } else if (strcmp(argv[i], "--capture-frame") == 0 && i + 1 < argc) {
      capture_frame = strtoull(argv[++i], NULL, 10);
    } else {
      script = argv[i];
    }
  }
  const char *entry_path = script ? script : "00_hello";

  if (!lua_ctx_init(&g_app.lua, &g_app))
    return SDL_APP_FAILURE;

  char modbuf[256] = {0};

#ifdef __EMSCRIPTEN__
  if (has_extension(entry_path, ".hxml")) {
    SDL_Log("FATAL: .hxml entry not supported on WASM");
    return SDL_APP_FAILURE;
  }
#else
  if (has_extension(entry_path, ".hxml")) {
    if (!haxe_pipeline_start(&g_app.haxe, entry_path)) {
      SDL_Log("FATAL: haxe pipeline start failed");
      return SDL_APP_FAILURE;
    }
    g_app.haxe_enabled = true;
    path_basename_noext(entry_path, modbuf, sizeof(modbuf));
    SDL_snprintf(g_app.entry_module_name, sizeof(g_app.entry_module_name), "%s",
                 modbuf);
    char dir[512];
    path_dirname(entry_path, dir, sizeof(dir));
    char lua_path[768];
    SDL_snprintf(lua_path, sizeof(lua_path), "%s/.lub/%s.lua", dir, modbuf);
    SDL_snprintf(g_app.entry_path, sizeof(g_app.entry_path), "%s", lua_path);
    g_app.entry_mtime_cache = 0;
    lua_ctx_add_package_path(&g_app.lua, dir);
  }
#endif

  if (!has_extension(entry_path, ".hxml")) {
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

    SDL_strlcpy(g_app.entry_module_name, modbuf,
                sizeof(g_app.entry_module_name));
    char sample_dir[512];
    SDL_snprintf(sample_dir, sizeof(sample_dir), "samples/%s", modbuf);
    SDL_snprintf(g_app.entry_path, sizeof(g_app.entry_path), "%s/.lub/%s.lua",
                 sample_dir, modbuf);
    g_app.entry_mtime_cache = 0;
    lua_ctx_add_package_path(&g_app.lua, sample_dir);
  }

  if (!lua_ctx_load_entry(&g_app.lua, modbuf))
    return SDL_APP_FAILURE;
  lua_ctx_call_init(&g_app.lua);
  if (g_app.cfg_w > 0 && g_app.cfg_h > 0) {
    SDL_SetWindowSize(g_app.window, g_app.cfg_w, g_app.cfg_h);
  }
  if (!app_backend_init(&g_app))
    return SDL_APP_FAILURE;

  if (capture_path) {
    capture_schedule(&g_app.capture, capture_path, capture_frame);
    SDL_Log("capture scheduled: path=%s at_frame=%llu", capture_path,
            (unsigned long long)capture_frame);
  }
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
  if (event->type == SDL_EVENT_QUIT)
    return SDL_APP_SUCCESS;
  if (event->type == SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED) {
    g_app.pending_resize = true;
  }
  switch (event->type) {
  case SDL_EVENT_KEY_DOWN:
    if (!event->key.repeat && event->key.scancode < SDL_SCANCODE_COUNT)
      g_app.key_pressed[event->key.scancode] = true;
    break;
  case SDL_EVENT_KEY_UP:
    if (event->key.scancode < SDL_SCANCODE_COUNT)
      g_app.key_released[event->key.scancode] = true;
    break;
  case SDL_EVENT_MOUSE_BUTTON_DOWN:
    g_app.mouse_pressed_mask |= SDL_BUTTON_MASK(event->button.button);
    break;
  case SDL_EVENT_MOUSE_BUTTON_UP:
    g_app.mouse_released_mask |= SDL_BUTTON_MASK(event->button.button);
    break;
  case SDL_EVENT_MOUSE_MOTION:
    g_app.mouse_rel_x += event->motion.xrel;
    g_app.mouse_rel_y += event->motion.yrel;
    break;
  case SDL_EVENT_MOUSE_WHEEL:
    g_app.mouse_wheel_x += event->wheel.x;
    g_app.mouse_wheel_y += event->wheel.y;
    break;
  default:
    break;
  }
  lua_ctx_call_event(&g_app.lua, event);
  return SDL_APP_CONTINUE;
}

static void input_latch_clear(App *app) {
  memset(app->key_pressed, 0, sizeof(app->key_pressed));
  memset(app->key_released, 0, sizeof(app->key_released));
  app->mouse_pressed_mask = 0;
  app->mouse_released_mask = 0;
  app->mouse_rel_x = 0.0f;
  app->mouse_rel_y = 0.0f;
  app->mouse_wheel_x = 0.0f;
  app->mouse_wheel_y = 0.0f;
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
  int w, h;
  SDL_GetWindowSizeInPixels(g_app.window, &w, &h);
  if (w == 0 || h == 0) {
    SDL_Delay(16);
    return SDL_APP_CONTINUE;
  }
  uint64_t now = SDL_GetPerformanceCounter();
  if (g_app.frame_prev_counter != 0) {
    g_app.frame_dt = (double)(now - g_app.frame_prev_counter) /
                     (double)SDL_GetPerformanceFrequency();
    if (g_app.frame_dt > 0.25)
      g_app.frame_dt = 0.25;
  } else {
    g_app.frame_dt = 1.0 / 60.0;
  }
  g_app.frame_prev_counter = now;
  uint64_t profile_frame = g_app.frame_index;
  profile_frame_begin(&g_app.profile, profile_frame);
  profile_begin_scope(&g_app.profile, "runtime.begin_frame");
  app_frame_begin(&g_app, &w, &h);
  profile_end_scope(&g_app.profile, "runtime.begin_frame");
  ui_new_frame(&g_app, (float)g_app.frame_dt, w, h);
  profile_begin_scope(&g_app.profile, "script.onFrame");
  lua_ctx_call_frame(&g_app.lua, g_app.frame_dt);
  profile_end_scope(&g_app.profile, "script.onFrame");
  input_latch_clear(&g_app);
  profile_begin_scope(&g_app.profile, "runtime.pass_guard");
  if (pass_state_in_pass(&g_app.pass))
    pass_state_end(&g_app.pass);
  profile_end_scope(&g_app.profile, "runtime.pass_guard");
  profile_begin_scope(&g_app.profile, "runtime.end_frame");
  app_frame_end(&g_app);
  profile_end_scope(&g_app.profile, "runtime.end_frame");
  profile_frame_end(&g_app.profile, profile_frame);
  if (g_app.quit_requested)
    return SDL_APP_SUCCESS;
  if (g_app.capture_then_exit)
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
  lua_ctx_call_quit(&g_app.lua);
  ui_shutdown();
  app_shutdown(&g_app);
  lua_ctx_shutdown(&g_app.lua);
  SDL_Quit();
}
