#pragma once
// Gfx.Pick: finds the frontmost draw at the pointed pixel.
// In the frame after the request, it records the draws of that app to the
// pointed target (main_tex or an XR eye). Call sites come from the function the
// player installs with pick_set_sites, which is made to write the current call
// stack at each draw. At the end of that frame, it replays the recorded draws
// for the single pointed pixel, with the same vertex shader and a fragment
// shader that writes the draw index, and reads it back. Depth test and depth
// write stay as in the original draws, so what remains is the draw that appears
// frontmost on screen. When the readback arrives, it returns the draw index and
// the call sites recorded for that draw. If a buffer or texture used by a
// recorded draw is recreated or updated during that frame, pick keeps the old
// one and uses it for the replay. Storage buffers updated by a later compute in
// the same frame are replayed with their updated contents.
#include "backend.h"
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

struct App;
struct ResEntry;

// Function that writes the call sites of a draw (the contents of PickHit.Site).
// It is called inside a draw that pick records (inside lub_gfx_draw_items), so
// the player writes the current call stack. out is an area of cap bytes; write
// a NUL-terminated string into it. Write one "file:line" per line, from the
// inside (where draw was called) outward, separated by newlines. If that level
// has a self instance, "\t" and the Id may follow (it becomes PickHit.Self).
// Whatever does not fit may be cut off.
// If nothing is written (out[0] stays NUL), Site is empty.
typedef void (*PickSitesFn)(void *user, char *out, size_t cap);

// Installs the function that writes call sites (if fn is NULL, removes it and
// Site becomes empty). The player installs it after lub_host_create. The tcs VM
// player (src/vm_player.c) installs a function that writes the VM call stack.
void pick_set_sites(struct App *app, PickSitesFn fn, void *user);

// target is -1 = main_tex, 0 / 1 = XR eye.
// Discards the result of the previous request (when called in a frame being
// recorded, that frame keeps recording the previous request and its result is
// discarded, and this request is recorded from the next frame)
void pick_request(struct App *app, int target, int x, int y);

// Call at the start of the frame (before OnFrame).
// If there is a request, records this frame
void pick_frame_begin(struct App *app);

// Call at every begin_pass.
// target is -1 = main_tex, 0 / 1 = XR eye, -2 = anything else (not recorded)
void pick_begin_pass(struct App *app, int target);

// Records a draw (does nothing in frames that are not recorded).
// The following pick_record_uniform calls are the uniform blocks of this draw
void pick_record_draw(struct App *app, struct ResEntry *sh,
                      const BindingsDesc *bind, int cull, int prim,
                      bool depth_test, bool depth_write, int count,
                      int instance_count);
void pick_record_uniform(struct App *app, int stage, int slot,
                         const float *data, size_t bytes);

// Call before recreating or updating a buffer or image.
// If a draw being recorded uses it, pick keeps it (destroying it after the
// replay) and returns true, and the caller creates a new one without destroying
// or updating it
bool pick_take_buffer(struct App *app, BackendBuffer buf);
bool pick_take_image(struct App *app, BackendImage image);

// Call at the end of the frame (outside a pass, before the backend end_frame).
// If the frame was recorded, replays it and requests a readback, and receives
// any pending readback
void pick_frame_end(struct App *app);

// true if the result has arrived.
// draw is the draw index within the frame (0-based, -1 if none), site is the
// call sites (newline-separated, from the inside), self is the per-level self
// Ids in the same order as site (an empty line for levels without one; empty if
// no level has one). site and self are valid until the next pick_result or
// pick_request
bool pick_result(struct App *app, int *draw, const char **site,
                 const char **self);

void pick_shutdown(struct App *app);
