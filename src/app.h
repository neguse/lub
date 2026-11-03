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
#include "lub/lub_api.h"
#include "pass.h"
#include "physics_box2d.h"
#include "physics_box3d.h"
#include "pipeline.h"
#include "profile.h"
#include "resources.h"
#include <stdbool.h>
#include <stdint.h>

typedef enum { APP_PHASE_PRE_BACKEND, APP_PHASE_POST_BACKEND } AppPhase;

// App.Config で Width / Height を指定しないときの window の大きさ。
#define LUB_DEFAULT_WINDOW_W 1280
#define LUB_DEFAULT_WINDOW_H 720

// Input.Gamepad* の player の数 (0..3)。
#define LUB_GAMEPAD_SLOTS 4

struct App;
// 検査の口 (Sys.TakeTrace / TakeValues / Inspect* と Rewind の状態の控え)。
// 結果の配列と文字列は次の同じ呼び出しまで有効。
typedef struct LubDebugHooks {
  void (*take_trace)(struct App *app, const LubStr **out, int32_t *count);
  void (*take_values)(struct App *app, const LubTraceValue **out,
                      int32_t *count);
  void (*inspect)(struct App *app, LubStr path, int32_t offset, int32_t count,
                  const LubInspectNode **out, int32_t *n);
  void (*inspect_pin)(struct App *app, LubStr key, LubStr path);
  bool (*inspect_pinned)(struct App *app, LubStr key, LubStr id);
  // ゲームの状態を控える (失敗は NULL)、書き戻す、捨てる
  void *(*snapshot_take)(struct App *app);
  void (*snapshot_restore)(struct App *app, void *snapshot);
  void (*snapshot_free)(struct App *app, void *snapshot);
  // 控えの中の path の節の数の値 (数でなければ false)
  bool (*snapshot_number)(struct App *app, void *snapshot, LubStr path,
                          float *out);
  // 控えを戻せなくなるたびに変わる数
  uint32_t (*layout_version)(struct App *app);
} LubDebugHooks;

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
  // lub_audio_* の初回呼び出しで lazy に生成される (デバイス起動込み)。
  // NULL のままなら音を一度も使っていない。
  AudioState *audio;
  PipelineCache pip_cache;
  uint64_t frame_index;
  // callback (OnFrame / OnEvent) の呼び出しごとに 1 進む数。
  // 同じ key の違う version の宣言が同じ呼び出しの中か (矛盾か) を見る。
  // 0 は「まだ宣言していない」に使うので 1 から始める。
  uint64_t declare_epoch;
  // app_frame_begin から app_frame_end までの間 true。
  bool in_frame;
  // resource の寿命を数える frame 番号。
  // OnFrame が error で終わった frame (VM が無い frame を含む) では進まないので、直している間は sweep されない。
  int64_t resource_frame;

  // Offscreen capture
  CaptureState capture;
  DigestState digest;     // --digest (host opts)
  bool capture_then_exit; // set by app_frame_end after a successful capture
  int last_w, last_h;     // last extents seen by app_frame_begin
  int cfg_w, cfg_h;       // App.Config の Width / Height。0 = 既定維持
  // > 0: main target をこの大きさで描く (metal backend の capture run)。
  // 画面より大きい window は縮められ、Retina では pixel が倍になるので、window の実 pixel で描くと capture の大きさが機材で変わる。
  int fixed_w, fixed_h;
  bool quit_requested; // App.Quit と window の close が立てる
  int32_t exit_code;   // Quit の code (0 = 成功)
  // updated once per second after backend end_frame/present.
  // --fixed-dt の間は壁時計に依らず、fixed dt の逆数 (1e-3 に丸め) を 1 秒分の frame ごとに更新する。
  double actual_fps;
  uint64_t fps_fixed_ns;
  uint64_t fps_last_ns;
  int fps_frame_count;

  // Set by lub_host_translate_event on SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED.
  // The backend's begin_frame checks this and rebuilds its swapchain before
  // acquire. The vulkan backend's VkResult path (OUT_OF_DATE / SUBOPTIMAL)
  // sets its own need_recreate, which begin_frame checks alongside this flag,
  // so the swapchain is only ever rebuilt at frame start.
  bool pending_resize;

  // Input latch: lub_host_translate_event accumulates edge events here so a
  // press that begins and ends inside one frame is still observable from the
  // game. lub_host_frame_end clears the latches right after the game's
  // OnFrame.
  bool key_pressed[SDL_SCANCODE_COUNT];
  bool key_released[SDL_SCANCODE_COUNT];
  uint32_t mouse_pressed_mask;
  uint32_t mouse_released_mask;
  // Per-frame mouse motion sum (framebuffer px). lub_input_mouse_delta reads
  // this, so it is idempotent within a frame (unlike
  // SDL_GetRelativeMouseState).
  float mouse_rel_x, mouse_rel_y;
  // Per-frame wheel sum (SDL wheel units; +y = away from user).
  float mouse_wheel_x, mouse_wheel_y;
  // Gamepads by player slot. A slot keeps its pad until that pad is removed.
  SDL_Gamepad *gamepads[LUB_GAMEPAD_SLOTS];
  SDL_JoystickID gamepad_ids[LUB_GAMEPAD_SLOTS];
  uint32_t gamepad_pressed[LUB_GAMEPAD_SLOTS];
  uint32_t gamepad_released[LUB_GAMEPAD_SLOTS];

  // Effective frame delta time in seconds, passed to UI and OnFrame(dt).
  // Normally measured from the real frame clock and clamped so a debugger
  // pause or window drag does not produce a giant step.
  double frame_dt;
  // Test-only override from --fixed-dt. 0 means use the real frame clock.
  double fixed_frame_dt;
  uint64_t frame_prev_counter;

  // Backend selection. app_init sets phase = PRE_BACKEND and backend_name
  // from env LUB_BACKEND (unset = platform default: Windows "d3d12",
  // Linux "vulkan", Apple "metal", wasm "webgpu", else "sdlgpu"). The game's
  // App.Config() may
  // overwrite backend_name during OnInit (PRE_BACKEND only).
  // app_backend_init flips phase to POST_BACKEND after init() succeeds.
  AppPhase phase;
  char backend_name[16];

  // 宣言 (use* / snd / readback の poll) が途切れた resource を何 frame 後に破棄するか。
  // 既定 120、0 で破棄しない。
  // app_frame_end が last_seen_frame が (resource_frame - resource_sweep_after_frames) より古い entry を捨てる。
  // OnInit の Config で変えられる。
  int resource_sweep_after_frames;

  // Default readback queue depth for Gfx.Readback(key) queues. Configurable
  // via App.Config (ReadbackDepth) during OnInit.
  int readback_depth;
  // readback の届く frame を保証するモード (ConfigOpts.ReadbackFixedLatency、起動オプション --readback-fixed-latency)。
  // api_gfx.c が使う。
  bool readback_fixed_latency;

  // C API (include/lub/lub_api.h) の状態。
  // last_error は直近の LUB_ERROR の message、readbacks は key で宣言する readback queue (api_gfx.c 所有)。
  char last_error[512];
  struct GfxReadbackQueues *readbacks;
  struct GfxFrameRanges
      *frame_ranges; // このフレームの FrameBuffer (api_gfx.c 所有)
  struct AudioSnds *audio_snds; // key で宣言する snd (api_audio.c 所有)
  struct IoCache *io_cache;     // lub_io_* / lub_png_load の file cache
  // frame 有効の view の実体 (readback の pixel、audio_decode の PCM、font / mesh の結果など)。
  // app_frame_end が各 free_fn で解放する。
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
  char entry_path[256];        // e.g. "samples/01_triangle/.lub/Triangle01.lua"
  char entry_module_name[128]; // e.g. "Triangle01"
  int64_t entry_mtime_cache;   // last observed mtime in ns; 0 means "unknown /
                               // first poll"
  // VM を作り直した後の OnInit の間だけ true。
  // backend の後なので、App.Config は backend と window の大きさを変えずに他の項目だけ効かせる
  bool entry_restarting;
  // Rewind の状態 (rewind.c 所有、最初の Record で作る)
  struct RewindState *rewind;
  // player が持つ検査の口 (tcs VM の player が置く。無ければ Sys.TakeTrace などは空を返す)
  const struct LubDebugHooks *debug_hooks;
  // overlay の callback の中か (player が立てる)。
  // overlay_grab_* は overlay が Input.Grab で取った入力の種類で、取っている間はゲームの OnEvent に届かず、overlay の外から読む Input は何も押されていない状態を返す
  bool in_overlay;
  bool overlay_grab_keyboard;
  bool overlay_grab_mouse;
  // Gfx.Pick の状態 (pick.c 所有、最初の要求で作る)。
  struct PickState *pick;
  // Gfx.Pick が記録する draw の呼び出し元を書く関数と、その引数 (player が pick_set_sites で立てる、pick.h)。
  // NULL なら PickHit.Site は空。
  void (*pick_sites)(void *user, char *out, size_t cap);
  void *pick_sites_user;
  // reload chunk の適用が失敗した (本文の error、または guard の assert)。
  // VM は途中まで当たった状態なので、main.c が出力から作り直す (web はページを読み直す)。
  bool reload_chunk_failed;
} App;

bool app_init(App *app);
bool app_backend_init(App *app); // call after lua onInit has run; returns false
                                 // on backend init failure
void app_frame_begin(App *app, int *out_w, int *out_h);
// frame_ok は OnFrame が error なく終わったか (resource_frame を進めるか)。
void app_frame_end(App *app, bool frame_ok);
// GPU を待てない backend (cannot_block_on_gpu) で、次の frame が要る GPU の結果か capture の readback がまだ届いていなければ true。
// host はその間 frame を始めない。
bool app_frame_waits_for_gpu(App *app);
void app_shutdown(App *app);

// Returns mtime of `path` in nanoseconds since epoch (sub-second precision;
// SDL_GetPathInfo on Windows). Returns 0 if the file does not exist or stat
// fails. Used by the entry-Lua mtime poll (app_frame_begin, main.c) and the
// lub_io_* file cache.
int64_t app_file_mtime_ns(const char *path);

// tcs の reload chunk を積む (内容は複製する)。
// 届いた順に、次の frame の頭で実行中の VM に当てる。
// native は main.c が tcs のパイプから、web は lub_queue_reload_chunk_file (serve の SSE) から積む。
void app_queue_reload_chunk(const char *lua, size_t len);

// まだ当てていない reload chunk を捨てる。
// VM を作り直すとき (tcs の restart_required) は、それまでの chunk が前の build 向けになる。
void app_drop_reload_chunks(void);
