// Per-frame digest.
// Folds the structure of C API calls (function name, key, count, handle,
// version, draw Blend) with FNV-1a and prints it to stdout at the end of the
// frame (--digest). CI checks that the same game produces the same sequence
// across execution forms (tcs VM player / .NET execution). Float values are not
// mixed in (float differences between execution forms are allowed).
#pragma once
#include "lub/lub_api.h"
#include <stdbool.h>
#include <stdint.h>

struct App;

typedef struct DigestState {
  bool enabled;
  uint64_t h;
} DigestState;

void digest_tag(struct App *app, const char *tag);
void digest_str(struct App *app, LubStr s);
void digest_i32(struct App *app, int32_t v);
// Print the frame's value and move on to the next frame (called by
// app_frame_end).
void digest_frame_end(struct App *app);
