#ifndef LUB_FONT_H
#define LUB_FONT_H

// TTF glyph utilities: pure functions from font bytes to pixels / mesh /
// metrics. No caching, no resource handles — atlas management and layout
// live outside the runtime core (lubx). All rasterization and tessellation
// is CPU-side and deterministic across platforms.
//
//   font_metrics(ttf) -> { ascent, descent, line_gap }        (em units)
//   font_glyph(ttf, codepoint, px)
//     -> nil (glyph missing; caller falls back to another font)
//      | { w, h, xoff, yoff, advance, bytes }
//     px is pixels per em (CSS font-size semantics). bytes is a Lua string
//     of w*h R8 coverage values, row-major top-down (a string so scripts can
//     read it for atlas blits); nil for empty glyphs (spaces). xoff/yoff are
//     the top-left offset from the baseline origin in pixels, y-down.
//   font_glyph_mesh(ttf, codepoint [, tolerance])
//     -> nil | mesh table (load_gltf convention: positions/normals/indices/
//        vert_count/index_count) plus `advance`. Coordinates are em units,
//        y-up, baseline origin, z=0, normals +z. tolerance is the max curve
//        flattening error in em (default 0.002).
//   font_kern(ttf, cp1, cp2) -> advance adjustment in em units.
//
// ttf accepts a Lua string or lub Bytes. Invalid font data raises an error;
// a missing glyph returns nil.

struct lua_State;

int lub_font_metrics(struct lua_State *L);
int lub_font_glyph(struct lua_State *L);
int lub_font_glyph_mesh(struct lua_State *L);
int lub_font_kern(struct lua_State *L);

#endif
