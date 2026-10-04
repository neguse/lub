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
  assert(count == 3 && bindings_count == 2 && opts->has_depth && !opts->depth);
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
  }
  assert(seen == 3);
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
