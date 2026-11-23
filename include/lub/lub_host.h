// The lub host API.
// A pull-style surface used by whatever drives the runtime (the lub player, the
// .NET execution host, the tcs2c execution host), separate from the surface the
// game sees (lub_api.h). The host calls
//   create → game OnInit → start → { poll_event* → frame_begin →
//   game OnFrame → frame_end }* → game OnQuit → destroy
// in this order.
// Main thread only.
#pragma once
#include "lub_api.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct LubHostOpts {
  // "d3d12" / "vulkan" / "metal" / "openxr" / "sdlgpu".
  // len 0 = the environment variable LUB_BACKEND or the default.
  // If the game's Config specifies one, that wins.
  LubStr backend;
  // If > 0, this dt is used every frame (for tests).
  // 0 = measured.
  float fixed_dt;
  // len 0 = none.
  // Writes frame number capture_frame to a PNG, after which
  // lub_host_quit_requested returns true.
  LubStr capture_path;
  int32_t capture_frame;
  // Prints a digest of each frame (the structure of draws and resource
  // declarations) to stdout.
  bool digest;
  // Default for the mode that guarantees the frame in which readbacks arrive
  // (ReadbackFixedLatency in the game's Config wins if specified).
  bool readback_fixed_latency;
} LubHostOpts;

// Initializes SDL and the runtime and creates a context.
// NULL on failure (already logged).
LUB_API LubContext *lub_host_create(const LubHostOpts *opts);

// Call after the game's OnInit (Config).
// Applies the window size and starts the backend.
LUB_API LubStatus lub_host_start(LubContext *ctx);

// Takes one pending event.
// false if there is none.
// A quit request arrives as LUB_EVENT_KIND_QUIT, after which quit_requested
// becomes true.
LUB_API bool lub_host_poll_event(LubContext *ctx, LubEventData *out);

// Begins a frame and returns dt (seconds).
// false means a frame that cannot be drawn (the window has 0 size, or on the
// web the GPU results the frame needs have not arrived yet), and the host waits
// a little and moves on to the next frame.
LUB_API bool lub_host_frame_begin(LubContext *ctx, float *dt);

// Ends the frame (after the game's OnFrame).
// frame_ok is whether the game's OnFrame finished without an error.
// Frames with false are not counted toward the frames until the resource sweep.
LUB_API void lub_host_frame_end(LubContext *ctx, bool frame_ok);

// Quit was called, the capture finished, or the window was closed.
LUB_API bool lub_host_quit_requested(LubContext *ctx);

// The exit code passed to Quit (0 if there was no Quit).
LUB_API int32_t lub_host_exit_code(LubContext *ctx);

// Shuts down the runtime and SDL and discards the context.
// If env LUB_EXIT_FILE is set, writes the exit code to that file (for the iOS
// simulator and the web, which cannot receive the exit code).
LUB_API void lub_host_destroy(LubContext *ctx);

#ifdef __cplusplus
}
#endif
