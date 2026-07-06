// TTF glyph utilities (see font.h for the Lua contract). Rasterization is
// stb_truetype, tessellation is libtess2; both are CPU-only and give the
// same output on every platform, so glyph pixels and meshes stay
// golden-capture friendly.
#include "font.h"

#include <lauxlib.h>
#include <lua.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

#include "lua_api.h"
#include "stb_truetype.h"
#include "surfacenets.h"
#include "tesselator.h"

// ---------------------------------------------------------------------------
// helpers

static void font_check(lua_State *L, int idx, stbtt_fontinfo *f) {
  size_t len = 0;
  const uint8_t *data = lub_bytes_arg(L, idx, &len);
  int off = stbtt_GetFontOffsetForIndex(data, 0);
  if (off < 0 || !stbtt_InitFont(f, data, off))
    luaL_error(L, "font: invalid font data");
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
// font_metrics(ttf) -> { ascent, descent, line_gap }

int lub_font_metrics(lua_State *L) {
  stbtt_fontinfo f;
  font_check(L, 1, &f);
  int ascent, descent, line_gap;
  stbtt_GetFontVMetrics(&f, &ascent, &descent, &line_gap);
  float s = font_em_scale(&f);
  lua_createtable(L, 0, 3);
  lua_pushnumber(L, ascent * s);
  lua_setfield(L, -2, "ascent");
  lua_pushnumber(L, descent * s);
  lua_setfield(L, -2, "descent");
  lua_pushnumber(L, line_gap * s);
  lua_setfield(L, -2, "line_gap");
  return 1;
}

// ---------------------------------------------------------------------------
// font_glyph(ttf, codepoint, px) -> nil | { w, h, xoff, yoff, advance, bytes }

int lub_font_glyph(lua_State *L) {
  stbtt_fontinfo f;
  font_check(L, 1, &f);
  int cp = (int)luaL_checkinteger(L, 2);
  float px = (float)luaL_checknumber(L, 3);
  if (!(px > 0.0f) || px > 4096.0f)
    return luaL_error(L, "font_glyph: px out of range");

  int g = stbtt_FindGlyphIndex(&f, cp);
  if (g == 0) {
    lua_pushnil(L);
    return 1;
  }

  float scale = stbtt_ScaleForMappingEmToPixels(&f, px);
  int adv, lsb;
  stbtt_GetGlyphHMetrics(&f, g, &adv, &lsb);
  (void)lsb;

  int w = 0, h = 0, xo = 0, yo = 0;
  unsigned char *bmp =
      stbtt_GetGlyphBitmap(&f, scale, scale, g, &w, &h, &xo, &yo);

  lua_createtable(L, 0, 6);
  lua_pushinteger(L, w);
  lua_setfield(L, -2, "w");
  lua_pushinteger(L, h);
  lua_setfield(L, -2, "h");
  lua_pushinteger(L, xo);
  lua_setfield(L, -2, "xoff");
  lua_pushinteger(L, yo);
  lua_setfield(L, -2, "yoff");
  lua_pushnumber(L, adv * scale);
  lua_setfield(L, -2, "advance");
  if (bmp && w > 0 && h > 0) {
    // Lua string: readable from script (string.byte) so the atlas blit can
    // happen outside the core.
    lua_pushlstring(L, (const char *)bmp, (size_t)w * (size_t)h);
    lua_setfield(L, -2, "bytes");
  }
  if (bmp)
    stbtt_FreeBitmap(bmp, NULL);
  return 1;
}

// ---------------------------------------------------------------------------
// font_glyph_mesh(ttf, codepoint [, tolerance]) -> nil | mesh table

int lub_font_glyph_mesh(lua_State *L) {
  stbtt_fontinfo f;
  font_check(L, 1, &f);
  int cp = (int)luaL_checkinteger(L, 2);
  float tol = (float)luaL_optnumber(L, 3, 0.002);
  if (tol < 1e-5f)
    tol = 1e-5f;

  int g = stbtt_FindGlyphIndex(&f, cp);
  if (g == 0) {
    lua_pushnil(L);
    return 1;
  }

  float s = font_em_scale(&f);
  int adv, lsb;
  stbtt_GetGlyphHMetrics(&f, g, &adv, &lsb);
  (void)lsb;

  stbtt_vertex *v = NULL;
  int nv = stbtt_GetGlyphShape(&f, g, &v);

  const char *err = NULL;
  TESStesselator *tess = NULL;
  PtBuf pts = {0};
  SnMesh m = {0};
  float tol2 = tol * tol;
  float x = 0, y = 0;

  int contours = 0;

  tess = tessNewTess(NULL);
  if (!tess) {
    err = "font_glyph_mesh: out of memory";
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
    err = "font_glyph_mesh: out of memory";
    goto done;
  }

  // Empty outline (spaces): return an empty mesh, not nil — the glyph exists
  // and its advance is meaningful.
  if (contours > 0 &&
      !tessTesselate(tess, TESS_WINDING_NONZERO, TESS_POLYGONS, 3, 2, NULL)) {
    err = "font_glyph_mesh: tessellation failed";
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
      err = "font_glyph_mesh: out of memory";
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
  if (err) {
    sn_mesh_free(&m);
    return luaL_error(L, "%s", err);
  }
  sn_mesh_push(L, &m);
  sn_mesh_free(&m);
  lua_pushnumber(L, adv * s);
  lua_setfield(L, -2, "advance");
  return 1;
}

// ---------------------------------------------------------------------------
// font_kern(ttf, cp1, cp2) -> em advance adjustment

int lub_font_kern(lua_State *L) {
  stbtt_fontinfo f;
  font_check(L, 1, &f);
  int cp1 = (int)luaL_checkinteger(L, 2);
  int cp2 = (int)luaL_checkinteger(L, 3);
  int g1 = stbtt_FindGlyphIndex(&f, cp1);
  int g2 = stbtt_FindGlyphIndex(&f, cp2);
  if (g1 == 0 || g2 == 0) {
    lua_pushnumber(L, 0.0);
    return 1;
  }
  lua_pushnumber(L, stbtt_GetGlyphKernAdvance(&f, g1, g2) * font_em_scale(&f));
  return 1;
}
