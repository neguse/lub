// phys3d の C smoke: App を最小限に組み、C API (include/lub/lub_api.h) を
// 直接回す。落下と接触、euler の初期回転、raycast を確かめる。
#include "api_internal.h"
#include "physics_box3d.h"

#include <box3d/math_functions.h>

#include <math.h>
#include <stdio.h>
#include <string.h>

static int g_failures;

static void check(bool cond, const char *msg) {
  if (!cond) {
    fprintf(stderr, "FAIL: %s\n", msg);
    g_failures++;
  }
}

static LubStr S(const char *s) { return lub_str_c(s); }

static LubHandle declare_body(LubContext *ctx, LubHandle world, const char *key,
                              int32_t type, float x, float y, float z) {
  LubBodyDesc3d d;
  memset(&d, 0, sizeof(d));
  d.has_type = true;
  d.type = type;
  d.has_initial = true;
  d.initial.has_x = true;
  d.initial.x = x;
  d.initial.has_y = true;
  d.initial.y = y;
  d.initial.has_z = true;
  d.initial.z = z;
  d.initial.has_euler = true;
  d.initial.euler.y = 0.5f;
  LubHandle h = 0;
  check(lub_phys3d_body(ctx, world, S(key), &d, &h) == LUB_OK && h != 0,
        "phys3d_body");
  return h;
}

static void declare_box(LubContext *ctx, LubHandle body, const char *key,
                        float hx, float hy, float hz) {
  LubBoxDesc3d d;
  memset(&d, 0, sizeof(d));
  d.base.has_density = true;
  d.base.density = 1.0f;
  d.base.has_contact = true;
  d.base.contact = true;
  d.hx = hx;
  d.hy = hy;
  d.hz = hz;
  LubHandle h = 0;
  check(lub_phys3d_box(ctx, body, S(key), &d, &h) == LUB_OK && h != 0,
        "phys3d_box");
}

int main(void) {
  static App app;
  memset(&app, 0, sizeof(app));
  phys3d_state_init(&app.phys3);
  LubContext *ctx = lub_api_ctx(&app);

  LubWorldOpts3d o;
  memset(&o, 0, sizeof(o));
  o.has_fixed_dt = true;
  o.fixed_dt = 1.0f / 60.0f;
  LubHandle w = 0;
  check(lub_phys3d_world(ctx, S("c_fall"), &o, &w) == LUB_OK && w != 0,
        "phys3d_world");

  LubStepInfo3d info;
  LubPose3d pose;
  memset(&pose, 0, sizeof(pose));
  bool touched = false;
  LubHandle ball = 0;
  for (int frame = 0; frame < 240 && !touched; ++frame) {
    lub_phys3d_begin(ctx, w, NULL);
    LubHandle ground = declare_body(ctx, w, "ground",
                                    LUB_PHYS3D_BODY_TYPE_STATIC, 0, -0.25f, 0);
    declare_box(ctx, ground, "solid", 4.0f, 0.25f, 4.0f);
    ball =
        declare_body(ctx, w, "ball", LUB_PHYS3D_BODY_TYPE_DYNAMIC, 0, 2.0f, 0);
    declare_box(ctx, ball, "solid", 0.1f, 0.1f, 0.1f);
    check(lub_phys3d_step(ctx, w, 1.0f / 60.0f, &info) == LUB_OK, "step");
    const LubContactEvent3d *events = NULL;
    int32_t n = 0;
    check(lub_phys3d_contacts(ctx, w, NULL, &events, &n) == LUB_OK, "contacts");
    for (int32_t i = 0; i < n; ++i)
      if (lub_str_eq(events[i].a.body, "ball") ||
          lub_str_eq(events[i].b.body, "ball"))
        touched = true;
    check(lub_phys3d_pose(ctx, ball, &pose) == LUB_OK, "pose");
  }
  check(touched, "ball never touched the ground");
  check(pose.y < 1.0f, "ball did not fall");
  // euler.y = 0.5 の初期回転が四元数に写っている (静止 body で確かめる)
  LubPose3d gpose;
  LubHandle ground = lub_phys3d_find_body(ctx, w, S("ground"));
  check(ground != 0 && lub_phys3d_pose(ctx, ground, &gpose) == LUB_OK,
        "ground pose");
  // Box3D の近似 cos / sin で作った四元数と一致する (旧 Lua binding と同じ)
  b3Quat expect = b3MakeQuatFromAxisAngle(b3Vec3_axisY, 0.5f);
  if (!(fabsf(gpose.qy - expect.v.y) < 1e-6f &&
        fabsf(gpose.qw - expect.s) < 1e-6f)) {
    fprintf(stderr, "ground rotation: %f %f %f %f (expected 0 %f 0 %f)\n",
            gpose.qx, gpose.qy, gpose.qz, gpose.qw, expect.v.y, expect.s);
    check(false, "euler initial rotation");
  }

  // 上から下への raycast が地面に当たる
  LubRaycastDesc3d ray;
  memset(&ray, 0, sizeof(ray));
  ray.has_x = ray.has_y = ray.has_z = true;
  ray.x = 2.0f;
  ray.y = 3.0f;
  ray.z = 0.0f;
  ray.has_dx = ray.has_dy = ray.has_dz = true;
  ray.dy = -10.0f;
  LubRayHit3d hit;
  bool has = false;
  check(lub_phys3d_raycast(ctx, w, &ray, &hit, &has) == LUB_OK && has,
        "raycast hit");
  check(has && lub_str_eq(hit.base.body, "ground"), "raycast hit ground");

  phys3d_state_shutdown(&app.phys3);
  if (g_failures) {
    fprintf(stderr, "phys3d smoke: %d failure(s)\n", g_failures);
    return 1;
  }
  printf("phys3d smoke OK\n");
  return 0;
}
