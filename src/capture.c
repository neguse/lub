#include "capture.h"
#include "app.h"
#include "backend.h"

#include <SDL3/SDL.h>
#include <stdlib.h>
#include <string.h>

// On Windows the SDL_GPU swapchain may legitimately return NULL for several
// frames in a row (e.g. while the OS compositor catches up after window
// creation). Allow the capture to slip forward up to this many frames before
// giving up so a one-shot --capture --capture-frame N still succeeds.
#define CAPTURE_RETRY_FRAMES 120

void capture_state_init(CaptureState *c) {
  if (!c)
    return;
  c->pending = false;
  c->path = NULL;
  c->target_frame = 0;
  c->retries_left = 0;
}

void capture_state_shutdown(CaptureState *c) {
  if (!c)
    return;
  if (c->path) {
    free(c->path);
    c->path = NULL;
  }
  c->pending = false;
  c->target_frame = 0;
}

void capture_schedule(CaptureState *c, const char *path, uint64_t at_frame) {
  if (!c || !path)
    return;
  if (c->path) {
    free(c->path);
    c->path = NULL;
  }
  size_t n = strlen(path);
  c->path = (char *)malloc(n + 1);
  if (c->path) {
    memcpy(c->path, path, n + 1);
  }
  // `at_frame` is the 1-based count of rendered frames ("capture after N
  // frames"); frame_index is 0-based, so the Nth frame has index N-1.
  // 0 keeps the "capture as soon as possible" meaning.
  c->target_frame = at_frame > 0 ? at_frame - 1 : 0;
  c->pending = true;
  c->retries_left = CAPTURE_RETRY_FRAMES;
}

bool capture_state_drain(CaptureState *c, struct App *app) {
  if (!c || !c->pending || !c->path)
    return false;
  if (app->frame_index < c->target_frame)
    return false;

  bool ok = g_backend->capture(app, c->path);
  if (ok) {
    // Log the 1-based frame count to match the --capture-frame argument.
    SDL_Log("captured frame %llu -> %s",
            (unsigned long long)(app->frame_index + 1), c->path);
  } else if (c->retries_left > 0) {
    c->retries_left--;
    // Slip the target forward one frame and try again next iteration.
    c->target_frame = app->frame_index + 1;
    return false; // not consumed yet
  } else {
    SDL_Log("capture failed (frame %llu, path=%s)",
            (unsigned long long)app->frame_index, c->path);
  }
  free(c->path);
  c->path = NULL;
  c->pending = false;
  return true; // consume the request
}
