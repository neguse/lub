#pragma once
// TTF glyph utilities: pure functions from font bytes to pixels / mesh /
// metrics. No caching, no resource handles — atlas management and layout
// live outside the runtime core (lubx). All rasterization and tessellation
// is CPU-side and deterministic across platforms. The C API is in
// include/lub/lub_api.h (lub_font_*), and the tcs VM host functions are in
// src/gen/vm_api_gen.c.
#include "surfacenets.h"
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

// Vertical metrics in em units.
// false = invalid font data.
bool font_metrics_get(const uint8_t *ttf, size_t len, float *ascent,
                      float *descent, float *line_gap);

// Rasterize a glyph bitmap.
// 1 = found (bytes is malloc'ed; an empty glyph gives NULL / w = h = 0), 0 = no
// glyph, -1 = invalid font data.
int font_glyph_bitmap(const uint8_t *ttf, size_t len, int codepoint, float px,
                      int *w, int *h, int *xoff, int *yoff, float *advance,
                      unsigned char **bytes);
void font_glyph_bitmap_free(unsigned char *bytes);

// Tessellate a glyph outline into a mesh (em units, y-up, z = 0).
// 1 = ok, 0 = no glyph, -1 = error (reason in err).
int font_glyph_mesh_build(const uint8_t *ttf, size_t len, int codepoint,
                          float tolerance, SnMesh *out, float *advance,
                          char *err, size_t err_size);

// Kerning advance adjustment (em).
// false = invalid font data.
bool font_kern_get(const uint8_t *ttf, size_t len, int cp1, int cp2,
                   float *out);
