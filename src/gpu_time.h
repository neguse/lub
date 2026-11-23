#pragma once
#include "lub/lub_api.h"
#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

struct App;
struct Process;

// Per-pass GPU time and draw count while cost is being counted
// (Sys.TakeGpuPasses). pass.c reports the start and end of passes, and the code
// that issues draws reports draws. Backends that can measure timestamps write
// them before and after each pass and return them through gpu_time_resolved
// after the GPU finishes that frame. Passes on backends that cannot measure are
// output with time -1 at the end of the frame. Counting is per app
// (Sys.ProfileLines); passes a counting app begins with Gfx.BeginPass are
// recorded (excluding passes internal to lub), and if the debug hook (tcs VM
// player) returns the current --trace line, that is recorded too. If counting
// starts mid-frame, recording starts with the next frame's passes.

// Maximum number of passes recorded per frame. Backend timestamp indices are 2i
// before pass i and 2i + 1 after it
#define GPU_TIME_PASSES 64
#define GPU_TIME_STAMPS (GPU_TIME_PASSES * 2)
// A timestamp that was not written
#define GPU_TIME_NONE UINT64_MAX

void gpu_time_enable(struct App *app, bool on);
bool gpu_time_enabled(void);
// Whether to record the pass being started (pass.c calls this before the
// backend's begin_pass and stores it in PassBeginDesc.timed. Used by backends
// that request timestamps when creating a pass)
bool gpu_time_wants_pass(struct App *app);
// target is the render target name (NULL for passes started outside
// Gfx.BeginPass, which are not recorded)
void gpu_time_pass_begin(struct App *app, const char *target);
void gpu_time_pass_end(struct App *app);
void gpu_time_draw(struct App *app);
// Call before the backend's end_frame
void gpu_time_frame_end(struct Process *proc);
// The backend returns the frame's timestamps (ns, count of them). Unwritten
// timestamps are GPU_TIME_NONE
void gpu_time_resolved(uint64_t frame, const uint64_t *ns, uint32_t count);
// Hand over, oldest first, the passes begun by app that arrived since the last
// take (valid until the next call)
void gpu_time_take(const struct App *app, const LubGpuPass **out,
                   int32_t *count);
// Call when app ends
void gpu_time_forget(struct App *app);

#ifdef __cplusplus
}
#endif
