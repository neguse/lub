#pragma once
// TTF glyph utilities: pure functions from font bytes to pixels / mesh /
// metrics. No caching, no resource handles — atlas management and layout
// live outside the runtime core (lubx). All rasterization and tessellation
// is CPU-side and deterministic across platforms. C API は
// include/lub/lub_api.h (lub_font_*)、Lua binding は src/lua_api.c。
#include "surfacenets.h"
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

// em 単位の vertical metrics。false = invalid font data。
bool font_metrics_get(const uint8_t *ttf, size_t len, float *ascent,
                      float *descent, float *line_gap);

// glyph の bitmap を rasterize する。1 = found (bytes は malloc、空 glyph は
// NULL / w = h = 0)、0 = glyph 無し、-1 = invalid font data。
int font_glyph_bitmap(const uint8_t *ttf, size_t len, int codepoint, float px,
                      int *w, int *h, int *xoff, int *yoff, float *advance,
                      unsigned char **bytes);
void font_glyph_bitmap_free(unsigned char *bytes);

// glyph の輪郭を tessellate して mesh にする (em 単位、y-up、z = 0)。
// 1 = ok、0 = glyph 無し、-1 = error (err に理由)。
int font_glyph_mesh_build(const uint8_t *ttf, size_t len, int codepoint,
                          float tolerance, SnMesh *out, float *advance,
                          char *err, size_t err_size);

// kern の advance 調整 (em)。false = invalid font data。
bool font_kern_get(const uint8_t *ttf, size_t len, int cp1, int cp2,
                   float *out);
