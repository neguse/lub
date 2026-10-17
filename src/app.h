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
#include "digest.h"
#include "lua_api.h"
#include "pass.h"
#include "physics_box2d.h"
#include "physics_box3d.h"
#include "pipeline.h"
#include "profile.h"
#include "resources.h"
#include <stdbool.h>
#include <stdint.h>

typedef enum { APP_PHASE_PRE_BACKEND, APP_PHASE_POST_BACKEND } AppPhase;

// config({width,height}) が無いときの window の大きさ。
#define LUB_DEFAULT_WINDOW_W 1280
#define LUB_DEFAULT_WINDOW_H 720

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
  // app_frame_begin から app_frame_end までの間 true。
  bool in_frame;
  // resource の寿命を数える frame 番号。OnFrame が error で終わった frame
  // (VM が無い frame を含む) では進まないので、直している間は sweep されない。
  int64_t resource_frame;

  // Offscreen capture
  CaptureState capture;
  DigestState digest;     // --digest (host opts)
  bool capture_then_exit; // set by app_frame_end after a successful capture
  int last_w, last_h;     // last extents seen by app_frame_begin
  int cfg_w, cfg_h; // config({width,height}) で要求された窓サイズ。0 = 既定維持
  // > 0: main target をこの大きさで描く (metal backend の capture run)。
  // 画面より大きい window は縮められ、Retina では pixel が倍になるので、
  // window の実 pixel で描くと capture の大きさが機材で変わる。
  int fixed_w, fixed_h;
  bool quit_requested; // Lua quit() が立てる。AppIterate が SUCCESS で抜ける
  int32_t exit_code;   // Quit の code (0 = 成功)
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
  // from env LUB_BACKEND (unset = platform default: Windows "d3d12",
  // Linux "vulkan", Apple "metal", wasm "webgpu", else "sdlgpu"). Lua's
  // config() may
  // overwrite backend_name during onInit (PRE_BACKEND only).
  // app_backend_init flips phase to POST_BACKEND after init() succeeds.
  AppPhase phase;
  char backend_name[16];

  // 宣言 (use* / snd / readback の poll) が途切れた resource を何 frame 後に
  // 破棄するか。既定 120、0 で破棄しない。app_frame_end が last_seen_frame が
  // (resource_frame - resource_sweep_after_frames) より古い entry を捨てる。
  // OnInit の Config で変えられる。
  int resource_sweep_after_frames;

  // Default readback queue depth for Gfx.readback(key) queues. Configurable via
  // Lua config({ readback_depth = N }) during onInit.
  int readback_depth;
  // readback の届く frame を保証するモード (ConfigOpts.ReadbackFixedLatency、
  // 起動オプション --readback-fixed-latency)。api_gfx.c が使う。
  bool readback_fixed_latency;

  // C API (include/lub/lub_api.h) の状態。last_error は直近の LUB_ERROR の
  // message、readbacks は key で宣言する readback queue (api_gfx.c 所有)。
  char last_error[512];
  struct GfxReadbackQueues *readbacks;
  struct AudioSnds *audio_snds; // key で宣言する snd (api_audio.c 所有)
  struct IoCache *io_cache;     // lub_io_* / lub_png_load の file cache
  // frame 有効の view の実体 (readback の pixel、audio_decode の PCM、font /
  // mesh の結果など)。app_frame_end が各 free_fn で解放する。
  struct FrameGarbage *frame_garbage;
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
  // frame and calls lume.hotswap when it changes (except for a tcs
  // --reload-chunks entry, which app_queue_reload_chunk updates instead).
  char entry_path[256];        // e.g. "samples/01_triangle.lua"
  char entry_module_name[128]; // e.g. "01_triangle"
  int64_t entry_mtime_cache;   // last observed mtime in ns; 0 means "unknown /
                               // first poll"
  // VM を作り直した後の OnInit の間だけ true。backend の後なので、
  // App.Config は backend と window の大きさを変えずに他の項目だけ効かせる
  bool entry_restarting;
} App;

bool app_init(App *app);
bool app_backend_init(App *app); // call after lua onInit has run; returns false
                                 // on backend init failure
void app_frame_begin(App *app, int *out_w, int *out_h);
// frame_ok は OnFrame が error なく終わったか (resource_frame を進めるか)。
void app_frame_end(App *app, bool frame_ok);
// GPU を待てない backend (cannot_block_on_gpu) で、次の frame が要る GPU の
// 結果がまだ届いていなければ true。host はその間 frame を始めない。
bool app_frame_waits_for_gpu(App *app);
void app_shutdown(App *app);

// Returns mtime of `path` in nanoseconds since epoch (sub-second precision on
// POSIX, seconds * 1e9 on Windows). Returns 0 if the file does not exist or
// stat fails. Used by both the C-side entry-Lua mtime poll in app_frame_begin
// (hot reload of the entry Lua).
int64_t app_file_mtime_ns(const char *path);

// tcs の reload chunk を積む (内容は複製する)。届いた順に、次の frame の頭で
// 実行中の VM に当てる。native は main.c が tcs のパイプから、web は
// lub_queue_reload_chunk (serve の SSE) から積む。
void app_queue_reload_chunk(const char *lua, size_t len);

// まだ当てていない reload chunk を捨てる。VM を作り直すとき (tcs の
// restart_required) は、それまでの chunk が前の build 向けになる。
void app_drop_reload_chunks(void);
