#include "pass.h"
#include "backend.h"
#include <SDL3/SDL.h>
#include <string.h>

void pass_state_init(PassState *p) {
  p->in_pass = false;
  p->app = NULL;
  p->current_n_color_targets = 1;
  for (int i = 0; i < SGL_MAX_COLOR_TARGETS; ++i) {
    p->current_color_fmts[i] = SGL_PF_RGBA8;
  }
  p->current_has_depth = false;
  p->current_depth_fmt = SGL_PF_DEPTH24_STENCIL8;
}

void pass_state_set_app(PassState *p, struct App *app) { p->app = app; }

bool pass_state_in_pass(const PassState *p) { return p->in_pass; }

void pass_state_begin(PassState *p, uintptr_t target_image, SglPixelFormat fmt,
                      int target_w, int target_h, float r, float g, float b,
                      float a, SglLoadAction load) {
  if (p->in_pass) {
    SDL_Log("begin_pass called while already in pass (nested passes not "
            "supported)");
    return;
  }
  SglPixelFormat use_fmt =
      target_image ? fmt : g_backend->swapchain_color_format(p->app);
  PassBeginDesc d = {
      .n_color_targets = 1,
      .targets = {target_image},
      .color_fmts = {use_fmt},
      .target_w = target_w,
      .target_h = target_h,
      .clear = {{r, g, b, a}},
      .clear_depth = 1.0f,
      .has_depth = (target_image == 0),
      .depth_fmt = SGL_PF_DEPTH24_STENCIL8,
      .load = load,
  };
  g_backend->begin_pass(p->app, &d);
  p->in_pass = true;
  p->current_n_color_targets = 1;
  p->current_color_fmts[0] = use_fmt;
  // Swapchain passes use the default depth/stencil attachment; offscreen
  // render-target passes need an explicit depth target.
  p->current_has_depth = (target_image == 0);
  p->current_depth_fmt = SGL_PF_DEPTH24_STENCIL8;
}

void pass_state_begin_ex(PassState *p, int n_targets, const uintptr_t *targets,
                         const SglPixelFormat *fmts, int target_w, int target_h,
                         const float (*clears)[4], uintptr_t depth_target,
                         SglPixelFormat depth_fmt, float clear_depth,
                         SglLoadAction load) {
  if (p->in_pass) {
    SDL_Log("begin_pass called while already in pass (nested passes not "
            "supported)");
    return;
  }
  if (n_targets < 0)
    n_targets = 0;
  if (n_targets > SGL_MAX_COLOR_TARGETS)
    n_targets = SGL_MAX_COLOR_TARGETS;
  PassBeginDesc d = {0};
  d.n_color_targets = n_targets;
  d.target_w = target_w;
  d.target_h = target_h;
  d.depth_target = depth_target;
  d.depth_fmt = depth_fmt;
  d.clear_depth = clear_depth;
  d.has_depth = (depth_target != 0);
  d.load = load;
  for (int i = 0; i < n_targets; ++i) {
    d.targets[i] = targets[i];
    d.color_fmts[i] = fmts[i];
    d.clear[i][0] = clears[i][0];
    d.clear[i][1] = clears[i][1];
    d.clear[i][2] = clears[i][2];
    d.clear[i][3] = clears[i][3];
  }
  g_backend->begin_pass(p->app, &d);
  p->in_pass = true;
  p->current_n_color_targets = n_targets;
  for (int i = 0; i < n_targets; ++i) {
    p->current_color_fmts[i] = fmts[i];
  }
  p->current_has_depth = (depth_target != 0);
  p->current_depth_fmt = depth_fmt;
}

void pass_state_begin_mrt(PassState *p, int n_targets, const uintptr_t *targets,
                          const SglPixelFormat *fmts, int target_w,
                          int target_h, const float (*clears)[4]) {
  if (n_targets < 1)
    n_targets = 1;
  if (n_targets > SGL_MAX_COLOR_TARGETS)
    n_targets = SGL_MAX_COLOR_TARGETS;
  pass_state_begin_ex(p, n_targets, targets, fmts, target_w, target_h, clears,
                      0, SGL_PF_DEPTH24_STENCIL8, 1.0f, SGL_LOAD_CLEAR);
}

void pass_state_end(PassState *p) {
  if (!p->in_pass) {
    SDL_Log("end_pass called without matching begin_pass");
    return;
  }
  g_backend->end_pass(p->app);
  p->in_pass = false;
}
