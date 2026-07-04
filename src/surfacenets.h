#ifndef LUB_SURFACENETS_H
#define LUB_SURFACENETS_H

#include <stddef.h>
#include <stdint.h>

// Naive surface nets: SDF grid (Lua float table) -> triangle mesh table.
//
//   surface_nets(grid, nx, ny, nz [, cell [, ox, oy, oz]]) -> mesh
//
// grid is a 1-indexed flat float table of nx*ny*nz signed distances,
// x fastest: grid[1 + x + y*nx + z*nx*ny] is the sample at world position
// (ox + x*cell, oy + y*cell, oz + z*cell). Negative = inside. cell defaults
// to 1.0 and ox/oy/oz to 0.
//
// The mesh table follows the load_gltf convention (src/gltf.h) so it plugs
// into Io.interleavePn / use_buffer directly:
//   positions   flat float array {x0,y0,z0, ...}, 1-indexed
//   normals     flat float array, from the trilinearly sampled grid gradient
//   indices     flat int array, 0-based vertex indices, CCW seen from outside
//   vert_count / index_count
struct lua_State;

int lub_surface_nets(struct lua_State *L);

// C-side core shared with sdf.c (which meshes an SDF tree without a Lua
// grid round trip). sn_mesh_from_grid returns 0 on OOM; free with
// sn_mesh_free. sn_mesh_push pushes the mesh table described above.
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
void sn_mesh_push(struct lua_State *L, const SnMesh *m);

#endif
