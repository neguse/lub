#ifdef __EMSCRIPTEN__
#include "webxr.h"
#include <emscripten/emscripten.h>

// clang-format off
EM_JS(bool, lubwebxr_active, (void), { return !!Module.lubXR; })
EM_JS(bool, lubwebxr_focused, (void), {
  return !!(Module.lubXR && Module.lubXR.focused);
})
EM_JS(void, lubwebxr_present, (bool rendered), {
  if (Module.lubXR) Module.lubXR.rendered = !!rendered;
})
EM_JS(bool, read_view, (int eye, float near_plane, float far_plane,
  int *size, float *position, float *orientation, float *matrix), {
  var state = Module.lubXR, view = state && state.views && state.views[eye];
  if (!view) return false;
  HEAP32[size >> 2] = state.width;
  HEAP32[(size >> 2) + 1] = state.height;
  var pose = view.transform.position, rotation = view.transform.orientation;
  HEAPF32.set([pose.x, pose.y, pose.z], position >> 2);
  HEAPF32.set([rotation.x, rotation.y, rotation.z, rotation.w], orientation >> 2);
  var p = view.projectionMatrix, v = view.transform.inverse.matrix;
  for (var row = 0; row < 4; row++) for (var column = 0; column < 4; column++) {
    var value = 0;
    for (var k = 0; k < 4; k++) {
      var coefficient = row == 2 ? (k == 2 ? far_plane / (near_plane - far_plane) :
        k == 3 ? near_plane * far_plane / (near_plane - far_plane) : 0) : p[k * 4 + row];
      value += coefficient * v[column * 4 + k];
    }
    HEAPF32[(matrix >> 2) + row * 4 + column] = value;
  }
  return true;
})
EM_JS(bool, read_input, (int hand, float *values, unsigned char *buttons), {
  var state = Module.lubXR, input = state && state.inputs && state.inputs[hand];
  if (!state || !state.focused || !input || !input.active) return false;
  HEAPF32.set([input.stickX, input.stickY, input.trigger, input.grip], values >> 2);
  HEAPU8.set([input.primary, input.secondary, input.menu, input.stickClick], buttons);
  return true;
})
// clang-format on

bool lubwebxr_view(int eye, float near_plane, float far_plane, LubXrView *out) {
  int size[2];
  if (!read_view(eye, near_plane, far_plane, size, out->position,
                 out->orientation, out->view_projection))
    return false;
  out->width = size[0];
  out->height = size[1];
  return true;
}

bool lubwebxr_input(int hand, LubXrInput *out) {
  if (!lubwebxr_active())
    return false;
  float values[4] = {0};
  unsigned char buttons[4] = {0};
  out->active = read_input(hand, values, buttons);
  out->stick_x = values[0];
  out->stick_y = values[1];
  out->trigger = values[2];
  out->grip = values[3];
  out->primary = buttons[0];
  out->secondary = buttons[1];
  out->menu = buttons[2];
  out->stick_click = buttons[3];
  return true;
}

void lubwebxr_begin(void) { lubwebxr_present(false); }
#endif
