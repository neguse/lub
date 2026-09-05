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
  free(app->mesh_scratch);
  app->mesh_scratch = NULL;
}

static void sn_to_view(const SnMesh *m, LubMeshData *out) {
  memset(out, 0, sizeof(*out));
  out->positions = m->positions;
  out->normals = m->normals;
  out->indices = (const uint32_t *)m->indices;
  out->vert_count = (int32_t)m->vert_count;
  out->index_count = (int32_t)m->index_count;
}

LubStatus lub_mesh_surface_nets(LubContext *ctx, const float *grid, int32_t nx,
                                int32_t ny, int32_t nz, float cell, float ox,
                                float oy, float oz, LubMeshData *out) {
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
  struct MeshScratch *s = mesh_scratch(app);
  if (!s)
    return lub_api_fail(app, "surface_nets: out of memory");
  mesh_scratch_clear(s);
  if (!sn_mesh_from_grid(grid, nx, ny, nz, cell > 0 ? cell : 1.0f, ox, oy, oz,
                         &s->nets))
    return lub_api_fail(app, "surface_nets: out of memory");
  sn_to_view(&s->nets, out);
  return LUB_OK;
}

LubStatus lub_mesh_sdf(LubContext *ctx, const LubSdfNode *nodes, int32_t count,
                       int32_t root, int32_t n, float skin_k, LubSdfMesh *out) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  struct MeshScratch *s = mesh_scratch(app);
  if (!s)
    return lub_api_fail(app, "sdf_mesh: out of memory");
  mesh_scratch_clear(s);
  char err[256];
  SdfTree tree;
  if (!sdf_tree_convert(nodes, count, root, &tree, err, sizeof(err)))
    return lub_api_fail(app, "%s", err);
  bool ok = sdf_mesh_build(&tree, n, skin_k, &s->sdf, err, sizeof(err));
  // bone の name は SdfTree の parts が持つので scratch に写してから解放する
  memcpy(s->parts, tree.parts, sizeof(s->parts));
  int part_count = tree.part_count;
  sdf_tree_free(&tree);
  if (!ok)
    return lub_api_fail(app, "%s", err);
  sn_to_view(&s->sdf.mesh, &out->mesh);
  out->mesh.colors = s->sdf.colors;
  out->mesh.metal_rough = s->sdf.metal_rough;
  out->mesh.joints = s->sdf.joints;
  out->mesh.weights = s->sdf.weights;
  for (int i = 0; i < part_count; ++i) {
    s->bones[i].name = lub_str_c(s->parts[i].name);
    memcpy(s->bones[i].pivot, s->parts[i].pivot, sizeof(s->bones[i].pivot));
  }
  out->bones = s->bones;
  out->bone_count = part_count;
  memcpy(out->bounds_min, s->sdf.mn, sizeof(out->bounds_min));
  memcpy(out->bounds_max, s->sdf.mx, sizeof(out->bounds_max));
  out->cell = s->sdf.cell;
  return LUB_OK;
}
