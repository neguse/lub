// mesh (surface_nets / sdf) の C API。結果は runtime が次の mesh_* 呼び出し
// まで持つ view で返す。
#include "api_internal.h"
#include "sdf.h"
#include "surfacenets.h"
#include <stdlib.h>
#include <string.h>

struct MeshScratch {
  SnMesh nets;
  SdfMeshOut sdf;
  LubSdfBone bones[SDF_MAX_PARTS];
  SdfPart parts[SDF_MAX_PARTS]; // bones[].name の実体
  float bounds_min[3], bounds_max[3];
  int32_t *joints; // sdf の joints (float) を int32 に写したもの
  int32_t joints_cap;
};

static struct MeshScratch *mesh_scratch(App *app) {
  if (!app->mesh_scratch)
    app->mesh_scratch =
        (struct MeshScratch *)calloc(1, sizeof(struct MeshScratch));
  return app->mesh_scratch;
}

static void mesh_scratch_clear(struct MeshScratch *s) {
  sn_mesh_free(&s->nets);
  memset(&s->nets, 0, sizeof(s->nets));
  sdf_mesh_out_free(&s->sdf);
}

void api_mesh_shutdown(App *app) {
  if (!app->mesh_scratch)
    return;
  mesh_scratch_clear(app->mesh_scratch);
  free(app->mesh_scratch->joints);
  free(app->mesh_scratch);
  app->mesh_scratch = NULL;
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
  struct MeshScratch *s = mesh_scratch(app);
  if (!s)
    return lub_api_fail(app, "surface_nets: out of memory");
  mesh_scratch_clear(s);
  float c = cell && *cell > 0 ? *cell : 1.0f;
  if (!sn_mesh_from_grid(grid, nx, ny, nz, c, ox ? *ox : 0.0f, oy ? *oy : 0.0f,
                         oz ? *oz : 0.0f, &s->nets))
    return lub_api_fail(app, "surface_nets: out of memory");
  lub_mesh_view_from_sn(&s->nets, out);
  return LUB_OK;
}

LubStatus lub_mesh_sdf_mesh(LubContext *ctx, const LubSdfNodeDesc *nodes,
                            int32_t nodes_count, int32_t root, int32_t n,
                            const float *skin_k, LubMeshData *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  struct MeshScratch *s = mesh_scratch(app);
  if (!s)
    return lub_api_fail(app, "sdf_mesh: out of memory");
  mesh_scratch_clear(s);
  char err[256];
  SdfTree tree;
  if (!sdf_tree_convert(nodes, nodes_count, root, &tree, err, sizeof(err)))
    return lub_api_fail(app, "%s", err);
  bool ok = sdf_mesh_build(&tree, n, skin_k ? *skin_k : 0.0f, &s->sdf, err,
                           sizeof(err));
  // bone の name は SdfTree の parts が持つので scratch に写してから解放する
  memcpy(s->parts, tree.parts, sizeof(s->parts));
  int part_count = tree.part_count;
  sdf_tree_free(&tree);
  if (!ok)
    return lub_api_fail(app, "%s", err);
  lub_mesh_view_from_sn(&s->sdf.mesh, out);
  int32_t vc = out->vert_count;
  out->colors = s->sdf.colors;
  out->colors_count = s->sdf.colors ? vc * 3 : 0;
  out->metal_rough = s->sdf.metal_rough;
  out->metal_rough_count = s->sdf.metal_rough ? vc * 2 : 0;
  if (s->sdf.joints) {
    if (s->joints_cap < vc * 2) {
      free(s->joints);
      s->joints = (int32_t *)malloc(sizeof(int32_t) * (size_t)(vc * 2));
      s->joints_cap = s->joints ? vc * 2 : 0;
    }
    if (!s->joints)
      return lub_api_fail(app, "sdf_mesh: out of memory");
    for (int32_t i = 0; i < vc * 2; ++i)
      s->joints[i] = (int32_t)s->sdf.joints[i];
    out->joints = s->joints;
    out->joints_count = vc * 2;
  }
  out->weights = s->sdf.weights;
  out->weights_count = s->sdf.weights ? vc * 2 : 0;
  for (int i = 0; i < part_count; ++i) {
    s->bones[i].name = lub_str_c(s->parts[i].name);
    s->bones[i].x = s->parts[i].pivot[0];
    s->bones[i].y = s->parts[i].pivot[1];
    s->bones[i].z = s->parts[i].pivot[2];
  }
  out->bones = part_count > 0 ? s->bones : NULL;
  out->bones_count = part_count;
  memcpy(s->bounds_min, s->sdf.mn, sizeof(s->bounds_min));
  memcpy(s->bounds_max, s->sdf.mx, sizeof(s->bounds_max));
  out->bounds_min = s->bounds_min;
  out->bounds_min_count = 3;
  out->bounds_max = s->bounds_max;
  out->bounds_max_count = 3;
  out->has_cell = true;
  out->cell = s->sdf.cell;
  return LUB_OK;
}
