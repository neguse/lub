// mesh (surface_nets / sdf) の C API。結果は frame の終わりまで有効な view で返す (実体は呼び出しごとに作り、frame garbage に預ける)。
#include "api_internal.h"
#include "sdf.h"
#include "surfacenets.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// sdf_mesh の view のうち SdfMeshOut に無いもの (bone、bounds、int の joints)。
typedef struct SdfMeshExtra {
  LubSdfBone bones[SDF_MAX_PARTS];
  SdfPart parts[SDF_MAX_PARTS]; // bones[].name の実体
  float bounds_min[3], bounds_max[3];
  int32_t joints[]; // sdf の joints (float) を int32 に写したもの
} SdfMeshExtra;

// SnMesh の配列を frame の終わりまで預ける (m は空になる)。
void lub_mesh_frame_garbage(App *app, SnMesh *m) {
  app_frame_garbage_push(app, m->positions);
  app_frame_garbage_push(app, m->normals);
  app_frame_garbage_push(app, m->indices);
  memset(m, 0, sizeof(*m));
}

static void sdf_frame_garbage(App *app, SdfMeshOut *o) {
  lub_mesh_frame_garbage(app, &o->mesh);
  app_frame_garbage_push(app, o->colors);
  app_frame_garbage_push(app, o->metal_rough);
  app_frame_garbage_push(app, o->joints);
  app_frame_garbage_push(app, o->weights);
  memset(o, 0, sizeof(*o));
}

// SnMesh を LubMeshData の view にする (positions / normals / indices)。
void lub_mesh_view_from_sn(const SnMesh *m, LubMeshData *out) {
  memset(out, 0, sizeof(*out));
  out->positions = m->positions;
  out->positions_count = (int32_t)m->vert_count * 3;
  out->normals = m->normals;
  out->normals_count = m->normals ? (int32_t)m->vert_count * 3 : 0;
  out->indices = (const int32_t *)m->indices;
  out->indices_count = (int32_t)m->index_count;
  out->vert_count = (int32_t)m->vert_count;
  out->index_count = (int32_t)m->index_count;
}

LubStatus lub_mesh_surface_nets(LubContext *ctx, const float *grid,
                                int32_t grid_count, int32_t nx, int32_t ny,
                                int32_t nz, const float *cell, const float *ox,
                                const float *oy, const float *oz,
                                LubMeshData *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  if (!grid)
    return lub_api_fail(app, "surface_nets: grid required");
  if (nx < 2 || ny < 2 || nz < 2)
    return lub_api_fail(
        app, "surface_nets: grid dims must be >= 2 (got %dx%dx%d)", nx, ny, nz);
  size_t total = (size_t)nx * (size_t)ny * (size_t)nz;
  if (total > (size_t)1 << 27)
    return lub_api_fail(app, "surface_nets: grid too large (%dx%dx%d)", nx, ny,
                        nz);
  if ((size_t)grid_count < total)
    return lub_api_fail(app, "surface_nets: grid has %d values, need %zu",
                        grid_count, total);
  float c = cell && *cell > 0 ? *cell : 1.0f;
  SnMesh nets = {0};
  if (!sn_mesh_from_grid(grid, nx, ny, nz, c, ox ? *ox : 0.0f, oy ? *oy : 0.0f,
                         oz ? *oz : 0.0f, &nets)) {
    sn_mesh_free(&nets);
    return lub_api_fail(app, "surface_nets: out of memory");
  }
  lub_mesh_view_from_sn(&nets, out);
  lub_mesh_frame_garbage(app, &nets);
  return LUB_OK;
}

LubStatus lub_mesh_sdf_mesh(LubContext *ctx, const LubSdfNodeDesc *nodes,
                            int32_t nodes_count, int32_t root, int32_t n,
                            const float *skin_k, LubMeshData *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  char err[256];
  SdfTree tree;
  if (!sdf_tree_convert(nodes, nodes_count, root, &tree, err, sizeof(err)))
    return lub_api_fail(app, "%s", err);
  SdfMeshOut sdf;
  bool ok =
      sdf_mesh_build(&tree, n, skin_k ? *skin_k : 0.0f, &sdf, err, sizeof(err));
  int32_t vc = ok ? (int32_t)sdf.mesh.vert_count : 0;
  int32_t joint_count = ok && sdf.joints ? vc * 2 : 0;
  SdfMeshExtra *x = NULL;
  if (ok) {
    x = (SdfMeshExtra *)malloc(sizeof(*x) +
                               sizeof(int32_t) * (size_t)joint_count);
    if (!x) {
      ok = false;
      snprintf(err, sizeof(err), "sdf_mesh: out of memory");
    }
  }
  if (!ok) {
    sdf_mesh_out_free(&sdf);
    sdf_tree_free(&tree);
    return lub_api_fail(app, "%s", err);
  }
  // bone の name は SdfTree の parts が持つので写してから解放する
  memcpy(x->parts, tree.parts, sizeof(x->parts));
  int part_count = tree.part_count;
  sdf_tree_free(&tree);
  app_frame_garbage_push(app, x);
  lub_mesh_view_from_sn(&sdf.mesh, out);
  out->colors = sdf.colors;
  out->colors_count = sdf.colors ? vc * 3 : 0;
  out->metal_rough = sdf.metal_rough;
  out->metal_rough_count = sdf.metal_rough ? vc * 2 : 0;
  if (joint_count > 0) {
    for (int32_t i = 0; i < joint_count; ++i)
      x->joints[i] = (int32_t)sdf.joints[i];
    out->joints = x->joints;
    out->joints_count = joint_count;
  }
  out->weights = sdf.weights;
  out->weights_count = sdf.weights ? vc * 2 : 0;
  for (int i = 0; i < part_count; ++i) {
    x->bones[i].name = lub_str_c(x->parts[i].name);
    x->bones[i].x = x->parts[i].pivot[0];
    x->bones[i].y = x->parts[i].pivot[1];
    x->bones[i].z = x->parts[i].pivot[2];
  }
  out->bones = part_count > 0 ? x->bones : NULL;
  out->bones_count = part_count;
  memcpy(x->bounds_min, sdf.mn, sizeof(x->bounds_min));
  memcpy(x->bounds_max, sdf.mx, sizeof(x->bounds_max));
  out->bounds_min = x->bounds_min;
  out->bounds_min_count = 3;
  out->bounds_max = x->bounds_max;
  out->bounds_max_count = 3;
  out->has_cell = true;
  out->cell = sdf.cell;
  sdf_frame_garbage(app, &sdf);
  return LUB_OK;
}
