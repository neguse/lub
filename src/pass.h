#pragma once
#include "backend.h"
#include "enums.h"
#include <stdbool.h>
#include <stdint.h>

struct App; // 前方宣言

typedef struct PassState {
  bool in_pass;
  struct App *app; // backend pass calls need the App context
  int current_n_color_targets;
  SglPixelFormat
      current_color_fmts[SGL_MAX_COLOR_TARGETS]; // valid while in_pass
  bool current_has_depth;                        // valid while in_pass
  SglPixelFormat current_depth_fmt;              // valid when current_has_depth
} PassState;

void pass_state_init(PassState *p);
void pass_state_set_app(PassState *p, struct App *app);
bool pass_state_in_pass(const PassState *p);

// Begin a pass with one color target (offscreen if target_image != 0, swapchain
// otherwise). Used by the swapchain / XR pass in api_gfx.c. For MRT use
// pass_state_begin_ex. load == SGL_LOAD_LOAD keeps the previous attachment
// contents (0 behaves as SGL_LOAD_CLEAR). xr_eye >= 0 draws that XR eye
// instead of the window swapchain (target_image must be 0).
void pass_state_begin(PassState *p, uintptr_t target_image, SglPixelFormat fmt,
                      int target_w, int target_h, float r, float g, float b,
                      float a, SglLoadAction load, int xr_eye);

// General offscreen path. n_targets may be 0 for a depth-only pass;
// depth_target is optional for color passes. A swapchain pass is still
// represented by pass_state_begin() with target_image == 0.
void pass_state_begin_ex(PassState *p, int n_targets, const uintptr_t *targets,
                         const SglPixelFormat *fmts, int target_w, int target_h,
                         const float (*clears)[4], uintptr_t depth_target,
                         SglPixelFormat depth_fmt, float clear_depth,
                         SglLoadAction load);

void pass_state_end(PassState *p);
