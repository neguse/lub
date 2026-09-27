#include "api_internal.h"
#include "xr.h"
#include "xr_linear.h"
#include <math.h>
#include <stdio.h>

static int failures;

static void close_to(float actual, float expected) {
  if (!isfinite(actual) || fabsf(actual - expected) > 0.00001f) {
    fprintf(stderr, "XR_MATH: got %.8f, expected %.8f\n", actual, expected);
    failures++;
  }
}

static float project(const float *m, float x, float y, float z, int row) {
  float w = m[12] * x + m[13] * y + m[14] * z + m[15];
  return (m[row * 4] * x + m[row * 4 + 1] * y + m[row * 4 + 2] * z +
          m[row * 4 + 3]) /
         w;
}

int main(void) {
  float matrix[16];
  XrPosef pose = {.orientation.w = 1};
  XrFovf fov = {-0.7f, 0.9f, 0.8f, -0.6f};
  lubxr_view_projection(&pose, &fov, .1f, 100, matrix);
  close_to(project(matrix, 0, 0, -.1f, 2), 0);
  close_to(project(matrix, 0, 0, -100, 2), 1);
  close_to(project(matrix, tanf(fov.angleLeft), 0, -1, 0), -1);
  close_to(project(matrix, tanf(fov.angleRight), 0, -1, 0), 1);
  close_to(project(matrix, 0, tanf(fov.angleUp), -1, 1), 1);
  close_to(project(matrix, 0, tanf(fov.angleDown), -1, 1), -1);
  for (int eye = 0; eye < 2; ++eye) {
    pose.position = (XrVector3f){eye ? .032f : -.032f, 1.6f, .5f};
    pose.orientation = (XrQuaternionf){.1f, .2f, .3f, sqrtf(.86f)};
    lubxr_view_projection(&pose, &fov, .05f, 50, matrix);
    XrMatrix4x4f world, view, projection, expected;
    XrMatrix4x4f_CreateFromRigidTransform(&world, &pose);
    XrMatrix4x4f_InvertRigidBody(&view, &world);
    XrMatrix4x4f_CreateProjectionFov(&projection, GRAPHICS_D3D, fov, .05f, 50);
    XrMatrix4x4f_Multiply(&expected, &projection, &view);
    for (int row = 0; row < 4; ++row)
      for (int col = 0; col < 4; ++col)
        close_to(matrix[row * 4 + col], expected.m[col * 4 + row]);
  }
  App app = {0};
  LubXrView view = {0};
  if (lub_xr_get_view(lub_api_ctx(&app), 0, 0, 100, &view) != LUB_ERROR)
    failures++;
  if (lub_xr_get_view(lub_api_ctx(&app), 2, .1f, 100, &view) != LUB_ERROR)
    failures++;
  if (lub_xr_get_view(lub_api_ctx(&app), 0, .1f, NAN, &view) != LUB_ERROR)
    failures++;
  if (lub_xr_get_view(lub_api_ctx(&app), 0, .1f, 100, &view) != LUB_NOT_FOUND)
    failures++;
  if (lub_xr_focused(lub_api_ctx(&app)))
    failures++;
  printf("XR_MATH failures=%d\n", failures);
  return failures ? 1 : 0;
}
