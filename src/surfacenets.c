#include "surfacenets.h"

#include <math.h>
#include <stdint.h>
#include <stdlib.h>

// Naive surface nets (see surfacenets.h for the Lua API):
//   pass 1: every cell whose 8 corners mix signs gets one vertex at the
//           average of its edge crossings.
//   pass 2: every grid edge with a sign change joins the 4 cells around it
//           into a quad (2 triangles), wound CCW seen from the outside
//           (positive-distance side).
// Normals come from the central-difference gradient of the grid, trilinearly
// sampled at the vertex position, so a coarse grid still shades smoothly.

// corner order: bit0 = +x, bit1 = +y, bit2 = +z
static const int EDGE_CORNERS[12][2] = {
    {0, 1}, {2, 3}, {4, 5}, {6, 7}, // x edges
    {0, 2}, {1, 3}, {4, 6}, {5, 7}, // y edges
    {0, 4}, {1, 5}, {2, 6}, {3, 7}, // z edges
};

// Trilinear sample of the grid at float coords, clamped to the grid.
static float grid_sample(const float *s, int nx, int ny, int nz, float x,
                         float y, float z) {
  if (x < 0)
    x = 0;
  if (y < 0)
    y = 0;
  if (z < 0)
    z = 0;
  if (x > (float)(nx - 1))
    x = (float)(nx - 1);
  if (y > (float)(ny - 1))
    y = (float)(ny - 1);
  if (z > (float)(nz - 1))
    z = (float)(nz - 1);
  int x0 = (int)x, y0 = (int)y, z0 = (int)z;
  if (x0 > nx - 2)
    x0 = nx - 2;
  if (y0 > ny - 2)
    y0 = ny - 2;
  if (z0 > nz - 2)
    z0 = nz - 2;
  float fx = x - (float)x0, fy = y - (float)y0, fz = z - (float)z0;
  const float *p = s + (size_t)x0 + (size_t)y0 * nx + (size_t)z0 * nx * ny;
  size_t sy = (size_t)nx, sz = (size_t)nx * ny;
  float c00 = p[0] + (p[1] - p[0]) * fx;
  float c10 = p[sy] + (p[sy + 1] - p[sy]) * fx;
  float c01 = p[sz] + (p[sz + 1] - p[sz]) * fx;
  float c11 = p[sz + sy] + (p[sz + sy + 1] - p[sz + sy]) * fx;
  float c0 = c00 + (c10 - c00) * fy;
  float c1 = c01 + (c11 - c01) * fy;
  return c0 + (c1 - c0) * fz;
}

typedef struct {
  float *data;
  size_t len, cap;
} FloatVec;

typedef struct {
  int32_t *data;
  size_t len, cap;
} IntVec;

static int fv_push3(FloatVec *v, float a, float b, float c) {
  if (v->len + 3 > v->cap) {
    size_t cap = v->cap ? v->cap * 2 : 1024;
    float *p = (float *)realloc(v->data, cap * sizeof(float));
    if (!p)
      return 0;
    v->data = p;
    v->cap = cap;
  }
  v->data[v->len++] = a;
  v->data[v->len++] = b;
  v->data[v->len++] = c;
  return 1;
}

static int iv_push3(IntVec *v, int32_t a, int32_t b, int32_t c) {
  if (v->len + 3 > v->cap) {
    size_t cap = v->cap ? v->cap * 2 : 1024;
    int32_t *p = (int32_t *)realloc(v->data, cap * sizeof(int32_t));
    if (!p)
      return 0;
    v->data = p;
    v->cap = cap;
  }
  v->data[v->len++] = a;
  v->data[v->len++] = b;
  v->data[v->len++] = c;
  return 1;
}

void sn_mesh_free(SnMesh *m) {
  free(m->positions);
  free(m->normals);
  free(m->indices);
  m->positions = m->normals = NULL;
  m->indices = NULL;
  m->vert_count = m->index_count = 0;
}

int sn_mesh_from_grid(const float *s, int nx, int ny, int nz, float cell,
                      float ox, float oy, float oz, SnMesh *out) {
  out->positions = out->normals = NULL;
  out->indices = NULL;
  out->vert_count = out->index_count = 0;

  int cx = nx - 1, cy = ny - 1, cz = nz - 1;
  size_t ncells = (size_t)cx * cy * cz;
  int32_t *cellv = (int32_t *)malloc(ncells * sizeof(int32_t));
  FloatVec pos = {0}, nrm = {0};
  IntVec idx = {0};
  if (!cellv)
    goto oom;
  for (size_t i = 0; i < ncells; ++i)
    cellv[i] = -1;

  // pass 1: one vertex per sign-changing cell
  for (int z = 0; z < cz; ++z) {
    for (int y = 0; y < cy; ++y) {
      for (int x = 0; x < cx; ++x) {
        float v[8];
        int mask = 0;
        for (int c = 0; c < 8; ++c) {
          size_t si = (size_t)(x + (c & 1)) +
                      (size_t)(y + ((c >> 1) & 1)) * nx +
                      (size_t)(z + ((c >> 2) & 1)) * nx * ny;
          v[c] = s[si];
          if (v[c] < 0.0f)
            mask |= 1 << c;
        }
        if (mask == 0 || mask == 0xFF)
          continue;

        // average of edge crossings, in grid coords relative to the cell
        float px = 0, py = 0, pz = 0;
        int crossings = 0;
        for (int e = 0; e < 12; ++e) {
          int a = EDGE_CORNERS[e][0], b = EDGE_CORNERS[e][1];
          if ((v[a] < 0.0f) == (v[b] < 0.0f))
            continue;
          float t = v[a] / (v[a] - v[b]);
          px += (float)(a & 1) + t * (float)((b & 1) - (a & 1));
          py += (float)((a >> 1) & 1) +
                t * (float)(((b >> 1) & 1) - ((a >> 1) & 1));
          pz += (float)((a >> 2) & 1) +
                t * (float)(((b >> 2) & 1) - ((a >> 2) & 1));
          crossings++;
        }
        float gx = (float)x + px / (float)crossings;
        float gy = (float)y + py / (float)crossings;
        float gz = (float)z + pz / (float)crossings;

        // normal = normalized grid gradient at the vertex (central diff,
        // trilinear). h = half a cell in grid units.
        const float h = 0.5f;
        float dx = grid_sample(s, nx, ny, nz, gx + h, gy, gz) -
                   grid_sample(s, nx, ny, nz, gx - h, gy, gz);
        float dy = grid_sample(s, nx, ny, nz, gx, gy + h, gz) -
                   grid_sample(s, nx, ny, nz, gx, gy - h, gz);
        float dz = grid_sample(s, nx, ny, nz, gx, gy, gz + h) -
                   grid_sample(s, nx, ny, nz, gx, gy, gz - h);
        float len = sqrtf(dx * dx + dy * dy + dz * dz);
        if (len > 0) {
          dx /= len;
          dy /= len;
          dz /= len;
        } else {
          dx = 0;
          dy = 1;
          dz = 0;
        }

        cellv[(size_t)x + (size_t)y * cx + (size_t)z * cx * cy] =
            (int32_t)(pos.len / 3);
        if (!fv_push3(&pos, ox + gx * cell, oy + gy * cell, oz + gz * cell) ||
            !fv_push3(&nrm, dx, dy, dz))
          goto oom;
      }
    }
  }

  // pass 2: quad per sign-changing grid edge, joining the 4 adjacent cells.
  // For axis d with (d,u,v) a cyclic (right-handed) triple, walking the
  // cells in (u,v) order (0,0)->(1,0)->(1,1)->(0,1) is CCW seen from +d, so
  // it faces outside when the inside is at the low end of the edge.
  int n[3] = {nx, ny, nz};
  int cdim[3] = {cx, cy, cz};
  for (int d = 0; d < 3; ++d) {
    int u = (d + 1) % 3, v = (d + 2) % 3;
    int p[3];
    for (p[2] = 0; p[2] < nz; ++p[2]) {
      for (p[1] = 0; p[1] < ny; ++p[1]) {
        for (p[0] = 0; p[0] < nx; ++p[0]) {
          if (p[d] >= n[d] - 1)
            continue;
          if (p[u] < 1 || p[u] >= n[u] - 1 || p[v] < 1 || p[v] >= n[v] - 1)
            continue;
          size_t i0 = (size_t)p[0] + (size_t)p[1] * nx + (size_t)p[2] * nx * ny;
          size_t step = d == 0 ? 1 : (d == 1 ? (size_t)nx : (size_t)nx * ny);
          float s0 = s[i0], s1 = s[i0 + step];
          if ((s0 < 0.0f) == (s1 < 0.0f))
            continue;

          int32_t q[4]; // cells at (u,v) offsets (0,0) (1,0) (1,1) (0,1)
          static const int OFF[4][2] = {{0, 0}, {1, 0}, {1, 1}, {0, 1}};
          for (int k = 0; k < 4; ++k) {
            int c[3];
            c[d] = p[d];
            c[u] = p[u] - 1 + OFF[k][0];
            c[v] = p[v] - 1 + OFF[k][1];
            q[k] = cellv[(size_t)c[0] + (size_t)c[1] * cdim[0] +
                         (size_t)c[2] * cdim[0] * cdim[1]];
          }
          int ok;
          if (s0 < 0.0f)
            ok = iv_push3(&idx, q[0], q[1], q[2]) &&
                 iv_push3(&idx, q[0], q[2], q[3]);
          else
            ok = iv_push3(&idx, q[0], q[2], q[1]) &&
                 iv_push3(&idx, q[0], q[3], q[2]);
          if (!ok)
            goto oom;
        }
      }
    }
  }

  free(cellv);
  out->positions = pos.data;
  out->normals = nrm.data;
  out->indices = idx.data;
  out->vert_count = pos.len / 3;
  out->index_count = idx.len;
  return 1;

oom:
  free(cellv);
  free(pos.data);
  free(nrm.data);
  free(idx.data);
  return 0;
}
