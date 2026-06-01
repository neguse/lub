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
} CaptureState;

void capture_state_init(CaptureState *c);
void capture_state_shutdown(CaptureState *c);

// Schedule a capture: takes ownership of a copy of `path`.
// `at_frame` 0 means "capture as soon as possible (next frame)".
void capture_schedule(CaptureState *c, const char *path, uint64_t at_frame);

// Called from app_frame_end. If a capture is pending and the current frame
// is at/after target_frame, dispatch g_backend->capture(app, path) and clear
// the pending flag. Returns true if capture was performed.
bool capture_state_drain(CaptureState *c, struct App *app);
