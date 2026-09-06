#ifndef LUB_SURFACENETS_H
#define LUB_SURFACENETS_H

#include <stddef.h>
#include <stdint.h>

// Naive surface nets: SDF grid -> triangle mesh. C API は
// include/lub/lub_api.h の lub_mesh_surface_nets、Lua binding は
// src/lua_api.c。
//
// grid is a flat float array of nx*ny*nz signed distances, x fastest:
// grid[x + y*nx + z*nx*ny] is the sample at world position
// (ox + x*cell, oy + y*cell, oz + z*cell). Negative = inside.
//
//   positions   flat float array {x0,y0,z0, ...}
//   normals     flat float array, from the trilinearly sampled grid gradient
//   indices     flat int array, 0-based vertex indices, CCW seen from outside
//   vert_count / index_count
//
// sn_mesh_from_grid returns 0 on OOM; free with sn_mesh_free.
typedef struct {
  float *positions; // 3 * vert_count
  float *normals;   // 3 * vert_count
  int32_t *indices; // index_count, 0-based
  size_t vert_count;
  size_t index_count;
} SnMesh;

int sn_mesh_from_grid(const float *s, int nx, int ny, int nz, float cell,
                      float ox, float oy, float oz, SnMesh *out);
void sn_mesh_free(SnMesh *m);

#endif
