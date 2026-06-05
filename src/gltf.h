#pragma once

#include <stdbool.h>

struct lua_State;

// load_gltf(path) -> table | nil
//
// Lua-side return value (success): single table with fields:
//   positions   = { x0,y0,z0, x1,y1,z1, ... }    -- always present, vec3 floats
//   normals     = { nx,ny,nz, ... }              -- nil if not present
//   uvs         = { u0,v0, u1,v1, ... }          -- nil if not present
//   (TEXCOORD_0 only) tangents    = { tx,ty,tz,tw, ... }           -- nil if
//   not present (vec4, w=handedness) indices     = { i0,i1,i2, ... } -- nil if
//   non-indexed vert_count  = N index_count = M  (0 if non-indexed)
//   primitives = { primitive tables with the same fields + material = {...} }
//   primitive_count = P
//
// The top-level position/normal/uv/index fields mirror primitives[1] for
// compatibility with older samples. Triangle primitives required.
int lub_load_gltf(struct lua_State *L);
