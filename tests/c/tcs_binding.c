// clang-format off
#include "game.c"
#include "binding.c"
// clang-format on
#include <assert.h>

const char *lub_last_error(LubContext *ctx) {
  (void)ctx;
  return "failure";
}
LubHandle lub_gfx_main_tex(LubContext *ctx) {
  (void)ctx;
  return -1;
}
bool lub_gfx_resource_info(LubContext *ctx, int32_t handle, LubStr *key,
                           int32_t *version) {
  (void)ctx;
  (void)key;
  if (handle < 0)
    return false;
  assert(handle == 42);
  *version = 7;
  return true;
}
LubStatus lub_gfx_use_texture(LubContext *ctx, LubStr key, int32_t w, int32_t h,
                              int32_t format, const int32_t *data,
                              int32_t data_count, const int32_t *version,
                              const LubTextureOpts *opts, LubHandle *out) {
  (void)ctx;
  (void)format;
  assert(key.len == 5 && !memcmp(key.ptr, "image", 5));
  assert(w == 1 && h == 1 && data_count == 4 && data[1] == 128);
  assert(*version == 7 && opts->has_target && opts->target);
  *out = 42;
  return LUB_OK;
}
LubStatus lub_gfx_draw(LubContext *ctx, int32_t count,
                       const LubBinding *bindings, int32_t bindings_count,
                       const LubDrawOpts *opts) {
  (void)ctx;
  assert(count == 3 && bindings_count == 3 && opts->has_depth && !opts->depth);
  int seen = 0;
  for (int i = 0; i < bindings_count; i++) {
    if (!memcmp(bindings[i].name.ptr, "image", 5)) {
      assert(bindings[i].handle == 42);
      seen |= 1;
    }
    if (!memcmp(bindings[i].name.ptr, "color", 5)) {
      assert(bindings[i].count == 4 && bindings[i].values[3] == 4);
      seen |= 2;
    }
    if (!memcmp(bindings[i].name.ptr, "scale", 5)) {
      assert(bindings[i].count == 2 && bindings[i].values[1] == 6);
      seen |= 4;
    }
  }
  assert(seen == 7);
  return LUB_OK;
}
LubStatus lub_xr_view(LubContext *ctx, int32_t eye, float near_plane,
                      float far_plane, LubXrView *out) {
  (void)ctx;
  assert(near_plane == .05f && far_plane == 500);
  if (eye == 1)
    return LUB_NOT_FOUND;
  memset(out, 0, sizeof(*out));
  out->target = -2;
  out->width = 123;
  out->view_projection[15] = 1;
  return LUB_OK;
}
bool lub_audio_voice(LubContext *ctx, LubStr key, int32_t snd,
                     const LubVoiceOpts *opts) {
  (void)ctx;
  (void)key;
  assert(snd == 12 && opts->has_loop && opts->loop);
  assert(opts->base.has_volume && opts->base.volume == .25f);
  return true;
}
int32_t lub_frame_index(LubContext *ctx) {
  (void)ctx;
  return 5;
}
LubStatus lub_io_load_bytes(LubContext *ctx, LubStr path, LubView *bytes,
                            int32_t *version, int32_t *status, LubStr *error) {
  (void)ctx;
  (void)path;
  (void)error;
  static const uint8_t data[] = {1, 2, 3};
  *bytes = (LubView){data, 3, 5};
  *version = 1;
  *status = 0;
  return LUB_OK;
}
LubStatus lub_audio_snd_bytes(LubContext *ctx, LubStr key, const uint8_t *data,
                              int32_t data_len, int32_t channels, int32_t rate,
                              const int32_t *version, int32_t *out) {
  (void)ctx;
  (void)key;
  assert(data_len == 3 && data[2] == 3 && channels == 1 && rate == 48000);
  assert(!version);
  *out = 9;
  return LUB_OK;
}
void lub_audio_info(LubContext *ctx, LubAudioInfo *out) {
  (void)ctx;
  memset(out, 0, sizeof(*out));
  out->rate = 48000;
}
LubStatus lub_mesh_sdf_mesh(LubContext *ctx, const LubSdfNodeDesc *nodes,
                            int32_t nodes_count, int32_t root, int32_t n,
                            const float *skin_k, LubMeshData *out) {
  (void)ctx;
  static const float positions[6] = {0, 1, 2, 3, 4, 5};
  static const LubSdfBone bones[1] = {{{"b", 1}, 1, 2, 3}};
  assert(nodes_count == 1 && root == 0 && n == 8 && !skin_k);
  assert(nodes[0].op == LUB_MESH_SDF_OP_SPHERE && nodes[0].a == -1);
  assert(nodes[0].params_count == 2 && nodes[0].params[1] == 2);
  assert(nodes[0].name.len == 4 && !memcmp(nodes[0].name.ptr, "root", 4));
  memset(out, 0, sizeof(*out));
  out->positions = positions;
  out->positions_count = 6;
  out->vert_count = 2;
  out->bones = bones;
  out->bones_count = 1;
  return LUB_OK;
}
LubStatus lub_io_interleave_pncm(LubContext *ctx, const LubMeshData *mesh,
                                 const float **out, int32_t *out_count) {
  (void)ctx;
  static const float packed[2] = {7, 8};
  assert(mesh->positions_count == 6 && mesh->positions[5] == 5);
  assert(mesh->vert_count == 2 && !mesh->uvs && mesh->uvs_count == 0);
  assert(mesh->bones_count == 1 && mesh->bones[0].x == 1);
  assert(mesh->bones[0].name.len == 1 && mesh->bones[0].name.ptr[0] == 'b');
  *out = packed;
  *out_count = 2;
  return LUB_OK;
}
LubStatus lub_io_load_floats(LubContext *ctx, LubStr path, const float **data,
                             int32_t *data_count, int32_t *version,
                             int32_t *status, LubStr *error) {
  (void)ctx;
  (void)path;
  (void)error;
  *data = NULL;
  *data_count = 0;
  *version = 0;
  *status = LUB_IO_STATUS_PENDING;
  return LUB_OK;
}
LubStatus lub_font_glyph(LubContext *ctx, const uint8_t *ttf, int32_t ttf_len,
                         int32_t codepoint, float px, LubGlyphBitmap *out,
                         bool *has) {
  (void)ctx;
  static const uint8_t alpha[] = {9, 10, 11};
  assert(ttf_len == 3 && ttf[0] == 1 && px == 12);
  *has = codepoint == 66;
  memset(out, 0, sizeof(*out));
  out->w = 3;
  out->bytes = (LubView){alpha, 3, 5};
  return LUB_OK;
}
LubStatus lub_phys2d_world(LubContext *ctx, LubStr key,
                           const LubWorldOpts *opts, LubHandle *out) {
  (void)ctx;
  (void)key;
  assert(opts->has_substeps && opts->substeps == 4 && !opts->callbacks.filter);
  *out = 50;
  return LUB_OK;
}
LubHandle lub_phys2d_find_joint(LubContext *ctx, LubHandle world, LubStr key) {
  (void)ctx;
  assert(world == 50);
  return key.len == 1 ? 51 : 52;
}
LubStatus lub_phys2d_joint_angle(LubContext *ctx, LubHandle joint, float *out,
                                 bool *has) {
  (void)ctx;
  *has = joint == 51;
  *out = *has ? 2.5f : 0;
  return LUB_OK;
}
LubStatus lub_io_load_gltf(LubContext *ctx, LubStr path, LubGltfMesh *mesh,
                           bool *has_mesh, int32_t *version, int32_t *status,
                           LubStr *error) {
  (void)ctx;
  (void)error;
  memset(mesh, 0, sizeof(*mesh));
  *has_mesh = path.len == 5;
  mesh->base.vert_count = 11;
  *version = 1;
  *status = 0;
  return LUB_OK;
}
LubStatus lub_phys2d_raycast_all(LubContext *ctx, LubHandle world,
                                 const LubRaycastDesc *query,
                                 LubPhys2dRaycastAllVisitorFn visitor,
                                 void *visitor_user, const LubRayHit **out,
                                 int32_t *out_count) {
  static LubRayHit hits[2];
  (void)ctx;
  assert(world == 50 && query->has_dx && query->dx == 1);
  assert(query->has_filter && query->filter.has_category_bits);
  assert(query->filter.category_bits == 0x1f && !query->filter.has_mask_bits);
  memset(hits, 0, sizeof(hits));
  hits[0].fraction = .25f;
  hits[1].fraction = -1;
  hits[1].base.has_category_bits = true;
  hits[1].base.category_bits = 0xabc;
  int32_t kept = 0;
  for (int i = 0; i < 2; i++)
    if (visitor(visitor_user, &hits[i]) >= 0)
      hits[kept++] = hits[i];
  *out = hits;
  *out_count = kept;
  return LUB_OK;
}
