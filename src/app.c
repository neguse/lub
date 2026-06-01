#include "app.h"
#include "backend.h"
#include "lua_api.h"
#include <SDL3/SDL.h>
#include <stdlib.h>
#include <string.h>

bool app_init(App *app) {
  memset(app, 0, sizeof(*app));
  // Window creation flag set: native asks for a Vulkan-capable surface so
  // SDL_Vulkan_* APIs work; wasm just needs the canvas-backed default.
#ifdef __EMSCRIPTEN__
  app->window = SDL_CreateWindow("lub", 1280, 720, SDL_WINDOW_RESIZABLE);
#else
  app->window = SDL_CreateWindow("lub", 1280, 720,
                                 SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE);
#endif
  if (!app->window) {
    SDL_Log("SDL_CreateWindow failed: %s", SDL_GetError());
    return false;
  }
  pass_state_init(&app->pass);
  pass_state_set_app(&app->pass, app);
  res_table_init(&app->res);
  pipeline_cache_init(&app->pip_cache);
  capture_state_init(&app->capture);
  app->capture_then_exit = false;
  app->last_w = 0;
  app->last_h = 0;
  app->frame_index = 0;
  app->cfg_w = 0;
  app->cfg_h = 0;
  app->quit_requested = false;
  app->phase = APP_PHASE_PRE_BACKEND;
  strcpy(app->backend_name, "sokol");
  return true;
}

bool app_backend_init(App *app) {
#ifndef __EMSCRIPTEN__
  if (strcmp(app->backend_name, "sdlgpu") == 0) {
    g_backend = &g_backend_sdlgpu;
  } else {
    g_backend = &g_backend_sokol;
  }
#else
  // wasm build: only the sokol/WGPU backend is compiled in.
  g_backend = &g_backend_sokol;
#endif
  SDL_Log("backend selected: %s", g_backend->name);
  if (!g_backend->init(app)) {
    SDL_Log("backend init failed");
    return false;
  }
  app->phase = APP_PHASE_POST_BACKEND;
  return true;
}

void app_frame_begin(App *app, int *out_w, int *out_h) {
  int w = 0, h = 0;
  g_backend->begin_frame(app, &w, &h);
  if (out_w)
    *out_w = w;
  if (out_h)
    *out_h = h;
  app->last_w = w;
  app->last_h = h;

  // .hxml entry のときは毎フレーム haxe pipeline を tick して .hx 変更を拾い
  // .lub/<base>.lua へ atomic write する。下の mtime polling が新しい .lua を
  // 検出して lume.hotswap を回す、という二段構え。
#ifndef __EMSCRIPTEN__
  if (app->haxe_enabled) {
    haxe_pipeline_tick(&app->haxe);
  }
#endif

  // Hot-reload entry Lua when its mtime changes. Skip during PRE_BACKEND
  // (callbacks aren't being driven yet) and on the very first poll
  // (cache == 0) — record the baseline instead of triggering an
  // unnecessary swap at boot.
  if (app->phase == APP_PHASE_POST_BACKEND && app->entry_module_name[0]) {
    int64_t now = app_file_mtime_ns(app->entry_path);
    if (now && now != app->entry_mtime_cache) {
      if (app->entry_mtime_cache != 0) {
        SDL_Log("entry mtime changed, hotswapping %s", app->entry_module_name);
        lua_ctx_hotswap(&app->lua, app->entry_module_name);
      }
      app->entry_mtime_cache = now;
    }
  }
}

static void app_on_shader_release(void *ctx, uintptr_t old_shader) {
  App *app = (App *)ctx;
  pipeline_cache_invalidate_shader(&app->pip_cache, old_shader);
}

void app_frame_end(App *app) {
  g_backend->end_frame(app);
  if (capture_state_drain(&app->capture, app)) {
    app->capture_then_exit = true;
  }
  if (app->resource_sweep_after_frames > 0) {
    int64_t cf = (int64_t)app->frame_index;
    int64_t thr = (int64_t)app->resource_sweep_after_frames;
    // Pipelines first: invalidate_shader (called by res sweep) walks the
    // same buckets, so ordering avoids touching freed entries.
    pipeline_cache_sweep(&app->pip_cache, cf, thr);
    res_table_sweep(&app->res, cf, thr, app_on_shader_release, app);
  }
  app->frame_index++;
}

void app_shutdown(App *app) {
  // Pipelines reference shaders, so destroy pipelines before resources.
  pipeline_cache_shutdown(&app->pip_cache);
  res_table_shutdown(&app->res);
  capture_state_shutdown(&app->capture);
  g_backend->shutdown(app);

  if (app->window)
    SDL_DestroyWindow(app->window);

#ifndef __EMSCRIPTEN__
  // .hxml entry を踏んでいたときだけ haxe pipeline (server + watch) を止める。
  // 通常の .lua-only 実行は haxe_enabled = false のままなので no-op。
  if (app->haxe_enabled) {
    haxe_pipeline_stop(&app->haxe);
  }
#endif
}
