// font の C API。src/font.c の純関数を包み、結果 (bitmap / mesh) は frame の
// 終わりまで有効な view で返す (実体は呼び出しごとに作り、frame garbage に
// 預ける)。
#include "api_internal.h"
#include "font.h"
#include <stdlib.h>
#include <string.h>

static void bitmap_free(void *bytes) {
  font_glyph_bitmap_free((unsigned char *)bytes);
}

static bool font_arg(App *app, LubStr ttf, const char *fn) {
  if (!ttf.ptr || ttf.len <= 0) {
    lub_api_fail(app, "%s: font data required", fn);
    return false;
  }
  return true;
}

LubStatus lub_font_metrics(LubContext *ctx, const uint8_t *ttf_ptr,
                           int32_t ttf_len, LubFontMetrics *out) {
  LubStr ttf = {(const char *)ttf_ptr, ttf_len};
  App *app = lub_api_app(ctx);
  if (!font_arg(app, ttf, "font_metrics"))
    return LUB_ERROR;
  if (!font_metrics_get((const uint8_t *)ttf.ptr, (size_t)ttf.len, &out->ascent,
                        &out->descent, &out->line_gap))
    return lub_api_fail(app, "font: invalid font data");
  return LUB_OK;
}

LubStatus lub_font_glyph(LubContext *ctx, const uint8_t *ttf_ptr,
                         int32_t ttf_len, int32_t codepoint, float px,
                         LubGlyphBitmap *out, bool *has) {
  LubStr ttf = {(const char *)ttf_ptr, ttf_len};
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  *has = false;
  if (!font_arg(app, ttf, "font_glyph"))
    return LUB_ERROR;
  if (!(px > 0.0f) || px > 4096.0f)
    return lub_api_fail(app, "font_glyph: px out of range");
  int w = 0, h = 0, xo = 0, yo = 0;
  float adv = 0;
  unsigned char *bytes = NULL;
  int r = font_glyph_bitmap((const uint8_t *)ttf.ptr, (size_t)ttf.len,
                            codepoint, px, &w, &h, &xo, &yo, &adv, &bytes);
  if (r < 0)
    return lub_api_fail(app, "font: invalid font data");
  if (r == 0)
    return LUB_OK; // has = false
  app_frame_garbage_push_fn(app, bytes, bitmap_free);
  *has = true;
  out->w = w;
  out->h = h;
  out->xoff = xo;
  out->yoff = yo;
  out->advance = adv;
  if (bytes) {
    out->bytes.ptr = bytes;
    out->bytes.len = w * h;
    out->bytes.frame = (int32_t)app->frame_index;
  }
  return LUB_OK;
}

void lub_mesh_view_from_sn(const SnMesh *m, LubMeshData *out);
void lub_mesh_frame_garbage(App *app, SnMesh *m);

LubStatus lub_font_glyph_mesh(LubContext *ctx, const uint8_t *ttf_ptr,
                              int32_t ttf_len, int32_t codepoint,
                              const float *tolerance_p, LubGlyphMesh *out,
                              bool *has) {
  LubStr ttf = {(const char *)ttf_ptr, ttf_len};
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  *has = false;
  float tolerance = tolerance_p ? *tolerance_p : 0.0f;
  if (!font_arg(app, ttf, "font_glyph_mesh"))
    return LUB_ERROR;
  char err[256];
  float adv = 0;
  SnMesh mesh = {0};
  int r = font_glyph_mesh_build((const uint8_t *)ttf.ptr, (size_t)ttf.len,
                                codepoint, tolerance > 0 ? tolerance : 0.002f,
                                &mesh, &adv, err, sizeof(err));
  if (r < 0)
    return lub_api_fail(app, "%s", err);
  if (r == 0)
    return LUB_OK;
  *has = true;
  out->advance = adv;
  lub_mesh_view_from_sn(&mesh, &out->base);
  lub_mesh_frame_garbage(app, &mesh);
  return LUB_OK;
}

LubStatus lub_font_kern(LubContext *ctx, const uint8_t *ttf_ptr,
                        int32_t ttf_len, int32_t cp1, int32_t cp2, float *out) {
  LubStr ttf = {(const char *)ttf_ptr, ttf_len};
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!font_arg(app, ttf, "font_kern"))
    return LUB_ERROR;
  if (!font_kern_get((const uint8_t *)ttf.ptr, (size_t)ttf.len, cp1, cp2, out))
    return lub_api_fail(app, "font: invalid font data");
  return LUB_OK;
}
