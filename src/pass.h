#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "enums.h"
#include "backend.h"

struct App; // 前方宣言

typedef struct PassState {
    bool in_pass;
    struct App *app;     // backend pass calls need the App context
    int current_n_color_targets;
    SglPixelFormat current_color_fmts[SGL_MAX_COLOR_TARGETS]; // valid while in_pass
    bool current_has_depth;           // valid while in_pass
} PassState;

void pass_state_init(PassState *p);
void pass_state_set_app(PassState *p, struct App *app);
bool pass_state_in_pass(const PassState *p);

// Begin a pass with one color target (offscreen if target_image != 0, swapchain otherwise).
// Convenience for single-target paths (Sample 01-05). For MRT use pass_state_begin_mrt.
void pass_state_begin(PassState *p, uintptr_t target_image, SglPixelFormat fmt,
                      int target_w, int target_h,
                      float r, float g, float b, float a);

// Begin a multi-target offscreen pass. Each targets[i] must be a non-zero
// render-target image handle (swapchain MRT not supported).
void pass_state_begin_mrt(PassState *p,
                          int n_targets,
                          const uintptr_t *targets,
                          const SglPixelFormat *fmts,
                          int target_w, int target_h,
                          const float (*clears)[4]);

void pass_state_end(PassState *p);
