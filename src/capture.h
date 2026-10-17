#pragma once
#include <stdbool.h>
#include <stdint.h>

struct App;

// Capture state attached to App.
typedef struct CaptureState {
  bool pending;          // true = capture next presented frame
  char *path;            // strdup'd PNG path (free on shutdown / after capture)
  uint64_t target_frame; // frame index at/after which to capture (0 = next)
  uint32_t
      retries_left; // remaining retries when backend can't read swapchain yet
  // request_capture's readback (BackendReadback) of frame target_frame;
  // 0 when none is in flight.
  uintptr_t readback;
} CaptureState;

void capture_state_init(CaptureState *c);
void capture_state_shutdown(CaptureState *c);

// Schedule a capture: takes ownership of a copy of `path`.
// `at_frame` is the 1-based count of rendered frames: N captures the Nth
// frame's draw ("N フレーム描画後"). 0 means "capture as soon as possible".
void capture_schedule(CaptureState *c, const char *path, uint64_t at_frame);

// True when the frame `frame_index` (0-based) is to be captured.
bool capture_state_due(const CaptureState *c, uint64_t frame_index);

// Called from app_frame_end. If a capture is pending and the current frame
// is at/after target_frame, dispatch g_backend->capture(app, path) and clear
// the pending flag. Returns true if capture was performed. With
// request_capture it returns false and leaves the readback in flight.
bool capture_state_drain(CaptureState *c, struct App *app);

bool capture_state_in_flight(const CaptureState *c);
// Writes the PNG of the in-flight readback once it is READY. Returns true
// when the capture is over (written or failed), false while it is pending.
bool capture_state_finish(CaptureState *c);
