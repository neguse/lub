#include "api_internal.h"
#include <math.h>
#if defined(LUB_HAS_OPENXR)
#include "xr.h"
#endif

#if defined(LUB_HAS_OPENXR)
static bool enabled(App *app) {
  return strcmp(app->backend_name, "openxr") == 0;
}
#endif

bool lub_xr_focused(LubContext *ctx) {
#if defined(LUB_HAS_OPENXR)
  return enabled(lub_api_app(ctx)) && lubxr_focused();
#else
  (void)ctx;
  return false;
#endif
}

LubStatus lub_xr_get_view(LubContext *ctx, int32_t eye, float near_plane,
                          float far_plane, LubXrView *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  if (eye < 0 || eye > 1 || !isfinite(near_plane) || !isfinite(far_plane) ||
      near_plane <= 0 || far_plane <= near_plane)
    return lub_api_fail(app, "xr.get_view: invalid eye or clip distances");
#if defined(LUB_HAS_OPENXR)
  if (enabled(app) &&
      lubxr_view(eye, near_plane, far_plane, out->view_projection)) {
    lubxr_size(&out->width, &out->height);
    lubxr_pose(eye, out->position, out->orientation);
    return LUB_OK;
  }
#endif
  return LUB_NOT_FOUND;
}

LubStatus lub_xr_select_eye(LubContext *ctx, int32_t eye) {
  App *app = lub_api_app(ctx);
  if (pass_state_in_pass(&app->pass))
    return lub_api_fail(app, "xr.select_eye: finish the current pass first");
#if defined(LUB_HAS_OPENXR)
  if (enabled(app) && lubxr_select_eye(eye))
    return LUB_OK;
#else
  (void)eye;
#endif
  return lub_api_fail(app, "xr.select_eye: eye unavailable");
}

LubStatus lub_xr_get_input(LubContext *ctx, int32_t hand, LubXrInput *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  if (hand < 0 || hand > 1)
    return lub_api_fail(app, "xr.get_input: invalid hand");
#if defined(LUB_HAS_OPENXR)
  if (enabled(app)) {
    float values[4] = {0};
    bool buttons[4] = {0};
    out->active = lubxr_input(hand, values, buttons);
    out->stick_x = values[0];
    out->stick_y = values[1];
    out->trigger = values[2];
    out->grip = values[3];
    out->primary = buttons[0];
    out->secondary = buttons[1];
    out->menu = buttons[2];
    out->stick_click = buttons[3];
    return LUB_OK;
  }
#endif
  return LUB_NOT_FOUND;
}
