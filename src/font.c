// TTF glyph utilities (see font.h for the Lua contract). Rasterization is
// stb_truetype, tessellation is libtess2; both are CPU-only and give the
// same output on every platform, so glyph pixels and meshes stay
// golden-capture friendly.
#include "font.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "stb_truetype.h"
#include "surfacenets.h"
#include "tesselator.h"

// ---------------------------------------------------------------------------
// helpers

static bool font_init(const uint8_t *data, size_t len, stbtt_fontinfo *f) {
  (void)len;
  int off = stbtt_GetFontOffsetForIndex(data, 0);
  return off >= 0 && stbtt_InitFont(f, data, off);
}

static float font_em_scale(const stbtt_fontinfo *f) {
  return stbtt_ScaleForMappingEmToPixels((stbtt_fontinfo *)f, 1.0f);
}

// growable float2 buffer for flattened contour points
typedef struct {
  float *data;
  int len; // floats, not points
  int cap;
  int oom;
} PtBuf;

static void ptbuf_push(PtBuf *b, float x, float y) {
  if (b->oom)
    return;
  if (b->len + 2 > b->cap) {
    int cap = b->cap ? b->cap * 2 : 256;
    float *p = (float *)realloc(b->data, (size_t)cap * sizeof(float));
    if (!p) {
      b->oom = 1;
      return;
    }
    b->data = p;
    b->cap = cap;
  }
  b->data[b->len++] = x;
  b->data[b->len++] = y;
}

// Adaptive flattening. tol is the max chord deviation (same units as the
// coordinates). Depth cap keeps degenerate control points from recursing
// forever.
static void flat_quad(PtBuf *b, float x0, float y0, float cx, float cy,
                      float x1, float y1, float tol2, int depth) {
  float mx = (x0 + 2.0f * cx + x1) * 0.25f;
  float my = (y0 + 2.0f * cy + y1) * 0.25f;
  float dx = (x0 + x1) * 0.5f - mx;
  float dy = (y0 + y1) * 0.5f - my;
  if (depth >= 16 || dx * dx + dy * dy <= tol2) {
    ptbuf_push(b, x1, y1);
    return;
  }
  float acx = (x0 + cx) * 0.5f, acy = (y0 + cy) * 0.5f;
  float cbx = (cx + x1) * 0.5f, cby = (cy + y1) * 0.5f;
  flat_quad(b, x0, y0, acx, acy, mx, my, tol2, depth + 1);
  flat_quad(b, mx, my, cbx, cby, x1, y1, tol2, depth + 1);
}

static void flat_cubic(PtBuf *b, float x0, float y0, float c0x, float c0y,
                       float c1x, float c1y, float x1, float y1, float tol2,
                       int depth) {
  float d0x = c0x - (x0 * 2.0f + x1) / 3.0f;
  float d0y = c0y - (y0 * 2.0f + y1) / 3.0f;
  float d1x = c1x - (x0 + x1 * 2.0f) / 3.0f;
  float d1y = c1y - (y0 + y1 * 2.0f) / 3.0f;
  float e0 = d0x * d0x + d0y * d0y;
  float e1 = d1x * d1x + d1y * d1y;
  float err = (e0 > e1 ? e0 : e1) * 9.0f / 4.0f;
  if (depth >= 16 || err <= tol2) {
    ptbuf_push(b, x1, y1);
    return;
  }
  float ax = (x0 + c0x) * 0.5f, ay = (y0 + c0y) * 0.5f;
  float bx = (c0x + c1x) * 0.5f, by = (c0y + c1y) * 0.5f;
  float cx = (c1x + x1) * 0.5f, cy = (c1y + y1) * 0.5f;
  float abx = (ax + bx) * 0.5f, aby = (ay + by) * 0.5f;
  float bcx = (bx + cx) * 0.5f, bcy = (by + cy) * 0.5f;
  float mx = (abx + bcx) * 0.5f, my = (aby + bcy) * 0.5f;
  flat_cubic(b, x0, y0, ax, ay, abx, aby, mx, my, tol2, depth + 1);
  flat_cubic(b, mx, my, bcx, bcy, cx, cy, x1, y1, tol2, depth + 1);
}

// ---------------------------------------------------------------------------
// metrics

bool font_metrics_get(const uint8_t *ttf, size_t len, float *ascent,
                      float *descent, float *line_gap) {
  stbtt_fontinfo f;
  if (!font_init(ttf, len, &f))
    return false;
  int a, d, g;
  stbtt_GetFontVMetrics(&f, &a, &d, &g);
  float s = font_em_scale(&f);
  *ascent = a * s;
  *descent = d * s;
  *line_gap = g * s;
  return true;
}

// ---------------------------------------------------------------------------
// glyph bitmap

int font_glyph_bitmap(const uint8_t *ttf, size_t len, int cp, float px, int *w,
                      int *h, int *xoff, int *yoff, float *advance,
                      unsigned char **bytes) {
  stbtt_fontinfo f;
  if (!font_init(ttf, len, &f))
    return -1;
  int g = stbtt_FindGlyphIndex(&f, cp);
  if (g == 0)
    return 0;
  float scale = stbtt_ScaleForMappingEmToPixels(&f, px);
  int adv, lsb;
  stbtt_GetGlyphHMetrics(&f, g, &adv, &lsb);
  (void)lsb;
  int bw = 0, bh = 0, xo = 0, yo = 0;
  unsigned char *bmp =
      stbtt_GetGlyphBitmap(&f, scale, scale, g, &bw, &bh, &xo, &yo);
  *w = bw;
  *h = bh;
  *xoff = xo;
  *yoff = yo;
  *advance = adv * scale;
  if (bmp && bw > 0 && bh > 0) {
    *bytes = bmp;
  } else {
    if (bmp)
      stbtt_FreeBitmap(bmp, NULL);
    *bytes = NULL;
    *w = bw;
    *h = bh;
  }
  return 1;
}

void font_glyph_bitmap_free(unsigned char *bytes) {
  if (bytes)
    stbtt_FreeBitmap(bytes, NULL);
}

// ---------------------------------------------------------------------------
// glyph mesh

int font_glyph_mesh_build(const uint8_t *ttf, size_t len, int cp, float tol,
                          SnMesh *out, float *advance, char *err,
                          size_t err_size) {
  stbtt_fontinfo f;
  if (!font_init(ttf, len, &f)) {
    snprintf(err, err_size, "font: invalid font data");
    return -1;
  }
  if (tol < 1e-5f)
    tol = 1e-5f;
  int g = stbtt_FindGlyphIndex(&f, cp);
  if (g == 0)
    return 0;

  float s = font_em_scale(&f);
  int adv, lsb;
  stbtt_GetGlyphHMetrics(&f, g, &adv, &lsb);
  (void)lsb;

  stbtt_vertex *v = NULL;
  int nv = stbtt_GetGlyphShape(&f, g, &v);

  const char *fail = NULL;
  TESStesselator *tess = NULL;
  PtBuf pts = {0};
  SnMesh m = {0};
  float tol2 = tol * tol;
  float x = 0, y = 0;
  int contours = 0;

  tess = tessNewTess(NULL);
  if (!tess) {
    fail = "font_glyph_mesh: out of memory";
    goto done;
  }
  for (int i = 0; i < nv; ++i) {
    switch (v[i].type) {
    case STBTT_vmove:
      if (pts.len >= 6) {
        tessAddContour(tess, 2, pts.data, 2 * sizeof(float), pts.len / 2);
        contours++;
      }
      pts.len = 0;
      x = v[i].x * s;
      y = v[i].y * s;
      ptbuf_push(&pts, x, y);
      break;
    case STBTT_vline:
      x = v[i].x * s;
      y = v[i].y * s;
      ptbuf_push(&pts, x, y);
      break;
    case STBTT_vcurve: {
      float nx = v[i].x * s, ny = v[i].y * s;
      flat_quad(&pts, x, y, v[i].cx * s, v[i].cy * s, nx, ny, tol2, 0);
      x = nx;
      y = ny;
      break;
    }
    case STBTT_vcubic: {
      float nx = v[i].x * s, ny = v[i].y * s;
      flat_cubic(&pts, x, y, v[i].cx * s, v[i].cy * s, v[i].cx1 * s,
                 v[i].cy1 * s, nx, ny, tol2, 0);
      x = nx;
      y = ny;
      break;
    }
    }
  }
  if (pts.len >= 6) {
    tessAddContour(tess, 2, pts.data, 2 * sizeof(float), pts.len / 2);
    contours++;
  }
  if (pts.oom) {
    fail = "font_glyph_mesh: out of memory";
    goto done;
  }

  // Empty outline (spaces): return an empty mesh, not nil — the glyph exists
  // and its advance is meaningful.
  if (contours > 0 &&
      !tessTesselate(tess, TESS_WINDING_NONZERO, TESS_POLYGONS, 3, 2, NULL)) {
    fail = "font_glyph_mesh: tessellation failed";
    goto done;
  }

  {
    int nvert = contours > 0 ? tessGetVertexCount(tess) : 0;
    int nelem = contours > 0 ? tessGetElementCount(tess) : 0;
    const TESSreal *tv = tessGetVertices(tess);
    const TESSindex *te = tessGetElements(tess);

    m.positions =
        (float *)malloc(sizeof(float) * 3 * (size_t)(nvert ? nvert : 1));
    m.normals =
        (float *)malloc(sizeof(float) * 3 * (size_t)(nvert ? nvert : 1));
    m.indices =
        (int32_t *)malloc(sizeof(int32_t) * 3 * (size_t)(nelem ? nelem : 1));
    if (!m.positions || !m.normals || !m.indices) {
      fail = "font_glyph_mesh: out of memory";
      goto done;
    }
    for (int i = 0; i < nvert; ++i) {
      m.positions[i * 3 + 0] = tv[i * 2 + 0];
      m.positions[i * 3 + 1] = tv[i * 2 + 1];
      m.positions[i * 3 + 2] = 0.0f;
      m.normals[i * 3 + 0] = 0.0f;
      m.normals[i * 3 + 1] = 0.0f;
      m.normals[i * 3 + 2] = 1.0f;
    }
    m.vert_count = (size_t)nvert;
    size_t ni = 0;
    for (int e = 0; e < nelem; ++e) {
      TESSindex a = te[e * 3 + 0], b = te[e * 3 + 1], c = te[e * 3 + 2];
      if (a == TESS_UNDEF || b == TESS_UNDEF || c == TESS_UNDEF)
        continue;
      m.indices[ni++] = (int32_t)a;
      m.indices[ni++] = (int32_t)b;
      m.indices[ni++] = (int32_t)c;
    }
    m.index_count = ni;
  }

done:
  free(pts.data);
  if (v)
    stbtt_FreeShape(&f, v);
  if (tess)
    tessDeleteTess(tess);
  if (fail) {
    sn_mesh_free(&m);
    snprintf(err, err_size, "%s", fail);
    return -1;
  }
  *out = m;
  *advance = adv * s;
  return 1;
}

// ---------------------------------------------------------------------------
// kern

bool font_kern_get(const uint8_t *ttf, size_t len, int cp1, int cp2,
                   float *out) {
  stbtt_fontinfo f;
  if (!font_init(ttf, len, &f))
    return false;
  int g1 = stbtt_FindGlyphIndex(&f, cp1);
  int g2 = stbtt_FindGlyphIndex(&f, cp2);
  *out = (g1 == 0 || g2 == 0)
             ? 0.0f
             : stbtt_GetGlyphKernAdvance(&f, g1, g2) * font_em_scale(&f);
  return true;
}
