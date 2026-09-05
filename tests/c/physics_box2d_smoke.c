// phys2d の C smoke: App を最小限に組み、C API (include/lub/lub_api.h) を
// 直接回す。GPU も window も Lua も要らない。key の再宣言 / prune /
// accumulator の clamp / 落下と接触を確かめる。
#include "api_internal.h"
#include "physics_box2d.h"

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

static bool near(float a, float b) { return fabsf(a - b) < 0.001f; }

static LubStr S(const char *s) { return lub_str_c(s); }

static LubHandle declare_body(LubContext *ctx, LubHandle world, const char *key,
                              int32_t type, float x, float y, bool version) {
  LubBodyDesc d;
  memset(&d, 0, sizeof(d));
  d.has_version = version;
  d.version = 1;
  d.has_type = true;
  d.type = type;
  d.has_initial = true;
  d.initial.has_x = true;
  d.initial.x = x;
  d.initial.has_y = true;
  d.initial.y = y;
  LubHandle h = 0;
  check(lub_phys2d_body(ctx, world, S(key), &d, &h) == LUB_OK && h != 0,
        "phys2d_body");
  return h;
}

static void declare_box(LubContext *ctx, LubHandle body, const char *key,
                        float hx, float hy, bool version) {
  LubBoxDesc d;
  memset(&d, 0, sizeof(d));
  d.base.has_version = version;
  d.base.version = 1;
  d.base.has_density = true;
  d.base.density = 1.0f;
  d.base.has_contact = true;
  d.base.contact = true;
  d.hx = hx;
  d.hy = hy;
  LubHandle h = 0;
  check(lub_phys2d_box(ctx, body, S(key), &d, &h) == LUB_OK && h != 0,
        "phys2d_box");
}

static LubHandle make_world(LubContext *ctx, const char *key, float gy,
                            int32_t max_steps) {
  LubWorldOpts o;
  memset(&o, 0, sizeof(o));
  o.has_gravity = true;
  o.gravity.y = gy;
  o.has_fixed_dt = true;
  o.fixed_dt = 1.0f / 60.0f;
  o.has_max_steps = true;
  o.max_steps = max_steps;
  LubHandle w = 0;
  check(lub_phys2d_world(ctx, S(key), &o, &w) == LUB_OK && w != 0,
        "phys2d_world");
  return w;
}

int main(void) {
  static App app;
  memset(&app, 0, sizeof(app));
  phys2d_state_init(&app.phys);
  LubContext *ctx = lub_api_ctx(&app);
  LubBeginOpts keep = {true, false}; // prune = false
  LubStepInfo info;
  LubPose pose;

  // key map: 明示 version の body は同じ version の再宣言で作り直されない
  {
    LubHandle w = make_world(ctx, "c_keymap", 0.0f, 1);
    lub_phys2d_begin(ctx, w, &keep);
    LubHandle body = declare_body(ctx, w, "temp", LUB_PHYS2D_BODY_TYPE_DYNAMIC,
                                  1.0f, 0.0f, true);
    declare_box(ctx, body, "solid", 0.1f, 0.1f, true);
    lub_phys2d_step(ctx, w, 0.0f, &info);
    lub_phys2d_begin(ctx, w, &keep);
    body = declare_body(ctx, w, "temp", LUB_PHYS2D_BODY_TYPE_DYNAMIC, 3.0f,
                        0.0f, true);
    declare_box(ctx, body, "solid", 0.1f, 0.1f, true);
    lub_phys2d_step(ctx, w, 0.0f, &info);
    check(lub_phys2d_pose_by_key(ctx, w, S("temp"), &pose) == LUB_OK &&
              near(pose.x, 1.0f),
          "key map update recreated explicit-version body");
    lub_phys2d_begin(ctx, w, NULL); // prune
    lub_phys2d_step(ctx, w, 0.0f, &info);
    check(lub_phys2d_pose_by_key(ctx, w, S("temp"), &pose) == LUB_NOT_FOUND,
          "key map prune did not remove body");
  }

  // accumulator clamp
  {
    LubHandle w = make_world(ctx, "c_clamp", 0.0f, 2);
    lub_phys2d_begin(ctx, w, &keep);
    check(lub_phys2d_step(ctx, w, 1.0f, &info) == LUB_OK, "step");
    check(info.steps == 2 && info.dropped, "accumulator clamp did not drop");
  }

  // 落下して接触する
  {
    LubHandle w = make_world(ctx, "c_fall", -9.8f, 4);
    bool touched = false;
    for (int frame = 0; frame < 240 && !touched; ++frame) {
      lub_phys2d_begin(ctx, w, NULL);
      LubHandle ground = declare_body(
          ctx, w, "ground", LUB_PHYS2D_BODY_TYPE_STATIC, 0, -0.25f, false);
      declare_box(ctx, ground, "solid", 4.0f, 0.25f, false);
      LubHandle ball = declare_body(
          ctx, w, "ball", LUB_PHYS2D_BODY_TYPE_DYNAMIC, 0, 2.0f, false);
      declare_box(ctx, ball, "solid", 0.1f, 0.1f, false);
      check(lub_phys2d_step(ctx, w, 1.0f / 60.0f, &info) == LUB_OK, "step");
      const LubContactEvent *events = NULL;
      int32_t n = 0;
      check(lub_phys2d_contacts(ctx, w, NULL, &events, &n) == LUB_OK,
            "contacts");
      for (int32_t i = 0; i < n; ++i)
        if (lub_str_eq(events[i].a.body, "ball") ||
            lub_str_eq(events[i].b.body, "ball"))
          touched = true;
      check(lub_phys2d_pose(ctx, ball, &pose) == LUB_OK, "pose");
    }
    check(touched, "ball never touched the ground");
    check(pose.y < 1.0f, "ball did not fall");
  }

  phys2d_state_shutdown(&app.phys);
  if (g_failures) {
    fprintf(stderr, "phys2d smoke: %d failure(s)\n", g_failures);
    return 1;
  }
  printf("phys2d smoke OK\n");
  return 0;
}
