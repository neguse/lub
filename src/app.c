#include "app.h"
#include "api_internal.h"
#include "backend.h"
#include "gpu_stats.h"
#include "gpu_time.h"
#include "physics_box2d.h"
#include "physics_box3d.h"
#include "pick.h"
#include <SDL3/SDL.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

// Selected in app_backend_init; NULL until then. All GPU work goes through
// g_backend->xxx() after that point.
const RenderBackend *g_backend = NULL;

// Default backend when unspecified = the most direct implementation for that
// platform.
#if defined(_WIN32)
#define LUB_DEFAULT_BACKEND "d3d12"
#elif defined(__ANDROID__) && defined(LUB_HAS_OPENXR)
// The Android player is for VR headsets (Meta Quest)
#define LUB_DEFAULT_BACKEND "openxr"
#elif defined(__linux__)
#define LUB_DEFAULT_BACKEND "vulkan"
#elif defined(__APPLE__)
#define LUB_DEFAULT_BACKEND "metal"
#else
#define LUB_DEFAULT_BACKEND "sdlgpu"
#endif

#ifndef __EMSCRIPTEN__
// vulkan / sdlgpu (Vulkan driver) require a Vulkan-capable surface.
// d3d12 does not use Vulkan, so drop the flag so that a window can be created
// even in environments without a Vulkan ICD (CI without a GPU, etc.). metal
// uses a view with a CAMetalLayer and draws Retina / iPhone screens at physical
// pixels.
static SDL_WindowFlags window_flags_for_backend(const char *backend_name) {
  if (strcmp(backend_name, "openxr") == 0)
    return SDL_WINDOW_HIDDEN;
  if (export_active())
    return SDL_WINDOW_RESIZABLE;
  SDL_WindowFlags flags = SDL_WINDOW_RESIZABLE;
  if (strcmp(backend_name, "metal") == 0)
    flags |= SDL_WINDOW_METAL | SDL_WINDOW_HIGH_PIXEL_DENSITY;
  else if (strcmp(backend_name, "d3d12") != 0)
    flags |= SDL_WINDOW_VULKAN;
#ifdef SDL_PLATFORM_IOS
  // On iPhone, the window always covers the whole screen.
  // Making it fullscreen hides the status bar.
  flags |= SDL_WINDOW_FULLSCREEN;
#endif
  return flags;
}
#endif

bool process_init(Process *proc) {
  memset(proc, 0, sizeof(*proc));
  gpu_stats_init_from_env();
  // The backend is finalized by App.Config (after the window is created), but
  // the default is decided here from env LUB_BACKEND so that the harness's
  // backend choice is respected even for samples that do not specify it in
  // App.Config. An explicit setting in App.Config wins.
#ifdef __EMSCRIPTEN__
  strcpy(proc->backend_name, "webgpu");
#else
  {
    const char *env_b = getenv("LUB_BACKEND");
    if (env_b && *env_b) {
      strncpy(proc->backend_name, env_b, sizeof(proc->backend_name) - 1);
      proc->backend_name[sizeof(proc->backend_name) - 1] = '\0';
    } else {
      strcpy(proc->backend_name, LUB_DEFAULT_BACKEND);
    }
  }
#endif

  // wasm has only the canvas-backed default.
#ifdef __EMSCRIPTEN__
  proc->window = SDL_CreateWindow("lub", LUB_DEFAULT_WINDOW_W,
                                  LUB_DEFAULT_WINDOW_H, SDL_WINDOW_RESIZABLE);
#else
  if (export_active()) {
    // The window for export only has a size.
    // SDL on macOS adds OpenGL to a window without a graphics flag, and the
    // offscreen video driver fails because it cannot load it, so create it as a
    // window that uses an external context.
    SDL_PropertiesID props = SDL_CreateProperties();
    SDL_SetStringProperty(props, SDL_PROP_WINDOW_CREATE_TITLE_STRING, "lub");
    SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER,
                          LUB_DEFAULT_WINDOW_W);
    SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER,
                          LUB_DEFAULT_WINDOW_H);
    SDL_SetBooleanProperty(
        props, SDL_PROP_WINDOW_CREATE_EXTERNAL_GRAPHICS_CONTEXT_BOOLEAN, true);
    proc->window = SDL_CreateWindowWithProperties(props);
    SDL_DestroyProperties(props);
  } else {
    proc->window =
        SDL_CreateWindow("lub", LUB_DEFAULT_WINDOW_W, LUB_DEFAULT_WINDOW_H,
                         window_flags_for_backend(proc->backend_name));
  }
#endif
  if (!proc->window) {
    SDL_Log("SDL_CreateWindow failed: %s", SDL_GetError());
    return false;
  }
  proc->phase = APP_PHASE_PRE_BACKEND;
  capture_state_init(&proc->capture);
  return true;
}

void process_shutdown(Process *proc) {
  if (g_backend)
    g_backend->shutdown(proc);
  gpu_stats_shutdown(g_backend ? g_backend->name : NULL);
  capture_state_shutdown(&proc->capture);
  if (proc->window)
    SDL_DestroyWindow(proc->window);
  proc->window = NULL;
}

bool app_init(App *app, Process *proc) {
  memset(app, 0, sizeof(*app));
  app->proc = proc;
  pass_state_init(&app->pass);
  pass_state_set_app(&app->pass, app);
  profile_state_init(&app->profile);
  res_table_init(&app->res, &proc->res_next_handle);
  phys2d_state_init(&app->phys);
  phys3d_state_init(&app->phys3);
  pipeline_cache_init(&app->pip_cache);
  app->frame_index = 0;
  app->declare_epoch = 1;
  app->resource_frame = 0;
  app->quit_requested = false;
  app->exit_code = 0;
  app->readback_depth = 8;
  app->resource_sweep_after_frames = 120;
  return true;
}

bool app_backend_init(App *app) {
#ifndef __EMSCRIPTEN__
  // If App.Config overrides the backend after the window is created, the window
  // may lack SDL_WINDOW_VULKAN. Recreate it only in that case (the reverse — an
  // extra flag — is harmless, so leave it).
  SDL_WindowFlags want = window_flags_for_backend(app->proc->backend_name);
  if ((want & SDL_WINDOW_VULKAN) &&
      !(SDL_GetWindowFlags(app->proc->window) & SDL_WINDOW_VULKAN)) {
    int w = 0, h = 0;
    SDL_GetWindowSize(app->proc->window, &w, &h);
    SDL_DestroyWindow(app->proc->window);
    app->proc->window = SDL_CreateWindow("lub", w, h, want);
    if (!app->proc->window) {
      SDL_Log("SDL_CreateWindow (vulkan-capable) failed: %s", SDL_GetError());
      return false;
    }
  }

  if (export_active()) {
    // Export does not depend on the backend name (environment variable or
    // App.Config)
    g_backend = &g_backend_export;
  } else if (strcmp(app->proc->backend_name, "sdlgpu") == 0) {
    g_backend = &g_backend_sdlgpu;
  } else if (strcmp(app->proc->backend_name, "d3d12") == 0) {
#if defined(_WIN32)
    g_backend = &g_backend_d3d12;
#else
    SDL_Log("backend 'd3d12' is Windows-only");
    return false;
#endif
  } else if (strcmp(app->proc->backend_name, "vulkan") == 0 ||
             strcmp(app->proc->backend_name, "openxr") == 0) {
#if defined(LUB_HAS_VULKAN)
    g_backend = &g_backend_vulkan;
#else
    SDL_Log("backend 'vulkan' is not available in this build");
    return false;
#endif
  } else if (strcmp(app->proc->backend_name, "metal") == 0) {
#if defined(__APPLE__)
    g_backend = &g_backend_metal;
#else
    SDL_Log("backend 'metal' is Apple-only");
    return false;
#endif
  } else {
    SDL_Log("unknown backend '%s' (expected 'd3d12', 'vulkan', 'metal', "
            "'openxr' or 'sdlgpu')",
            app->proc->backend_name);
    return false;
  }
#else
  // wasm build: webgpu backend only (backend_name is ignored).
  g_backend = &g_backend_webgpu;
#endif
  SDL_Log("backend selected: %s", strcmp(app->proc->backend_name, "openxr") == 0
                                      ? "openxr"
                                      : g_backend->name);
  if (!g_backend->init(app->proc)) {
    SDL_Log("backend init failed");
    return false;
  }
  app->proc->phase = APP_PHASE_POST_BACKEND;
  return true;
}

bool process_frame_waits_for_gpu(Process *proc) {
  if (!capture_state_in_flight(&proc->capture))
    return false;
  if (capture_state_finish(&proc->capture))
    proc->capture_then_exit = true;
  return true;
}

void process_frame_begin(Process *proc, int *out_w, int *out_h) {
  int w = 0, h = 0;
  g_backend->begin_frame(proc, &w, &h);
  proc->in_frame = true;
  if (out_w)
    *out_w = w;
  if (out_h)
    *out_h = h;
  proc->last_w = w;
  proc->last_h = h;
}

// Recount fps every second
static void process_count_fps(Process *proc) {
  uint64_t now_ns;
  if (proc->fixed_frame_dt > 0.0) {
    proc->fps_fixed_ns += (uint64_t)(proc->fixed_frame_dt * 1e9 + 0.5);
    now_ns = proc->fps_fixed_ns;
  } else {
    now_ns = SDL_GetTicksNS();
  }
  if (proc->fps_last_ns == 0) {
    proc->fps_last_ns = now_ns;
    proc->fps_frame_count = 0;
    return;
  }
  proc->fps_frame_count++;
  uint64_t elapsed_ns = now_ns - proc->fps_last_ns;
  if (elapsed_ns < 1000000000ULL)
    return;
  if (proc->fixed_frame_dt > 0.0) {
    // fixed dt comes from a float, so 1/dt is off from 60 or 80 by about 1e-6.
    // Round to 1e-3 to get a clean value
    proc->actual_fps = floor(1000.0 / proc->fixed_frame_dt + 0.5) / 1000.0;
  } else {
    proc->actual_fps =
        (double)proc->fps_frame_count * 1000000000.0 / (double)elapsed_ns;
  }
  proc->fps_last_ns = now_ns;
  proc->fps_frame_count = 0;
}

void process_frame_submit(Process *proc) {
  bool capture_before_end_frame =
      g_backend && g_backend->capture_before_end_frame;
  gpu_time_frame_end(proc);
  if (capture_before_end_frame && capture_state_drain(&proc->capture, proc))
    proc->capture_then_exit = true;
  g_backend->end_frame(proc);
  proc->in_frame = false;
  process_count_fps(proc);
  if (!capture_before_end_frame && capture_state_drain(&proc->capture, proc))
    proc->capture_then_exit = true;
}

void process_frame_end(Process *proc) {
  proc->frame_index++;
  gpu_stats_frame(proc->frame_index, g_backend ? g_backend->name : NULL);
}

bool app_frame_waits_for_gpu(App *app) {
  if (!g_backend || !g_backend->cannot_block_on_gpu)
    return false;
  // In the guaranteed mode, readback needs the results to deliver in this frame
  return app->readback_fixed_latency && !api_gfx_readbacks_due_ready(app);
}

static void app_on_shader_release(void *ctx, uintptr_t old_shader) {
  App *app = (App *)ctx;
  pipeline_cache_invalidate_shader(&app->pip_cache, old_shader);
}

void app_frame_end(App *app, bool frame_ok) {
  // Sweep of snds / readback queues declared by key.
  // The PCM of retired snds is reclaimed afterwards by audio_state_frame_end.
  api_audio_frame_end(app);
  api_app_frame_end(app);
  api_gfx_frame_end(app);
  if (app->audio) {
    audio_state_frame_end(app->audio);
  }
  if (app->resource_sweep_after_frames > 0) {
    int64_t cf = app->resource_frame;
    int64_t thr = (int64_t)app->resource_sweep_after_frames;
    // Pipelines first: invalidate_shader (called by res sweep) walks the
    // same buckets, so ordering avoids touching freed entries.
    pipeline_cache_sweep(&app->pip_cache, cf, thr);
    res_table_sweep(&app->res, cf, thr, app_on_shader_release, app);
  }
  // Reclaim the backing data of frame-scoped views
  app_frame_garbage_drain(app);
  digest_frame_end(app);
  if (frame_ok)
    app->resource_frame++;
  app->frame_index++;
}

void app_shutdown(App *app) {
  // Close opened apps while the backend still exists
  api_app_shutdown(app);
  gpu_time_forget(app);
  pick_shutdown(app);
  // Pipelines reference shaders, so destroy pipelines before resources.
  pipeline_cache_shutdown(&app->pip_cache);
  api_gfx_shutdown(
      app); // readback queues (including the backend's readback requests)
  api_audio_shutdown(app);
  app_frame_garbage_drain(app);
  free(app->frame_garbage);
  app->frame_garbage = NULL;
  app->frame_garbage_count = app->frame_garbage_cap = 0;
  api_io_shutdown(app);
  audio_state_destroy(app->audio);
  app->audio = NULL;
  phys3d_state_shutdown(&app->phys3);
  phys2d_state_shutdown(&app->phys);
  res_table_shutdown(&app->res);
}

int64_t app_file_mtime_ns(const char *path) {
  if (!path)
    return 0;
#ifdef _WIN32
  // st_mtime of MSVC's struct _stat64 has one-second precision, so it misses a
  // second save within the same second. SDL converts ftLastWriteTime of
  // GetFileAttributesExW (100ns precision) to ns and returns it.
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
