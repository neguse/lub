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
  // pass の中で最後に backend に渡した pipeline (pass_state_apply_pipeline)。
  bool pipeline_applied;
  BackendPipeline applied_pipeline;
} PassState;

void pass_state_init(PassState *p);
void pass_state_set_app(PassState *p, struct App *app);
bool pass_state_in_pass(const PassState *p);

// Begin a pass with one color target (offscreen if target_image != 0, swapchain
// otherwise). Convenience for single-target paths (Sample 01-05). For MRT use
// pass_state_begin_mrt. load == SGL_LOAD_LOAD keeps the previous attachment
// contents (0 behaves as SGL_LOAD_CLEAR).
void pass_state_begin(PassState *p, uintptr_t target_image, SglPixelFormat fmt,
                      int target_w, int target_h, float r, float g, float b,
                      float a, SglLoadAction load);

// Begin a multi-target offscreen pass. Each targets[i] must be a non-zero
// render-target image handle (swapchain MRT not supported).
void pass_state_begin_mrt(PassState *p, int n_targets, const uintptr_t *targets,
                          const SglPixelFormat *fmts, int target_w,
                          int target_h, const float (*clears)[4]);

// General offscreen path. n_targets may be 0 for a depth-only pass;
// depth_target is optional for color passes. A swapchain pass is still
// represented by pass_state_begin() with target_image == 0.
void pass_state_begin_ex(PassState *p, int n_targets, const uintptr_t *targets,
                         const SglPixelFormat *fmts, int target_w, int target_h,
                         const float (*clears)[4], uintptr_t depth_target,
                         SglPixelFormat depth_fmt, float clear_depth,
                         SglLoadAction load);

void pass_state_end(PassState *p);

// draw の pipeline を backend に渡す。pass の中で直前に渡したものと同じなら
// 渡さない (backend.h の apply_pipeline)。
void pass_state_apply_pipeline(PassState *p, BackendPipeline pip);
// 直前に渡した pipeline を忘れ、次の draw で必ず渡す。runtime の draw の外で
// backend の pipeline が変わったとき (ImGui の描画) と、pipeline が破棄され
// うるとき (shader の作り直し) に呼ぶ。begin_pass / end_pass も忘れる。
void pass_state_forget_pipeline(PassState *p);
