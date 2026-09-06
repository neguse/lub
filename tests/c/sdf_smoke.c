// sdf_mesh smoke: mesh SDF trees and check the output — a plain sphere tree
// reproduces the analytic sphere (vertices, normals, winding), a nested
// move+rotate+smin tree stays inside its reported bounds, paint / bones bake
// per-vertex data, and invalid trees are rejected by the converter instead
// of passing silently. C core (sdf_tree_convert / sdf_mesh_build) を直接
// 叩く。Lua 面 (table の木) は lub 本体の Lua テストと golden が見る。
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
  printf("sdf smoke OK\n");
  return 0;
}
