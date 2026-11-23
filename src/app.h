#pragma once
#include "audio.h"
#include "capture.h"
#include "digest.h"
#include "input.h"
#include "lub/lub_api.h"
#include "pass.h"
#include "physics_box2d.h"
#include "physics_box3d.h"
#include "pipeline.h"
#include "profile.h"
#include "resources.h"
#include <SDL3/SDL.h>
#include <stdbool.h>
#include <stdint.h>

typedef enum { APP_PHASE_PRE_BACKEND, APP_PHASE_POST_BACKEND } AppPhase;

// Window size when App.Config does not specify Width / Height.
#define LUB_DEFAULT_WINDOW_W 1280
#define LUB_DEFAULT_WINDOW_H 720

struct App;
// Inspection entry points (Sys.TakeTrace / TakeValues / Inspect*).
// Result arrays and strings are valid until the next call of the same function.
typedef struct LubDebugHooks {
  // Exclude lines passed only by other instances of the same class as the
  // instance remembered for key (all lines if key is empty)
  void (*take_trace)(struct App *app, LubStr key, const LubStr **out,
                     int32_t *count);
  void (*take_values)(struct App *app, const LubTraceValue **out,
                      int32_t *count);
  void (*inspect)(struct App *app, LubStr path, int32_t offset, int32_t count,
                  const LubInspectNode **out, int32_t *n);
  void (*inspect_pin)(struct App *app, LubStr key, LubStr path);
  bool (*inspect_pinned)(struct App *app, LubStr key, LubStr id);
  // Update the value at path to text (Sys.InspectSet). If it cannot be written,
  // return false and write the reason to error (cap bytes)
  bool (*inspect_set)(struct App *app, LubStr path, LubStr text, char *error,
                      int32_t cap);
  // Start / stop counting per-line cost, per-line cost and GC pauses since the
  // last take, and objects carried over across frame boundaries
  void (*profile_lines)(struct App *app, bool on);
  void (*take_profile)(struct App *app, const LubProfileLine **out,
                       int32_t *count);
  void (*take_gc_pauses)(struct App *app, const LubGcPause **out,
                         int32_t *count);
  void (*take_kept)(struct App *app, const LubKeptObject **out, int32_t *count);
  // The current --trace line ("file:line") while cost is being counted. false
  // if none
  bool (*profile_site)(struct App *app, char *out, int32_t cap);
  // Class instances reachable from statics (Sys.InspectInstances)
  void (*inspect_instances)(struct App *app, int32_t max_depth,
                            int32_t max_children,
                            const LubInspectInstance **out, int32_t *n);
} LubDebugHooks;

// The per-process part: SDL, window and screen size, backend choice, gamepads,
// frame clock. App (the per-VM context) points to this via proc.
typedef struct Process {
  SDL_Window *window;
  int last_w, last_h; // last extents seen by process_frame_begin
  int cfg_w, cfg_h;   // Width / Height from App.Config. 0 = keep the default
  // > 0: draw the main target at this size (metal backend capture runs).
  // A window larger than the screen gets shrunk, and on Retina the pixels are
  // doubled, so drawing at the window's physical pixels makes the capture size
  // depend on the machine.
  int fixed_w, fixed_h;
  // Set by lub_host_translate_event on SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED.
  // The backend's begin_frame checks this and rebuilds its swapchain before
  // acquire. The vulkan backend's VkResult path (OUT_OF_DATE / SUBOPTIMAL)
  // sets its own need_recreate, which begin_frame checks alongside this flag,
  // so the swapchain is only ever rebuilt at frame start.
  bool pending_resize;

  // Backend selection. process_init sets phase = PRE_BACKEND and backend_name
  // from env LUB_BACKEND (unset = platform default: Windows "d3d12",
  // Linux "vulkan", Apple "metal", wasm "webgpu", else "sdlgpu"). The game's
  // App.Config() may overwrite backend_name during OnInit (PRE_BACKEND only).
  // app_backend_init flips phase to POST_BACKEND after init() succeeds.
  AppPhase phase;
  char backend_name[16];

  // Gamepads by player slot. A slot keeps its pad until that pad is removed.
  SDL_Gamepad *gamepads[LUB_GAMEPAD_SLOTS];
  SDL_JoystickID gamepad_ids[LUB_GAMEPAD_SLOTS];

  // Effective frame delta time in seconds, passed to UI and OnFrame(dt).
  // Normally measured from the real frame clock and clamped so a debugger
  // pause or window drag does not produce a giant step.
  double frame_dt;
  // Test-only override from --fixed-dt. 0 means use the real frame clock.
  double fixed_frame_dt;
  uint64_t frame_prev_counter;
  // updated once per second after backend end_frame/present.
  // With --fixed-dt, it does not depend on the wall clock and is updated to the
  // reciprocal of fixed dt (rounded to 1e-3) every second's worth of frames.
  double actual_fps;
  uint64_t fps_fixed_ns;
  uint64_t fps_last_ns;
  int fps_frame_count;

  // Screen frame number (used by the backend, screen capture, and GPU timing).
  // The App's frame_index is the frame number the game sees and is counted
  // separately.
  uint64_t frame_index;
  // true from process_frame_begin until process_frame_submit (from the
  // backend's begin_frame to end_frame).
  bool in_frame;
  // Screen capture (--capture)
  CaptureState capture;
  bool capture_then_exit; // set by process_frame_submit after a successful
                          // capture
  // Set when the window is closed or the XR session ends (App.Quit uses the
  // App's quit_requested)
  bool quit_requested;
  // The last resource handle issued.
  // Apps in the same process share the numbering, so one app's handle never
  // points to another app's resource
  int32_t res_next_handle;
} Process;

// Entry points to open and run other apps (App.Use / App.Step, api_app.c). Set
// by the tcs VM player.
typedef struct LubAppHooks {
  // Open the entry of the module (.tcsb) on child and call OnInit.
  // Returns the player state even if the module cannot be loaded (waits for it
  // to be rewritten). NULL only when out of memory
  void *(*open)(struct App *owner, struct App *child, const char *module,
                const char *entry);
  // Call OnFrame(dt). false if there is no module or OnFrame fails
  bool (*step)(void *player, float dt);
  // Call OnQuit and discard the player state
  void (*close)(void *player);
  // Call OnEvent(e). If there is no module or no OnEvent, do nothing and return
  // true. false on failure
  bool (*event)(void *player, const LubEventData *e);
  // Current status (App.Status). out is passed zero-filled. Strings are valid
  // until the next hook call
  void (*status)(void *player, LubAppStatus *out);
  // Write the state to a byte array. bytes is allocated with malloc, and the
  // caller frees it. If it cannot be written, return false and write the reason
  // to error
  bool (*save)(void *player, uint8_t **bytes, size_t *size, char *error,
               size_t error_size);
  // Load the state from a byte array. If refused, return false, leave the state
  // unchanged, and write the reason to error
  bool (*load)(void *player, const uint8_t *bytes, size_t size, char *error,
               size_t error_size);
  // Collect the inputs for compiling the .csproj (App.Inputs).
  // false if they cannot be collected. Strings are valid until the next call.
  // NULL in players that cannot read .csproj
  bool (*inputs)(const char *csproj, LubProjectInputs *out);
} LubAppHooks;

typedef struct App {
  Process *proc;

  PassState pass;
  ProfileState profile;
  ResTable res;
  PhysState phys;
  Phys3dState phys3;
  // Created lazily on the first lub_audio_* call (including starting the
  // device). If it is still NULL, audio has never been used.
  AudioState *audio;
  PipelineCache pip_cache;
  uint64_t frame_index;
  // A counter that advances by 1 on each callback call (OnFrame / OnEvent).
  // Used to check whether declarations of different versions for the same key
  // are in the same call (a contradiction). 0 is used for "not declared yet",
  // so it starts at 1.
  uint64_t declare_epoch;
  // Frame number used to count resource lifetimes.
  // It does not advance on frames where OnFrame ended in an error (including
  // frames without a VM), so nothing is swept while the code is being fixed.
  int64_t resource_frame;

  DigestState digest;  // --digest (host opts)
  bool quit_requested; // Set by App.Quit
  int32_t exit_code;   // Quit code (0 = success)
  // Set by App.Restart; the player recreates it at the end of the frame
  bool restart_requested;

  // Input read by Input.* (input.h).
  InputState input;

  // How many frames after its declarations (use* / snd / readback poll) stop a
  // resource is destroyed. Default 120; 0 never destroys. app_frame_end drops
  // entries whose last_seen_frame is older than (resource_frame -
  // resource_sweep_after_frames). Can be changed with Config in OnInit.
  int resource_sweep_after_frames;

  // Default readback queue depth for Gfx.Readback(key) queues. Configurable
  // via App.Config (ReadbackDepth) during OnInit.
  int readback_depth;
  // Mode that guarantees the frame in which readbacks arrive
  // (ConfigOpts.ReadbackFixedLatency, startup option --readback-fixed-latency).
  // Used by api_gfx.c.
  bool readback_fixed_latency;

  // State for the C API (include/lub/lub_api.h).
  // last_error is the message of the latest LUB_ERROR, and readbacks are the
  // readback queues declared by key (owned by api_gfx.c).
  char last_error[512];
  struct GfxReadbackQueues *readbacks;
  struct GfxFrameRanges
      *frame_ranges; // FrameBuffers of this frame (owned by api_gfx.c)
  struct AudioSnds *audio_snds; // snds declared by key (owned by api_audio.c)
  struct IoCache *io_cache;     // File cache for lub_io_* / lub_png_load
  // Backing data of frame-scoped views (readback pixels, audio_decode PCM, font
  // / mesh results, etc.). app_frame_end releases each with its free_fn.
  struct FrameGarbage *frame_garbage;
  int frame_garbage_count, frame_garbage_cap;

  // true only during OnInit after the VM is recreated.
  // This is after the backend starts, so App.Config applies only the other
  // settings without changing the backend or window size
  bool entry_restarting;
  // Inspection entry points held by the player (set by the tcs VM player. If
  // missing, Sys.TakeTrace and others return empty)
  const struct LubDebugHooks *debug_hooks;
  // Player state passed to the inspection entry points (the entry point
  // functions look it up from app)
  void *debug_user;
  // true only while the opener steps with recorded input (StepInput.Replay).
  // The same frame is being recreated, so do not play sound
  bool replaying;
  // Whether the GPU time of passes is being counted (Sys.ProfileLines), and the
  // screen frame where counting starts (src/gpu_time.c)
  bool gpu_time_on;
  uint64_t gpu_time_from;
  // Gfx.Pick state (owned by pick.c, created on the first request).
  struct PickState *pick;
  // Function that writes the caller of a draw recorded by Gfx.Pick, and its
  // argument (set by the player with pick_set_sites, pick.h). If NULL,
  // PickHit.Site is empty.
  void (*pick_sites)(void *user, char *out, size_t cap);
  void *pick_sites_user;

  // Table of apps opened with App.Use (owned by api_app.c), and the entry
  // points to open them.
  struct OpenApps *open_apps;
  const LubAppHooks *app_hooks;
  // The app that opened this app with App.Use (NULL for the app that draws to
  // the window).
  struct App *opened_by;
  // Main render target of an opened app (only during App.Step. 0 otherwise).
  // main_w / main_h are the target size of the last Step, returned by Gfx.Size.
  uintptr_t main_color, main_depth;
  SglPixelFormat main_fmt;
  int main_w, main_h;
  // Whether XR is hidden from an opened app (during a Step where StepInput.Xr
  // is false).
  bool xr_hidden;
  // XR eyes provided by the opener (only during a Step that passed
  // StepInput.XrTargets). Render target and depth, size, view-projection.
  bool xr_given;
  uintptr_t xr_color[2], xr_depth[2];
  SglPixelFormat xr_fmt[2];
  int xr_w[2], xr_h[2];
  float xr_vp[2][16];
} App;

// Call after SDL_Init. Decides the default backend and creates the window
bool process_init(Process *proc);
// Shut down the backend and destroy the window (after every app_shutdown,
// before SDL_Quit)
void process_shutdown(Process *proc);
bool app_init(App *app, Process *proc);
bool app_backend_init(App *app); // call after OnInit has run; returns false
                                 // on backend init failure
// A screen frame starts with process_frame_begin; after the context callbacks,
// process_frame_submit hands it to the backend to present, and after the
// contexts' app_frame_end, it ends with process_frame_end. true if the screen
// capture readback has not arrived yet (after writing, capture_then_exit
// exits). The host does not start a frame meanwhile.
bool process_frame_waits_for_gpu(Process *proc);
void process_frame_begin(Process *proc, int *out_w, int *out_h);
void process_frame_submit(Process *proc);
void process_frame_end(Process *proc);
// true on a backend that cannot wait for the GPU (cannot_block_on_gpu) if the
// GPU results needed by this context's next frame have not arrived yet. The
// host does not start a frame meanwhile.
bool app_frame_waits_for_gpu(App *app);
// End-of-frame cleanup for a context (resource sweep, reclaiming frame-scoped
// views, digest). frame_ok is whether OnFrame finished without an error
// (whether to advance resource_frame).
void app_frame_end(App *app, bool frame_ok);
void app_shutdown(App *app);

// Returns mtime of `path` in nanoseconds since epoch (sub-second precision;
// SDL_GetPathInfo on Windows). Returns 0 if the file does not exist or stat
// fails. Used by the lub_io_* file cache.
int64_t app_file_mtime_ns(const char *path);
