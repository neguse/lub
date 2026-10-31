// sdf_mesh smoke: mesh SDF trees and check the output — a plain sphere tree
// reproduces the analytic sphere (vertices, normals, winding), a nested
// move+rotate+smin tree stays inside its reported bounds, paint / bones bake
// per-vertex data, a round cone matches the hull of its two end spheres, the
// other primitives and operators land on independently derived surfaces, and
// invalid trees are rejected by the converter instead of passing silently. C core (sdf_tree_convert / sdf_mesh_build) を直接叩く。
// C# 面 (SdfNodeDesc の木) は tests/cs と golden (19_sdf) が見る。
#include "sdf.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int fail(const char *msg) {
  fprintf(stderr, "sdf smoke FAILED: %s\n", msg);
  return 1;
}

static LubSdfNodeDesc node(int op, int a, int b, const float *p, int np) {
  LubSdfNodeDesc n;
  memset(&n, 0, sizeof(n));
  n.op = op;
  n.a = a;
  n.b = b;
  for (int i = 0; i < np; ++i)
    n.params[i] = p[i];
  return n;
}

static int build(const LubSdfNodeDesc *nodes, int count, int root, int n,
                 float skin_k, SdfMeshOut *out, char *err, size_t err_size) {
  SdfTree t;
  if (!sdf_tree_convert(nodes, count, root, &t, err, err_size))
    return 0;
  int ok = sdf_mesh_build(&t, n, skin_k, out, err, err_size);
  sdf_tree_free(&t);
  return ok;
}

// --- primitives and operators beyond the original set -----------------------
// Each check meshes one small tree and tests the vertices against geometry
// worked out independently of sdf.c (closed-form surfaces, extents, or the
// placement an operator must produce).

typedef int (*VertCheck)(const float *p, float cell, const float *arg);

// Meshes nodes (root 0) and runs check on every vertex; also returns the
// vertex AABB so callers can compare extents.
static int mesh_check(const LubSdfNodeDesc *nodes, int count, int n,
                      VertCheck check, const float *arg, const char *what,
                      float vmn[3], float vmx[3]) {
  char err[256];
  SdfMeshOut m;
  if (!build(nodes, count, 0, n, 0, &m, err, sizeof(err))) {
    fprintf(stderr, "%s: %s\n", what, err);
    return fail(what);
  }
  if (m.mesh.vert_count == 0)
    return fail(what);
  for (int i = 0; i < 3; ++i) {
    vmn[i] = 1e30f;
    vmx[i] = -1e30f;
  }
  for (size_t v = 0; v < m.mesh.vert_count; ++v) {
    const float *p = &m.mesh.positions[v * 3];
    for (int i = 0; i < 3; ++i) {
      vmn[i] = fminf(vmn[i], p[i]);
      vmx[i] = fmaxf(vmx[i], p[i]);
    }
    if (check && !check(p, m.cell, arg)) {
      fprintf(stderr, "%s: vertex (%g %g %g) off the surface\n", what, p[0],
              p[1], p[2]);
      sdf_mesh_out_free(&m);
      return fail(what);
    }
  }
  sdf_mesh_out_free(&m);
  return 0;
}

static int near_ext(const float vmn[3], const float vmx[3], const float *lo,
                    const float *hi, float tol) {
  for (int i = 0; i < 3; ++i)
    if (fabsf(vmn[i] - lo[i]) > tol || fabsf(vmx[i] - hi[i]) > tol)
      return 0;
  return 1;
}

static float box_dist(float x, float y, float z, float hx, float hy, float hz) {
  float qx = fabsf(x) - hx, qy = fabsf(y) - hy, qz = fabsf(z) - hz;
  float ox = fmaxf(qx, 0), oy = fmaxf(qy, 0), oz = fmaxf(qz, 0);
  return sqrtf(ox * ox + oy * oy + oz * oz) +
         fminf(fmaxf(qx, fmaxf(qy, qz)), 0);
}

// arg: hx hy hz e — rounded box = inner box (h - e) grown by e
static int chk_round_box(const float *p, float cell, const float *a) {
  float d = box_dist(p[0], p[1], p[2], a[0] - a[3], a[1] - a[3], a[2] - a[3]);
  return fabsf(d - a[3]) < cell;
}

// arg: rx ry rz
static int chk_ellipsoid(const float *p, float cell, const float *a) {
  float k = sqrtf(p[0] * p[0] / (a[0] * a[0]) + p[1] * p[1] / (a[1] * a[1]) +
                  p[2] * p[2] / (a[2] * a[2]));
  return fabsf(k - 1) * fminf(a[0], fminf(a[1], a[2])) < cell;
}

// arg: r h e — rounded cylinder = inner cylinder (r - e, h - e) grown by e
static int chk_rounded_cyl(const float *p, float cell, const float *a) {
  float dx = sqrtf(p[0] * p[0] + p[2] * p[2]) - (a[0] - a[2]);
  float dy = fabsf(p[1]) - (a[1] - a[2]);
  float d = sqrtf(fmaxf(dx, 0) * fmaxf(dx, 0) + fmaxf(dy, 0) * fmaxf(dy, 0)) +
            fminf(fmaxf(dx, dy), 0);
  return fabsf(d - a[2]) < cell;
}

// arg: angle ra rb — tube of radius rb around an arc of radius ra in the xy
// plane, centred on +y and spanning +-angle
static int chk_capped_torus(const float *p, float cell, const float *a) {
  float x = fabsf(p[0]), y = p[1], z = p[2];
  float phi = atan2f(x, y);
  float d;
  if (phi <= a[0]) {
    float r = sqrtf(x * x + y * y) - a[1];
    d = sqrtf(r * r + z * z);
  } else {
    float ex = a[1] * sinf(a[0]), ey = a[1] * cosf(a[0]);
    d = sqrtf((x - ex) * (x - ex) + (y - ey) * (y - ey) + z * z);
  }
  return fabsf(d - a[2]) < cell;
}

// arg: s
static int chk_octahedron(const float *p, float cell, const float *a) {
  float l1 = fabsf(p[0]) + fabsf(p[1]) + fabsf(p[2]);
  return fabsf(l1 - a[0]) < 2 * cell;
}

// arg: r h — sphere above the plane y = h
static int chk_cut_sphere(const float *p, float cell, const float *a) {
  float len = sqrtf(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
  float rad = sqrtf(p[0] * p[0] + p[2] * p[2]);
  float w = sqrtf(a[0] * a[0] - a[1] * a[1]);
  int on_sphere = fabsf(len - a[0]) < cell && p[1] > a[1] - cell;
  int on_cap = fabsf(p[1] - a[1]) < cell && rad < w + cell;
  return on_sphere || on_cap;
}

// arg: angle r — sector of a sphere around +y
static int chk_solid_angle(const float *p, float cell, const float *a) {
  float qx = sqrtf(p[0] * p[0] + p[2] * p[2]), qy = p[1];
  float len = sqrtf(qx * qx + qy * qy);
  float phi = atan2f(qx, qy);
  int on_sphere = fabsf(len - a[1]) < cell && phi < a[0] + 2 * cell / a[1];
  float side = qx * cosf(a[0]) - qy * sinf(a[0]); // distance to the cone
  int on_cone = fabsf(side) < cell && len < a[1] + cell;
  return on_sphere || on_cone;
}

// arg: w h — square pyramid on y = 0 with apex (0, h, 0)
static int chk_pyramid(const float *p, float cell, const float *a) {
  float w = a[0], h = a[1];
  float m = fmaxf(fabsf(p[0]), fabsf(p[2]));
  int on_base = fabsf(p[1]) < cell && m < w + cell;
  float slope = (m - w * (1 - p[1] / h)) * h / sqrtf(h * h + w * w);
  int on_side = fabsf(slope) < 1.5f * cell && p[1] > -cell && p[1] < h + cell;
  return on_base || on_side;
}

// arg: hx hy hz r — box rounded by r
static int chk_round_op(const float *p, float cell, const float *a) {
  return fabsf(box_dist(p[0], p[1], p[2], a[0], a[1], a[2]) - a[3]) < cell;
}

// arg: r t — sphere shell between r - t and r + t
static int chk_onion(const float *p, float cell, const float *a) {
  float len = sqrtf(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
  return fabsf(len - (a[0] + a[1])) < cell || fabsf(len - (a[0] - a[1])) < cell;
}

// arg: h r — sphere r elongated by h along x: capsule from -h to h
static int chk_elongate(const float *p, float cell, const float *a) {
  float cx = fmaxf(-a[0], fminf(a[0], p[0]));
  float d = sqrtf((p[0] - cx) * (p[0] - cx) + p[1] * p[1] + p[2] * p[2]);
  return fabsf(d - a[1]) < cell;
}

// arg: s n r — spheres of radius r at x = k s for k in [-n, n], plus a
// marker sphere of radius r at x = 1.6 that only widens the grid
static int chk_repeat(const float *p, float cell, const float *a) {
  float dm = sqrtf((p[0] - 1.6f) * (p[0] - 1.6f) + p[1] * p[1] + p[2] * p[2]);
  if (fabsf(dm - a[2]) < cell)
    return 1;
  for (int k = -(int)a[1]; k <= (int)a[1]; ++k) {
    float dx = p[0] - k * a[0];
    if (fabsf(sqrtf(dx * dx + p[1] * p[1] + p[2] * p[2]) - a[2]) < cell)
      return 1;
  }
  return 0;
}

// arg: k hx hy hz — box twisted about y: untwist and test the box
static int chk_twist(const float *p, float cell, const float *a) {
  float c = cosf(a[0] * p[1]), s = sinf(a[0] * p[1]);
  float x = c * p[0] - s * p[2], z = s * p[0] + c * p[2];
  return fabsf(box_dist(x, p[1], z, a[1], a[2], a[3])) < 2 * cell;
}

// arg: c1x r1 c2x r2 — inside both spheres (smooth intersection only shrinks)
static int chk_smax(const float *p, float cell, const float *a) {
  float d1 = sqrtf((p[0] - a[0]) * (p[0] - a[0]) + p[1] * p[1] + p[2] * p[2]);
  float d2 = sqrtf((p[0] - a[2]) * (p[0] - a[2]) + p[1] * p[1] + p[2] * p[2]);
  return d1 < a[1] + cell && d2 < a[3] + cell;
}

// arg: c1x r1 c2x r2 — on one of the two sphere surfaces
static int chk_xor(const float *p, float cell, const float *a) {
  float d1 = sqrtf((p[0] - a[0]) * (p[0] - a[0]) + p[1] * p[1] + p[2] * p[2]);
  float d2 = sqrtf((p[0] - a[2]) * (p[0] - a[2]) + p[1] * p[1] + p[2] * p[2]);
  return fabsf(d1 - a[1]) < cell || fabsf(d2 - a[3]) < cell;
}

static int expect_reject(int op, const float *p, int np, const char *what) {
  char err[256];
  LubSdfNodeDesc nodes[] = {node(op, -1, -1, p, np)};
  SdfTree t;
  if (sdf_tree_convert(nodes, 1, 0, &t, err, sizeof(err))) {
    sdf_tree_free(&t);
    return fail(what);
  }
  return 0;
}

static int test_more_ops(void) {
  float mn[3], mx[3];
  // round_box
  {
    float p[] = {0.5f, 0.3f, 0.4f, 0.1f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_ROUND_BOX, -1, -1, p, 4)};
    if (mesh_check(nd, 1, 48, chk_round_box, p, "round_box", mn, mx))
      return 1;
    float lo[] = {-0.5f, -0.3f, -0.4f}, hi[] = {0.5f, 0.3f, 0.4f};
    if (!near_ext(mn, mx, lo, hi, 0.03f))
      return fail("round_box extents");
  }
  // box_frame: only the 12 edges remain, the faces are open
  {
    float p[] = {0.5f, 0.4f, 0.3f, 0.05f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_BOX_FRAME, -1, -1, p, 4)};
    if (mesh_check(nd, 1, 64, NULL, NULL, "box_frame", mn, mx))
      return 1;
    float lo[] = {-0.5f, -0.4f, -0.3f}, hi[] = {0.5f, 0.4f, 0.3f};
    if (!near_ext(mn, mx, lo, hi, 0.03f))
      return fail("box_frame extents");
    SdfMeshOut m;
    char err[256];
    if (!build(nd, 1, 0, 64, 0, &m, err, sizeof(err)))
      return fail(err);
    for (size_t v = 0; v < m.mesh.vert_count; ++v) {
      const float *q = &m.mesh.positions[v * 3];
      int inner = 0; // how many axes are well inside the bars
      for (int i = 0; i < 3; ++i)
        inner += fabsf(q[i]) < p[i] - 2 * p[3] - m.cell;
      if (inner >= 2)
        return fail("box_frame has a vertex on a face");
    }
    sdf_mesh_out_free(&m);
  }
  // ellipsoid
  {
    float p[] = {0.6f, 0.3f, 0.4f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_ELLIPSOID, -1, -1, p, 3)};
    if (mesh_check(nd, 1, 64, chk_ellipsoid, p, "ellipsoid", mn, mx))
      return 1;
    float lo[] = {-0.6f, -0.3f, -0.4f}, hi[] = {0.6f, 0.3f, 0.4f};
    if (!near_ext(mn, mx, lo, hi, 0.03f))
      return fail("ellipsoid extents");
  }
  // capped_cylinder and capped_cone along x: flat caps at both ends
  {
    float pc[] = {-0.5f, 0, 0, 0.5f, 0, 0, 0.25f};
    LubSdfNodeDesc nd[] = {
        node(LUB_MESH_SDF_OP_CAPPED_CYLINDER, -1, -1, pc, 7)};
    if (mesh_check(nd, 1, 48, NULL, NULL, "capped_cylinder", mn, mx))
      return 1;
    float lo[] = {-0.5f, -0.25f, -0.25f}, hi[] = {0.5f, 0.25f, 0.25f};
    if (!near_ext(mn, mx, lo, hi, 0.03f))
      return fail("capped_cylinder extents");
    float pk[] = {-0.5f, 0, 0, 0.5f, 0, 0, 0.3f, 0.1f};
    LubSdfNodeDesc nk[] = {node(LUB_MESH_SDF_OP_CAPPED_CONE, -1, -1, pk, 8)};
    if (mesh_check(nk, 1, 48, NULL, NULL, "capped_cone", mn, mx))
      return 1;
    float klo[] = {-0.5f, -0.3f, -0.3f}, khi[] = {0.5f, 0.3f, 0.3f};
    if (!near_ext(mn, mx, klo, khi, 0.03f))
      return fail("capped_cone extents");
  }
  // rounded_cylinder
  {
    float p[] = {0.4f, 0.3f, 0.1f};
    LubSdfNodeDesc nd[] = {
        node(LUB_MESH_SDF_OP_ROUNDED_CYLINDER, -1, -1, p, 3)};
    if (mesh_check(nd, 1, 48, chk_rounded_cyl, p, "rounded_cylinder", mn, mx))
      return 1;
    float lo[] = {-0.4f, -0.3f, -0.4f}, hi[] = {0.4f, 0.3f, 0.4f};
    if (!near_ext(mn, mx, lo, hi, 0.03f))
      return fail("rounded_cylinder extents");
  }
  // capped_torus: arc of +-1 rad around +y
  {
    float p[] = {1.0f, 0.5f, 0.1f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_CAPPED_TORUS, -1, -1, p, 3)};
    if (mesh_check(nd, 1, 64, chk_capped_torus, p, "capped_torus", mn, mx))
      return 1;
    if (mn[1] < 0.5f * cosf(1.0f) - 0.1f - 0.03f)
      return fail("capped_torus reaches below its arc");
  }
  // link: ring of radius 0.3 stretched by 0.2 along y
  {
    float p[] = {0.2f, 0.3f, 0.08f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_LINK, -1, -1, p, 3)};
    if (mesh_check(nd, 1, 48, NULL, NULL, "link", mn, mx))
      return 1;
    float lo[] = {-0.38f, -0.58f, -0.08f}, hi[] = {0.38f, 0.58f, 0.08f};
    if (!near_ext(mn, mx, lo, hi, 0.03f))
      return fail("link extents");
  }
  // hex_prism: flat top at y = h, corners at x = h / cos 30
  {
    float p[] = {0.4f, 0.2f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_HEX_PRISM, -1, -1, p, 2)};
    if (mesh_check(nd, 1, 48, NULL, NULL, "hex_prism", mn, mx))
      return 1;
    float lo[] = {-0.4619f, -0.4f, -0.2f}, hi[] = {0.4619f, 0.4f, 0.2f};
    if (!near_ext(mn, mx, lo, hi, 0.03f))
      return fail("hex_prism extents");
  }
  // tri_prism: base at y = -h/2, apex at y = h
  {
    float p[] = {0.6f, 0.2f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_TRI_PRISM, -1, -1, p, 2)};
    if (mesh_check(nd, 1, 48, NULL, NULL, "tri_prism", mn, mx))
      return 1;
    float lo[] = {-0.5196f, -0.3f, -0.2f}, hi[] = {0.5196f, 0.6f, 0.2f};
    if (!near_ext(mn, mx, lo, hi, 0.04f))
      return fail("tri_prism extents");
  }
  // octahedron
  {
    float p[] = {0.5f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_OCTAHEDRON, -1, -1, p, 1)};
    if (mesh_check(nd, 1, 48, chk_octahedron, p, "octahedron", mn, mx))
      return 1;
    float lo[] = {-0.5f, -0.5f, -0.5f}, hi[] = {0.5f, 0.5f, 0.5f};
    if (!near_ext(mn, mx, lo, hi, 0.04f))
      return fail("octahedron extents");
  }
  // pyramid
  {
    float p[] = {0.4f, 0.7f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_PYRAMID, -1, -1, p, 2)};
    if (mesh_check(nd, 1, 48, chk_pyramid, p, "pyramid", mn, mx))
      return 1;
    float lo[] = {-0.4f, 0, -0.4f}, hi[] = {0.4f, 0.7f, 0.4f};
    if (!near_ext(mn, mx, lo, hi, 0.04f))
      return fail("pyramid extents");
  }
  // cut_sphere
  {
    float p[] = {0.5f, -0.2f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_CUT_SPHERE, -1, -1, p, 2)};
    if (mesh_check(nd, 1, 48, chk_cut_sphere, p, "cut_sphere", mn, mx))
      return 1;
    float lo[] = {-0.5f, -0.2f, -0.5f}, hi[] = {0.5f, 0.5f, 0.5f};
    if (!near_ext(mn, mx, lo, hi, 0.03f))
      return fail("cut_sphere extents");
  }
  // solid_angle
  {
    float p[] = {0.6f, 0.6f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_SOLID_ANGLE, -1, -1, p, 2)};
    if (mesh_check(nd, 1, 64, chk_solid_angle, p, "solid_angle", mn, mx))
      return 1;
  }
  // rhombus: half-diagonals 0.5 (x) and 0.3 (z), rounded 0.05, h 0.1
  {
    float p[] = {0.5f, 0.3f, 0.1f, 0.05f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_RHOMBUS, -1, -1, p, 4)};
    if (mesh_check(nd, 1, 64, NULL, NULL, "rhombus", mn, mx))
      return 1;
    // the rounded tips pull in from the sharp ones by about r / sin(half angle)
    if (mx[0] > 0.55f + 0.03f || mx[0] < 0.4f || fabsf(mx[1] - 0.1f) > 0.03f)
      return fail("rhombus extents");
  }
  // vesica: pointed ends at x = +-0.5, widest 0.2 at the middle
  {
    float p[] = {-0.5f, 0, 0, 0.5f, 0, 0, 0.2f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_VESICA, -1, -1, p, 7)};
    if (mesh_check(nd, 1, 64, NULL, NULL, "vesica", mn, mx))
      return 1;
    float lo[] = {-0.5f, -0.2f, -0.2f}, hi[] = {0.5f, 0.2f, 0.2f};
    if (!near_ext(mn, mx, lo, hi, 0.04f))
      return fail("vesica extents");
  }

  // mirror_y / mirror_z: a sphere off-centre appears on both sides
  {
    float pr[] = {0.2f}, pm[] = {0, 0.4f, 0.3f};
    for (int axis = 0; axis < 2; ++axis) {
      int op = axis == 0 ? LUB_MESH_SDF_OP_MIRROR_Y : LUB_MESH_SDF_OP_MIRROR_Z;
      LubSdfNodeDesc nd[] = {node(op, 1, -1, NULL, 0),
                             node(LUB_MESH_SDF_OP_MOVE, 2, -1, pm, 3),
                             node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
      if (mesh_check(nd, 3, 48, NULL, NULL, "mirror_y/z", mn, mx))
        return 1;
      int i = axis == 0 ? 1 : 2;
      if (fabsf(mn[i] + mx[i]) > 0.03f || mx[i] < pm[i] + 0.15f)
        return fail("mirror_y/z did not mirror");
    }
  }
  // elongate: sphere stretched along x becomes a capsule
  {
    float pe[] = {0.3f, 0, 0}, pr[] = {0.25f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_ELONGATE, 1, -1, pe, 3),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    float arg[] = {0.3f, 0.25f};
    if (mesh_check(nd, 2, 48, chk_elongate, arg, "elongate", mn, mx))
      return 1;
  }
  // twist: a flat box twisted a quarter turn over its height
  {
    float k = 1.5707963f;
    float pt[] = {k}, pb[] = {0.4f, 0.5f, 0.1f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_TWIST, 1, -1, pt, 1),
                           node(LUB_MESH_SDF_OP_BOX, -1, -1, pb, 3)};
    float arg[] = {k, 0.4f, 0.5f, 0.1f};
    if (mesh_check(nd, 2, 48, chk_twist, arg, "twist", mn, mx))
      return 1;
  }
  // bend: a bar bent upward stays connected and leaves the x axis
  {
    float pk[] = {1.2f}, pb[] = {0.6f, 0.08f, 0.08f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_BEND, 1, -1, pk, 1),
                           node(LUB_MESH_SDF_OP_BOX, -1, -1, pb, 3)};
    if (mesh_check(nd, 2, 48, NULL, NULL, "bend", mn, mx))
      return 1;
    if (mx[1] < 0.15f || mx[0] > 0.6f)
      return fail("bend did not curve the bar");
  }
  // repeat: 5 spheres along x and no more (a far marker sphere widens the
  // grid, so copies past the count would show)
  {
    float pp[] = {0.3f, 0, 0, 2, 0, 0}, pr[] = {0.1f}, pm[] = {1.6f, 0, 0};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_UNION, 1, 3, NULL, 0),
                           node(LUB_MESH_SDF_OP_REPEAT, 2, -1, pp, 6),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1),
                           node(LUB_MESH_SDF_OP_MOVE, 4, -1, pm, 3),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    float arg[] = {0.3f, 2, 0.1f};
    if (mesh_check(nd, 5, 128, chk_repeat, arg, "repeat", mn, mx))
      return 1;
    if (fabsf(mn[0] + 0.7f) > 0.03f)
      return fail("repeat extents");
  }
  // round of a box, keeping the child's paint
  {
    float pr[] = {0.1f}, pc[] = {1, 0, 0, 0, 0.8f}, pb[] = {0.3f, 0.2f, 0.25f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_ROUND, 1, -1, pr, 1),
                           node(LUB_MESH_SDF_OP_PAINT, 2, -1, pc, 5),
                           node(LUB_MESH_SDF_OP_BOX, -1, -1, pb, 3)};
    float arg[] = {0.3f, 0.2f, 0.25f, 0.1f};
    if (mesh_check(nd, 3, 48, chk_round_op, arg, "round", mn, mx))
      return 1;
    SdfMeshOut m;
    char err[256];
    if (!build(nd, 3, 0, 32, 0, &m, err, sizeof(err)))
      return fail(err);
    for (size_t v = 0; v < m.mesh.vert_count; ++v)
      if (fabsf(m.colors[v * 3] - 1) > 1e-5f || m.colors[v * 3 + 1] > 1e-5f)
        return fail("round lost the child's paint");
    sdf_mesh_out_free(&m);
  }
  // onion: a sphere becomes a shell with an inner and an outer surface
  {
    float pt[] = {0.05f}, pr[] = {0.4f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_ONION, 1, -1, pt, 1),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    float arg[] = {0.4f, 0.05f};
    if (mesh_check(nd, 2, 64, chk_onion, arg, "onion", mn, mx))
      return 1;
    SdfMeshOut m;
    char err[256];
    if (!build(nd, 2, 0, 64, 0, &m, err, sizeof(err)))
      return fail(err);
    size_t inner = 0;
    for (size_t v = 0; v < m.mesh.vert_count; ++v) {
      const float *q = &m.mesh.positions[v * 3];
      inner += sqrtf(q[0] * q[0] + q[1] * q[1] + q[2] * q[2]) < 0.4f;
    }
    sdf_mesh_out_free(&m);
    if (inner == 0)
      return fail("onion has no inner surface");
  }
  // smax and xor of two overlapping spheres
  {
    float pk[] = {0.1f}, pr[] = {0.4f}, pl[] = {-0.2f, 0, 0},
          pm[] = {0.2f, 0, 0};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_SMAX, 1, 3, pk, 1),
                           node(LUB_MESH_SDF_OP_MOVE, 2, -1, pl, 3),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1),
                           node(LUB_MESH_SDF_OP_MOVE, 4, -1, pm, 3),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    float arg[] = {-0.2f, 0.4f, 0.2f, 0.4f};
    if (mesh_check(nd, 5, 48, chk_smax, arg, "smax", mn, mx))
      return 1;
    nd[0] = node(LUB_MESH_SDF_OP_XOR, 1, 3, NULL, 0);
    if (mesh_check(nd, 5, 48, chk_xor, arg, "xor", mn, mx))
      return 1;
    // the overlap is hollowed out: some surface lies inside both spheres
    SdfMeshOut m;
    char err[256];
    if (!build(nd, 5, 0, 48, 0, &m, err, sizeof(err)))
      return fail(err);
    size_t inside = 0;
    for (size_t v = 0; v < m.mesh.vert_count; ++v) {
      const float *q = &m.mesh.positions[v * 3];
      float d1 =
          sqrtf((q[0] + 0.2f) * (q[0] + 0.2f) + q[1] * q[1] + q[2] * q[2]);
      float d2 =
          sqrtf((q[0] - 0.2f) * (q[0] - 0.2f) + q[1] * q[1] + q[2] * q[2]);
      inside += d1 < 0.4f - m.cell && d2 < 0.4f + m.cell;
    }
    sdf_mesh_out_free(&m);
    if (inside == 0)
      return fail("xor did not hollow the overlap");
  }
  // a bone under the new point transforms still binds every vertex
  {
    float pp[] = {0.3f, 0, 0, 1, 0, 0}, pb[] = {0, 0, 0}, pr[] = {0.1f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_REPEAT, 1, -1, pp, 6),
                           node(LUB_MESH_SDF_OP_BONE, 2, -1, pb, 3),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    nd[1].name.ptr = "ball";
    nd[1].name.len = 4;
    if (mesh_check(nd, 3, 32, NULL, NULL, "bone under repeat", mn, mx))
      return 1;
  }

  // invalid parameters are rejected
  {
    float rb[] = {0.2f, 0.2f, 0.2f, 0.3f};
    float el[] = {0.2f, 0, 0.2f};
    float cy[] = {0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.2f};
    float cs[] = {0.3f, 0.3f};
    float ve[] = {0, 0, 0, 1, 0, 0, 0};
    float py[] = {0, 0.5f};
    float rc[] = {0.2f, 0.1f, 0.15f};
    if (expect_reject(LUB_MESH_SDF_OP_ROUND_BOX, rb, 4, "round_box e > h") ||
        expect_reject(LUB_MESH_SDF_OP_ELLIPSOID, el, 3, "ellipsoid r = 0") ||
        expect_reject(LUB_MESH_SDF_OP_CAPPED_CYLINDER, cy, 7,
                      "capped_cylinder a == b") ||
        expect_reject(LUB_MESH_SDF_OP_CUT_SPHERE, cs, 2, "cut_sphere h >= r") ||
        expect_reject(LUB_MESH_SDF_OP_VESICA, ve, 7, "vesica w = 0") ||
        expect_reject(LUB_MESH_SDF_OP_PYRAMID, py, 2, "pyramid w = 0") ||
        expect_reject(LUB_MESH_SDF_OP_ROUNDED_CYLINDER, rc, 3,
                      "rounded_cylinder e > h"))
      return 1;
    char err[256];
    float t0[] = {0}, sr[] = {0.2f}, rp[] = {-0.1f, 0, 0, 1, 0, 0};
    LubSdfNodeDesc on[] = {node(LUB_MESH_SDF_OP_ONION, 1, -1, t0, 1),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, sr, 1)};
    LubSdfNodeDesc re[] = {node(LUB_MESH_SDF_OP_REPEAT, 1, -1, rp, 6),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, sr, 1)};
    SdfTree t;
    if (sdf_tree_convert(on, 2, 0, &t, err, sizeof(err)))
      return fail("onion t = 0 accepted");
    if (sdf_tree_convert(re, 2, 0, &t, err, sizeof(err)))
      return fail("repeat s < 0 accepted");
  }
  return 0;
}

// --- domain folds, lathe / extrusion, noise and the hg_sdf joins ------------

static float sph(const float *p, float cx, float cy, float cz, float r) {
  return sqrtf((p[0] - cx) * (p[0] - cx) + (p[1] - cy) * (p[1] - cy) +
               (p[2] - cz) * (p[2] - cz)) -
         r;
}

// arg: n R r — n spheres of radius r on a circle of radius R around y,
// the first one on +x
static int chk_polar(const float *p, float cell, const float *a) {
  for (int k = 0; k < (int)a[0]; ++k) {
    float t = 6.2831853f * k / a[0];
    if (fabsf(sph(p, a[1] * cosf(t), 0, a[1] * sinf(t), a[2])) < cell)
      return 1;
  }
  return 0;
}

// arg: R r — torus about y from revolving a circle of radius r at x = R
static int chk_revolve(const float *p, float cell, const float *a) {
  float q = sqrtf(p[0] * p[0] + p[2] * p[2]) - a[0];
  return fabsf(sqrtf(q * q + p[1] * p[1]) - a[1]) < cell;
}

// arg: r h — disc of radius r in xy extruded to a cylinder along z
static int chk_extrude(const float *p, float cell, const float *a) {
  float dx = sqrtf(p[0] * p[0] + p[1] * p[1]) - a[0];
  float dz = fabsf(p[2]) - a[1];
  float d = sqrtf(fmaxf(dx, 0) * fmaxf(dx, 0) + fmaxf(dz, 0) * fmaxf(dz, 0)) +
            fminf(fmaxf(dx, dz), 0);
  return fabsf(d) < cell;
}

// arg: r amp — sphere displaced by at most amp
static int chk_displace(const float *p, float cell, const float *a) {
  return fabsf(sph(p, 0, 0, 0, a[0])) < a[1] + cell;
}

// Two unit-ish boxes meeting at a right angle: a spans x in [-0.5, 0.5],
// y in [-0.5, 0]; b spans x in [0, 0.5], y in [-0.5, 0.5].
static float ba(const float *p) {
  return box_dist(p[0], p[1] + 0.25f, p[2], 0.5f, 0.25f, 0.3f);
}
static float bb(const float *p) {
  return box_dist(p[0] - 0.25f, p[1], p[2], 0.25f, 0.5f, 0.3f);
}

// arg: r sign — on a, on b, or on the chamfer plane (a + sign * r + b) = 0
static int chk_chamfer(const float *p, float cell, const float *a) {
  float da = ba(p), db = bb(p);
  return fabsf(da) < cell || fabsf(db) < cell ||
         fabsf((da + a[1] * a[0] + db) * 0.70710678f) < cell;
}

// arg: r — union stairs stay within the union grown by r and outside both
// shapes shrunk by a cell
static int chk_stairs(const float *p, float cell, const float *a) {
  float da = ba(p), db = bb(p);
  return fminf(da, db) > -cell && fminf(da, db) < a[0] + cell;
}

static int test_shader_ops(void) {
  float mn[3], mx[3];
  // polar_repeat: 6 spheres around y
  {
    float pn[] = {6}, pm[] = {0.5f, 0, 0}, pr[] = {0.12f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_POLAR_REPEAT, 1, -1, pn, 1),
                           node(LUB_MESH_SDF_OP_MOVE, 2, -1, pm, 3),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    float arg[] = {6, 0.5f, 0.12f};
    if (mesh_check(nd, 3, 64, chk_polar, arg, "polar_repeat", mn, mx))
      return 1;
    // a sphere sits at -x (k = 3) and none at +-z (k = 1.5 is not a copy)
    if (mn[0] > -0.6f || mx[2] > 0.5f * 0.8660254f + 0.12f + 0.03f)
      return fail("polar_repeat placement");
  }
  // revolve: a circle at x = 0.45 spun about y is a torus
  {
    float po[] = {0.45f}, pr[] = {0.15f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_REVOLVE, 1, -1, po, 1),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    float arg[] = {0.45f, 0.15f};
    if (mesh_check(nd, 2, 64, chk_revolve, arg, "revolve", mn, mx))
      return 1;
    float lo[] = {-0.6f, -0.15f, -0.6f}, hi[] = {0.6f, 0.15f, 0.6f};
    if (!near_ext(mn, mx, lo, hi, 0.03f))
      return fail("revolve extents");
  }
  // extrude: a circle in xy pushed along z is a cylinder
  {
    float ph[] = {0.4f}, pr[] = {0.3f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_EXTRUDE, 1, -1, ph, 1),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    float arg[] = {0.3f, 0.4f};
    if (mesh_check(nd, 2, 64, chk_extrude, arg, "extrude", mn, mx))
      return 1;
    // both flat ends are closed
    char err[256];
    SdfMeshOut m;
    if (!build(nd, 2, 0, 64, 0, &m, err, sizeof(err)))
      return fail(err);
    size_t front = 0, back = 0;
    for (size_t v = 0; v < m.mesh.vert_count; ++v) {
      const float *q = &m.mesh.positions[v * 3];
      if (sqrtf(q[0] * q[0] + q[1] * q[1]) < 0.3f - 2 * m.cell) {
        front += fabsf(q[2] + 0.4f) < m.cell;
        back += fabsf(q[2] - 0.4f) < m.cell;
      }
    }
    sdf_mesh_out_free(&m);
    if (front == 0 || back == 0)
      return fail("extrude ends not capped");
  }
  // displace: within amp of the sphere, actually moved somewhere, and the
  // same parameters give the same mesh
  {
    float pd[] = {0.12f, 4, 3, 7}, pr[] = {0.5f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_DISPLACE, 1, -1, pd, 4),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    float arg[] = {0.5f, 0.12f};
    if (mesh_check(nd, 2, 64, chk_displace, arg, "displace", mn, mx))
      return 1;
    char err[256];
    SdfMeshOut m1, m2;
    if (!build(nd, 2, 0, 48, 0, &m1, err, sizeof(err)) ||
        !build(nd, 2, 0, 48, 0, &m2, err, sizeof(err)))
      return fail(err);
    float moved = 0;
    for (size_t v = 0; v < m1.mesh.vert_count; ++v)
      moved =
          fmaxf(moved, fabsf(sph(&m1.mesh.positions[v * 3], 0, 0, 0, 0.5f)));
    if (moved < 2 * m1.cell)
      return fail("displace did not move the surface");
    if (m1.mesh.vert_count != m2.mesh.vert_count ||
        memcmp(m1.mesh.positions, m2.mesh.positions,
               m1.mesh.vert_count * 3 * sizeof(float)) != 0)
      return fail("displace is not deterministic");
    sdf_mesh_out_free(&m1);
    sdf_mesh_out_free(&m2);
  }
  // chamfers: an L of two boxes; every vertex lies on a box or on the
  // 45-degree chamfer plane, and the chamfer plane is actually used
  {
    float r[] = {0.15f};
    float pa[] = {0, -0.25f, 0}, sa[] = {0.5f, 0.25f, 0.3f};
    float pb[] = {0.25f, 0, 0}, sb[] = {0.25f, 0.5f, 0.3f};
    int ops[] = {LUB_MESH_SDF_OP_UNION_CHAMFER,
                 LUB_MESH_SDF_OP_INTERSECT_CHAMFER};
    float sign[] = {-1, 1};
    for (int i = 0; i < 2; ++i) {
      LubSdfNodeDesc nd[] = {node(ops[i], 1, 3, r, 1),
                             node(LUB_MESH_SDF_OP_MOVE, 2, -1, pa, 3),
                             node(LUB_MESH_SDF_OP_BOX, -1, -1, sa, 3),
                             node(LUB_MESH_SDF_OP_MOVE, 4, -1, pb, 3),
                             node(LUB_MESH_SDF_OP_BOX, -1, -1, sb, 3)};
      float arg[] = {0.15f, sign[i]};
      if (mesh_check(nd, 5, 64, chk_chamfer, arg, "chamfer", mn, mx))
        return 1;
      char err[256];
      SdfMeshOut m;
      if (!build(nd, 5, 0, 64, 0, &m, err, sizeof(err)))
        return fail(err);
      size_t on_plane = 0;
      for (size_t v = 0; v < m.mesh.vert_count; ++v) {
        const float *q = &m.mesh.positions[v * 3];
        on_plane += fabsf(ba(q)) > 2 * m.cell && fabsf(bb(q)) > 2 * m.cell;
      }
      sdf_mesh_out_free(&m);
      if (on_plane == 0)
        return fail("chamfer plane missing");
    }
    // subtract_chamfer: a box with a box cut out; the cut edge is bevelled
    float ca[] = {0.5f, 0.3f, 0.3f}, cb[] = {0.2f, 0.5f, 0.5f};
    LubSdfNodeDesc sd[] = {node(LUB_MESH_SDF_OP_SUBTRACT_CHAMFER, 1, 2, r, 1),
                           node(LUB_MESH_SDF_OP_BOX, -1, -1, ca, 3),
                           node(LUB_MESH_SDF_OP_BOX, -1, -1, cb, 3)};
    char err[256];
    SdfMeshOut m;
    if (!build(sd, 3, 0, 64, 0, &m, err, sizeof(err)))
      return fail(err);
    size_t bevel = 0;
    for (size_t v = 0; v < m.mesh.vert_count; ++v) {
      const float *q = &m.mesh.positions[v * 3];
      float da = box_dist(q[0], q[1], q[2], 0.5f, 0.3f, 0.3f);
      float db = box_dist(q[0], q[1], q[2], 0.2f, 0.5f, 0.5f);
      if (!(fabsf(da) < m.cell || fabsf(db) < m.cell ||
            fabsf((da + 0.15f - db) * 0.70710678f) < m.cell))
        return fail("subtract_chamfer vertex off the surface");
      bevel += fabsf(da) > 2 * m.cell && fabsf(db) > 2 * m.cell;
    }
    sdf_mesh_out_free(&m);
    if (bevel == 0)
      return fail("subtract_chamfer bevel missing");
  }
  // union_stairs: the corner fills with steps within r of the union
  {
    float pr[] = {0.2f, 3};
    float pa[] = {0, -0.25f, 0}, sa[] = {0.5f, 0.25f, 0.3f};
    float pb[] = {0.25f, 0, 0}, sb[] = {0.25f, 0.5f, 0.3f};
    LubSdfNodeDesc nd[] = {node(LUB_MESH_SDF_OP_UNION_STAIRS, 1, 3, pr, 2),
                           node(LUB_MESH_SDF_OP_MOVE, 2, -1, pa, 3),
                           node(LUB_MESH_SDF_OP_BOX, -1, -1, sa, 3),
                           node(LUB_MESH_SDF_OP_MOVE, 4, -1, pb, 3),
                           node(LUB_MESH_SDF_OP_BOX, -1, -1, sb, 3)};
    float arg[] = {0.2f};
    if (mesh_check(nd, 5, 64, chk_stairs, arg, "union_stairs", mn, mx))
      return 1;
  }
  // groove / tongue / engrave / pipe: a box cut or grown along a sphere's
  // surface that crosses it
  {
    float sbx[] = {0.5f, 0.2f, 0.5f}, ssp[] = {0.45f}, mv[] = {0, 0.2f, 0};
    float g[] = {0.08f, 0.04f};
    int ops[] = {LUB_MESH_SDF_OP_GROOVE, LUB_MESH_SDF_OP_TONGUE,
                 LUB_MESH_SDF_OP_ENGRAVE, LUB_MESH_SDF_OP_PIPE};
    int np[] = {2, 2, 1, 1};
    for (int i = 0; i < 4; ++i) {
      LubSdfNodeDesc nd[] = {node(ops[i], 1, 2, g, np[i]),
                             node(LUB_MESH_SDF_OP_BOX, -1, -1, sbx, 3),
                             node(LUB_MESH_SDF_OP_MOVE, 3, -1, mv, 3),
                             node(LUB_MESH_SDF_OP_SPHERE, -1, -1, ssp, 1)};
      char err[256];
      SdfMeshOut m;
      if (!build(nd, 4, 0, 64, 0, &m, err, sizeof(err)))
        return fail(err);
      if (m.mesh.vert_count == 0)
        return fail("groove family: empty");
      size_t special = 0;
      for (size_t v = 0; v < m.mesh.vert_count; ++v) {
        const float *q = &m.mesh.positions[v * 3];
        float a = box_dist(q[0], q[1], q[2], 0.5f, 0.2f, 0.5f);
        float b = sph(q, 0, 0.2f, 0, 0.45f);
        float t = m.cell;
        int ok;
        if (ops[i] == LUB_MESH_SDF_OP_GROOVE) // inside a; walls / floor
          ok = a < t && (fabsf(a) < t || fabsf(fabsf(b) - g[1]) < t ||
                         fabsf(a + g[0]) < t);
        else if (ops[i] == LUB_MESH_SDF_OP_TONGUE) // a, or the ridge on top
          ok = a > -t && (fabsf(a) < t || fabsf(fabsf(b) - g[1]) < t ||
                          fabsf(a - g[0]) < t);
        else if (ops[i] == LUB_MESH_SDF_OP_ENGRAVE) // a, or the V cut
          ok = a < t &&
               (fabsf(a) < t || fabsf((a + g[0] - fabsf(b)) * 0.70710678f) < t);
        else // pipe: r from the curve where both surfaces meet
          ok = fabsf(sqrtf(a * a + b * b) - g[0]) < t;
        if (!ok) {
          fprintf(stderr, "op %d vertex (%g %g %g)\n", ops[i], q[0], q[1],
                  q[2]);
          return fail("groove family vertex off the surface");
        }
        special += fabsf(a) > 2 * t;
      }
      float top = -1e30f;
      for (size_t v = 0; v < m.mesh.vert_count; ++v)
        top = fmaxf(top, m.mesh.positions[v * 3 + 1]);
      // the tongue's ridge rises exactly ra above the top face
      if (ops[i] == LUB_MESH_SDF_OP_TONGUE &&
          fabsf(top - (0.2f + g[0])) > m.cell)
        return fail("tongue ridge height");
      sdf_mesh_out_free(&m);
      if (ops[i] != LUB_MESH_SDF_OP_PIPE && special == 0)
        return fail("groove family made no cut or ridge");
    }
  }
  // invalid parameters are rejected
  {
    char err[256];
    SdfTree t;
    float sr[] = {0.2f}, z[] = {0}, d[] = {0.1f, 0, 2, 0}, st[] = {0.1f, 0};
    LubSdfNodeDesc pr[] = {node(LUB_MESH_SDF_OP_POLAR_REPEAT, 1, -1, z, 1),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, sr, 1)};
    LubSdfNodeDesc ex[] = {node(LUB_MESH_SDF_OP_EXTRUDE, 1, -1, z, 1),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, sr, 1)};
    LubSdfNodeDesc dp[] = {node(LUB_MESH_SDF_OP_DISPLACE, 1, -1, d, 4),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, sr, 1)};
    LubSdfNodeDesc us[] = {node(LUB_MESH_SDF_OP_UNION_STAIRS, 1, 1, st, 2),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, sr, 1)};
    LubSdfNodeDesc ch[] = {node(LUB_MESH_SDF_OP_UNION_CHAMFER, 1, 1, z, 1),
                           node(LUB_MESH_SDF_OP_SPHERE, -1, -1, sr, 1)};
    if (sdf_tree_convert(pr, 2, 0, &t, err, sizeof(err)))
      return fail("polar_repeat n = 0 accepted");
    if (sdf_tree_convert(ex, 2, 0, &t, err, sizeof(err)))
      return fail("extrude h = 0 accepted");
    if (sdf_tree_convert(dp, 2, 0, &t, err, sizeof(err)))
      return fail("displace freq = 0 accepted");
    if (sdf_tree_convert(us, 2, 0, &t, err, sizeof(err)))
      return fail("union_stairs n = 0 accepted");
    if (sdf_tree_convert(ch, 2, 0, &t, err, sizeof(err)))
      return fail("union_chamfer r = 0 accepted");
  }
  return 0;
}

int main(void) {
  char err[256];
  const float r = 0.5f;
  // sphere: analytic checks
  {
    float p[] = {r};
    LubSdfNodeDesc nodes[] = {node(LUB_MESH_SDF_OP_SPHERE, -1, -1, p, 1)};
    SdfMeshOut m;
    if (!build(nodes, 1, 0, 32, 0, &m, err, sizeof(err)))
      return fail(err);
    if (m.mesh.vert_count == 0)
      return fail("no vertices");
    if (m.mesh.index_count % 3 != 0)
      return fail("index_count not a multiple of 3");
    if (!(m.cell > 0))
      return fail("cell missing");
    if (m.mn[0] != -r || m.mx[1] != r)
      return fail("sphere bounds");
    for (size_t v = 0; v < m.mesh.vert_count; ++v) {
      const float *pp = &m.mesh.positions[v * 3];
      float len = sqrtf(pp[0] * pp[0] + pp[1] * pp[1] + pp[2] * pp[2]);
      if (fabsf(len - r) >= m.cell)
        return fail("vertex off the sphere surface");
      const float *nn = &m.mesh.normals[v * 3];
      if ((nn[0] * pp[0] + nn[1] * pp[1] + nn[2] * pp[2]) / len <= 0.9f)
        return fail("normal not radial");
      if (fabsf(m.colors[v * 3] - 0.8f) >= 1e-5f)
        return fail("default albedo wrong");
    }
    for (size_t t = 0; t < m.mesh.index_count / 3; ++t) {
      const float *a = &m.mesh.positions[(size_t)m.mesh.indices[t * 3] * 3];
      const float *b = &m.mesh.positions[(size_t)m.mesh.indices[t * 3 + 1] * 3];
      const float *c = &m.mesh.positions[(size_t)m.mesh.indices[t * 3 + 2] * 3];
      float ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
      float vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
      float fx = uy * vz - uz * vy, fy = uz * vx - ux * vz,
            fz = ux * vy - uy * vx;
      if (fx * (a[0] + b[0] + c[0]) + fy * (a[1] + b[1] + c[1]) +
              fz * (a[2] + b[2] + c[2]) <=
          0)
        return fail("winding not CCW from outside");
    }
    sdf_mesh_out_free(&m);
  }
  // round cone: every vertex lies on the hull of the two end spheres (the
  // union of spheres whose center and radius move linearly from a to b), the
  // bounds reach both caps, and equal radii reproduce the capsule
  {
    const float ax = -0.4f, bx = 0.5f, ay = 0.1f, by = -0.2f;
    const float radii[][2] = {{0.3f, 0.08f}, {0.05f, 0.25f}, {0.2f, 0.2f}};
    for (int c = 0; c < 3; ++c) {
      float ra = radii[c][0], rb = radii[c][1];
      float p[] = {ax, ay, 0, bx, by, 0, ra, rb};
      LubSdfNodeDesc nodes[] = {node(LUB_MESH_SDF_OP_ROUND_CONE, -1, -1, p, 8)};
      SdfMeshOut m;
      if (!build(nodes, 1, 0, 48, 0, &m, err, sizeof(err)))
        return fail(err);
      if (m.mesh.vert_count == 0)
        return fail("round cone: no vertices");
      if (fabsf(m.mn[0] - (ax - ra)) > 1e-5f ||
          fabsf(m.mx[0] - (bx + rb)) > 1e-5f)
        return fail("round cone bounds");
      float minx = 1e30f, maxx = -1e30f;
      for (size_t v = 0; v < m.mesh.vert_count; ++v) {
        const float *pp = &m.mesh.positions[v * 3];
        float d = 1e30f;
        for (int i = 0; i <= 400; ++i) {
          float t = i / 400.0f;
          float cx = ax + (bx - ax) * t, cy = ay + (by - ay) * t;
          float dx = pp[0] - cx, dy = pp[1] - cy, dz = pp[2];
          float e = sqrtf(dx * dx + dy * dy + dz * dz) - (ra + (rb - ra) * t);
          d = e < d ? e : d;
        }
        if (fabsf(d) >= m.cell)
          return fail("round cone vertex off the sphere hull");
        minx = pp[0] < minx ? pp[0] : minx;
        maxx = pp[0] > maxx ? pp[0] : maxx;
      }
      if (minx > ax - ra + m.cell || maxx < bx + rb - m.cell)
        return fail("round cone caps missing");
      sdf_mesh_out_free(&m);
    }
    // one sphere containing the other: the hull is the larger sphere
    float pin[] = {0, 0, 0, 0.1f, 0, 0, 0.5f, 0.2f};
    LubSdfNodeDesc inner[] = {node(LUB_MESH_SDF_OP_ROUND_CONE, -1, -1, pin, 8)};
    SdfMeshOut m;
    if (!build(inner, 1, 0, 32, 0, &m, err, sizeof(err)))
      return fail(err);
    for (size_t v = 0; v < m.mesh.vert_count; ++v) {
      const float *pp = &m.mesh.positions[v * 3];
      float len = sqrtf(pp[0] * pp[0] + pp[1] * pp[1] + pp[2] * pp[2]);
      if (fabsf(len - 0.5f) >= m.cell)
        return fail("contained round cone is not the larger sphere");
    }
    sdf_mesh_out_free(&m);
    float pneg[] = {0, 0, 0, 1, 0, 0, -0.1f, 0.2f};
    LubSdfNodeDesc bad[] = {node(LUB_MESH_SDF_OP_ROUND_CONE, -1, -1, pneg, 8)};
    SdfTree t;
    if (sdf_tree_convert(bad, 1, 0, &t, err, sizeof(err)))
      return fail("negative round cone radius accepted");
  }
  // nested move+rotate+smin+mirror: bounds contain every vertex
  {
    float s2 = sqrtf(0.5f);
    float pk[] = {0.2f};
    float pmove[] = {0.4f, 0.6f, -0.2f};
    float prot[] = {0, 0, s2, s2};
    float pbox[] = {0.5f, 0.2f, 0.2f};
    float pcap[] = {0.3f, 0, 0, 0.9f, 0.5f, 0, 0.15f};
    LubSdfNodeDesc nodes[] = {
        node(LUB_MESH_SDF_OP_SMIN, 1, 4, pk, 1),        // 0
        node(LUB_MESH_SDF_OP_MOVE, 2, -1, pmove, 3),    // 1
        node(LUB_MESH_SDF_OP_ROTATE, 3, -1, prot, 4),   // 2
        node(LUB_MESH_SDF_OP_BOX, -1, -1, pbox, 3),     // 3
        node(LUB_MESH_SDF_OP_MIRROR_X, 5, -1, NULL, 0), // 4
        node(LUB_MESH_SDF_OP_CAPSULE, -1, -1, pcap, 7), // 5
    };
    SdfMeshOut m;
    if (!build(nodes, 6, 0, 48, 0, &m, err, sizeof(err)))
      return fail(err);
    if (m.mesh.vert_count == 0)
      return fail("nested tree produced no mesh");
    int mirrored = 0;
    for (size_t v = 0; v < m.mesh.vert_count; ++v) {
      for (int i = 0; i < 3; ++i) {
        float p = m.mesh.positions[v * 3 + i];
        if (p < m.mn[i] - m.cell || p > m.mx[i] + m.cell)
          return fail("vertex outside reported bounds");
      }
      if (m.mesh.positions[v * 3] < -0.3f)
        mirrored = 1;
    }
    if (!mirrored)
      return fail("mirror_x did not mirror");
    sdf_mesh_out_free(&m);
  }
  // scale: sphere r=0.25 scaled by 2 == sphere r=0.5 bounds
  {
    float ps[] = {2.0f};
    float pr[] = {0.25f};
    LubSdfNodeDesc nodes[] = {node(LUB_MESH_SDF_OP_SCALE, 1, -1, ps, 1),
                              node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    SdfMeshOut m;
    if (!build(nodes, 2, 0, 24, 0, &m, err, sizeof(err)))
      return fail(err);
    if (fabsf(m.mx[0] - 0.5f) >= 1e-5f)
      return fail("scale bounds wrong");
    sdf_mesh_out_free(&m);
  }
  // paint: innermost wins, cut face takes cutter's material
  {
    float pk[] = {0.05f};
    float pred[] = {1, 0, 0, 0, 0.8f};
    float pblue[] = {0, 0, 1, 1, 0.1f};
    float pbig[] = {0.5f};
    float pmove[] = {0, 0, -0.5f};
    float psmall[] = {0.15f};
    LubSdfNodeDesc nodes[] = {
        node(LUB_MESH_SDF_OP_SSUB, 1, 3, pk, 1),         // 0
        node(LUB_MESH_SDF_OP_PAINT, 2, -1, pred, 5),     // 1
        node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pbig, 1),   // 2
        node(LUB_MESH_SDF_OP_PAINT, 4, -1, pblue, 5),    // 3
        node(LUB_MESH_SDF_OP_MOVE, 5, -1, pmove, 3),     // 4
        node(LUB_MESH_SDF_OP_SPHERE, -1, -1, psmall, 1), // 5
    };
    SdfMeshOut m;
    if (!build(nodes, 6, 0, 40, 0, &m, err, sizeof(err)))
      return fail(err);
    int reds = 0, blues = 0;
    for (size_t v = 0; v < m.mesh.vert_count; ++v) {
      float cr = m.colors[v * 3], cb = m.colors[v * 3 + 2];
      if (cr > 0.9f && cb < 0.1f)
        reds++;
      if (cb > 0.9f && cr < 0.1f) {
        blues++;
        if (m.metal_rough[v * 2] <= 0.9f)
          return fail("cutter metallic not baked");
      }
    }
    if (reds == 0)
      return fail("painted body color missing");
    if (blues == 0)
      return fail("cut face did not take cutter material");
    sdf_mesh_out_free(&m);
  }
  // bones: 2 部位の重み焼き
  {
    float pl[] = {-0.5f, 0, 0};
    float pml[] = {-0.5f, 0, 0};
    float pr_[] = {0.5f, 0, 0};
    float pmr[] = {0.5f, 0, 0};
    float ps[] = {0.3f};
    LubSdfNodeDesc nodes[] = {
        node(LUB_MESH_SDF_OP_UNION, 1, 4, NULL, 0),  // 0
        node(LUB_MESH_SDF_OP_BONE, 2, -1, pl, 3),    // 1
        node(LUB_MESH_SDF_OP_MOVE, 3, -1, pml, 3),   // 2
        node(LUB_MESH_SDF_OP_SPHERE, -1, -1, ps, 1), // 3
        node(LUB_MESH_SDF_OP_BONE, 5, -1, pr_, 3),   // 4
        node(LUB_MESH_SDF_OP_MOVE, 6, -1, pmr, 3),   // 5
        node(LUB_MESH_SDF_OP_SPHERE, -1, -1, ps, 1), // 6
    };
    nodes[1].name = (LubStr){"left", 4};
    nodes[4].name = (LubStr){"right", 5};
    SdfTree t;
    if (!sdf_tree_convert(nodes, 7, 0, &t, err, sizeof(err)))
      return fail(err);
    if (t.part_count != 2 || strcmp(t.parts[0].name, "left") != 0 ||
        strcmp(t.parts[1].name, "right") != 0)
      return fail("bone names wrong");
    if (t.parts[1].pivot[0] != 0.5f)
      return fail("bone pivot wrong");
    SdfMeshOut m;
    if (!sdf_mesh_build(&t, 32, 0.05f, &m, err, sizeof(err)))
      return fail(err);
    sdf_tree_free(&t);
    if (!m.joints || !m.weights)
      return fail("joints / weights missing");
    for (size_t v = 0; v < m.mesh.vert_count; ++v) {
      float w0 = m.weights[v * 2], w1 = m.weights[v * 2 + 1];
      if (fabsf(w0 + w1 - 1) >= 1e-4f)
        return fail("weights do not sum to 1");
      if (w0 < w1 - 1e-6f)
        return fail("primary weight not dominant");
      float px = m.mesh.positions[v * 3];
      int j0 = (int)m.joints[v * 2];
      if (px < -0.2f && j0 != 0)
        return fail("left vertex bound to wrong bone");
      if (px > 0.2f && j0 != 1)
        return fail("right vertex bound to wrong bone");
    }
    sdf_mesh_out_free(&m);
  }
  {
    LubSdfNodeDesc nodes[66];
    char names[16][8];
    float radius[] = {0.12f};
    for (int i = 0; i < 16; ++i) {
      float pos[] = {(i % 4) * 0.35f, (i / 4) * 0.35f, 0};
      nodes[i * 4] = node(LUB_MESH_SDF_OP_SPHERE, -1, -1, radius, 1);
      nodes[i * 4 + 1] = node(LUB_MESH_SDF_OP_MOVE, i * 4, -1, pos, 3);
      nodes[i * 4 + 2] = node(LUB_MESH_SDF_OP_BONE, i * 4 + 1, -1, pos, 3);
      int len = snprintf(names[i], sizeof(names[i]), "bone%d", i);
      nodes[i * 4 + 2].name = (LubStr){names[i], len};
      nodes[i * 4 + 3] = node(LUB_MESH_SDF_OP_UNION, i * 4 + 2,
                              i ? i * 4 - 1 : i * 4, NULL, 0);
    }
    SdfTree t;
    if (!sdf_tree_convert(nodes, 64, 63, &t, err, sizeof(err)))
      return fail(err);
    if (t.part_count != 16)
      return fail("16-bone palette truncated");
    SdfMeshOut m;
    if (!sdf_mesh_build(&t, 32, 0.025f, &m, err, sizeof(err)))
      return fail(err);
    int seen = 0;
    for (size_t v = 0; v < m.mesh.vert_count; ++v)
      seen |= 1 << (int)m.joints[v * 2];
    if (seen != 65535)
      return fail("mesh lost a bone beyond the first eight");
    sdf_mesh_out_free(&m);
    sdf_tree_free(&t);
    nodes[64] = node(LUB_MESH_SDF_OP_BONE, 0, -1, NULL, 0);
    nodes[64].name = (LubStr){"overflow", 8};
    nodes[65] = node(LUB_MESH_SDF_OP_UNION, 63, 64, NULL, 0);
    if (sdf_tree_convert(nodes, 66, 65, &t, err, sizeof(err)))
      return fail("17th bone did not error");
  }
  // invalid trees must be rejected
  {
    float pk[] = {0.1f};
    float pr[] = {1.0f};
    LubSdfNodeDesc bad_op[] = {node(99, -1, -1, NULL, 0)};
    SdfTree t;
    if (sdf_tree_convert(bad_op, 1, 0, &t, err, sizeof(err)))
      return fail("unknown op did not error");
    LubSdfNodeDesc missing_child[] = {
        node(LUB_MESH_SDF_OP_SMIN, 1, -1, pk, 1),
        node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    if (sdf_tree_convert(missing_child, 2, 0, &t, err, sizeof(err)))
      return fail("missing child did not error");
    float pzero[] = {0.0f};
    LubSdfNodeDesc bad_k[] = {node(LUB_MESH_SDF_OP_SMIN, 1, 1, pzero, 1),
                              node(LUB_MESH_SDF_OP_SPHERE, -1, -1, pr, 1)};
    if (sdf_tree_convert(bad_k, 2, 0, &t, err, sizeof(err)))
      return fail("k <= 0 did not error");
  }
  if (test_more_ops() || test_shader_ops())
    return 1;
  printf("sdf smoke OK\n");
  return 0;
}
