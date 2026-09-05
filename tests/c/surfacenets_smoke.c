// surface_nets smoke: mesh a sphere SDF grid and check the output mesh —
// vertices sit on the sphere, normals are radial, indices are in range and
// wound CCW seen from outside, and an all-outside grid yields an empty mesh.
// C core (sn_mesh_from_grid) を直接叩く。Lua 面は lub 本体の Lua テストが見る。
#include "surfacenets.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>

static int fail(const char *msg) {
  fprintf(stderr, "surfacenets smoke FAILED: %s\n", msg);
  return 1;
}

int main(void) {
  const int n = 24;
  const float r = 0.5f, half = 0.75f;
  const float cell = 2 * half / (float)(n - 1);
  float *g = (float *)malloc((size_t)n * n * n * sizeof(float));
  if (!g)
    return fail("out of memory");
  size_t i = 0;
  for (int z = 0; z < n; ++z) {
    float pz = -half + (float)z * cell;
    for (int y = 0; y < n; ++y) {
      float py = -half + (float)y * cell;
      for (int x = 0; x < n; ++x) {
        float px = -half + (float)x * cell;
        g[i++] = sqrtf(px * px + py * py + pz * pz) - r;
      }
    }
  }
  SnMesh m = {0};
  if (!sn_mesh_from_grid(g, n, n, n, cell, -half, -half, -half, &m))
    return fail("sn_mesh_from_grid failed");
  free(g);
  if (m.vert_count == 0)
    return fail("no vertices");
  if (m.index_count == 0 || m.index_count % 3 != 0)
    return fail("index_count not a positive multiple of 3");
  for (size_t v = 0; v < m.vert_count; ++v) {
    float px = m.positions[v * 3], py = m.positions[v * 3 + 1],
          pz = m.positions[v * 3 + 2];
    float len = sqrtf(px * px + py * py + pz * pz);
    if (fabsf(len - r) >= cell)
      return fail("vertex off the sphere surface");
    float nx = m.normals[v * 3], ny = m.normals[v * 3 + 1],
          nz = m.normals[v * 3 + 2];
    float nlen = sqrtf(nx * nx + ny * ny + nz * nz);
    if (fabsf(nlen - 1) >= 0.001f)
      return fail("normal not unit length");
    if ((nx * px + ny * py + nz * pz) / len <= 0.9f)
      return fail("normal not radial");
  }
  for (size_t k = 0; k < m.index_count; ++k)
    if (m.indices[k] < 0 || (size_t)m.indices[k] >= m.vert_count)
      return fail("index out of range");
  for (size_t t = 0; t < m.index_count / 3; ++t) {
    const float *a = &m.positions[(size_t)m.indices[t * 3] * 3];
    const float *b = &m.positions[(size_t)m.indices[t * 3 + 1] * 3];
    const float *c = &m.positions[(size_t)m.indices[t * 3 + 2] * 3];
    float ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
    float vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
    float fx = uy * vz - uz * vy, fy = uz * vx - ux * vz,
          fz = ux * vy - uy * vx;
    float mx = (a[0] + b[0] + c[0]) / 3, my = (a[1] + b[1] + c[1]) / 3,
          mz = (a[2] + b[2] + c[2]) / 3;
    if (fx * mx + fy * my + fz * mz <= 0)
      return fail("triangle winding is not CCW seen from outside");
  }
  sn_mesh_free(&m);

  float g2[8];
  for (int k = 0; k < 8; ++k)
    g2[k] = 1;
  SnMesh m2 = {0};
  if (!sn_mesh_from_grid(g2, 2, 2, 2, 1.0f, 0, 0, 0, &m2))
    return fail("sn_mesh_from_grid (empty) failed");
  if (m2.vert_count != 0 || m2.index_count != 0)
    return fail("all-outside grid should produce an empty mesh");
  sn_mesh_free(&m2);
  printf("surfacenets smoke OK\n");
  return 0;
}
