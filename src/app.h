#pragma once
#include <SDL3/SDL.h>
#ifndef __EMSCRIPTEN__
// SDL_GPU headers only exist on the native build.
#include <SDL3/SDL_gpu.h>
#else
// emdawnwebgpu port supplies webgpu/webgpu.h. Defining types here keeps the
// App struct WGPU fields real (opaque pointer typedefs) instead of void*
// placeholders.
#include <webgpu/webgpu.h>
#endif
#include "audio.h"
#include "capture.h"
#include "lua_api.h"
#include "pass.h"
#include "physics_box2d.h"
#include "physics_box3d.h"
#include "pipeline.h"
#include "profile.h"
#include "resources.h"
#include <stdbool.h>
#include <stdint.h>
#ifndef __EMSCRIPTEN__
#include "haxe_pipeline.h"
#endif

typedef enum { APP_PHASE_PRE_BACKEND, APP_PHASE_POST_BACKEND } AppPhase;

typedef struct App {
  SDL_Window *window;

#ifdef __EMSCRIPTEN__
  // WGPU state. backend_webgpu.c creates these in init and tears them down
  // in shutdown. The surface lives across resizes; depth_stencil + the
  // current swapchain view get rebuilt when canvas extents change.
  WGPUInstance wgpu_instance;
  WGPUDevice wgpu_device;
  WGPUSurface wgpu_surface;
  WGPUTextureFormat wgpu_surface_format; // WGPUTextureFormat_BGRA8Unorm
  WGPUTexture wgpu_depth_tex;
  WGPUTextureView wgpu_depth_view;
  // Per-frame: acquired from wgpuSurfaceGetCurrentTexture in begin_frame,
  // released in end_frame. begin_pass reads it to populate the swapchain
  // attachment.
  WGPUTexture wgpu_swapchain_tex;
  WGPUTextureView wgpu_swapchain_view;
#endif // __EMSCRIPTEN__

  LuaCtx lua;
  PassState pass;
  ProfileState profile;
  ResTable res;
  PhysState phys;
  Phys3dState phys3;
  // audio_* Lua API の初回呼び出しで lazy に生成される (デバイス起動込み)。
  // NULL のままなら音を一度も使っていない。
  AudioState *audio;
  PipelineCache pip_cache;
  uint64_t frame_index;

  // Offscreen capture
  CaptureState capture;
  bool capture_then_exit; // set by app_frame_end after a successful capture
  int last_w, last_h;     // last extents seen by app_frame_begin
  int cfg_w, cfg_h; // config({width,height}) で要求された窓サイズ。0 = 既定維持
  bool quit_requested; // Lua quit() が立てる。AppIterate が SUCCESS で抜ける
  double actual_fps; // updated once per second after backend end_frame/present
  uint64_t fps_last_ns;
  int fps_frame_count;

  // Set by SDL_AppEvent on SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED. The backend's
  // begin_frame checks this and rebuilds its swapchain before acquire. The
  // VkResult path (OUT_OF_DATE / SUBOPTIMAL) is also a trigger — both write
  // through this flag so the swapchain is only ever rebuilt at frame start.
  bool pending_resize;

  // Input latch: SDL_AppEvent accumulates edge events here so a press that
  // begins and ends inside one frame is still observable from the script.
  // SDL_AppIterate clears the latches right after the script's onFrame.
  bool key_pressed[SDL_SCANCODE_COUNT];
  bool key_released[SDL_SCANCODE_COUNT];
  uint32_t mouse_pressed_mask;
  uint32_t mouse_released_mask;
  // Per-frame mouse motion sum (window px). mouse_delta() reads this, so it
  // is idempotent within a frame (unlike SDL_GetRelativeMouseState).
  float mouse_rel_x, mouse_rel_y;
  // Per-frame wheel sum (SDL wheel units; +y = away from user).
  float mouse_wheel_x, mouse_wheel_y;

  // Effective frame delta time in seconds, passed to UI and onFrame(dt).
  // Normally measured from the real frame clock and clamped so a debugger
  // pause or window drag does not produce a giant step.
  double frame_dt;
  // Test-only override from --fixed-dt. 0 means use the real frame clock.
  double fixed_frame_dt;
  uint64_t frame_prev_counter;

  // Backend selection. app_init sets phase = PRE_BACKEND and backend_name
  // from env LUB_BACKEND (default "native"). Lua's config() may overwrite
  // backend_name during onInit (PRE_BACKEND only). app_backend_init flips
  // phase to POST_BACKEND after the backend's init() succeeds.
  AppPhase phase;
  char backend_name[16];

  // Frame-based GC threshold. 0 disables sweeping. When > 0, app_frame_end
  // releases resource / pipeline entries whose last_seen_frame is older than
  // (frame_index - resource_sweep_after_frames). Configurable via Lua
  // config({ resource_sweep_after_frames = N }) during onInit.
  int resource_sweep_after_frames;

  // Default readback queue depth for Gfx.readback(key) queues. Configurable via
  // Lua config({ readback_depth = N }) during onInit.
  int readback_depth;

  // C API (include/lub/lub_api.h) の状態。last_error は直近の LUB_ERROR の
  // message、readbacks は key で宣言する readback queue (api_gfx.c 所有)。
  char last_error[512];
  struct GfxReadbackQueues *readbacks;
  struct AudioSnds *audio_snds;     // key で宣言する snd (api_audio.c 所有)
  struct IoCache *io_cache;         // lub_io_* / lub_png_load の file cache
  struct FontScratch *font_scratch; // lub_font_* の view の実体
  struct MeshScratch *mesh_scratch; // lub_mesh_* の view の実体
  unsigned char *host_poll_buf;     // lub_host_poll の view の実体
  // frame 有効の view の実体 (readback の pixel、audio_decode の PCM)。
  // app_frame_end が free する。
  void **frame_garbage;
  int frame_garbage_count, frame_garbage_cap;

#ifndef __EMSCRIPTEN__
  // SDL3 GPU backend state. Owned/used by backend_sdlgpu.c only.
  SDL_GPUDevice *gpu_device;
  SDL_GPUTexture *gpu_swapchain_tex; // current frame の swapchain
  SDL_GPUCommandBuffer *gpu_cmd;     // current frame
  SDL_GPUTexture *gpu_depth_tex;     // swapchain-sized depth/stencil target
  int gpu_depth_w;
  int gpu_depth_h;
  SDL_GPUTextureFormat gpu_depth_fmt;
#endif // __EMSCRIPTEN__

  // Entry .lua hot-reload state. main.c populates entry_path /
  // entry_module_name after app_init; app_frame_begin polls mtime each
  // frame and calls lume.hotswap when it changes.
  char entry_path[256];        // e.g. "samples/01_triangle.lua"
  char entry_module_name[128]; // e.g. "01_triangle"
  int64_t entry_mtime_cache;   // last observed mtime in ns; 0 means "unknown /
                               // first poll"

#ifndef __EMSCRIPTEN__
  // Haxe pipeline state. .hxml entry path のとき main.c が haxe_enabled = true
  // に し、haxe_pipeline_start で server + initial build + watch
  // を一括起動する。 app_shutdown が haxe_enabled ガード下で haxe_pipeline_stop
  // を呼ぶ。 WASM ビルドでは子プロセスを spawn
  // しないためフィールドごと存在しない。
  HaxePipeline haxe;
  bool haxe_enabled;
#endif
} App;

bool app_init(App *app);
bool app_backend_init(App *app); // call after lua onInit has run; returns false
                                 // on backend init failure
void app_frame_begin(App *app, int *out_w, int *out_h);
void app_frame_end(App *app);
void app_shutdown(App *app);

// Returns mtime of `path` in nanoseconds since epoch (sub-second precision on
// POSIX, seconds * 1e9 on Windows). Returns 0 if the file does not exist or
// stat fails. Used by both the C-side entry-Lua mtime poll in app_frame_begin
// and the `file_mtime` Lua binding consumed by samples/lub_io.lua.
int64_t app_file_mtime_ns(const char *path);
