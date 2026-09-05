#include "app.h"
#include "api_internal.h"
#include "backend.h"
#include "gpu_stats.h"
#include "host.h"
#include "lua_api.h"
#include "physics_box2d.h"
#include "physics_box3d.h"
#include <SDL3/SDL.h>
#include <stdlib.h>
#include <string.h>

// Selected in app_backend_init; NULL until then. All GPU work goes through
// g_backend->xxx() after that point.
const RenderBackend *g_backend = NULL;

bool app_init(App *app) {
  memset(app, 0, sizeof(*app));

  // backend は config()/Boot.config で最終決定される(window 生成より後)が、
  // config() を呼ばない sample でも harness の backend 選択を尊重できるよう、
  // ここで default を env LUB_BACKEND から決める。config() が明示すれば勝つ。
#ifdef __EMSCRIPTEN__
  strcpy(app->backend_name, "webgpu");
#else
  {
    const char *env_b = getenv("LUB_BACKEND");
    if (env_b && *env_b) {
      strncpy(app->backend_name, env_b, sizeof(app->backend_name) - 1);
      app->backend_name[sizeof(app->backend_name) - 1] = '\0';
    } else {
      strcpy(app->backend_name, "native");
    }
  }
#endif

  // Window creation flag: sdlgpu (Vulkan driver) は Vulkan-capable surface を
  // 要求する。D3D12 直接実装 (Windows の "native") は Vulkan を使わないため
  // flag を外し、Vulkan ICD の無い環境 (GPU 無しの CI 等) でも window を
  // 作れるようにする。wasm は canvas-backed default のみ。
#ifdef __EMSCRIPTEN__
  app->window = SDL_CreateWindow("lub", 1280, 720, SDL_WINDOW_RESIZABLE);
#else
  SDL_WindowFlags win_flags = SDL_WINDOW_RESIZABLE;
#ifdef _WIN32
  if (strcmp(app->backend_name, "native") != 0)
    win_flags |= SDL_WINDOW_VULKAN;
#else
  win_flags |= SDL_WINDOW_VULKAN;
#endif
  app->window = SDL_CreateWindow("lub", 1280, 720, win_flags);
#endif
  if (!app->window) {
    SDL_Log("SDL_CreateWindow failed: %s", SDL_GetError());
    return false;
  }
  pass_state_init(&app->pass);
  pass_state_set_app(&app->pass, app);
  profile_state_init(&app->profile);
  gpu_stats_init_from_env();
  res_table_init(&app->res);
  phys2d_state_init(&app->phys);
  phys3d_state_init(&app->phys3);
  pipeline_cache_init(&app->pip_cache);
  capture_state_init(&app->capture);
  app->capture_then_exit = false;
  app->last_w = 0;
  app->last_h = 0;
  app->frame_index = 0;
  app->cfg_w = 0;
  app->cfg_h = 0;
  app->quit_requested = false;
  app->actual_fps = 0.0;
  app->fps_last_ns = 0;
  app->fps_frame_count = 0;
  app->fixed_frame_dt = 0.0;
  app->phase = APP_PHASE_PRE_BACKEND;
  app->readback_depth = 8;
  return true;
}

bool app_backend_init(App *app) {
#ifndef __EMSCRIPTEN__
  // "native" = このプラットフォームの最短距離実装。Windows は D3D12 直接、
  // Linux は Vulkan 直接 (backend_vk.c)。それ以外は sdlgpu が代行する。
  if (strcmp(app->backend_name, "sdlgpu") == 0) {
    g_backend = &g_backend_sdlgpu;
  } else if (strcmp(app->backend_name, "native") == 0) {
#if defined(_WIN32)
    g_backend = &g_backend_dx12;
#elif defined(__linux__)
    g_backend = &g_backend_vk;
#else
    g_backend = &g_backend_sdlgpu;
#endif
  } else {
    SDL_Log("unknown backend '%s' (expected 'native' or 'sdlgpu')",
            app->backend_name);
    return false;
  }
#else
  // wasm build: webgpu backend only (backend_name is ignored).
  g_backend = &g_backend_webgpu;
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
  bool capture_before_end_frame =
      g_backend && g_backend->capture_before_end_frame;
  if (capture_before_end_frame && capture_state_drain(&app->capture, app)) {
    app->capture_then_exit = true;
  }
  g_backend->end_frame(app);
  uint64_t now_ns = SDL_GetTicksNS();
  if (app->fps_last_ns == 0) {
    app->fps_last_ns = now_ns;
    app->fps_frame_count = 0;
  } else {
    app->fps_frame_count++;
    uint64_t elapsed_ns = now_ns - app->fps_last_ns;
    if (elapsed_ns >= 1000000000ULL) {
      app->actual_fps =
          (double)app->fps_frame_count * 1000000000.0 / (double)elapsed_ns;
      app->fps_last_ns = now_ns;
      app->fps_frame_count = 0;
    }
  }
  if (!capture_before_end_frame && capture_state_drain(&app->capture, app)) {
    app->capture_then_exit = true;
  }
  // key で宣言する snd / readback queue の sweep。退役した snd の PCM 回収は
  // この後の audio_state_frame_end が行う。
  api_audio_frame_end(app);
  api_gfx_frame_end(app);
  if (app->audio) {
    audio_state_frame_end(app->audio);
  }
  if (app->resource_sweep_after_frames > 0) {
    int64_t cf = (int64_t)app->frame_index;
    int64_t thr = (int64_t)app->resource_sweep_after_frames;
    // Pipelines first: invalidate_shader (called by res sweep) walks the
    // same buckets, so ordering avoids touching freed entries.
    pipeline_cache_sweep(&app->pip_cache, cf, thr);
    res_table_sweep(&app->res, cf, thr, app_on_shader_release, app);
  }
  // frame 有効の view の実体を回収する
  for (int i = 0; i < app->frame_garbage_count; ++i)
    free(app->frame_garbage[i]);
  app->frame_garbage_count = 0;
  app->frame_index++;
  gpu_stats_frame(app->frame_index, g_backend ? g_backend->name : NULL);
}

void app_shutdown(App *app) {
  // Pipelines reference shaders, so destroy pipelines before resources.
  pipeline_cache_shutdown(&app->pip_cache);
  api_gfx_shutdown(app); // readback queue (backend の readback request を含む)
  api_audio_shutdown(app);
  for (int i = 0; i < app->frame_garbage_count; ++i)
    free(app->frame_garbage[i]);
  free(app->frame_garbage);
  app->frame_garbage = NULL;
  app->frame_garbage_count = app->frame_garbage_cap = 0;
  api_host_shutdown(app);
  api_io_shutdown(app);
  api_font_shutdown(app);
  api_mesh_shutdown(app);
  audio_state_destroy(app->audio);
  app->audio = NULL;
  phys3d_state_shutdown(&app->phys3);
  phys2d_state_shutdown(&app->phys);
  res_table_shutdown(&app->res);
  capture_state_shutdown(&app->capture);
  if (g_backend)
    g_backend->shutdown(app);
  gpu_stats_shutdown(g_backend ? g_backend->name : NULL);

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
