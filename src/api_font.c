// font の C API。src/font.c の純関数を包み、結果 (bitmap / mesh) は runtime が
// 次の font_* 呼び出しまで持つ view で返す。
#include "api_internal.h"
#include "font.h"
#include <stdlib.h>
#include <string.h>

struct FontScratch {
  unsigned char *bitmap;
  SnMesh mesh;
};

static struct FontScratch *font_scratch(App *app) {
  if (!app->font_scratch)
    app->font_scratch =
        (struct FontScratch *)calloc(1, sizeof(struct FontScratch));
  return app->font_scratch;
}

static void font_scratch_clear(struct FontScratch *s) {
  font_glyph_bitmap_free(s->bitmap);
  s->bitmap = NULL;
  sn_mesh_free(&s->mesh);
  memset(&s->mesh, 0, sizeof(s->mesh));
}

void api_font_shutdown(App *app) {
  if (!app->font_scratch)
    return;
  font_scratch_clear(app->font_scratch);
  free(app->font_scratch);
  app->font_scratch = NULL;
}

static bool font_arg(App *app, LubStr ttf, const char *fn) {
  if (!ttf.ptr || ttf.len <= 0) {
    lub_api_fail(app, "%s: font data required", fn);
    return false;
  }
  return true;
}

LubStatus lub_font_metrics(LubContext *ctx, LubStr ttf, LubFontMetrics *out) {
  App *app = lub_api_app(ctx);
  if (!font_arg(app, ttf, "font_metrics"))
    return LUB_ERROR;
  if (!font_metrics_get((const uint8_t *)ttf.ptr, (size_t)ttf.len, &out->ascent,
                        &out->descent, &out->line_gap))
    return lub_api_fail(app, "font: invalid font data");
  return LUB_OK;
}

LubStatus lub_font_glyph(LubContext *ctx, LubStr ttf, int32_t codepoint,
                         float px, LubGlyphBitmap *out, bool *has) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  *has = false;
  if (!font_arg(app, ttf, "font_glyph"))
    return LUB_ERROR;
  if (!(px > 0.0f) || px > 4096.0f)
    return lub_api_fail(app, "font_glyph: px out of range");
  struct FontScratch *s = font_scratch(app);
  if (!s)
    return lub_api_fail(app, "font_glyph: out of memory");
  font_scratch_clear(s);
  int w = 0, h = 0, xo = 0, yo = 0;
  float adv = 0;
  unsigned char *bytes = NULL;
  int r = font_glyph_bitmap((const uint8_t *)ttf.ptr, (size_t)ttf.len,
                            codepoint, px, &w, &h, &xo, &yo, &adv, &bytes);
  if (r < 0)
    return lub_api_fail(app, "font: invalid font data");
  if (r == 0)
    return LUB_OK; // has = false
  s->bitmap = bytes;
  *has = true;
  out->w = w;
  out->h = h;
  out->xoff = xo;
  out->yoff = yo;
  out->advance = adv;
  if (bytes) {
    out->bytes.ptr = (const char *)bytes;
    out->bytes.len = w * h;
  }
  return LUB_OK;
}

void lub_mesh_view_from_sn(const SnMesh *m, LubMeshData *out);

LubStatus lub_font_glyph_mesh(LubContext *ctx, LubStr ttf, int32_t codepoint,
                              const float *tolerance_p, LubGlyphMesh *out,
                              bool *has) {
  App *app = lub_api_app(ctx);
  memset(out, 0, sizeof(*out));
  *has = false;
  float tolerance = tolerance_p ? *tolerance_p : 0.0f;
  if (!font_arg(app, ttf, "font_glyph_mesh"))
    return LUB_ERROR;
  struct FontScratch *s = font_scratch(app);
  if (!s)
    return lub_api_fail(app, "font_glyph_mesh: out of memory");
  font_scratch_clear(s);
  char err[256];
  float adv = 0;
  int r = font_glyph_mesh_build((const uint8_t *)ttf.ptr, (size_t)ttf.len,
                                codepoint, tolerance > 0 ? tolerance : 0.002f,
                                &s->mesh, &adv, err, sizeof(err));
  if (r < 0)
    return lub_api_fail(app, "%s", err);
  if (r == 0)
    return LUB_OK;
  *has = true;
  out->advance = adv;
  lub_mesh_view_from_sn(&s->mesh, &out->base);
  return LUB_OK;
}

LubStatus lub_font_kern(LubContext *ctx, LubStr ttf, int32_t cp1, int32_t cp2,
                        float *out) {
  App *app = lub_api_app(ctx);
  *out = 0;
  if (!font_arg(app, ttf, "font_kern"))
    return LUB_ERROR;
  if (!font_kern_get((const uint8_t *)ttf.ptr, (size_t)ttf.len, cp1, cp2, out))
    return lub_api_fail(app, "font: invalid font data");
  return LUB_OK;
}
